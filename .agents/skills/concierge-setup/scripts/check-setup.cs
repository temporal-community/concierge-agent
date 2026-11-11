// The Web SDK supplies the configuration providers from the shared framework, so they track
// the installed SDK instead of pinned package versions.
#:sdk Microsoft.NET.Sdk.Web
#:package Microsoft.CodeAnalysis.CSharp@5.9.0

// Reports Concierge setup state. Required settings are read from the repo's own sources
// (aspire.config.json, AppHost.cs, AppHost appsettings) so the checks follow the code
// instead of a copied list. Secrets are reported as set or missing, never printed; only
// non-secret AI settings (provider, endpoint, model) are printed with values.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;

var problems = 0;
void Ok(string message) => Console.WriteLine($"  [ok]      {message}");
void Missing(string message) { Console.WriteLine($"  [missing] {message}"); problems++; }
void Info(string message) => Console.WriteLine($"  [info]    {message}");

var repoRoot = FindRepoRoot(ScriptDirectory());
if (repoRoot is null)
{
    Console.Error.WriteLine("Could not find aspire.config.json above this script. Run it from a concierge-agent clone.");
    return 2;
}

string? appHostPath;
try
{
    using var aspireConfig = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, "aspire.config.json")));
    appHostPath = aspireConfig.RootElement.GetProperty("appHost").GetProperty("path").GetString();
}
catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IOException)
{
    Console.Error.WriteLine($"Could not read appHost.path from aspire.config.json: {ex.Message}");
    return 2;
}
var appHostProject = appHostPath is null ? null : Path.GetFullPath(Path.Combine(repoRoot, appHostPath));
if (appHostProject is null || !File.Exists(appHostProject))
{
    Console.Error.WriteLine($"aspire.config.json points to an AppHost project that doesn't exist: {appHostPath}");
    return 2;
}
var appHostDir = Path.GetDirectoryName(appHostProject)!;
var appHostCs = Path.Combine(appHostDir, "AppHost.cs");
var projectXml = XDocument.Load(appHostProject);
var targetFramework = projectXml.Descendants("TargetFramework").FirstOrDefault()?.Value ?? "";
var sdkMajor = new string(targetFramework.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
var userSecretsId = projectXml.Descendants("UserSecretsId").FirstOrDefault()?.Value;

Console.WriteLine("Tools");
var sdks = await RunAsync("dotnet", "--list-sdks");
if (sdks?.Output.Split('\n').Any(line => line.StartsWith($"{sdkMajor}.", StringComparison.Ordinal)) == true)
{
    Ok($".NET {sdkMajor} SDK");
}
else
{
    Missing($".NET {sdkMajor} SDK (AppHost targets {targetFramework})");
}
foreach (var tool in new[] { "aspire", "just", "devtunnel", "docker" })
{
    if (FindOnPath(tool) is not null) Ok(tool); else Missing($"{tool} CLI (see README prerequisites)");
}

Console.WriteLine("Machine");
if ((await RunAsync("docker", "info", TimeSpan.FromSeconds(20)))?.ExitCode == 0)
    Ok("Docker daemon running");
else
    Missing("Docker daemon not reachable; start Docker");
if ((await RunAsync("dotnet", "dev-certs https --check --trust"))?.ExitCode == 0)
    Ok("HTTPS dev certificate trusted");
else
    Missing("HTTPS dev certificate not trusted; run: dotnet dev-certs https --trust");
if ((await RunAsync("devtunnel", "user show"))?.Output.TrimStart().StartsWith("Logged in", StringComparison.Ordinal) == true)
    Ok("devtunnel logged in");
else
    Missing("devtunnel not logged in; run: devtunnel user login");

// Same source order as the AppHost's host builder; user-secrets load only in Development.
var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
    ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
    ?? "Development";
var isDevelopment = string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase);
IConfigurationBuilder BaseConfiguration() => new ConfigurationBuilder()
    .AddJsonFile(Path.Combine(appHostDir, "appsettings.json"), optional: true)
    .AddJsonFile(Path.Combine(appHostDir, $"appsettings.{environment}.json"), optional: true);

