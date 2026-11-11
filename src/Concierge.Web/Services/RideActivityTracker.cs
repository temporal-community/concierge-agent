using System.Globalization;
using Concierge.Contracts;

namespace Concierge.Web.Services;

/// <summary>
/// The guest's ride activity, UI-free: a snapshot, then the live stream from its end offset, plus the
/// open/mark-as-read flow and the unread badge. A generation counter drops work from before the last
/// Start/Reset/Stop. (Not RideActivityFeed: that name is the stream contract in Concierge.Contracts.)
/// </summary>
/// <param name="dispatch">Runs a callback on the component's renderer (ComponentBase.InvokeAsync).</param>
/// <param name="changed">Requests a re-render; called on the dispatcher.</param>
/// <param name="onUnauthorized">The credential is no longer valid; called on the dispatcher.</param>
public sealed class RideActivityTracker(
    IConciergeService concierge,
    ILogger logger,
    Func<Func<Task>, Task> dispatch,
    Action changed,
    Func<Task> onUnauthorized) : IAsyncDisposable
{
    private readonly List<RideActivityEntry> entries = [];
    private readonly HashSet<string> readEntryKeys = new(StringComparer.Ordinal);
    private ConciergeRideActivityWindow? window;
    private CancellationTokenSource? cancellation;
    private Task? streamTask;
    private string? credential;
    private int generation;

    public IReadOnlyList<RideActivityEntry> Entries => entries;

    public IReadOnlySet<string> ReadEntryKeys => readEntryKeys;

    public bool IsOpen { get; private set; }

    public bool IsPaused { get; private set; }

    public bool IsReconnecting { get; private set; }

    public string? Error { get; private set; }

    public int UnreadCount => window?.UnreadCount ?? 0;

    public string Badge => UnreadCount > 9 ? "9+" : UnreadCount.ToString(CultureInfo.InvariantCulture);

    public string AccessibleName => UnreadCount switch
    {
        0 => "Activity",
        1 => "Activity, 1 unread ride update",
        _ => $"Activity, {UnreadCount} unread ride updates",
    };

    /// <summary>Loads the snapshot and follows the stream. A no-op when already following this credential.</summary>
    public async Task StartAsync(string guestCredential)
    {
        if (StringComparer.Ordinal.Equals(credential, guestCredential) && streamTask is not null)
        {
            return;
        }

        await StopAsync();
        var current = ++generation;
        credential = guestCredential;
        var source = new CancellationTokenSource();
        cancellation = source;
        try
        {
            var snapshot = await concierge.ReadRideActivitySnapshotAsync(guestCredential, source.Token);
            if (current != generation) return;
            Merge(snapshot);
            streamTask = ConsumeAsync(current, guestCredential, snapshot.ReadState.SnapshotOffset, source);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
        }
        catch (ConciergeApiException exception) when (IsUnauthorized(exception))
        {
            if (current == generation) await onUnauthorized();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (current != generation) return;
            Pause(exception is ConciergeApiException api
                ? api.Message
                : "Ride activity updates are temporarily unavailable.");
        }
    }

    /// <summary>Opens the panel (re-reading the snapshot and marking it read) or closes it.</summary>
    public async Task ToggleOpenAsync()
    {
        if (IsOpen)
        {
            Close();
            return;
        }

        IsOpen = true;
        if (credential is not { } current) return;
        var opened = generation;
        if (IsPaused || Error is not null)
        {
            IsPaused = false;
            IsReconnecting = true;
        }

        var token = cancellation?.Token ?? CancellationToken.None;
        try
        {
            // A fresh snapshot makes the read boundary include everything published before this open.
            var snapshot = await concierge.ReadRideActivitySnapshotAsync(current, token);
            if (opened != generation || !IsOpen) return;
            Merge(snapshot);
            var readState = await concierge.MarkRideActivitySnapshotReadAsync(
                current, snapshot.ReadState.SnapshotOffset, token);
            if (opened != generation || !IsOpen) return;
            window?.ApplyReadState(readState);
            RefreshReadKeys();
            IsPaused = false;
            IsReconnecting = false;
            Error = null;
            if (streamTask is null)
            {
                await StartAsync(current);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (ConciergeApiException exception) when (IsUnauthorized(exception))
        {
            if (opened == generation) await onUnauthorized();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (opened != generation) return;
            Pause(exception is ConciergeApiException api
                ? api.Message
                : "Ride activity could not be refreshed. Please try again.");
        }
    }

    public void Close() => IsOpen = false;

    /// <summary>Stops following the stream; entries stay until <see cref="ResetAsync"/>.</summary>
    public async Task StopAsync()
    {
        generation++;
        var task = streamTask;
        var source = cancellation;
        streamTask = null;
        cancellation = null;
        credential = null;
        if (source is not null)
        {
            await source.CancelAsync();
        }

        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Clears everything (Start over, End chat, expiry).</summary>
    public async Task ResetAsync()
    {
        IsOpen = false;
        await StopAsync();
        entries.Clear();
        readEntryKeys.Clear();
        window = null;
        IsPaused = false;
        IsReconnecting = false;
        Error = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task ConsumeAsync(int current, string guestCredential, long fromOffset, CancellationTokenSource source)
    {
        // Leave the caller's frame before the first await of the stream.
        await Task.Yield();
        try
        {
            await foreach (var item in concierge.StreamRideActivityAsync(guestCredential, fromOffset, source.Token)
                .WithCancellation(source.Token))
            {
                await dispatch(() =>
                {
                    if (current != generation || window is null) return Task.CompletedTask;
                    IsPaused = false;
                    IsReconnecting = false;
                    Error = null;
                    if (window.Apply(item))
                    {
                        SyncEntries();
                        RefreshReadKeys();
                    }

                    changed();
                    return Task.CompletedTask;
                });
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
        }
        catch (ConciergeApiException exception) when (IsUnauthorized(exception))
        {
            await dispatch(async () =>
            {
                if (current != generation) return;
                streamTask = null;
                await onUnauthorized();
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "Ride activity updates stopped for the current stay. Cause types: {CauseTypes}.",
                ConciergeDiagnostics.ExceptionTypeChain(exception));
            await dispatch(() =>
            {
                if (current == generation)
                {
                    Pause("Ride activity updates are temporarily unavailable.");
                    changed();
                }

                return Task.CompletedTask;
            });
        }
        finally
        {
            if (current == generation)
            {
                streamTask = null;
            }

            source.Dispose();
        }
    }

    private void Merge(ConciergeRideActivitySnapshot snapshot)
    {
        if (window is null)
        {
            window = new ConciergeRideActivityWindow(snapshot);
        }
        else
        {
            window.MergeSnapshot(snapshot);
        }

        SyncEntries();
        RefreshReadKeys();
        Error = null;
        IsPaused = false;
    }

    private void SyncEntries()
    {
        entries.Clear();
        if (window is null) return;
        entries.AddRange(window.Entries
            .Select(item => item.Entry)
            .OrderByDescending(entry => entry.ChangedAtUtc)
            .ThenByDescending(entry => entry.Sequence));
    }

    private void RefreshReadKeys()
    {
        readEntryKeys.Clear();
        if (window is null) return;
        foreach (var item in window.Entries)
        {
            if (item.Offset < window.ReadState.ReadOffset)
            {
                readEntryKeys.Add(EntryKey(item.Entry));
            }
        }
    }

    private void Pause(string message)
    {
        IsPaused = true;
        IsReconnecting = false;
        Error = message;
    }

    /// <summary>The key RideActivityPanel uses for read state.</summary>
    internal static string EntryKey(RideActivityEntry entry) =>
        $"{entry.RideId:N}:{entry.Sequence.ToString(CultureInfo.InvariantCulture)}";

    private static bool IsUnauthorized(ConciergeApiException exception) =>
        exception.StatusCode == StatusCodes.Status401Unauthorized
        || StringComparer.Ordinal.Equals(exception.Code, "unauthorized");
}
