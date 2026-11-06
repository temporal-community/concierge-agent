using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using Concierge.Contracts;
using Concierge.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Temporalio.Client;

namespace Concierge.Core.Tests;

public sealed class ConciergeRequestGuidanceTests
{
    public static TheoryData<ConciergeVerificationRequest?, string> InvalidVerificationRequests => new()
    {
        { null, "Provide a verification request with LastName and RoomNumber." },
        { new(null!, "412"), $"Enter LastName (up to {ConciergeVerifiedGuest.MaxNameLength} characters)." },
        { new("", "412"), $"Enter LastName (up to {ConciergeVerifiedGuest.MaxNameLength} characters)." },
        { new(" \t", "412"), $"Enter LastName (up to {ConciergeVerifiedGuest.MaxNameLength} characters)." },
        { new(new string('X', ConciergeVerifiedGuest.MaxNameLength + 1), "412"), $"Enter LastName (up to {ConciergeVerifiedGuest.MaxNameLength} characters)." },
        { new("PrivateGuestName", null!), "Enter RoomNumber as 1–4 ASCII digits with no leading zero." },
        { new("PrivateGuestName", ""), "Enter RoomNumber as 1–4 ASCII digits with no leading zero." },
        { new("PrivateGuestName", "0412"), "Enter RoomNumber as 1–4 ASCII digits with no leading zero." },
        { new("PrivateGuestName", "12345"), "Enter RoomNumber as 1–4 ASCII digits with no leading zero." },
        { new("PrivateGuestName", "１２"), "Enter RoomNumber as 1–4 ASCII digits with no leading zero." },
        { new("PrivateGuestName", " 412"), "Enter RoomNumber as 1–4 ASCII digits with no leading zero." },
    };

    public static TheoryData<ConciergeTurnRequest?, string> InvalidTurnRequests => new()
    {
        { null, "Provide a turn request with TurnId, UserMessage, and Channel." },
        { new(null!, "PrivateGuestQuestion", "web"), "Provide a nonblank TurnId of at most 128 characters." },
        { new("", "PrivateGuestQuestion", "web"), "Provide a nonblank TurnId of at most 128 characters." },
        { new(" \t", "PrivateGuestQuestion", "web"), "Provide a nonblank TurnId of at most 128 characters." },
        { new(new string('X', 129), "PrivateGuestQuestion", "web"), "Provide a nonblank TurnId of at most 128 characters." },
        { new("private-turn", null!, "web"), "Enter a nonblank UserMessage of at most 2000 UTF-8 bytes." },
        { new("private-turn", "", "web"), "Enter a nonblank UserMessage of at most 2000 UTF-8 bytes." },
        { new("private-turn", " \t", "web"), "Enter a nonblank UserMessage of at most 2000 UTF-8 bytes." },
        { new("private-turn", new string('X', 2001), "web"), "Enter a nonblank UserMessage of at most 2000 UTF-8 bytes." },
        { new("private-turn", new string('\u20AC', 667), "web"), "Enter a nonblank UserMessage of at most 2000 UTF-8 bytes." },
        { new("private-turn", "PrivateGuestQuestion", null!), "Set Channel to 'web' or 'voice' (case-sensitive)." },
        { new("private-turn", "PrivateGuestQuestion", ""), "Set Channel to 'web' or 'voice' (case-sensitive)." },
        { new("private-turn", "PrivateGuestQuestion", "sms"), "Set Channel to 'web' or 'voice' (case-sensitive)." },
        { new("private-turn", "PrivateGuestQuestion", "Web"), "Set Channel to 'web' or 'voice' (case-sensitive)." },
    };

    [Theory]
    [MemberData(nameof(InvalidVerificationRequests))]
    public async Task VerificationGuidanceRejectsMalformedInputBeforeTemporal(
        ConciergeVerificationRequest? request, string message)
    {
        var client = DispatchProxy.Create<ITemporalClient, RejectingTemporalClient>();
        var service = CreateService(client);

        var error = await Assert.ThrowsAsync<ConciergeApiException>(() => service.VerifyAsync(request!));

        AssertInvalid(error, message);
        Assert.Equal(0, ((RejectingTemporalClient)client).Calls);
    }

