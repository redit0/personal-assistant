using Assistant.Core.Connectors;
using Assistant.Core.Items;

namespace Assistant.Tools.Sync;

/// <summary>An existing item that a new one repeats.</summary>
/// <param name="ItemId">The existing item to merge into.</param>
/// <param name="AddedContext">What the new one adds that the existing item doesn't say, or null.</param>
/// <param name="Reason">Why they're the same request, for the note.</param>
/// <param name="Reopen">When the existing item is done: whether the new one shows the work needs doing again.</param>
public sealed record DuplicateMatch(int ItemId, string? AddedContext, string? Reason, bool Reopen = false);

/// <summary>
/// Decides whether a new item is the same request as an existing one, such as an email ask that someone
/// repeats in Slack, a Jira ticket for something first asked by email, or a notification about a pull request
/// that was just merged. The candidates are open items followed by ones finished lately.
/// </summary>
public interface IDuplicateDetector
{
    Task<DuplicateMatch?> FindAsync(SourceItem candidate, string sourceName, IReadOnlyList<Item> candidates, CancellationToken cancellationToken);
}

/// <summary>Used when no AI provider is set up: every new item stays its own.</summary>
public sealed class NoDuplicateDetector : IDuplicateDetector
{
    public Task<DuplicateMatch?> FindAsync(SourceItem candidate, string sourceName, IReadOnlyList<Item> candidates, CancellationToken cancellationToken) =>
        Task.FromResult<DuplicateMatch?>(null);
}
