using System.Text.RegularExpressions;

namespace Assistant.Core.Actions;

/// <summary>A repository on a code host, worked out from a git remote URL.</summary>
public abstract record RepoRef;

public sealed record GitHubRepo(string Owner, string Name) : RepoRef;

public sealed record BitbucketRepo(string Workspace, string Name) : RepoRef;

public sealed record AzureDevOpsRepo(string Organization, string Project, string Name) : RepoRef;

/// <summary>A pull request, issue, work item, or ticket, worked out from its web link.</summary>
public abstract record SourceRef;

public sealed record GitHubIssueOrPull(string Owner, string Repo, int Number, bool IsPull) : SourceRef;

public sealed record BitbucketPull(string Workspace, string Repo, int Id) : SourceRef;

public sealed record AzureDevOpsPull(string Organization, string Project, string Repo, int Id) : SourceRef;

public sealed record AzureDevOpsWorkItem(string Organization, string Project, int Id) : SourceRef;

public sealed record JiraIssue(Uri Site, string Key) : SourceRef;

/// <summary>Parses web links and git remotes for the code hosts and trackers the assistant can act on.</summary>
public static partial class SourceLinks
{
    public static SourceRef? Parse(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        var path = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/').Split('/');

        if (host == "github.com" && path.Length >= 4 && int.TryParse(path[3], out var number) && path[2] is "pull" or "issues")
        {
            return new GitHubIssueOrPull(path[0], path[1], number, path[2] == "pull");
        }

        if (host == "bitbucket.org" && path.Length >= 4 && path[2] == "pull-requests" && int.TryParse(path[3], out var bbId))
        {
            return new BitbucketPull(path[0], path[1], bbId);
        }

        if (AzureDevOpsPath(host, path) is { } ado)
        {
            var (org, rest) = ado;
            if (rest.Length >= 5 && rest[1] == "_git" && rest[3] == "pullrequest" && int.TryParse(rest[4], out var prId))
            {
                return new AzureDevOpsPull(org, rest[0], rest[2], prId);
            }

            if (rest.Length >= 4 && rest[1] == "_workitems" && rest[2] == "edit" && int.TryParse(rest[3], out var wiId))
            {
                return new AzureDevOpsWorkItem(org, rest[0], wiId);
            }
        }

        if (host.EndsWith(".atlassian.net", StringComparison.Ordinal) && path.Length >= 2 && path[0] == "browse" && JiraKey().IsMatch(path[1]))
        {
            return new JiraIssue(new Uri($"{uri.Scheme}://{uri.Host}/"), path[1].ToUpperInvariant());
        }

        return null;
    }

    /// <summary>
    /// Parses a git remote: https or ssh forms for GitHub, Bitbucket, and Azure DevOps (including the old
    /// visualstudio.com and the ssh.dev.azure.com v3 forms).
    /// </summary>
    public static RepoRef? ParseRemote(string? remote)
    {
        if (string.IsNullOrWhiteSpace(remote))
        {
            return null;
        }

        remote = remote.Trim();

        // git@github.com:owner/repo.git and git@ssh.dev.azure.com:v3/org/project/repo
        var scp = ScpRemote().Match(remote);
        string host;
        string[] path;
        if (scp.Success)
        {
            host = scp.Groups["host"].Value.ToLowerInvariant();
            path = scp.Groups["path"].Value.Trim('/').Split('/');
        }
        else if (Uri.TryCreate(remote, UriKind.Absolute, out var uri))
        {
            host = uri.Host.ToLowerInvariant();
            path = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/').Split('/');
        }
        else
        {
            return null;
        }

        // SSH host aliases from ~/.ssh/config, used to pick a key per account: git@github.com-work:owner/repo.git.
        host = new[] { "github.com", "bitbucket.org", "ssh.dev.azure.com", "vs-ssh.visualstudio.com" }
            .FirstOrDefault(known => host.StartsWith(known + "-", StringComparison.Ordinal) || host.StartsWith(known + "_", StringComparison.Ordinal)) ?? host;

        if (path.Length > 0)
        {
            path[^1] = path[^1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? path[^1][..^4] : path[^1];
        }

        if (host == "github.com" && path.Length >= 2)
        {
            return new GitHubRepo(path[0], path[1]);
        }

        if (host == "bitbucket.org" && path.Length >= 2)
        {
            return new BitbucketRepo(path[0], path[1]);
        }

        if (host is "ssh.dev.azure.com" or "vs-ssh.visualstudio.com" && path.Length >= 4 && path[0] == "v3")
        {
            return new AzureDevOpsRepo(path[1], path[2], path[3]);
        }

        if (AzureDevOpsPath(host, path) is { } ado && ado.Path.Length >= 3 && ado.Path[1] == "_git")
        {
            return new AzureDevOpsRepo(ado.Organization, ado.Path[0], ado.Path[2]);
        }

        return null;
    }

    /// <summary>Web link for a repository, for messages and item links.</summary>
    public static string WebUrl(RepoRef repo) => repo switch
    {
        GitHubRepo g => $"https://github.com/{g.Owner}/{g.Name}",
        BitbucketRepo b => $"https://bitbucket.org/{b.Workspace}/{b.Name}",
        AzureDevOpsRepo a => $"https://dev.azure.com/{a.Organization}/{Uri.EscapeDataString(a.Project)}/_git/{Uri.EscapeDataString(a.Name)}",
        _ => "",
    };

    /// <summary>Splits dev.azure.com/{org}/... and {org}.visualstudio.com/... into the organization and the rest of the path.</summary>
    private static (string Organization, string[] Path)? AzureDevOpsPath(string host, string[] path)
    {
        if (host == "dev.azure.com" && path.Length >= 2)
        {
            return (path[0], path[1..]);
        }

        if (host.EndsWith(".visualstudio.com", StringComparison.Ordinal) && !host.StartsWith("vs-ssh.", StringComparison.Ordinal))
        {
            var org = host[..host.IndexOf('.')];
            var rest = path.Length > 0 && path[0].Equals("DefaultCollection", StringComparison.OrdinalIgnoreCase) ? path[1..] : path;
            return (org, rest);
        }

        return null;
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*-\d+$")]
    private static partial Regex JiraKey();

    [GeneratedRegex(@"^(?:[^@/]+@)?(?<host>[A-Za-z0-9.-]+):(?<path>(?!//).+)$")]
    private static partial Regex ScpRemote();
}

/// <summary>What an action did, for the calling agent and the item's linked artifacts.</summary>
/// <param name="Url">Web link to what was created or changed.</param>
/// <param name="Summary">One sentence, such as "Opened pull request #14 in acme/web".</param>
/// <param name="Kind">Artifact kind for the item link, such as pull_request or comment.</param>
public sealed record ActionResult(string Url, string Summary, string Kind);

/// <summary>A status a ticket can move to from where it is.</summary>
/// <param name="Status">The status name, which change_status accepts.</param>
/// <param name="Category">The tracker's grouping, such as "To Do", "In Progress", or "Done" in Jira, or "Proposed", "InProgress", "Resolved", or "Completed" in Azure DevOps.</param>
/// <param name="Transition">Jira's name for the move, when it differs from the status.</param>
public sealed record StatusOption(string Status, string? Category, string? Transition = null);

public sealed record TicketStatuses(string? Current, IReadOnlyList<StatusOption> Options);

public enum ReviewDecision
{
    Approve,
    RequestChanges,
    Comment,
}