    [Theory]
    [MemberData(nameof(InvalidTurnRequests))]
    public async Task TurnGuidanceRejectsMalformedInputBeforeAuthenticationOrTemporal(
        ConciergeTurnRequest? request, string message)
    {
        var client = DispatchProxy.Create<ITemporalClient, RejectingTemporalClient>();
        var service = CreateService(client);
        // Guidance must not reveal whether a supplied credential has a valid format.
        foreach (var credential in new[] { "PrivateCredential", new string('A', 43) })
        {
            var error = await Assert.ThrowsAsync<ConciergeApiException>(
                () => service.SubmitTurnAsync(credential, request!));
            AssertInvalid(error, message);
        }
        Assert.Equal(0, ((RejectingTemporalClient)client).Calls);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("9999")]
    public async Task VerificationAcceptsTrimmedNameAndRoomBoundaries(string roomNumber)
    {
        var client = DispatchProxy.Create<ITemporalClient, RejectingTemporalClient>();
        var error = await Assert.ThrowsAsync<ConciergeApiException>(
            () => CreateService(client).VerifyAsync(new($" {new string('X', ConciergeVerifiedGuest.MaxNameLength)} ", roomNumber)));

        // The rejecting client proves validation reached Temporal, not the invalid-request branch.
        Assert.Equal("unavailable", error.Code);
        Assert.Equal(503, error.StatusCode);
        Assert.Equal(1, ((RejectingTemporalClient)client).Calls);
    }

    [Theory]
    [InlineData("web", false)]
    [InlineData("voice", false)]
    [InlineData("web", true)]
    [InlineData("voice", true)]
    public async Task TurnAcceptsExactLimitsAndSupportedChannelsWithoutChangingUnauthorized(
        string channel, bool multibyte)
    {
        var client = DispatchProxy.Create<ITemporalClient, RejectingTemporalClient>();
        var message = multibyte ? new string('\u20AC', 666) + "XX" : new string('X', 2000);
        Assert.Equal(ConciergeTurnLimits.MaxUserMessageUtf8Bytes, Encoding.UTF8.GetByteCount(message));
        var error = await Assert.ThrowsAsync<ConciergeApiException>(() => CreateService(client)
            .SubmitTurnAsync(null, new(new string('X', 128), message, channel)));

        Assert.Equal(401, error.StatusCode);
        Assert.Equal("unauthorized", error.Code);
        Assert.Equal("Authenticate again to continue.", error.Message);
        Assert.Equal(0, ((RejectingTemporalClient)client).Calls);
    }

    [Fact]
    public async Task ValidJsonMalformedBodiesExposeServiceGuidanceThroughProductionEndpoints()
    {
        var client = DispatchProxy.Create<ITemporalClient, RejectingTemporalClient>();
        using var factory = new WebApplicationFactory<ConciergeService>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services => services.AddSingleton(client));
        });
        using var http = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "PrivateCredential");

        foreach (var row in InvalidVerificationRequests)
        {
            await AssertResponseAsync(
                await http.PostAsJsonAsync("/api/concierge/sessions", (ConciergeVerificationRequest?)row[0]),
                (string)row[1]);
        }
        foreach (var row in InvalidTurnRequests)
        {
            await AssertResponseAsync(
                await http.PostAsJsonAsync("/api/concierge/turns", (ConciergeTurnRequest?)row[0]),
                (string)row[1]);
        }
        // Missing JSON properties must also reach service validation rather than a generic binding error.
        await AssertResponseAsync(
            await http.PostAsJsonAsync("/api/concierge/sessions", new { RoomNumber = "412" }),
            $"Enter LastName (up to {ConciergeVerifiedGuest.MaxNameLength} characters).");
        await AssertResponseAsync(
            await http.PostAsJsonAsync("/api/concierge/turns", new { TurnId = "private-turn", Channel = "web" }),
            "Enter a nonblank UserMessage of at most 2000 UTF-8 bytes.");
        Assert.Equal(0, ((RejectingTemporalClient)client).Calls);
    }

    private static ConciergeService CreateService(ITemporalClient client) =>
        new(
            client,
            Options.Create(new ConciergePersistenceOptions()),
            new Concierge.Web.Voice.VoiceSessionRegistry(TimeProvider.System),
            new StayTranscriptNotifier(NullLogger<StayTranscriptNotifier>.Instance),
            NullLogger<ConciergeService>.Instance);

    private static void AssertInvalid(ConciergeApiException error, string message)
    {
        Assert.Equal(400, error.StatusCode);
        Assert.Equal("invalid_request", error.Code);
        AssertSafeMessage(error.Message, message);
    }

    private static async Task AssertResponseAsync(HttpResponseMessage response, string message)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ConciergeApiError>())!;
            Assert.Equal("invalid_request", error.Code);
            AssertSafeMessage(error.Message, message);
        }
    }

    private static void AssertSafeMessage(string actual, string expected)
    {
        Assert.Equal(expected, actual);
        foreach (var value in new[] { "PrivateGuestName", "PrivateGuestQuestion", "private-turn", "PrivateCredential" })
        {
            Assert.DoesNotContain(value, actual, StringComparison.Ordinal);
        }
    }

    public class RejectingTemporalClient : DispatchProxy
    {
        public int Calls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls++;
            throw new InvalidOperationException("Unexpected Temporal work in validation test.");
        }
    }
}