Console.WriteLine("User-secrets store");
IConfiguration configuration;
try
{
    configuration = BaseConfiguration().AddEnvironmentVariables().Build();
}
catch (InvalidDataException ex)
{
    Console.Error.WriteLine($"AppHost appsettings can't be loaded: {ex.Message}");
    return 2;
}
if (!isDevelopment)
{
    Info($"skipped; the AppHost reads user-secrets only in Development (environment is {environment})");
}
else if (userSecretsId is null)
{
    Missing($"no <UserSecretsId> in {Path.GetRelativePath(repoRoot, appHostProject)}");
}
else
{
    try
    {
        configuration = BaseConfiguration().AddUserSecrets(userSecretsId).AddEnvironmentVariables().Build();
        Ok("readable");
    }
    catch (Exception ex) when (ex is InvalidDataException or FormatException)
    {
        // An unreadable store makes every secret look missing, so report the real cause.
        Missing($"store can't be read; {PathHelper.GetSecretsPathFromSecretsId(userSecretsId)} is not valid JSON "
            + "(an empty file or a lone byte-order mark both fail). Inspect it yourself; if it holds nothing, "
            + "replace its contents with {}");
    }
}

var appHostTree = CSharpSyntaxTree.ParseText(File.ReadAllText(appHostCs));
var invocations = appHostTree.GetCompilationUnitRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();

Console.WriteLine($"AppHost parameters (from {Path.GetRelativePath(repoRoot, appHostCs)})");
var parameters = invocations
    .Where(call => MethodName(call) == "AddParameter")
    .Select(call => StringLiteral(call.ArgumentList.Arguments.FirstOrDefault()))
    .OfType<string>()
    .Distinct()
    .ToList();
if (parameters.Count == 0)
{
    Info("No AddParameter(\"...\") calls found; AppHost.cs changed shape. Read it to find required parameters.");
}
foreach (var parameter in parameters)
{
    if (IsSet($"Parameters:{parameter}")) Ok($"Parameters:{parameter} is set"); else Missing($"Parameters:{parameter}");
}

var capabilities = invocations
    .Where(call => MethodName(call) == "ConfigureAIProvider" && call.ArgumentList.Arguments.Count >= 4)
    .Select(call => (
        Name: StringLiteral(call.ArgumentList.Arguments[2]),
        KeyParameter: StringLiteral(call.ArgumentList.Arguments[3])))
    .Where(c => c.Name is not null && c.KeyParameter is not null)
    .ToList();
// AppHost throws at startup when any key listed in ConfigureAIProvider's foreach is blank.
var requiredKeys = appHostTree.GetCompilationUnitRoot().DescendantNodes()
    .Where(node => node is LocalFunctionStatementSyntax { Identifier.ValueText: "ConfigureAIProvider" }
        or MethodDeclarationSyntax { Identifier.ValueText: "ConfigureAIProvider" })
    .SelectMany(node => node.DescendantNodes().OfType<ForEachStatementSyntax>())
    .Select(loop => loop.Expression.DescendantNodes().OfType<LiteralExpressionSyntax>()
        .Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression))
        .Select(literal => literal.Token.ValueText)
        .ToList())
    .FirstOrDefault(keys => keys.Count > 0) ?? [];
