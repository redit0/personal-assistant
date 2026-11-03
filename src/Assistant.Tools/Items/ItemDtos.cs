using Assistant.Core.Items;
using Assistant.Core.Text;
using Assistant.Tools.Catalog;

namespace Assistant.Tools.Items;

public sealed record ItemSummary(
    int Id,
    string Title,
    ItemType Type,
    ItemStatus Status,
    Priority Priority,
    DateTime? DueAt,
    string? Requester,
    string Source,
    string? SourceUrl,
    string? RepoHint,
    DateTime? SnoozedUntil,
    bool NeedsReview)
{
    public static ItemSummary From(Item i) => new(
        i.Id, i.Title, i.Type, i.Status, i.Priority, i.DueAt, i.Requester, i.Source, i.SourceUrl, i.RepoHint, i.SnoozedUntil, i.NeedsReview);
}

public sealed record ItemDetail(
    int Id,
    string Title,
    string? Details,
    ItemType Type,
    ItemStatus Status,
    Priority Priority,
    DateTime? DueAt,
    double? DueConfidence,
    string? Requester,
    SenderTrust Trust,
    string Source,
    string? SourceUrl,
    string? RepoHint,
    string? Reasoning,
    string? SourceContent,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? SnoozedUntil,
    DateTime? CompletedAt,
    DateTime? RemindAt,
    string? ReminderNote,
    IReadOnlyList<NoteDto> Notes,
    IReadOnlyList<ArtifactDto> Artifacts,
    int? MergedIntoId = null,
    IReadOnlyList<DuplicateDto>? MergedFrom = null)
{
    public static ItemDetail From(Item i) => new(
        i.Id, i.Title, SafeLinks.Unwrap(i.Details), i.Type, i.Status, i.Priority, i.DueAt, i.DueConfidence, i.Requester, i.Trust,
        i.Source, i.SourceUrl, i.RepoHint, i.Reasoning,
        UntrustedContent.Wrap(i.Source, SafeLinks.Unwrap(i.SourceContent)),
        i.CreatedAt, i.UpdatedAt, i.SnoozedUntil, i.CompletedAt, i.RemindAt, i.ReminderNote,
        [.. i.Notes.OrderBy(n => n.CreatedAt).Select(NoteDto.From)],
        [.. i.Artifacts.OrderBy(a => a.CreatedAt).Select(ArtifactDto.From)],
        i.MergedIntoId,
        [.. i.Duplicates.OrderBy(d => d.CreatedAt).Select(DuplicateDto.From)]);
}

/// <summary>
/// The same request as it arrived somewhere else, merged into this item. Its details and source text are part of
/// the request: read them for context the main source doesn't have.
/// </summary>
public sealed record DuplicateDto(
    int Id,
    string Title,
    string Source,
    string? SourceUrl,
    string? Requester,
    DateTime? DueAt,
    string? Details,
    string? SourceContent,
    DateTime CreatedAt)
{
    public static DuplicateDto From(Item d) => new(
        d.Id, d.Title, d.Source, d.SourceUrl, d.Requester, d.DueAt, SafeLinks.Unwrap(d.Details),
        UntrustedContent.Wrap(d.Source, SafeLinks.Unwrap(d.SourceContent)), d.CreatedAt);
}

public sealed record NoteDto(string Text, string Author, DateTime CreatedAt)
{
    public static NoteDto From(ItemNote n) => new(n.Text, n.Author, n.CreatedAt);
}

public sealed record ArtifactDto(string Url, string? Kind, string? Description, string AddedBy, DateTime CreatedAt)
{
    public static ArtifactDto From(ItemArtifact a) => new(a.Url, a.Kind, a.Description, a.AddedBy, a.CreatedAt);
}

/// <summary>Status filter for list_items.</summary>
public enum ItemListStatus
{
    /// <summary>Open and snoozed items.</summary>
    Active,
    Open,
    Snoozed,
    Done,
    Dismissed,
    All,

    /// <summary>Open items the triage agent wasn't sure about.</summary>
    NeedsReview,
}
