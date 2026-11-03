using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Assistant.Core.Connectors;
using Microsoft.Extensions.Logging;

namespace Assistant.Connectors.Microsoft;

/// <summary>
/// Gets Azure DevOps and Azure Resource Manager tokens from the Azure CLI. The CLI is already approved in
/// nearly every tenant, so this works where you're a guest without the client approving our app.
/// You sign in once per tenant with "az login --tenant ...".
/// </summary>
public interface IAzureCliToken
{
    bool IsAvailable { get; }

    Task<string> GetTokenAsync(string tenantId, CancellationToken cancellationToken);

    /// <summary>A token for another resource, such as <see cref="AzureCliToken.ManagementResource"/>.</summary>
    Task<string> GetTokenAsync(string tenantId, string resource, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <summary>The tenants the CLI is signed in to.</summary>
    Task<IReadOnlyList<string>> ListTenantsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}

public sealed partial class AzureCliToken(ILogger<AzureCliToken> logger) : IAzureCliToken
{
    public const string AzureDevOpsResource = "499b84ac-1321-427f-aa17-267ca6975798";
    public const string ManagementResource = "https://management.azure.com";
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly Lazy<string?> _azPath = new(FindAz);
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresOn)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public bool IsAvailable => _azPath.Value is not null;

    public static bool IsValidTenant(string tenantId) => TenantPattern().IsMatch(tenantId);

    public Task<string> GetTokenAsync(string tenantId, CancellationToken cancellationToken) =>
        GetTokenAsync(tenantId, AzureDevOpsResource, cancellationToken);

    public async Task<string> GetTokenAsync(string tenantId, string resource, CancellationToken cancellationToken)
    {
        // The tenant goes on a command line, so only allow GUIDs and domain names.
        if (!IsValidTenant(tenantId))
        {
            throw new ConnectorException($"'{tenantId}' isn't a valid tenant ID or domain.");
        }

        var key = $"{tenantId}|{resource}";
        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresOn - RefreshMargin > DateTimeOffset.UtcNow)
        {
            return cached.Token;
        }

        var (exitCode, stdout, stderr) = await RunAsync(
            ["account", "get-access-token", "--resource", resource, "--tenant", tenantId, "--output", "json"],
            $"get a token for {tenantId}", tenantId, cancellationToken);
        if (exitCode != 0)
        {
            throw new ConnectorException(Explain(tenantId, stderr));
        }

        using var doc = JsonDocument.Parse(stdout);
        var token = doc.RootElement.GetProperty("accessToken").GetString()!;
        var expiresOn = doc.RootElement.TryGetProperty("expires_on", out var e) && e.TryGetInt64(out var epoch)
            ? DateTimeOffset.FromUnixTimeSeconds(epoch)
            : DateTimeOffset.UtcNow.AddMinutes(30);

        _cache[key] = (token, expiresOn);
        return token;
    }

    public async Task<IReadOnlyList<string>> ListTenantsAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            return [];
        }

        var (exitCode, stdout, stderr) = await RunAsync(["account", "list", "--query", "[].tenantId", "--output", "json"], "list your tenants", null, cancellationToken);
        if (exitCode != 0)
        {
            logger.LogInformation("az account list failed: {Error}", stderr.Trim());
            return [];
        }

        using var doc = JsonDocument.Parse(stdout);
        return [.. doc.RootElement.EnumerateArray().Select(t => t.GetString()).OfType<string>().Where(IsValidTenant).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string[] arguments, string what, string? tenantId, CancellationToken cancellationToken)
    {
        var az = _azPath.Value ?? throw new ConnectorException("The Azure CLI isn't installed. Install it, then run az login" + (tenantId is null ? "" : $" --tenant {tenantId}"));

        var psi = new ProcessStartInfo(az)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new ConnectorException("Couldn't start the Azure CLI.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new ConnectorException($"The Azure CLI took too long to {what}.");
        }
    }

    private string Explain(string tenantId, string stderr)
    {
        var firstError = stderr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal))
            ?? stderr.Trim();
        logger.LogInformation("az get-access-token failed for {Tenant}: {Error}", tenantId, firstError);

        var needsLogin = firstError.Contains("az login", StringComparison.OrdinalIgnoreCase) ||
            firstError.Contains("AADSTS", StringComparison.Ordinal) ||
            firstError.Contains("expired", StringComparison.OrdinalIgnoreCase);

        return needsLogin
            ? $"The Azure CLI isn't signed in to {tenantId}, or the sign-in expired. Run: az login --tenant {tenantId}"
            : $"Azure CLI: {(firstError.Length > 300 ? firstError[..300] : firstError)}";
    }

    private static string? FindAz()
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var candidates = paths.Select(p => Path.Combine(p.Trim('"'), "az.cmd"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"));
        return candidates.FirstOrDefault(File.Exists);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.-]{0,253}$")]
    private static partial Regex TenantPattern();
}
