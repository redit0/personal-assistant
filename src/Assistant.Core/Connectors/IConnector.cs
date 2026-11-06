using Assistant.Core.Items;

namespace Assistant.Core.Connectors;

/// <summary>
/// Pulls the current set of things that need your attention from one source. Connectors don't write
/// items themselves; they return a snapshot and the sync engine reconciles it with the item list.
/// </summary>
public interface IConnector
{
    /// <summary>Stable id, also used as <see cref="Item.Source"/>, such as "github".</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>How often to poll.</summary>
    TimeSpan Interval { get; }

    /// <summary>Why the connector can't run (turned off, missing token), or null when it's ready.</summary>
    string? NotReadyReason();

    Task<ConnectorSnapshot> FetchAsync(CancellationToken cancellationToken);
}

/// <summary>One open thing at the source, such as a PR waiting on your review.</summary>
/// <param name="Scope">
/// The part of the source this came from, such as an Azure DevOps organization. When a scope fails to
/// load, items from it are left alone instead of being closed as if they had disappeared.
/// </param>
/// <param name="ExternalId">Unique within the connector, such as "owner/repo#12" or "PROJ-123".</param>
/// <param name="RequestStartedAt">Conversations only: when the current request began. Null for sources whose items are one thing for life, such as a ticket.</param>
/// <param name="ReopenedAt">Conversations only: when triage last saw that work you had marked done needs doing again.</param>
/// <param name="LatestMessageAt">Conversations only: when the newest message behind this item was sent.</param>
public sealed record SourceItem(
    string Scope,
    string ExternalId,
    string Title,
    ItemType Type,
    string Url,
    DateTime UpdatedAt,
    Priority Priority = Priority.Normal,
    DateTime? DueAt = null,
    string? Requester = null,
    SenderTrust Trust = SenderTrust.Organization,
    string? Details = null,
    string? SourceContent = null,
    string? RepoHint = null,
    double? DueConfidence = null,
    string? Reasoning = null,
    bool NeedsReview = false,
    DateTime? RequestStartedAt = null,
    DateTime? ReopenedAt = null,
    DateTime? LatestMessageAt = null);

public sealed record ConnectorSnapshot(
    IReadOnlyList<SourceItem> Items,
    IReadOnlySet<string> FailedScopes,
    IReadOnlyList<string> Warnings)
{
    public static ConnectorSnapshot Complete(IReadOnlyList<SourceItem> items) =>
        new(items, new HashSet<string>(), []);
}

/// <summary>Thrown for problems you can fix, such as a rejected token. The message is shown in Settings.</summary>
public sealed class ConnectorException(string message, Exception? inner = null) : Exception(message, inner);
