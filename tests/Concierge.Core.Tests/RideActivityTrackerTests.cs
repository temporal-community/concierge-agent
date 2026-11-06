using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Concierge.Contracts;
using Concierge.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Concierge.Core.Tests;

/// <summary>PROTOTYPE (ADR 0015): the UI-free ride feed the AG-UI page binds to RideActivityPanel.</summary>
public sealed class RideActivityTrackerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task StartsFromTheSnapshotThenFollowsTheStreamFromItsEnd()
    {
        var service = new FakeRides(Snapshot(readOffset: 0, Item(0), Item(1)));
        var (tracker, changes, _) = Create(service);

        await tracker.StartAsync("credential");
        Assert.Equal(2, tracker.Entries.Count);
        Assert.Equal(2, tracker.UnreadCount);
        Assert.Equal(2, await service.StreamFrom.Task);

        await service.Live.Writer.WriteAsync(Item(2));
        await WaitUntil(() => tracker.Entries.Count == 3);
        Assert.Equal(3, tracker.UnreadCount);
        Assert.True(changes() > 0);
        await tracker.DisposeAsync();
    }

    [Fact]
    public async Task OpeningMarksTheFreshSnapshotRead()
    {
        var service = new FakeRides(Snapshot(readOffset: 0, Item(0), Item(1)));
        var (tracker, _, _) = Create(service);
        await tracker.StartAsync("credential");

        await tracker.ToggleOpenAsync();

        Assert.True(tracker.IsOpen);
        Assert.Equal(2, service.MarkedReadOffset);
        Assert.Equal(0, tracker.UnreadCount);
        Assert.Equal("Activity", tracker.AccessibleName);
        Assert.Equal(2, tracker.ReadEntryKeys.Count);
        await tracker.ToggleOpenAsync();
        Assert.False(tracker.IsOpen);
        await tracker.DisposeAsync();
    }

    [Fact]
    public async Task BadgeAndAccessibleNameFollowTheUnreadCount()
    {
        var items = Enumerable.Range(0, 12).Select(offset => Item(offset)).ToArray();
        var (tracker, _, _) = Create(new FakeRides(Snapshot(readOffset: 0, items)));

        await tracker.StartAsync("credential");

        Assert.Equal(12, tracker.UnreadCount);
        Assert.Equal("9+", tracker.Badge);
        Assert.Equal("Activity, 12 unread ride updates", tracker.AccessibleName);
        await tracker.DisposeAsync();
    }

    [Fact]
    public async Task UnauthorizedSnapshotExpiresTheSessionAndOtherFailuresPause()
    {
        var expired = new FakeRides(Snapshot(0)) { SnapshotFailure = new ConciergeApiException("unauthorized", "Authenticate again.", 401) };
        var (tracker, _, unauthorized) = Create(expired);
        await tracker.StartAsync("credential");
        Assert.Equal(1, unauthorized());

        var down = new FakeRides(Snapshot(0)) { SnapshotFailure = new ConciergeApiException("unavailable", "Rides are down.", 503) };
        var (paused, _, notExpired) = Create(down);
        await paused.StartAsync("credential");
        Assert.True(paused.IsPaused);
        Assert.Equal("Rides are down.", paused.Error);
        Assert.Equal(0, notExpired());
    }

    [Fact]
    public async Task ResetDropsLaterStreamItemsAndClearsTheWindow()
    {
        var service = new FakeRides(Snapshot(readOffset: 0, Item(0)));
        var (tracker, _, _) = Create(service);
        await tracker.StartAsync("credential");
        await service.StreamFrom.Task;

        await tracker.ResetAsync();
        await service.Live.Writer.WriteAsync(Item(1));
        await Task.Delay(100);

        Assert.Empty(tracker.Entries);
        Assert.Equal(0, tracker.UnreadCount);
        Assert.False(tracker.IsOpen);
    }

    private static (RideActivityTracker Tracker, Func<int> Changes, Func<int> Unauthorized) Create(FakeRides service)
    {
        var changes = 0;
        var unauthorized = 0;
        var tracker = new RideActivityTracker(
            service,
            NullLogger.Instance,
            callback => callback(),
            () => Interlocked.Increment(ref changes),
            () =>
            {
                Interlocked.Increment(ref unauthorized);
                return Task.CompletedTask;
            });
        return (tracker, () => Volatile.Read(ref changes), () => Volatile.Read(ref unauthorized));
    }

    private static ConciergeRideActivityItem Item(long offset) =>
        new(offset, new RideActivityEntry(Guid.NewGuid(), 1, "Booked", Now.AddMinutes(offset), "Hotel ZGY", "Airport", Now.AddHours(1)));

    private static ConciergeRideActivitySnapshot Snapshot(long readOffset, params ConciergeRideActivityItem[] items) =>
        new(new ConciergeRideReadState(readOffset, 0, items.Length == 0 ? 0 : items[^1].Offset + 1), items);

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class FakeRides(ConciergeRideActivitySnapshot snapshot) : IConciergeService
    {
        public Channel<ConciergeRideActivityItem> Live { get; } = Channel.CreateUnbounded<ConciergeRideActivityItem>();

        public TaskCompletionSource<long> StreamFrom { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? SnapshotFailure { get; init; }

        public long MarkedReadOffset { get; private set; } = -1;

        public Task<ConciergeRideActivitySnapshot> ReadRideActivitySnapshotAsync(
            string? bearerCredential, CancellationToken cancellationToken = default) =>
            SnapshotFailure is { } failure ? Task.FromException<ConciergeRideActivitySnapshot>(failure) : Task.FromResult(snapshot);

        public async IAsyncEnumerable<ConciergeRideActivityItem> StreamRideActivityAsync(
            string? bearerCredential, long fromOffset, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamFrom.TrySetResult(fromOffset);
            await foreach (var item in Live.Reader.ReadAllAsync(cancellationToken))
            {
                yield return item;
            }
        }

        public Task<ConciergeRideReadState> MarkRideActivitySnapshotReadAsync(
            string? bearerCredential, long snapshotOffset, CancellationToken cancellationToken = default)
        {
            MarkedReadOffset = snapshotOffset;
            return Task.FromResult(new ConciergeRideReadState(snapshotOffset, 0, snapshotOffset));
        }

        public Task<ConciergeSessionResponse> VerifyAsync(ConciergeVerificationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConciergeTurnResult> SubmitTurnAsync(string? bearerCredential, ConciergeTurnRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConciergeHistoryResult> ReadHistoryAsync(string? bearerCredential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EndAsync(string? bearerCredential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResetAsync(string? bearerCredential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConciergeVoiceAuthorization> AuthorizeVoiceAsync(string? bearerCredential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConciergeTurnResult> SubmitVoiceTurnAsync(ConciergeSessionContext session, string turnId, string userMessage, CancellationToken cancellationToken = default, bool enableEndChatFunction = false) => throw new NotSupportedException();
        public Task MarkVoiceTurnInterruptedAsync(ConciergeSessionContext session, string turnId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
