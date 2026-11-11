using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Concierge.Web.Agentic;

/// <summary>
/// Development only (registered in Program.cs under IsDevelopment): the exception behind the last
/// failed AG-UI run for a guest credential, so the chat page can show it. The AG-UI host sends every server
/// failure to the client as the same generic RUN_ERROR text, and page and endpoint share this process.
/// Keys are credential hashes so the store never holds a credential.
/// </summary>
public sealed class AgenticRunFailures
{
    private readonly ConcurrentDictionary<string, string> failures = new(StringComparer.Ordinal);

    public void Clear(string credential) => failures.TryRemove(Key(credential), out _);

    public void Record(string credential, Exception exception) => failures[Key(credential)] = exception.ToString();

    /// <summary>The last failed run's exception text, or null when the latest run had none.</summary>
    public string? Latest(string credential) => failures.TryGetValue(Key(credential), out var detail) ? detail : null;

    private static string Key(string credential) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));
}
