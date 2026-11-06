using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Concierge.Contracts;
using Concierge.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Concierge.Core.Tests;

public sealed class StayTranscriptNotifierTests
{
    private static StayTranscriptNotifier NewNotifier() => new(NullLogger<StayTranscriptNotifier>.Instance);

    [Fact]
    public void DeliversOnlyToSubscribersOfTheChangedStay()
    {
        var notifier = NewNotifier();
        var stay = Guid.NewGuid();
        var otherStay = Guid.NewGuid();
        var source = Guid.NewGuid();
        var seen = new List<StayTranscriptChange>();
        var otherSeen = new List<StayTranscriptChange>();
        using var _ = notifier.Subscribe(stay, seen.Add);
        using var __ = notifier.Subscribe(otherStay, otherSeen.Add);

        notifier.Notify(stay, source, StayTranscriptChangeKind.TurnsChanged);

        var change = Assert.Single(seen);
        Assert.Equal(new StayTranscriptChange(stay, source, StayTranscriptChangeKind.TurnsChanged), change);
        Assert.Empty(otherSeen);
    }

    [Fact]
    public void DisposedSubscriptionStopsDeliveryAndDisposeIsIdempotent()
    {
        var notifier = NewNotifier();
        var stay = Guid.NewGuid();
        var calls = 0;
        var subscription = notifier.Subscribe(stay, _ => calls++);

        notifier.Notify(stay, Guid.NewGuid(), StayTranscriptChangeKind.TurnsChanged);
        subscription.Dispose();
        subscription.Dispose();
        notifier.Notify(stay, Guid.NewGuid(), StayTranscriptChangeKind.StayReset);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void ThrowingSubscriberDoesNotStopOthersOrTheNotifyingCaller()
    {
        var notifier = NewNotifier();
        var stay = Guid.NewGuid();
        var delivered = 0;
        using var _ = notifier.Subscribe(stay, _ => throw new InvalidOperationException("broken page"));
        using var __ = notifier.Subscribe(stay, _ => delivered++);

        var notify = Record.Exception(() => notifier.Notify(stay, Guid.NewGuid(), StayTranscriptChangeKind.TurnsChanged));

        Assert.Null(notify);
        Assert.Equal(1, delivered);
    }

    [Fact]
    public void SubscriberCanDisposeItselfDuringNotification()
    {
        var notifier = NewNotifier();
        var stay = Guid.NewGuid();
        IDisposable? self = null;
        var calls = 0;
        self = notifier.Subscribe(stay, _ =>
        {
            calls++;
            self!.Dispose();
        });

        notifier.Notify(stay, Guid.NewGuid(), StayTranscriptChangeKind.TurnsChanged);
        notifier.Notify(stay, Guid.NewGuid(), StayTranscriptChangeKind.TurnsChanged);

        Assert.Equal(1, calls);
    }
}

public sealed class StayTranscriptNotificationTests(PostgreSqlFixture postgreSql)
    : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task CommittedTurnsAndResetNotifyTheStayWithTheirSourceSession()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        var riveraStay = await host.SeedStayAsync("Rivera", "412", today, today.AddDays(2));
        var morganStay = await host.SeedStayAsync("Morgan", "812", today, today.AddDays(2));
        // A room device and a browser tab: two sessions for one stay.
        var device = await VerifyAsync(host, "Rivera", "412");
        var browser = await VerifyAsync(host, "Rivera", "412");
        Assert.Equal(riveraStay, device.StayId);
        Assert.NotEqual(device.SessionId, browser.SessionId);

        var notifier = host.App.Services.GetRequiredService<StayTranscriptNotifier>();
        var rivera = new ConcurrentQueue<StayTranscriptChange>();
        var other = new ConcurrentQueue<StayTranscriptChange>();
        using var _ = notifier.Subscribe(riveraStay, rivera.Enqueue);
        using var __ = notifier.Subscribe(morganStay, other.Enqueue);

        var turn = await SendAsync(host, "/api/concierge/turns", device.Credential,
            new ConciergeTurnRequest("dev:1", "What time does the pool open?", "voice"));
        Assert.Equal(HttpStatusCode.OK, turn.StatusCode);
        // The question is announced when handed to the workflow, then the turn when it is committed.
        Assert.Equal(
            [
                new StayTranscriptChange(riveraStay, device.SessionId, StayTranscriptChangeKind.TurnStarted,
                    "What time does the pool open?"),
                new StayTranscriptChange(riveraStay, device.SessionId, StayTranscriptChangeKind.TurnsChanged),
            ],
            rivera);
        Assert.Empty(other);

        // A rejected turn never reaches the workflow, so nothing changed and nobody is told.
        var rejected = await SendAsync(host, "/api/concierge/turns", device.Credential,
            new ConciergeTurnRequest("dev:2", " ", "voice"));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(2, rivera.Count);

        var reset = await SendAsync(host, "/api/concierge/reset", browser.Credential, body: null);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(3, rivera.Count);
        Assert.Equal(
            new StayTranscriptChange(riveraStay, browser.SessionId, StayTranscriptChangeKind.StayReset),
            rivera.Last());
        Assert.Empty(other);
    }

    private static async Task<ConciergeSessionResponse> VerifyAsync(ConciergeTestHost host, string lastName, string room)
    {
        var response = await host.Http.PostAsJsonAsync(
            "/api/concierge/sessions", new ConciergeVerificationRequest(lastName, room));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ConciergeSessionResponse>())!;
    }

    private static Task<HttpResponseMessage> SendAsync(
        ConciergeTestHost host, string path, string credential, object? body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = body is null ? null : JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return host.Http.SendAsync(request);
    }
}
