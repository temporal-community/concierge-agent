using System.Net;
using System.Net.Http.Json;
using Concierge.Contracts;
using Concierge.RideService;
using Concierge.RideService.Data;
using Concierge.RideService.Workflows;
using Concierge.WorkflowServer.Workflows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Activities;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Temporalio.Workflows;
using Temporalio.Extensions.WorkflowStreams;
using Temporalio.Exceptions;

namespace Concierge.Core.Tests;

public sealed class RideServiceApiTests(PostgreSqlFixture postgreSql)
    : IClassFixture<PostgreSqlFixture>
{
    private static readonly DateTimeOffset DefaultNow = new(2026, 10, 19, 18, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo HotelTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly Guid GuestId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid StayId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task ExactFiveMinutesSurvivesDelayedActivityAndStoresRowAndEventAtomically()
    {
        await using var host = await RideTestHost.StartAsync(postgreSql, DefaultNow);
        await host.ExecuteAsync(
            """
            CREATE FUNCTION delay_ride_insert() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                PERFORM pg_sleep(1);
                RETURN NEW;
            END;
            $$;
            CREATE TRIGGER delay_ride_insert BEFORE INSERT ON rides
                FOR EACH ROW EXECUTE FUNCTION delay_ride_insert();
            """);

        var request = Booking("exact-5", DefaultNow.AddMinutes(5));
        var startedAt = DateTimeOffset.UtcNow;
        var response = await host.BookAsync(request);
        var elapsed = DateTimeOffset.UtcNow - startedAt;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(elapsed >= TimeSpan.FromSeconds(1), $"Insert trigger delayed only {elapsed}.");
        var ride = (await ReadRideAsync(response))!;
        Assert.Equal(DefaultNow, ride.BookedAt.ToUniversalTime());
        Assert.Equal(DefaultNow.AddMinutes(5), ride.PickupAt.ToUniversalTime());
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM rides;"));
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM ride_events;"));

        var tooSoon = await host.BookAsync(Booking("under-5", DefaultNow.AddMinutes(5).AddTicks(-1)));
        Assert.Equal(HttpStatusCode.BadRequest, tooSoon.StatusCode);
        var error = (await tooSoon.Content.ReadFromJsonAsync<RideApiError>())!;
        Assert.Contains("at least 5 minutes", error.Message, StringComparison.Ordinal);
        Assert.Contains("earliest is", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfiguredMinimumAcceptsExactBoundaryRejectsJustUnderAndRetryUsesOriginalAdmission()
    {
        await using var host = await RideTestHost.StartAsync(
            postgreSql, DefaultNow, minimumAdvanceNoticeMinutes: 12);
        var request = Booking("exact-12", DefaultNow.AddMinutes(12));
        using var first = await host.BookAsync(request);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var original = (await ReadRideAsync(first))!;
        Assert.Equal(DefaultNow, original.BookedAt);
        Assert.Equal(DefaultNow.AddMinutes(12), original.PickupAt);

        using var rejected = await host.BookAsync(
            Booking("under-12", DefaultNow.AddMinutes(12).AddTicks(-1)));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var error = (await rejected.Content.ReadFromJsonAsync<RideApiError>())!;
        Assert.Contains("at least 12 minutes", error.Message, StringComparison.Ordinal);
        Assert.Contains("2026-10-19 14:12 -04:00", error.Message, StringComparison.Ordinal);

        host.AdvanceTime(TimeSpan.FromMinutes(30));
        using var retry = await host.BookAsync(request);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var retried = (await ReadRideAsync(retry))!;
        Assert.Equal(original.Id, retried.Id);
        Assert.Equal(original.BookedAt, retried.BookedAt);
        Assert.Equal(original.PickupAt, retried.PickupAt);

        using var immediate = await host.BookAsync(Booking("immediate-12", null, immediate: true));
        Assert.Equal(HttpStatusCode.OK, immediate.StatusCode);
        var immediateRide = (await ReadRideAsync(immediate))!;
        Assert.Equal(DefaultNow.AddMinutes(30), immediateRide.BookedAt);
        Assert.Equal(immediateRide.BookedAt, immediateRide.PickupAt);
    }

    [Fact]
    public async Task MatchingRetryAfterLostResponseAndClosedWorkflowReturnsOriginalBooking()
    {
        var admissionTime = DefaultNow.AddTicks(7);
        await using var host = await RideTestHost.StartAsync(postgreSql, admissionTime);
        var request = Booking("retry-after-close", null, immediate: true) with
        {
            RouteDurationSeconds = 0.01,
        };
        var first = await host.BookAsync(request);
        var original = (await ReadRideAsync(first))!;
        Assert.Equal(RideRepository.ToUtcMicrosecondPrecision(admissionTime), original.BookedAt);
        var workflow = host.Environment.Client.GetWorkflowHandle<RideBookingWorkflow>(
            original.Id.ToString("N"));
        await workflow.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var description = await workflow.DescribeAsync();
        Assert.Equal(WorkflowExecutionStatus.Completed, description.Status);

        // Ignore the first response to model a lost HTTP response, then retry after workflow closure.
        var retry = await host.BookAsync(request);
        var retried = (await ReadRideAsync(retry))!;
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(original.Id, retried.Id);
        Assert.Equal(original.BookedAt, retried.BookedAt);
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM rides;"));
        Assert.Equal(
            ["Booked", "DriverAssigned", "PickedUp", "Completed"],
            await host.EventStatusesAsync(original.Id));
        Assert.Equal("Completed", (await ReadOwnedRideAsync(host, original.Id))!.Status);

        var changed = request with { Pickup = request.Pickup with { Name = "Changed pickup" } };
        var conflict = await host.BookAsync(changed);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(
            "idempotency_conflict",
            (await conflict.Content.ReadFromJsonAsync<RideApiError>())!.Code);
    }

    [Fact]
    public async Task ConcurrentMatchingRequestsShareOneAdmissionAndOneWorkflow()
    {
        await using var host = await RideTestHost.StartAsync(postgreSql, DefaultNow);
        var request = Booking("concurrent-key", DefaultNow.AddMinutes(45));
        var responses = await Task.WhenAll(
            host.BookAsync(request),
            host.BookAsync(request));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var rides = await Task.WhenAll(responses.Select(ReadRideAsync));
        Assert.Equal(rides[0]!.Id, rides[1]!.Id);
        Assert.Equal(rides[0]!.BookedAt, rides[1]!.BookedAt);
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM rides;"));
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM ride_events;"));
    }

    [Fact]
    public async Task OwnershipScopesReadListAndCancellationAndListUsesDatabaseRows()
    {
        // The booking workflow runs on real time, so the fake clock must match it for the
        // immediate ride's one-hour route to keep it non-terminal for the whole test.
        var now = DateTimeOffset.UtcNow;
        await using var host = await RideTestHost.StartAsync(postgreSql, now);
        var immediateRequest = Booking(
            "immediate",
            null,
            immediate: true,
            checkoutDate: DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, HotelTimeZone).DateTime).AddDays(1)) with
        {
            RouteDurationSeconds = 3600,
        };
        var immediate = await host.BookAsync(immediateRequest);
        var immediateRide = (await ReadRideAsync(immediate))!;
        var futurePickup = DateTimeOffset.UtcNow.AddHours(1);
        var futureCheckoutDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(futurePickup, HotelTimeZone).DateTime).AddDays(1);
        var future = await host.BookAsync(
            Booking("future", futurePickup, checkoutDate: futureCheckoutDate));
        var futureRide = (await ReadRideAsync(future))!;

        var ownRead = await host.GetAsync($"/rides/{futureRide.Id}", GuestId, StayId);
        Assert.Equal(HttpStatusCode.OK, ownRead.StatusCode);

        var foreignGuestRead = await host.GetAsync($"/rides/{futureRide.Id}", Guid.NewGuid(), StayId);
        var foreignStayRead = await host.GetAsync($"/rides/{futureRide.Id}", GuestId, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, foreignGuestRead.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignStayRead.StatusCode);

        var wrongPathGuest = await host.GetListAsync(Guid.NewGuid(), GuestId, StayId, "upcoming");
        Assert.Equal(HttpStatusCode.NotFound, wrongPathGuest.StatusCode);
        var foreignStayList = await host.GetListAsync(GuestId, GuestId, Guid.NewGuid(), "upcoming");
        Assert.Empty((await foreignStayList.Content.ReadFromJsonAsync<RideResponse[]>())!);

        // An active immediate ride is upcoming even though its pickup time has passed.
        var upcoming = await host.GetListAsync(GuestId, GuestId, StayId, "upcoming");
        var upcomingRides = (await upcoming.Content.ReadFromJsonAsync<RideResponse[]>())!;
        Assert.Equal(
            new[] { immediateRide.Id, futureRide.Id }.Order(),
            upcomingRides.Select(ride => ride.Id).Order());
        var pastList = await host.GetListAsync(GuestId, GuestId, StayId, "past");
        Assert.Empty((await pastList.Content.ReadFromJsonAsync<RideResponse[]>())!);

        var foreignCancel = await host.DeleteAsync(futureRide.Id, Guid.NewGuid(), StayId);
        var foreignStayCancel = await host.DeleteAsync(futureRide.Id, GuestId, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, foreignCancel.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignStayCancel.StatusCode);
        var ownedCancel = await host.DeleteAsync(futureRide.Id, GuestId, StayId);
        Assert.Equal(HttpStatusCode.NoContent, ownedCancel.StatusCode);
        Assert.Equal("Cancelled", (await ReadOwnedRideAsync(host, futureRide.Id))!.Status);
        var upcomingAfterCancel = (await (await host.GetListAsync(GuestId, GuestId, StayId, "upcoming"))
            .Content.ReadFromJsonAsync<RideResponse[]>())!;
        Assert.Equal([immediateRide.Id], upcomingAfterCancel.Select(ride => ride.Id));
        var pastAfterCancel = (await (await host.GetListAsync(GuestId, GuestId, StayId, "past"))
            .Content.ReadFromJsonAsync<RideResponse[]>())!;
        Assert.Equal([futureRide.Id], pastAfterCancel.Select(ride => ride.Id));

        var originalRequest = immediateRequest;
        var foreignGuestBooking = await host.BookAsync(originalRequest with { GuestId = Guid.NewGuid() });
        var foreignStayBooking = await host.BookAsync(originalRequest with { StayId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, foreignGuestBooking.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, foreignStayBooking.StatusCode);
    }

    [Fact]
    public async Task PurgeTerminatesAndDeletesOnlyTheScopedStaysRidesIncludingPickedUp()
    {
        // Real time: the immediate ride must reach PickedUp, which cancellation would refuse.
        var now = DateTimeOffset.UtcNow;
        await using var host = await RideTestHost.StartAsync(postgreSql, now);
        var checkoutDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, HotelTimeZone).DateTime).AddDays(1);
        var pickedUp = (await ReadRideAsync(await host.BookAsync(
            Booking("purge-immediate", null, immediate: true, checkoutDate: checkoutDate) with
            {
                RouteDurationSeconds = 3600,
            })))!;
        var scheduled = (await ReadRideAsync(await host.BookAsync(
            Booking("purge-scheduled", now.AddHours(2), checkoutDate: checkoutDate))))!;
        var otherStayId = Guid.NewGuid();
        var otherStay = (await ReadRideAsync(await host.BookAsync(
            Booking("purge-other-stay", now.AddHours(2), checkoutDate: checkoutDate) with { StayId = otherStayId })))!;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while ((await ReadOwnedRideAsync(host, pickedUp.Id))?.Status != "PickedUp" && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        Assert.Equal("PickedUp", (await ReadOwnedRideAsync(host, pickedUp.Id))?.Status);

        using var mismatched = await host.PurgeAsync(Guid.NewGuid(), GuestId, StayId);
        Assert.Equal(HttpStatusCode.BadRequest, mismatched.StatusCode);
        using var purge = await host.PurgeAsync(StayId, GuestId, StayId);
        Assert.Equal(HttpStatusCode.NoContent, purge.StatusCode);

        foreach (var ride in new[] { pickedUp, scheduled })
        {
            var description = await host.Environment.Client.GetWorkflowHandle(ride.Id.ToString("N")).DescribeAsync();
            Assert.Equal(WorkflowExecutionStatus.Terminated, description.Status);
        }
        Assert.Equal(0L, await host.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM rides WHERE stay_id = '{StayId}';"));
        Assert.Equal(0L, await host.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM ride_events WHERE ride_id IN ('{pickedUp.Id}', '{scheduled.Id}');"));
        Assert.Empty((await (await host.GetListAsync(GuestId, GuestId, StayId, "upcoming"))
            .Content.ReadFromJsonAsync<RideResponse[]>())!);
        Assert.Empty((await (await host.GetListAsync(GuestId, GuestId, StayId, "past"))
            .Content.ReadFromJsonAsync<RideResponse[]>())!);
        using var otherRead = await host.GetAsync($"/rides/{otherStay.Id}", GuestId, otherStayId);
        Assert.Equal(HttpStatusCode.OK, otherRead.StatusCode);

        // A retried purge (for example, an activity retry after a lost response) is a no-op.
        using var repeated = await host.PurgeAsync(StayId, GuestId, StayId);
        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
    }

    [Theory]
    [InlineData("2026-03-08T12:00:00+00:00", "2026-03-08", "2026-03-09T03:59:59.9999999+00:00", true)]
    [InlineData("2026-03-08T12:00:00+00:00", "2026-03-08", "2026-03-09T04:00:00+00:00", false)]
    [InlineData("2026-10-31T16:00:00+00:00", "2026-11-01", "2026-11-02T04:59:59.9999999+00:00", true)]
    [InlineData("2026-10-31T16:00:00+00:00", "2026-11-01", "2026-11-02T05:00:00+00:00", false)]
    public async Task CheckoutCutoffUsesHotelLocalMidnightAcrossDst(
        string nowText,
        string checkoutDateText,
        string pickupText,
        bool accepted)
    {
        var now = DateTimeOffset.Parse(nowText);
        var checkoutDate = DateOnly.Parse(checkoutDateText);
        await using var host = await RideTestHost.StartAsync(postgreSql, now);
        var request = Booking(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.Parse(pickupText),
            checkoutDate: checkoutDate);
        var response = await host.BookAsync(request);
        Assert.Equal(accepted ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
        if (!accepted)
        {
            var error = (await response.Content.ReadFromJsonAsync<RideApiError>())!;
            Assert.Contains(checkoutDate.ToString("yyyy-MM-dd"), error.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The stay workflow reads its persisted transcript sequence on start. The ride database has no
    /// transcript table, so the stay worker here reports an empty transcript.
    /// </summary>
    public sealed class EmptyTranscriptSequenceActivities
    {
        [Activity("ReadNextSequenceAsync")]
        public Task<long> ReadNextSequenceAsync(ReadNextTranscriptSequenceRequest request) =>
            Task.FromResult(0L);
    }

    public sealed class UnavailableRideStreamPublishingActivities
    {
        [Activity(RideWorkflowNames.PublishRideEntryActivity)]
        public Task PublishRideEntryAsync(RideActivityPublication publication)
        {
            ActivityExecutionContext.Current.CancellationToken.ThrowIfCancellationRequested();
            throw new ApplicationFailureException(
                $"Simulated unavailable ride feed for {publication.StayWorkflowId}.",
                errorType: "RideFeedUnavailable");
        }
    }

    [Fact]
    public async Task EventFailureRollsBackTheRideRow()
    {
        await using var host = await RideTestHost.StartAsync(postgreSql, DefaultNow);
        await host.ExecuteAsync(
            """
            CREATE FUNCTION fail_ride_event() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'test event failure';
            END;
            $$;
            CREATE TRIGGER fail_ride_event BEFORE INSERT ON ride_events
                FOR EACH ROW EXECUTE FUNCTION fail_ride_event();
            """);

        var response = await host.BookAsync(Booking("atomic-failure", DefaultNow.AddMinutes(45)));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0L, await host.ScalarAsync<long>("SELECT count(*) FROM rides;"));
        Assert.Equal(0L, await host.ScalarAsync<long>("SELECT count(*) FROM ride_events;"));
    }

    [Theory]
    [InlineData(60, false)]
    [InlineData(8, true)]
    public async Task CancellationIsInStayFeedBeforeTerminalWorkflowClosesAndReconnectDeduplicates(
        int pickupInMinutes, bool assigned)
    {
        var now = DateTimeOffset.UtcNow;
        await using var host = await RideTestHost.StartAsync(postgreSql, now);
        var checkoutDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, HotelTimeZone).DateTime).AddDays(1);
        var response = await host.BookAsync(Booking(
            "cancel-feed", now.AddMinutes(pickupInMinutes), checkoutDate: checkoutDate));
        var ride = (await ReadRideAsync(response))!;
        using var cancelled = await host.DeleteAsync(ride.Id, GuestId, StayId);
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        var workflow = host.Environment.Client.GetWorkflowHandle<RideBookingWorkflow>(ride.Id.ToString("N"));
        await workflow.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        string[] expected = assigned ? ["Booked", "DriverAssigned", "Cancelled"] : ["Booked", "Cancelled"];
        Assert.Equal(expected, await host.EventStatusesAsync(ride.Id));
        Assert.Equal("Cancelled", (await ReadOwnedRideAsync(host, ride.Id))!.Status);

        // Subscribe only after closure: a connected browser must not be needed to retain it.
        var stayId = ConciergeStayWorkflowIds.ForStay(StayId);
        await using var client = new WorkflowStreamClient(host.Environment.Client, stayId);
        using var readDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var subscription = client.GetTopic<RideActivityEntry>(RideActivityFeed.Topic)
            .SubscribeAsync(0).GetAsyncEnumerator(readDeadline.Token);
        var entries = new List<RideActivityEntry>();
        while (entries.All(entry => entry.Status != "Cancelled"))
        {
            Assert.True(await subscription.MoveNextAsync());
            entries.Add(subscription.Current.Value);
        }
        Assert.Equal(expected, entries.Select(entry => entry.Status));
        Assert.Equal(Enumerable.Range(1, expected.Length), entries.Select(entry => entry.Sequence));

        await using var retry = new WorkflowStreamClient(host.Environment.Client, stayId);
        retry.GetTopic<RideActivityEntry>(RideActivityFeed.Topic).Publish(entries[^1], forceFlush: true);
        await retry.FlushAsync();
        Assert.True(await subscription.MoveNextAsync());
        entries.Add(subscription.Current.Value);
        var window = new Concierge.Web.Services.ConciergeRideActivityWindow(
            new Concierge.Web.Services.ConciergeRideActivitySnapshot(
                new Concierge.Contracts.ConciergeRideReadState(0, 0, 0), []));
        for (var index = 0; index < entries.Count; index++)
        {
            window.Apply(new Concierge.Web.Services.ConciergeRideActivityItem(index, entries[index]));
        }
        Assert.Equal(expected, window.Entries.Select(item => item.Entry.Status));
        await using var renderingServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new Microsoft.AspNetCore.Components.Web.HtmlRenderer(
            renderingServices, renderingServices.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var panel = await renderer.RenderComponentAsync<Concierge.Web.Components.Shared.RideActivityPanel>(
                Microsoft.AspNetCore.Components.ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Entries"] = window.Entries.Select(item => item.Entry).ToArray(),
                    ["IsOpen"] = true,
                }));
            return panel.ToHtmlString();
        });
        Assert.Matches("<h3[^>]*>Cancelled</h3>", html);
        Assert.Equal(expected.Length, html.Split("<li class=\"ride-activity-entry").Length - 1);
        using var repeated = await host.DeleteAsync(ride.Id, GuestId, StayId);
        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        Assert.Equal(expected, await host.EventStatusesAsync(ride.Id));
    }

    [Fact]
    public async Task ImmediateRidePublishesOrderedEntriesAndConsumersDeduplicateByRideSequence()
    {
        await using var host = await RideTestHost.StartAsync(postgreSql, DefaultNow);
        var stayWorkflowId = ConciergeStayWorkflowIds.ForStay(StayId);
        await using var subscriberClient = new WorkflowStreamClient(
            host.Environment.Client, stayWorkflowId);
        await using var subscription = subscriberClient
            .GetTopic<RideActivityEntry>(RideActivityFeed.Topic)
            .SubscribeAsync(0)
            .GetAsyncEnumerator();
        var nextEntry = subscription.MoveNextAsync().AsTask();

        var request = Booking("stream-immediate", null, immediate: true) with
        {
            RouteDurationSeconds = 0.01,
        };
        var response = await host.BookAsync(request);
        var ride = (await ReadRideAsync(response))!;
        await host.Environment.Client.GetWorkflowHandle<RideBookingWorkflow>(
            ride.Id.ToString("N")).GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var received = new List<RideActivityEntry>();
        for (var index = 0; index < 4; index++)
        {
            Assert.True(await nextEntry.WaitAsync(TimeSpan.FromSeconds(10)));
            received.Add(subscription.Current.Value);
            if (index < 3)
            {
                nextEntry = subscription.MoveNextAsync().AsTask();
            }
        }

        Assert.Equal(
            ["Booked", "DriverAssigned", "PickedUp", "Completed"],
            received.Select(entry => entry.Status));
        Assert.Equal([1, 2, 3, 4], received.Select(entry => entry.Sequence));
        Assert.Equal(ride.Id, Assert.Single(received.Select(entry => entry.RideId).Distinct()));
        Assert.Equal(ride.PickupAt.ToUniversalTime().AddSeconds(request.RouteDurationSeconds),
            received[0].ArrivalAtUtc);
        Assert.Equal("Alex Rivera", received[1].DriverName);
        Assert.Equal("ZGY Sedan", received[1].Car);
        Assert.Equal("ZGY-4821", received[1].Plate);

        // A new client represents an activity retry with a new package publisher ID.
        await using var retryPublisher = new WorkflowStreamClient(
            host.Environment.Client, stayWorkflowId);
        retryPublisher.GetTopic<RideActivityEntry>(RideActivityFeed.Topic)
            .Publish(received[^1], forceFlush: true);
        await retryPublisher.FlushAsync();
        Assert.True(await subscription.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        var duplicate = subscription.Current.Value;
        Assert.Equal((received[^1].RideId, received[^1].Sequence), (duplicate.RideId, duplicate.Sequence));

        var applied = new HashSet<(Guid RideId, int Sequence)>();
        Assert.Equal(4, received.Count(entry => applied.Add((entry.RideId, entry.Sequence))));
        Assert.False(applied.Add((duplicate.RideId, duplicate.Sequence)));
    }

    [Fact]
    public async Task PublicationToClosedStayIsDroppedAndRideStillCompletes()
    {
        await using var host = await RideTestHost.StartAsync(
            postgreSql, DefaultNow, startStayWorkflow: false);
        await host.CloseStayWorkflowAsync();

        var request = Booking("closed-stay", null, immediate: true) with
        {
            RouteDurationSeconds = 0.01,
        };
        var response = await host.BookAsync(request);
        var ride = (await ReadRideAsync(response))!;
        await host.Environment.Client.GetWorkflowHandle<RideBookingWorkflow>(
            ride.Id.ToString("N")).GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            ["Booked", "DriverAssigned", "PickedUp", "Completed"],
            await host.EventStatusesAsync(ride.Id));
        Assert.Equal("Completed", (await ReadOwnedRideAsync(host, ride.Id))!.Status);
        var past = (await (await host.GetListAsync(GuestId, GuestId, StayId, "past"))
            .Content.ReadFromJsonAsync<RideResponse[]>())!;
        Assert.Equal([ride.Id], past.Select(item => item.Id));
        var upcoming = (await (await host.GetListAsync(GuestId, GuestId, StayId, "upcoming"))
            .Content.ReadFromJsonAsync<RideResponse[]>())!;
        Assert.Empty(upcoming);
    }

    [Fact]
    public async Task StayStreamRetainsOnlyTheNewestOneHundredEntries()
    {
        await using var host = await RideTestHost.StartAsync(postgreSql, DefaultNow);
        var stayWorkflowId = ConciergeStayWorkflowIds.ForStay(StayId);
        await using var publisher = new WorkflowStreamClient(host.Environment.Client, stayWorkflowId);
        var rideId = Guid.NewGuid();
        var changedAt = DateTimeOffset.UtcNow;
        for (var sequence = 1; sequence <= 101; sequence++)
        {
            publisher.GetTopic<RideActivityEntry>(RideActivityFeed.Topic).Publish(
                new RideActivityEntry(
                    rideId,
                    sequence,
                    "DriverAssigned",
                    changedAt,
                    "Hotel ZGY",
                    "Miami Beach",
                    changedAt.AddMinutes(45)));
        }

        await publisher.FlushAsync();
        var offset = await publisher.GetOffsetAsync();
        Assert.Equal(101, offset);
        await host.Environment.Client.GetWorkflowHandle(stayWorkflowId).SignalAsync(
            ConciergeStayWorkflowIds.TruncateRideActivityStreamSignal,
            [new TruncateRideActivityStreamRequest(offset - RideActivityFeed.MaxRetainedEntries)]);

        await using var subscriber = new WorkflowStreamClient(
            host.Environment.Client, stayWorkflowId);
        await using var entries = subscriber
            .GetTopic<RideActivityEntry>(RideActivityFeed.Topic)
            .SubscribeAsync(0)
            .GetAsyncEnumerator();
        var retained = new List<RideActivityEntry>();
        while (retained.Count < RideActivityFeed.MaxRetainedEntries)
        {
            Assert.True(await entries.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            retained.Add(entries.Current.Value);
        }

        Assert.Equal(2, retained[0].Sequence);
        Assert.Equal(101, retained[^1].Sequence);
    }

    [Fact]
    public async Task UnavailableFeedCannotStopOverdueRideLifecycleBeyondFourPublicationDeadlines()
    {
        await using var host = await RideTestHost.StartAsync(
            postgreSql, DefaultNow, unavailableFeed: true);
        var startedAt = await host.Environment.GetCurrentTimeAsync();
        var request = Booking("unavailable-feed", null, immediate: true) with
        {
            RouteDurationSeconds = 0.01,
        };
        var response = await host.BookAsync(request);
        var ride = (await ReadRideAsync(response))!;
        var workflow = host.Environment.Client.GetWorkflowHandle<RideBookingWorkflow>(
            ride.Id.ToString("N"));
        await workflow.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(95));
        var elapsed = await host.Environment.GetCurrentTimeAsync() - startedAt;

        Assert.True(
            elapsed <= TimeSpan.FromSeconds(4 * 20 + 5),
            $"Four due transitions took {elapsed}; the D8 upper bound is N × 20 seconds.");
        Assert.Equal(
            ["Booked", "DriverAssigned", "PickedUp", "Completed"],
            await host.EventStatusesAsync(ride.Id));
        Assert.Equal("Completed", (await ReadOwnedRideAsync(host, ride.Id))!.Status);
        var history = await workflow.FetchHistoryAsync();
        var publicationTimeouts = history.Events
            .Where(item => item.EventType == Temporalio.Api.Enums.V1.EventType.ActivityTaskScheduled)
            .Select(item => item.ActivityTaskScheduledEventAttributes)
            .Where(item => item.ActivityType.Name == RideWorkflowNames.PublishRideEntryActivity)
            .ToArray();
        Assert.Equal(4, publicationTimeouts.Length);
        Assert.All(publicationTimeouts, item =>
        {
            Assert.Equal(10, item.StartToCloseTimeout.Seconds);
            Assert.Equal(20, item.ScheduleToCloseTimeout.Seconds);
        });
    }

    [Fact]
    public async Task CancellationRacingPickupEitherCommitsBeforePickupOrIsRejectedAfterIt()
    {
        await using var host = await RideTestHost.StartAsync(postgreSql, DefaultNow);
        var pickupAt = DateTimeOffset.UtcNow.AddSeconds(3);
        var checkoutDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(pickupAt, HotelTimeZone).DateTime).AddDays(1);
        var response = await host.BookAsync(
            Booking("pickup-cancel-race", pickupAt, checkoutDate: checkoutDate) with
            {
                RouteDurationSeconds = 60,
            });
        var ride = (await ReadRideAsync(response))!;

        var assignmentDeadline = DateTimeOffset.UtcNow.AddSeconds(2);
        RideResponse? current = null;
        while (DateTimeOffset.UtcNow < assignmentDeadline)
        {
            current = await ReadOwnedRideAsync(host, ride.Id);
            if (current?.Status == "DriverAssigned")
            {
                break;
            }

            await Task.Delay(25);
        }

        Assert.Equal("DriverAssigned", current?.Status);
        var untilPickup = pickupAt - DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(50);
        if (untilPickup > TimeSpan.Zero)
        {
            await Task.Delay(untilPickup);
        }

        using var cancellation = await host.DeleteAsync(ride.Id, GuestId, StayId);
        var statuses = await host.EventStatusesAsync(ride.Id);
        if (cancellation.StatusCode == HttpStatusCode.NoContent)
        {
            Assert.Equal(["Booked", "DriverAssigned", "Cancelled"], statuses);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Conflict, cancellation.StatusCode);
            Assert.Equal(["Booked", "DriverAssigned", "PickedUp"], statuses);
            Assert.Equal("PickedUp", (await ReadOwnedRideAsync(host, ride.Id))!.Status);
        }
    }

    [Fact]
    public async Task RetriedTransitionReusesItsSequenceAndEventFailureRollsBackCurrentStatus()
    {
        await using var host = await RideTestHost.StartAsync(postgreSql, DefaultNow);
        var pickupAt = DateTimeOffset.UtcNow.AddHours(1);
        var checkoutDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(pickupAt, HotelTimeZone).DateTime).AddDays(1);
        var response = await host.BookAsync(
            Booking("transition-idempotency", pickupAt, checkoutDate: checkoutDate) with
            {
                RouteDurationSeconds = 3600,
            });
        var ride = (await ReadRideAsync(response))!;
        var assignedAt = DateTimeOffset.UtcNow;
        var assignedCommand = new RideTransitionCommand(
            ride.Id,
            "Booked",
            "DriverAssigned",
            2,
            assignedAt,
            "Alex Rivera",
            "ZGY Sedan",
            "ZGY-4821");

        var first = await host.Repository.TransitionAsync(assignedCommand, CancellationToken.None);
        var retry = await host.Repository.TransitionAsync(assignedCommand, CancellationToken.None);
        Assert.True(first.Applied);
        Assert.True(retry.Applied);
        Assert.Equal(first.Entry, retry.Entry);
        Assert.Equal(2, retry.Entry!.Sequence);
        Assert.Equal(["Booked", "DriverAssigned"], await host.EventStatusesAsync(ride.Id));

        await host.ExecuteAsync(
            """
            CREATE FUNCTION fail_pickup_event() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.status = 'PickedUp' THEN
                    RAISE EXCEPTION 'test pickup event failure';
                END IF;
                RETURN NEW;
            END;
            $$;
            CREATE TRIGGER fail_pickup_event BEFORE INSERT ON ride_events
                FOR EACH ROW EXECUTE FUNCTION fail_pickup_event();
            """);
        await Assert.ThrowsAsync<PostgresException>(() =>
            host.Repository.TransitionAsync(
                new RideTransitionCommand(
                    ride.Id,
                    "DriverAssigned",
                    "PickedUp",
                    3,
                    assignedAt.AddMinutes(1)),
                CancellationToken.None));

        Assert.Equal("DriverAssigned", (await ReadOwnedRideAsync(host, ride.Id))!.Status);
        Assert.Equal(["Booked", "DriverAssigned"], await host.EventStatusesAsync(ride.Id));
    }

    [Fact]
    public async Task StoreTimeoutWithNothingPersistedClosesWorkflowWithoutResend()
    {
        await using var host = await RideTestHost.StartAsync(postgreSql, DefaultNow);
        // Every attempt hangs past its deadline; the self-bounded store rolls back each one.
        await host.ExecuteAsync(
            """
            CREATE FUNCTION hang_ride_insert() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                PERFORM pg_sleep(30);
                RETURN NEW;
            END;
            $$;
            CREATE TRIGGER hang_ride_insert BEFORE INSERT ON rides
                FOR EACH ROW EXECUTE FUNCTION hang_ride_insert();
            """);

        var request = Booking("store-timeout-nothing", DefaultNow.AddMinutes(45));
        var startedAt = DateTimeOffset.UtcNow;
        var response = await host.BookAsync(request);
        var elapsed = DateTimeOffset.UtcNow - startedAt;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(
            elapsed < Concierge.WorkflowServer.AI.ConciergeActionToolService.RideRequestTotalTimeout,
            $"Admission failed after {elapsed}, beyond the WorkflowServer HTTP budget.");
        var rideId = RideRepository.GetBookingId(request.IdempotencyKey);
        var workflow = host.Environment.Client.GetWorkflowHandle<RideBookingWorkflow>(
            rideId.ToString("N"));
        await workflow.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(WorkflowExecutionStatus.Completed, (await workflow.DescribeAsync()).Status);
        Assert.Equal(0L, await host.ScalarAsync<long>("SELECT count(*) FROM rides;"));

        // Repeating the same admitted turn cannot create a ride: the key's workflow is closed.
        await host.ExecuteAsync("DROP TRIGGER hang_ride_insert ON rides;");
        var repeat = await host.BookAsync(request);
        Assert.False(repeat.IsSuccessStatusCode, $"Repeat admission returned {repeat.StatusCode}.");
        Assert.Equal(0L, await host.ScalarAsync<long>("SELECT count(*) FROM rides;"));
    }

    [Fact]
    public async Task StoreTimeoutAfterPersistenceReconcilesAdmissionAndCancelFitsBudget()
    {
        // The booking workflow runs on real time; keep the pickup in the real future.
        var now = DateTimeOffset.UtcNow;
        await using var host = await RideTestHost.StartAsync(
            postgreSql, now, persistThenTimeOutStore: true);
        var pickup = now.AddHours(1);
        var request = Booking(
            "store-timeout-persisted",
            pickup,
            checkoutDate: DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(pickup, HotelTimeZone).DateTime).AddDays(1));
        var bookingStartedAt = DateTimeOffset.UtcNow;
        var response = await host.BookAsync(request);
        var bookingElapsed = DateTimeOffset.UtcNow - bookingStartedAt;

        // The update failed, but the database (retry authority) holds the committed ride.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(
            bookingElapsed < Concierge.WorkflowServer.AI.ConciergeActionToolService.RideRequestTotalTimeout,
            $"Admission returned after {bookingElapsed}.");
        var ride = (await ReadRideAsync(response))!;
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM rides;"));

        // Cancel immediately: it waits for reconciliation and still fits the HTTP budget.
        var cancelStartedAt = DateTimeOffset.UtcNow;
        var cancellation = await host.DeleteAsync(ride.Id, GuestId, StayId);
        var cancelElapsed = DateTimeOffset.UtcNow - cancelStartedAt;
        Assert.Equal(HttpStatusCode.NoContent, cancellation.StatusCode);
        Assert.True(
            cancelElapsed < Concierge.WorkflowServer.AI.ConciergeActionToolService.RideRequestTotalTimeout,
            $"Cancellation took {cancelElapsed}.");

        var workflow = host.Environment.Client.GetWorkflowHandle<RideBookingWorkflow>(
            ride.Id.ToString("N"));
        await workflow.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Cancelled", (await ReadOwnedRideAsync(host, ride.Id))!.Status);
        Assert.Equal(["Booked", "Cancelled"], await host.EventStatusesAsync(ride.Id));
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM rides;"));
    }

    [Fact]
    public void StoreAttemptStopsBeforeTheEarlierOfAttemptAndScheduleDeadline()
    {
        var now = new DateTime(2026, 10, 19, 18, 0, 0, DateTimeKind.Utc);
        TimeSpan? Remaining(TimeSpan sinceScheduled, TimeSpan sinceStarted) =>
            RideBookingActivities.RemainingAttemptTime(
                ActivityInfoAt(now - sinceScheduled, now - sinceStarted), now);

        // Attempt deadline (4s) governs a fresh first attempt.
        Assert.Equal(
            TimeSpan.FromSeconds(4) - RideBookingActivities.StoreDeadlineMargin,
            Remaining(TimeSpan.Zero, TimeSpan.Zero));
        // Schedule deadline (8s) governs a late retry.
        Assert.Equal(
            TimeSpan.FromSeconds(1) - RideBookingActivities.StoreDeadlineMargin,
            Remaining(TimeSpan.FromSeconds(7), TimeSpan.Zero));
        Assert.Equal(TimeSpan.Zero, Remaining(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(1)));
        Assert.True(RideBookingWorkflow.ReconcileGrace > RideBookingActivities.StoreDeadlineMargin);
    }

    private static Temporalio.Activities.ActivityInfo ActivityInfoAt(
        DateTime scheduled,
        DateTime started) =>
        new(
            ActivityId: "store",
            ActivityType: RideWorkflowNames.StoreBookingActivity,
            Attempt: 1,
            CurrentAttemptScheduledTime: started,
            DataConverter: Temporalio.Converters.DataConverter.Default,
            HeartbeatDetails: [],
            HeartbeatTimeout: null,
            IsLocal: false,
            Namespace: "default",
            Priority: Temporalio.Common.Priority.Default,
            RetryPolicy: null,
            ScheduleToCloseTimeout: RideBookingWorkflow.StoreActivityOptions.ScheduleToCloseTimeout,
            ScheduledTime: scheduled,
            StartToCloseTimeout: RideBookingWorkflow.StoreActivityOptions.StartToCloseTimeout,
            StartedTime: started,
            TaskQueue: RideWorkflowNames.TaskQueue,
            TaskToken: [],
            WorkflowId: "ride",
            WorkflowNamespace: "default",
            WorkflowRunId: "run",
            WorkflowType: RideWorkflowNames.BookingWorkflow);

    /// <summary>Commits the ride, then outlives every attempt so Temporal reports failure.</summary>
    public sealed class PersistThenTimeOutRideBookingActivities(
        RideRepository repository,
        RideStreamPublishingActivities publisher)
    {
        private readonly RideBookingActivities inner = new(repository, publisher);

        [Activity(RideWorkflowNames.StoreBookingActivity)]
        public async Task<RideResponse> StoreBookingAsync(RideBookingCommand booking)
        {
            var context = ActivityExecutionContext.Current;
            var stored = await repository.PersistBookingAsync(booking, context.CancellationToken);
            await Task.Delay(
                RideBookingWorkflow.StoreActivityOptions.StartToCloseTimeout!.Value
                    + TimeSpan.FromSeconds(1),
                context.CancellationToken);
            return stored;
        }

        [Activity(RideWorkflowNames.ReconcileBookingActivity)]
        public Task<RideResponse?> ReconcileBookingAsync(RideBookingCommand booking) =>
            inner.ReconcileBookingAsync(booking);

        [Activity(RideWorkflowNames.TransitionRideActivity)]
        public Task<RideTransitionResult> TransitionAsync(RideTransitionCommand command) =>
            inner.TransitionAsync(command);

        [Activity(RideWorkflowNames.CancelRideActivity)]
        public Task<RideCancellationResult> CancelAsync(Guid rideId, DateTimeOffset cancelledAtUtc) =>
            inner.CancelAsync(rideId, cancelledAtUtc);
    }

    private static RideBookingRequest Booking(
        string key,
        DateTimeOffset? pickupAt,
        bool immediate = false,
        DateOnly? checkoutDate = null) =>
        new(
            GuestId,
            StayId,
            checkoutDate ?? DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(DefaultNow, HotelTimeZone).DateTime),
            new RidePlaceRequest("Hotel ZGY", 25.7984634, -80.1956016),
            new RidePlaceRequest("Miami Beach", 25.7907, -80.13),
            immediate,
            pickupAt,
            key);

    private static async Task<RideResponse?> ReadRideAsync(HttpResponseMessage response) =>
        response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<RideResponse>()
            : null;

    private static async Task<RideResponse?> ReadOwnedRideAsync(RideTestHost host, Guid id)
    {
        using var response = await host.GetAsync($"/rides/{id}", GuestId, StayId);
        return await ReadRideAsync(response);
    }

    private sealed class RideTestHost : IAsyncDisposable
    {
        private readonly PostgreSqlTestDatabase database;
        private readonly NpgsqlDataSource dataSource;
        private readonly WebApplication app;
        private readonly FakeTimeProvider clock;

        private RideTestHost(
            PostgreSqlTestDatabase database,
            NpgsqlDataSource dataSource,
            Temporalio.Testing.WorkflowEnvironment environment,
            WebApplication app,
            FakeTimeProvider clock)
        {
            this.database = database;
            this.dataSource = dataSource;
            this.app = app;
            this.clock = clock;
            Environment = environment;
            Repository = app.Services.GetRequiredService<RideRepository>();
            Http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        }

        public WorkflowEnvironment Environment { get; }
        public RideRepository Repository { get; }
        public HttpClient Http { get; }

        public static async Task<RideTestHost> StartAsync(
            PostgreSqlFixture postgreSql,
            DateTimeOffset now,
            bool startStayWorkflow = true,
            bool unavailableFeed = false,
            bool persistThenTimeOutStore = false,
            int? minimumAdvanceNoticeMinutes = null)
        {
            var database = await postgreSql.CreateDatabaseAsync();
            var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
            await new RideDatabaseMigrator(dataSource, NullLogger<RideDatabaseMigrator>.Instance)
                .MigrateAsync();
            var environment = unavailableFeed
                ? await WorkflowEnvironment.StartTimeSkippingAsync()
                : await WorkflowEnvironment.StartLocalAsync();
            var clock = new FakeTimeProvider(now);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.Services.AddSingleton<ITemporalClient>(environment.Client);
            builder.Services.AddSingleton(dataSource);
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Services.AddSingleton(HotelTimeZone);
            builder.Services.AddSingleton<RideRepository>();
            builder.Services.AddSingleton<RideBookingService>();
            builder.Services.AddScoped<RideStreamPublishingActivities>();
            builder.Services.AddOptions<RideSchedulingOptions>()
                .Configure(options =>
                {
                    if (minimumAdvanceNoticeMinutes is { } minutes)
                    {
                        options.MinimumAdvanceNoticeMinutes = minutes;
                    }
                })
                .ValidateDataAnnotations()
                .ValidateOnStart();
            builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
            var rideWorker = builder.Services.AddHostedTemporalWorker(RideWorkflowNames.TaskQueue)
                .AddWorkflow<RideBookingWorkflow>();
            if (persistThenTimeOutStore)
            {
                rideWorker.AddScopedActivities<PersistThenTimeOutRideBookingActivities>();
            }
            else
            {
                rideWorker.AddScopedActivities<RideBookingActivities>();
            }
            if (unavailableFeed)
            {
                rideWorker.AddScopedActivities<UnavailableRideStreamPublishingActivities>();
            }
            else
            {
                rideWorker.AddScopedActivities<RideStreamPublishingActivities>();
            }
            builder.Services.AddHostedTemporalWorker(TemporalWorkerConnection.TaskQueue)
                .AddWorkflow<ConciergeStayWorkflow>()
                .AddScopedActivities<EmptyTranscriptSequenceActivities>();

            var app = builder.Build();
            app.MapRideEndpoints();
            await app.StartAsync();
            var host = new RideTestHost(database, dataSource, environment, app, clock);
            if (startStayWorkflow)
            {
                await host.StartStayWorkflowAsync(DateTimeOffset.UtcNow.AddHours(4));
            }

            return host;
        }

        public async Task CloseStayWorkflowAsync()
        {
            var handle = await StartStayWorkflowAsync(DateTimeOffset.UtcNow.AddSeconds(-1));
            await handle.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        private Task<WorkflowHandle<ConciergeStayWorkflow>> StartStayWorkflowAsync(
            DateTimeOffset deadline) =>
            Environment.Client.StartWorkflowAsync(
                (ConciergeStayWorkflow workflow) => workflow.RunAsync(
                    new ConciergeStayWorkflowInput(StayId, deadline, 60)),
                new WorkflowOptions(
                    ConciergeStayWorkflowIds.ForStay(StayId),
                    TemporalWorkerConnection.TaskQueue));

        public Task<HttpResponseMessage> BookAsync(RideBookingRequest request) =>
            Http.PostAsJsonAsync("/rides", request);

        public void AdvanceTime(TimeSpan elapsed) => clock.Advance(elapsed);

        public Task<HttpResponseMessage> GetAsync(string path, Guid guestId, Guid stayId) =>
            SendWithScope(HttpMethod.Get, path, guestId, stayId);

        public Task<HttpResponseMessage> PurgeAsync(Guid pathStayId, Guid guestId, Guid stayId) =>
            SendWithScope(HttpMethod.Post, $"/stays/{pathStayId}/rides/purge", guestId, stayId);

        public Task<HttpResponseMessage> DeleteAsync(Guid id, Guid guestId, Guid stayId) =>
            SendWithScope(HttpMethod.Delete, $"/rides/{id}", guestId, stayId);

        public Task<HttpResponseMessage> GetListAsync(
            Guid pathGuestId,
            Guid guestId,
            Guid stayId,
            string when) =>
            SendWithScope(
                HttpMethod.Get,
                $"/guests/{pathGuestId}/rides?when={when}",
                guestId,
                stayId);

        public async Task ExecuteAsync(string sql)
        {
            await using var command = dataSource.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<T> ScalarAsync<T>(string sql)
        {
            await using var command = dataSource.CreateCommand(sql);
            return (T)(await command.ExecuteScalarAsync())!;
        }

        public async Task<string[]> EventStatusesAsync(Guid rideId)
        {
            await using var command = dataSource.CreateCommand(
                "SELECT status FROM ride_events WHERE ride_id = $1 ORDER BY sequence;");
            command.Parameters.AddWithValue(rideId);
            await using var reader = await command.ExecuteReaderAsync();
            var statuses = new List<string>();
            while (await reader.ReadAsync())
            {
                statuses.Add(reader.GetString(0));
            }

            return statuses.ToArray();
        }

        private Task<HttpResponseMessage> SendWithScope(
            HttpMethod method,
            string path,
            Guid guestId,
            Guid stayId)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add("X-Verified-Guest-Id", guestId.ToString());
            request.Headers.Add("X-Verified-Stay-Id", stayId.ToString());
            request.Headers.Add(
                "X-Verified-Checkout-Date",
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), HotelTimeZone).DateTime)
                    .AddDays(1)
                    .ToString("yyyy-MM-dd"));
            return Http.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            await Environment.DisposeAsync();
            await dataSource.DisposeAsync();
            await database.DisposeAsync();
        }
    }
}
