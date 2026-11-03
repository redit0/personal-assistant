using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Assistant.Connectors.GitHub;

/// <summary>Borrows the GitHub CLI's login so you don't need a separate token.</summary>
public interface IGitHubCliToken
{
    bool IsAvailable { get; }

    Task<string?> GetTokenAsync(CancellationToken cancellationToken);
}

public sealed class GitHubCliToken(ILogger<GitHubCliToken> logger) : IGitHubCliToken
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private readonly Lazy<string?> _ghPath = new(FindGh);
    private string? _cached;
    private DateTime _cachedAt;

    public bool IsAvailable => _ghPath.Value is not null;

    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null && DateTime.UtcNow - _cachedAt < CacheFor)
        {
            return _cached;
        }

        if (_ghPath.Value is not { } gh)
        {
            return null;
        }

        var psi = new ProcessStartInfo(gh, "auth token --hostname github.com")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);

        if (process.ExitCode != 0)
        {
            logger.LogInformation("gh auth token exited with {Code}; the GitHub CLI is probably not signed in", process.ExitCode);
            return null;
        }

        _cached = output.Trim();
        _cachedAt = DateTime.UtcNow;
        return _cached;
    }

    private static string? FindGh()
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var candidates = paths.Select(p => Path.Combine(p.Trim('"'), "gh.exe"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }
}