if (capabilities.Count == 0 || requiredKeys.Count == 0)
{
    Info("ConfigureAIProvider calls or their required keys not found; AppHost.cs changed shape. "
        + "Read it and docs/configuration.md.");
}

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
foreach (var (capability, keyParameter) in capabilities)
{
    var provider = configuration[$"AI:{capability}:Provider"];
    Console.WriteLine($"AI {capability} (provider: {(string.IsNullOrWhiteSpace(provider) ? "unset" : provider)})");
    foreach (var key in requiredKeys)
    {
        var value = configuration[$"AI:{capability}:{key}"];
        if (!string.IsNullOrWhiteSpace(value)) Ok($"AI:{capability}:{key} = {value}"); else Missing($"AI:{capability}:{key}");
    }

    var endpoint = configuration[$"AI:{capability}:Endpoint"];
    switch (provider?.ToLowerInvariant())
    {
        case "foundry":
            if (!string.IsNullOrWhiteSpace(endpoint) && !endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Missing($"AI:{capability}:Endpoint must be HTTPS for Foundry");
            }
            if (IsSet($"Parameters:{keyParameter}"))
                Ok($"Parameters:{keyParameter} is set");
            else
                Info($"Parameters:{keyParameter} not set; DefaultAzureCredential will be used (az login)");
            break;
        case "ollama":
            var model = configuration[$"AI:{capability}:Model"] ?? "";
            var models = await OllamaModelsAsync(http, endpoint);
            if (models is null)
            {
                Missing(FindOnPath("ollama") is null
                    ? "ollama is not installed (provider is Ollama); see README prerequisites"
                    : $"Ollama server not reachable at {endpoint}; start Ollama");
            }
            else if (models.Contains(model) || models.Contains($"{model}:latest"))
            {
                Ok($"Ollama model {model} present");
            }
            else
            {
                Missing($"Ollama model {model}; use the README 'Prepare the models' commands");
            }
            break;
        default:
            Missing($"AI:{capability}:Provider must be Foundry or Ollama");
            break;
    }
}

Console.WriteLine();
Info(isDevelopment
    ? "Checks used the Development environment, which 'aspire run' and 'aspire start' use by default."
    : $"Checks used the {environment} environment; 'aspire run' and 'aspire start' default to Development.");
if (problems > 0)
{
    Console.WriteLine($"{problems} item(s) need action.");
    return 1;
}
Console.WriteLine("Setup looks complete. Start the AppHost and verify health.");
return 0;

bool IsSet(string key) => !string.IsNullOrWhiteSpace(configuration[key]);

static string? MethodName(InvocationExpressionSyntax call) => call.Expression switch
{
    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
    SimpleNameSyntax name => name.Identifier.ValueText,
    _ => null,
};

static string? StringLiteral(ArgumentSyntax? argument) =>
    argument?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)
        ? literal.Token.ValueText
        : null;

static async Task<HashSet<string>?> OllamaModelsAsync(HttpClient http, string? endpoint)
{
    if (!Uri.TryCreate(endpoint?.TrimEnd('/') + "/api/tags", UriKind.Absolute, out var tags)) return null;
    try
    {
        using var document = JsonDocument.Parse(await http.GetStringAsync(tags));
        return document.RootElement.GetProperty("models").EnumerateArray()
            .Select(m => m.GetProperty("name").GetString())
            .OfType<string>()
            .ToHashSet();
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
    {
        return null;
    }
}

// Returns null when the tool isn't installed or doesn't finish in time.
static async Task<(int ExitCode, string Output)?> RunAsync(string tool, string arguments, TimeSpan? timeout = null)
{
    var path = FindOnPath(tool);
    if (path is null) return null;
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo(path, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        },
    };
    try
    {
        process.Start();
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return null;
    }
    var output = process.StandardOutput.ReadToEndAsync();
    _ = process.StandardError.ReadToEndAsync();
    using var cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
    try
    {
        await process.WaitForExitAsync(cancel.Token);
    }
    catch (OperationCanceledException)
    {
        process.Kill(entireProcessTree: true);
        return null;
    }
    return (process.ExitCode, await output);
}

static string? FindOnPath(string tool)
{
    var extensions = OperatingSystem.IsWindows()
        ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
        : [""];
    return (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .SelectMany(directory => extensions.Select(extension => Path.Combine(directory, tool + extension)))
        .FirstOrDefault(File.Exists);
}

static string? FindRepoRoot(string start)
{
    for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "aspire.config.json"))) return directory.FullName;
    }
    return null;
}

static string ScriptDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
