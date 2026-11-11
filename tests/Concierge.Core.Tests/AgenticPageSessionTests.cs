using System.Reflection;
using Concierge.Contracts;
using Concierge.Web.Components.Pages;
using Concierge.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Concierge.Core.Tests;

/// <summary>
/// ADR 0015: the AG-UI page's session start. A failed history read used to escape the first render and
/// end the circuit (2026-11-06: a stay at Temporal's in-flight Update limit refused History with 503).
/// </summary>
public sealed class AgenticPageSessionTests
{
    [Theory]
    [InlineData(503, "unavailable", "The concierge couldn't load your conversation. Try again in a moment.")]
    [InlineData(429, "busy", "The concierge is busy. Try again.")]
    public async Task AFailedHistoryReadShowsAnErrorInsteadOfThrowing(int status, string code, string expected)
    {
        var page = Page(new FailingHistory(new ConciergeApiException(code, "The concierge is busy. Try again.", status)));

        await StartSessionAsync(page);

        Assert.Equal(expected, Field<string?>(page, "verificationError"));
        Assert.Null(Field<object?>(page, "session"));
    }

    [Fact]
    public async Task AnUnexpectedFailureIsAlsoContained()
    {
        var page = Page(new FailingHistory(new InvalidOperationException("secret-internal-detail")));

        await StartSessionAsync(page);

        Assert.Equal("The concierge couldn't load your conversation. Try again in a moment.", Field<string?>(page, "verificationError"));
    }

    private static Agentic Page(IConciergeService concierge)
    {
        var page = new Agentic();
        Set(page, "Concierge", concierge);
        Set(page, "Logger", NullLogger<Agentic>.Instance);
        return page;
    }

    private static Task StartSessionAsync(Agentic page)
    {
        var stored = Type.GetType("Concierge.Web.Components.Pages.Agentic+StoredSession, Concierge.Web")!;
        var session = Activator.CreateInstance(stored, "credential", Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1));
        return (Task)typeof(Agentic).GetMethod("StartSessionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, [session])!;
    }

    private static void Set(Agentic page, string property, object value) =>
        typeof(Agentic).GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static T Field<T>(Agentic page, string name) =>
        (T)typeof(Agentic).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;

    private sealed class FailingHistory(Exception failure) : IConciergeService
    {
        public Task<ConciergeHistoryResult> ReadHistoryAsync(string? bearerCredential, CancellationToken cancellationToken = default) =>
            Task.FromException<ConciergeHistoryResult>(failure);

        public Task<ConciergeSessionResponse> VerifyAsync(ConciergeVerificationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConciergeTurnResult> SubmitTurnAsync(string? bearerCredential, ConciergeTurnRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EndAsync(string? bearerCredential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResetAsync(string? bearerCredential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConciergeVoiceAuthorization> AuthorizeVoiceAsync(string? bearerCredential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConciergeTurnResult> SubmitVoiceTurnAsync(ConciergeSessionContext session, string turnId, string userMessage, CancellationToken cancellationToken = default, bool enableEndChatFunction = false) => throw new NotSupportedException();
        public Task MarkVoiceTurnInterruptedAsync(ConciergeSessionContext session, string turnId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConciergeRideActivitySnapshot> ReadRideActivitySnapshotAsync(string? bearerCredential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<ConciergeRideActivityItem> StreamRideActivityAsync(string? bearerCredential, long fromOffset, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConciergeRideReadState> MarkRideActivitySnapshotReadAsync(string? bearerCredential, long snapshotOffset, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
