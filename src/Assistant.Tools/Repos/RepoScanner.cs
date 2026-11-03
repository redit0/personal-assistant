using System.Text.RegularExpressions;
using Assistant.Core.Repos;
using Assistant.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Assistant.Tools.Repos;

/// <summary>Finds git clones under your scan folders and records where each remote lives locally.</summary>
public sealed partial class RepoScanner(AssistantDbContext db, TimeProvider time, ILogger<RepoScanner> logger)
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "packages", ".vs", ".idea", "dist", "build", "target",
    };

    public sealed record ScanResult(int Found, int Added, int Removed);

    public async Task<ScanResult> ScanAsync(IEnumerable<string> roots, int maxDepth = 4, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var rootList = roots.Where(Directory.Exists).Select(Path.GetFullPath).ToList();
        var found = new Dictionary<string, (string Remote, CodeHost Host, string Name)>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in rootList)
        {
            foreach (var repoPath in FindRepos(root, maxDepth, cancellationToken))
            {
                var remote = ReadOriginUrl(repoPath);
                if (remote is not null)
                {
                    found[repoPath] = (remote, DetectHost(remote), NameFromRemote(remote));
                }
            }
        }

        var existing = await db.RepoMappings.ToListAsync(cancellationToken);
        var added = 0;
        var removed = 0;

        foreach (var (path, info) in found)
        {
            var mapping = existing.FirstOrDefault(m => string.Equals(m.LocalPath, path, StringComparison.OrdinalIgnoreCase));
            if (mapping is null)
            {
                db.RepoMappings.Add(new RepoMapping
                {
                    Name = info.Name, RemoteUrl = info.Remote, LocalPath = path, Host = info.Host, LastSeenAt = now,
                });
                added++;
            }
            else
            {
                mapping.Name = info.Name;
                mapping.RemoteUrl = info.Remote;
                mapping.Host = info.Host;
                mapping.LastSeenAt = now;
            }
        }

        // Forget clones that were deleted, but only under the folders we just scanned.
        foreach (var mapping in existing)
        {
            var underScannedRoot = rootList.Any(r => mapping.LocalPath.StartsWith(r, StringComparison.OrdinalIgnoreCase));
            if (underScannedRoot && !found.ContainsKey(mapping.LocalPath))
            {
                db.RepoMappings.Remove(mapping);
                removed++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Repo scan found {Found} clones ({Added} new, {Removed} removed)", found.Count, added, removed);
        return new ScanResult(found.Count, added, removed);
    }

    public static CodeHost DetectHost(string remote) => remote.ToLowerInvariant() switch
    {
        var r when r.Contains("github.com") => CodeHost.GitHub,
        var r when r.Contains("bitbucket.org") => CodeHost.Bitbucket,
        var r when r.Contains("dev.azure.com") || r.Contains("visualstudio.com") => CodeHost.AzureDevOps,
        _ => CodeHost.Other,
    };

    public static string NameFromRemote(string remote)
    {
        var trimmed = remote.TrimEnd('/');
        var name = trimmed[(trimmed.LastIndexOfAny(['/', ':']) + 1)..];
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private IEnumerable<string> FindRepos(string root, int maxDepth, CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, depth) = pending.Pop();

            var gitPath = Path.Combine(path, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                yield return path;
                continue; // Don't descend into a repo looking for more repos.
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(path);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                logger.LogDebug("Skipping {Path}: {Message}", path, ex.Message);
                continue;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (!name.StartsWith('.') && !SkippedDirectories.Contains(name))
                {
                    pending.Push((child, depth + 1));
                }
            }
        }
    }

    private static string? ReadOriginUrl(string repoPath)
    {
        var gitPath = Path.Combine(repoPath, ".git");
        var gitDir = gitPath;

        // Worktrees and submodules have a .git file pointing at the real git directory.
        if (File.Exists(gitPath))
        {
            var pointer = File.ReadAllText(gitPath).Trim();
            if (!pointer.StartsWith("gitdir:", StringComparison.Ordinal))
            {
                return null;
            }

            gitDir = Path.GetFullPath(Path.Combine(repoPath, pointer["gitdir:".Length..].Trim()));
            var commonDir = Path.Combine(gitDir, "commondir");
            if (File.Exists(commonDir))
            {
                gitDir = Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(commonDir).Trim()));
            }
        }

        var configPath = Path.Combine(gitDir, "config");
        if (!File.Exists(configPath))
        {
            return null;
        }

        var match = OriginUrlPattern().Match(File.ReadAllText(configPath));
        return match.Success ? match.Groups["url"].Value.Trim() : null;
    }

    [GeneratedRegex(@"\[remote\s+""origin""\][^\[]*?^\s*url\s*=\s*(?<url>.+?)\s*$", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex OriginUrlPattern();
}
