using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Google.Protobuf;
using Temporalio.Api.Enums.V1;
using Concierge.Contracts;
using Concierge.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Concierge.WorkflowServer.Setup;
using Concierge.WorkflowServer.Workflows;

namespace Concierge.Core.Tests;

public sealed class ConciergeApiTests(PostgreSqlFixture postgreSql)
    : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task CredentialsReachOnlyTheirBoundStayAndChannelsShareStayContext()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        var riveraStay = await host.SeedStayAsync("Rivera", "412", today, today.AddDays(2));
        var morganStay = await host.SeedStayAsync("Morgan", "812", today, today.AddDays(2));

        await AssertErrorAsync(
            await Verify(host, "Rivera", "12345"), HttpStatusCode.BadRequest, "invalid_request");
        await AssertErrorAsync(
            await Verify(host, "Rivera", "0412"), HttpStatusCode.BadRequest, "invalid_request");
        await AssertErrorAsync(
            await Verify(host, "Morgan", "412"), HttpStatusCode.NotFound, "guest_not_found");

        // Each engagement authenticates anew and receives a distinct credential.
        var riveraWeb = await VerifiedCredentialAsync(host, "Rivera", "412");
        var riveraVoice = await VerifiedCredentialAsync(host, "rivera", "412");
        var morgan = await VerifiedCredentialAsync(host, "Morgan", "812");
        Assert.NotEqual(riveraWeb, riveraVoice);

        foreach (var credential in new string?[] { null, "forged-credential", riveraWeb + "x" })
        {
            await AssertErrorAsync(
                await Send(host, HttpMethod.Post, "/api/concierge/turns", credential, Turn("t", "Hi?", "web")),
                HttpStatusCode.Unauthorized,
                "unauthorized");
            await AssertErrorAsync(
                await Send(host, HttpMethod.Get, "/api/concierge/history", credential),
                HttpStatusCode.Unauthorized,
                "unauthorized");
            await AssertErrorAsync(
                await Send(host, HttpMethod.Post, "/api/concierge/end", credential),
                HttpStatusCode.Unauthorized,
                "unauthorized");
        }

        await AssertErrorAsync(
            await Send(host, HttpMethod.Post, "/api/concierge/turns", riveraWeb, Turn("t", "Hi?", "sms")),
            HttpStatusCode.BadRequest,
            "invalid_request");

        var webTurn = await SubmitAsync(host, riveraWeb, Turn("turn-1", "Where is breakfast?", "web"));
        var voiceTurn = await SubmitAsync(host, riveraVoice, Turn("turn-2", "What time?", "voice"));
        Assert.Equal(0, webTurn.Sequence);
        Assert.Equal(1, voiceTurn.Sequence);
        Assert.Contains("Where is breakfast?", host.ChatClient.Requests[^1].UserTexts);

        // The same turn ID on another stay is an unrelated turn with its own context.
        var morganTurn = await SubmitAsync(host, morgan, Turn("turn-1", "Is there a gym?", "web"));
        Assert.Equal(0, morganTurn.Sequence);
        Assert.DoesNotContain("Where is breakfast?", host.ChatClient.Requests[^1].UserTexts);

        await AssertErrorAsync(
            await Send(host, HttpMethod.Post, "/api/concierge/turns", riveraWeb, Turn("turn-1", "Changed", "web")),
            HttpStatusCode.Conflict,
            "turn_conflict");

        Assert.Equal(["turn-1", "turn-2"], await HistoryTurnIdsAsync(host, riveraWeb));
        Assert.Equal(["turn-1", "turn-2"], await HistoryTurnIdsAsync(host, riveraVoice));
        var morganHistory = await HistoryAsync(host, morgan);
        Assert.Equal(["Is there a gym?"], morganHistory.Turns.Select(turn => turn.GuestMessage));

        // Ending an engagement flushes the stay transcript and retires only that credential.
        var end = await Send(host, HttpMethod.Post, "/api/concierge/end", riveraWeb);
        Assert.Equal(HttpStatusCode.NoContent, end.StatusCode);
        Assert.Equal(["turn-1", "turn-2"], await host.ReadPersistedTurnIdsAsync(riveraStay));
        Assert.Empty(await host.ReadPersistedTurnIdsAsync(morganStay));
        await AssertErrorAsync(
            await Send(host, HttpMethod.Get, "/api/concierge/history", riveraWeb),
            HttpStatusCode.Unauthorized,
            "unauthorized");
        Assert.Equal(["turn-1", "turn-2"], await HistoryTurnIdsAsync(host, riveraVoice));

        // An expired credential cannot submit, read, or end.
        await host.ExecuteAsync(
            """
            UPDATE concierge_sessions
            SET expires_at_utc = CURRENT_TIMESTAMP - INTERVAL '1 second'
            WHERE stay_id = $1;
            """,
            riveraStay);
        await AssertErrorAsync(
            await Send(host, HttpMethod.Post, "/api/concierge/turns", riveraVoice, Turn("turn-3", "Hi?", "voice")),
            HttpStatusCode.Unauthorized,
            "unauthorized");
        await AssertErrorAsync(
            await Send(host, HttpMethod.Get, "/api/concierge/history", riveraVoice),
            HttpStatusCode.Unauthorized,
            "unauthorized");
        await AssertErrorAsync(
            await Send(host, HttpMethod.Post, "/api/concierge/end", riveraVoice),
            HttpStatusCode.Unauthorized,
            "unauthorized");
    }

    [Fact]
    public async Task VerifiedCredentialExpiresAtTheAbsoluteLocalEligibilityDeadline()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        await host.SeedStayAsync("Rivera", "412", today.AddDays(-1), today.AddDays(1));

        var response = await Verify(host, "Rivera", "412");
        response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<ConciergeSessionResponse>();

        Assert.Equal(ConciergeTestHost.HotelMidnightUtc(today.AddDays(2)), session!.ExpiresAtUtc);
        Assert.NotEqual(Guid.Empty, session.SessionId);
    }

    [Fact]
    public async Task SessionHistoryContainsOnlyTheCredentialDigestAndExpectedVerificationDetails()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        await host.SeedStayAsync("HistoryProbe", "412", today, today.AddDays(1));

        var credential = await VerifiedCredentialAsync(host, "HistoryProbe", "412");
        var digest = SHA256.HashData(Base64Url.DecodeFromChars(credential));
        Guid sessionId;
        byte[] persistedDigest;
        await using (var command = host.DataSource.CreateCommand(
            """
            SELECT session_id, credential_sha256
            FROM concierge_sessions
            WHERE credential_sha256 = $1;
            """))
        {
            command.Parameters.AddWithValue(digest);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            sessionId = reader.GetGuid(0);
            persistedDigest = reader.GetFieldValue<byte[]>(1);
        }

        // Verify runs as a Standalone Activity whose ID ends in a per-request GUID; find it by prefix.
        var activityIdPrefix = $"concierge-session-{sessionId:N}-verify-";
        string? activityId = null;
        for (var attempt = 0; activityId is null && attempt < 50; attempt++)
        {
            var page = await host.Environment.Client.ListActivitiesPaginatedAsync(
                $"ActivityType = '{ConciergeActivityNames.VerifySession}'", null);
            activityId = page.Activities
                .Select(activity => activity.ActivityId)
                .SingleOrDefault(id => id.StartsWith(activityIdPrefix, StringComparison.Ordinal));
            if (activityId is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200));
            }
        }

        Assert.NotNull(activityId);
        var description = await host.Environment.Client.GetActivityHandle(activityId).DescribeAsync(
            new() { IncludeInput = true, IncludeOutcome = true });
        Assert.True(description.HasInput);
        Assert.True(description.HasResult);
        var serializedHistory = string.Concat(
            description.RawInput!
                .Select(payload => payload.ToByteArray())
                .Append(description.RawOutcome!.ToByteArray())
                .Select(bytes => Encoding.UTF8.GetString(bytes)));

        Assert.Equal(digest, persistedDigest);
        Assert.DoesNotContain(credential, serializedHistory, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(digest), serializedHistory, StringComparison.Ordinal);
        Assert.Contains("HistoryProbe", serializedHistory, StringComparison.Ordinal);
        Assert.Contains("412", serializedHistory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelFailureReturnsCuratedReplyWithoutInternalDetails()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        await host.SeedStayAsync("Rivera", "412", today, today.AddDays(1));
        var credential = await VerifiedCredentialAsync(host, "Rivera", "412");
        host.ChatClient.Fail = true;

        var response = await Send(
            host,
            HttpMethod.Post,
            "/api/concierge/turns",
            credential,
            Turn("fails", "Is the spa open?", "web"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("couldn't reach the concierge service", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-internal-detail", body, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.7", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MessageOverTheUtf8ByteLimitGetsActionableBadRequest()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        await host.SeedStayAsync("Rivera", "412", today, today.AddDays(1));
        var credential = await VerifiedCredentialAsync(host, "Rivera", "412");

        // 700 characters, 2,100 UTF-8 bytes.
        var response = await Send(
            host,
            HttpMethod.Post,
            "/api/concierge/turns",
            credential,
            Turn("too-big", new string('\u20AC', 700), "web"));

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("2000 UTF-8 bytes", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
        Assert.Empty(host.ChatClient.Requests);
    }

    private static ConciergeTurnRequest Turn(string turnId, string message, string channel) =>
        new(turnId, message, channel);

    private static Task<HttpResponseMessage> Verify(
        ConciergeTestHost host,
        string lastName,
        string roomNumber) =>
        host.Http.PostAsJsonAsync(
            "/api/concierge/sessions",
            new ConciergeVerificationRequest(lastName, roomNumber));

    [Fact]
    public async Task VoiceTurnRetryJoinsTheInFlightWorkflowAttemptAcrossReplicas()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        var stayId = await host.SeedStayAsync("Rivera", "412", today, today.AddDays(2));
        var credential = await VerifiedCredentialAsync(host, "Rivera", "412");
        host.ChatClient.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Separate scopes model a retry that bypasses the Web's in-process join (for example, another replica).
        await using var firstScope = host.App.Services.CreateAsyncScope();
        await using var retryScope = host.App.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<IConciergeService>();
        var retry = retryScope.ServiceProvider.GetRequiredService<IConciergeService>();
        var stay = (await first.AuthorizeVoiceAsync(credential)).Session;
        var turnId = new VoiceUtterance(Guid.NewGuid(), 1, 1).TurnId("Is the pool open?");

        var admitted = first.SubmitVoiceTurnAsync(stay, turnId, "Is the pool open?");
        await host.ChatClient.WaitForCallAsync();
        var retried = retry.SubmitVoiceTurnAsync(stay, turnId, "Is the pool open?");
        host.ChatClient.Gate.SetResult();
        var results = await Task.WhenAll(admitted, retried);

        Assert.Equal(results[0], results[1]);
        Assert.Single(host.ChatClient.Requests);
        var workflow = host.Environment.Client.GetWorkflowHandle($"concierge-stay-{stayId:N}");
        var history = await workflow.FetchHistoryAsync();
        var turnUpdates = history.Events
            .Select(historyEvent => historyEvent.WorkflowExecutionUpdateAcceptedEventAttributes?.AcceptedRequest)
            .Where(update => update?.Input.Name == ConciergeWorkflowNames.SubmitTurnUpdate)
            .ToArray();
        Assert.Equal(2, turnUpdates.Length);
        Assert.Equal(2, turnUpdates.Select(update => update!.Meta.UpdateId).Distinct().Count());
    }

    [Fact]
    public async Task NewStayWorkflowContinuesAfterPreviousWorkflowWasTerminated()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        var stayId = await host.SeedStayAsync("Rivera", "412", today, today.AddDays(2));
        var firstCredential = await VerifiedCredentialAsync(host, "Rivera", "412");
        var firstTurn = await SubmitAsync(host, firstCredential, Turn("first", "Hello", "web"));
        Assert.Equal(0, firstTurn.Sequence);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(
            host, HttpMethod.Post, "/api/concierge/end", firstCredential)).StatusCode);

        await host.Environment.Client
            .GetWorkflowHandle($"concierge-stay-{stayId:N}")
            .TerminateAsync("Simulate a terminated stay workflow.");

        var replacementCredential = await VerifiedCredentialAsync(host, "Rivera", "412");
        var nextTurn = await SubmitAsync(
            host, replacementCredential, Turn("second", "Where is breakfast?", "web"));

        Assert.Equal(1, nextTurn.Sequence);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(
            host, HttpMethod.Post, "/api/concierge/end", replacementCredential)).StatusCode);
        Assert.Equal(["first", "second"], await host.ReadPersistedTurnIdsAsync(stayId));
    }

    /// <summary>
    /// Regression: after the stay workflow continues as new, the current run's first-execution run ID
    /// is the chain's original run, so Web updates must not pin the handle returned by
    /// StartWorkflowAsync(UseExisting) or the server rejects them as "workflow execution not found".
    /// </summary>
    [Fact]
    public async Task WebCallsSucceedAfterStayWorkflowContinuesAsNew()
    {
        await using var host = await ConciergeTestHost.StartAsync(
            postgreSql,
            devServerArgs:
            [
                "--dynamic-config-value",
                "limit.historyCount.suggestContinueAsNew=160",
            ]);
        var today = ConciergeTestHost.HotelToday;
        var stayId = await host.SeedStayAsync("Rivera", "412", today, today.AddDays(2));
        var credential = await VerifiedCredentialAsync(host, "Rivera", "412");
        var workflow = host.Environment.Client.GetWorkflowHandle($"concierge-stay-{stayId:N}");

        var submitted = new List<string> { (await SubmitAsync(host, credential, Turn("can-0", "Hello", "web"))).TurnId };
        var firstRunId = (await workflow.DescribeAsync()).RunId;
        for (var index = 1; index < 20 && (await workflow.DescribeAsync()).RunId == firstRunId; index++)
        {
            // The first submissions run while the original run is still current.
            submitted.Add((await SubmitAsync(host, credential, Turn($"can-{index}", $"Question {index}", "web"))).TurnId);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
            while ((await workflow.DescribeAsync()).RunId == firstRunId && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(100);
            }
        }

        var current = await workflow.DescribeAsync();
        Assert.NotEqual(firstRunId, current.RunId);
        Assert.Equal(WorkflowExecutionStatus.Running, current.Status);

        // Every Web operation that targets the stay workflow still reaches the continued run.
        var after = await SubmitAsync(host, credential, Turn("after-can", "Anything else?", "web"));
        Assert.Equal(submitted.Count, after.Sequence);
        submitted.Add(after.TurnId);
        Assert.Equal(submitted, await HistoryTurnIdsAsync(host, credential));

        await using var scope = host.App.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IConciergeService>();
        var voice = await service.AuthorizeVoiceAsync(credential);
        Assert.Equal(stayId, voice.Session.StayId);
        Assert.True(voice.HasTurns);

        var ended = await Send(host, HttpMethod.Post, "/api/concierge/end", credential);
        Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);
        Assert.Equal(submitted, await host.ReadPersistedTurnIdsAsync(stayId));
    }

    private static async Task<string> VerifiedCredentialAsync(
        ConciergeTestHost host,
        string lastName,
        string roomNumber)
    {
        var response = await Verify(host, lastName, roomNumber);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<ConciergeSessionResponse>();
        return session!.Credential;
    }

    [Fact]
    public async Task ResetStartsOnlyThatStayOverAndKeepsTheCredential()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        var riveraStay = await host.SeedStayAsync("Rivera", "412", today, today.AddDays(2));
        await host.SeedStayAsync("Morgan", "812", today, today.AddDays(2));
        var rivera = await VerifiedCredentialAsync(host, "Rivera", "412");
        var riveraOther = await VerifiedCredentialAsync(host, "Rivera", "412");
        var morgan = await VerifiedCredentialAsync(host, "Morgan", "812");

        foreach (var credential in new string?[] { null, "forged-credential", rivera + "x" })
        {
            await AssertErrorAsync(
                await Send(host, HttpMethod.Post, "/api/concierge/reset", credential),
                HttpStatusCode.Unauthorized,
                "unauthorized");
        }
        Assert.Empty(host.ActionTools.RidePurges);

        await SubmitAsync(host, rivera, Turn("r-1", "Where is breakfast?", "web"));
        await SubmitAsync(host, riveraOther, Turn("r-2", "What time?", "voice"));
        await SubmitAsync(host, morgan, Turn("m-1", "Is there a gym?", "web"));

        var reset = await Send(host, HttpMethod.Post, "/api/concierge/reset", rivera);

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(riveraStay, Assert.Single(host.ActionTools.RidePurges).StayId);
        Assert.Empty(await HistoryTurnIdsAsync(host, rivera));
        Assert.Empty(await HistoryTurnIdsAsync(host, riveraOther));
        Assert.Equal(["m-1"], await HistoryTurnIdsAsync(host, morgan));
        // The guest stays signed in and the stay's transcript starts again at sequence 0.
        Assert.Equal(0, (await SubmitAsync(host, rivera, Turn("r-3", "Hello again.", "web"))).Sequence);
        Assert.DoesNotContain("Where is breakfast?", host.ChatClient.Requests[^1].UserTexts);
    }

    [Fact]
    public async Task IncompleteResetReturnsTheWorkflowsSpecificGuidance()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        await host.SeedStayAsync("Rivera", "412", today, today.AddDays(2));
        var rivera = await VerifiedCredentialAsync(host, "Rivera", "412");
        await SubmitAsync(host, rivera, Turn("r-1", "Where is breakfast?", "web"));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            host.ActionTools.RidePurgeFailures.Enqueue(new InvalidOperationException("ride service down"));
        }

        var response = await Send(host, HttpMethod.Post, "/api/concierge/reset", rivera);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ConciergeApiError>())!;
        Assert.Equal("reset_incomplete", error.Code);
        Assert.Equal(
            "Your rides could not be removed, so nothing was reset. Please try Start over again.",
            error.Message);
        Assert.Equal(["r-1"], await HistoryTurnIdsAsync(host, rivera));
    }

    private static async Task<HttpResponseMessage> Send(
        ConciergeTestHost host,
        HttpMethod method,
        string path,
        string? credential,
        object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (credential is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await host.Http.SendAsync(request);
    }

    private static async Task<ConciergeTurnResult> SubmitAsync(
        ConciergeTestHost host,
        string credential,
        ConciergeTurnRequest turn)
    {
        var response = await Send(host, HttpMethod.Post, "/api/concierge/turns", credential, turn);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConciergeTurnResult>())!;
    }

    private static async Task<ConciergeHistoryResult> HistoryAsync(
        ConciergeTestHost host,
        string credential)
    {
        var response = await Send(host, HttpMethod.Get, "/api/concierge/history", credential);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConciergeHistoryResult>())!;
    }

    private static async Task<string[]> HistoryTurnIdsAsync(
        ConciergeTestHost host,
        string credential) =>
        [.. (await HistoryAsync(host, credential)).Turns.Select(turn => turn.TurnId)];

    private static async Task AssertErrorAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code)
    {
        Assert.Equal(status, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ConciergeApiError>();
        Assert.Equal(code, error!.Code);
    }
}
