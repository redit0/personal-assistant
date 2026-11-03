namespace Assistant.Core.Actions;

/// <summary>A CI check or build on a commit. State is "pending", "success", "failure", or "skipped".</summary>
public sealed record CheckStatus(string Name, string State, string? Url = null);

/// <summary>A pull request as the shipping tools see it, the same shape on every host.</summary>
/// <param name="State">"open", "merged", "closed", or "declined".</param>
/// <param name="HeadCommit">The latest commit on the source branch.</param>
/// <param name="MergeCommit">The commit the merge produced, once merged. Deployments are tracked by this one.</param>
/// <param name="Mergeable">Whether the host says it can merge now; null when it hasn't worked that out yet.</param>
/// <param name="Blockers">Why it can't merge yet, in the host's words: conflicts, failing checks, missing approvals.</param>
public sealed record PullRequestInfo(
    string Url,
    string Title,
    string State,
    string SourceBranch,
    string TargetBranch,
    string? HeadCommit,
    string? MergeCommit,
    bool? Mergeable,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<CheckStatus> Checks,
    IReadOnlyList<string> ApprovedBy,
    IReadOnlyList<string> ChangesRequestedBy,
    string? Author);

/// <summary>How to merge. Null means the host's or repo's default.</summary>
public enum MergeStrategy
{
    Merge,
    Squash,
    Rebase,
}

public static class CheckStates
{
    public const string Pending = "pending";
    public const string Success = "success";
    public const string Failure = "failure";
    public const string Skipped = "skipped";

    /// <summary>The overall state of a set of checks: failure wins, then pending; no checks counts as success.</summary>
    public static string Overall(IEnumerable<CheckStatus> checks)
    {
        var states = checks.Select(c => c.State).ToList();
        return states.Contains(Failure) ? Failure : states.Contains(Pending) ? Pending : Success;
    }
}
