using Assistant.Core.Connectors;
using Assistant.Core.Items;

namespace Assistant.Tools.Sync;

/// <summary>An open item that a new one repeats.</summary>
/// <param name="ItemId">The existing item to merge into.</param>
/// <param name="AddedContext">What the new one adds that the existing item doesn't say, or null.</param>
/// <param name="Reason">Why they're the same request, for the note.</param>
public sealed record DuplicateMatch(int ItemId, string? AddedContext, string? Reason);

/// <summary>
/// Decides whether a new item is the same request as one already open, such as an email ask that someone
/// repeats in Slack, or a Jira ticket for something first asked by email.
/// </summary>
public interface IDuplicateDetector
{
    Task<DuplicateMatch?> FindAsync(SourceItem candidate, string sourceName, IReadOnlyList<Item> openItems, CancellationToken cancellationToken);
}

/// <summary>Used when no AI provider is set up: every new item stays its own.</summary>
public sealed class NoDuplicateDetector : IDuplicateDetector
{
    public Task<DuplicateMatch?> FindAsync(SourceItem candidate, string sourceName, IReadOnlyList<Item> openItems, CancellationToken cancellationToken) =>
        Task.FromResult<DuplicateMatch?>(null);
}
