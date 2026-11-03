using Assistant.Core.Connectors;
using Assistant.Core.Items;
using Assistant.Data;
using Assistant.Tools.Items;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Tools.Sync;

public sealed record SyncResult(int Added, int Updated, int Closed, int Reopened, IReadOnlyList<string> AddedTitles, int Merged = 0)
{
    public bool Changed => Added + Updated + Closed + Reopened + Merged > 0;
}

/// <summary>
/// Makes the item list match a connector's snapshot:
/// new source items become items, changed ones are updated, ones that vanished are closed, and ones a
/// connector closed earlier reopen if they come back. Items you closed or dismissed yourself stay that way.
/// A new item that repeats an open one, from any source, is merged into it instead of listed twice.
/// </summary>
public sealed class SyncReconciler(AssistantDbContext db, TimeProvider time, IDuplicateDetector duplicates)
{
    /// <summary>How many open items the duplicate check compares against, most recently updated first.</summary>
    private const int MaxDuplicateCandidates = 150;

    public async Task<SyncResult> ApplyAsync(string source, ConnectorSnapshot snapshot, string sourceName, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var existing = await db.Items.Include(i => i.Notes)
            .Where(i => i.Source == source)
            .ToListAsync(cancellationToken);
        var byExternalId = existing
            .Where(i => i.ExternalId is not null)
            .GroupBy(i => i.ExternalId!)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(i => i.UpdatedAt).First());

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var addedTitles = new List<string>();
        int added = 0, updated = 0, closed = 0, reopened = 0, merged = 0;
        List<Item>? openItems = null;

        foreach (var src in snapshot.Items)
        {
            if (!seen.Add(src.ExternalId))
            {
                continue; // The connector should dedupe, but don't create two items if it didn't.
            }

            if (!byExternalId.TryGetValue(src.ExternalId, out var item) || StartsAfterYouClosed(src, item))
            {
                item = NewItem(source, src, now);
                openItems ??= await OpenItemsAsync(cancellationToken);
                var match = await duplicates.FindAsync(src, sourceName, openItems, cancellationToken);
                var into = match is null ? null : openItems.FirstOrDefault(i => i.Id == match.ItemId);

                db.Items.Add(item);
                if (into is null)
                {
                    addedTitles.Add(src.Title);
                    added++;

                    // Save now so a later item in this same sync can be checked against this one.
                    await db.SaveChangesAsync(cancellationToken);
                    openItems.Insert(0, item);
                }
                else
                {
                    ItemMerger.Merge(item, into, source, sourceName, match!.AddedContext, now);
                    merged++;
                }

                continue;
            }

            if (item.Status == ItemStatus.Merged)
            {
                // A merged duplicate that comes back at its source reopens the item it was merged into.
                if (item.ClosedBySync)
                {
                    item.ClosedBySync = false;
                    item.CompletedAt = null;
                    if (await PrimaryAsync(item, cancellationToken) is { ClosedBySync: true, Status: ItemStatus.Done } primary)
                    {
                        Reopen(primary, $"Reopened: the same request is open again in {sourceName}.", source, now);
                        reopened++;
                    }
                }
            }
            else if (item.ClosedBySync && item.Status is ItemStatus.Done)
            {
                Reopen(item, $"Reopened: it's open again in {sourceName}.", source, now);
                reopened++;
            }

            // A conversation you closed keeps what you closed. Copying later judgments over it would rewrite its history.
            if (!(src.RequestStartedAt is not null && ClosedByYou(item)) && CopyFields(src, item))
            {
                item.UpdatedAt = now;
                updated++;
            }
        }

        foreach (var item in existing)
        {
            if (item.ExternalId is null || seen.Contains(item.ExternalId) || InFailedScope(item, snapshot))
            {
                continue;
            }

            if (item.Status == ItemStatus.Merged)
            {
                // Resolving any copy of the request resolves the item that holds them all.
                if (!item.ClosedBySync)
                {
                    item.ClosedBySync = true;
                    item.CompletedAt = now;
                    if (await PrimaryAsync(item, cancellationToken) is { Status: ItemStatus.Open or ItemStatus.Snoozed } primary)
                    {
                        Close(primary, $"Closed automatically: the same request was resolved in {sourceName}.", source, now);
                        closed++;
                    }
                }

                continue;
            }

            if (item.Status is not (ItemStatus.Open or ItemStatus.Snoozed))
            {
                continue;
            }

            Close(item, $"Closed automatically: it's no longer open or waiting on you in {sourceName}.", source, now);
            closed++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new SyncResult(added, updated, closed, reopened, addedTitles, merged);
    }

    private static bool ClosedByYou(Item item) => item.Status is ItemStatus.Done or ItemStatus.Dismissed && !item.ClosedBySync;

    /// <summary>A different request in a conversation whose item you closed is new work, not that item coming back.</summary>
    private static bool StartsAfterYouClosed(SourceItem src, Item item) =>
        ClosedByYou(item) && src.RequestStartedAt > item.CompletedAt;

    private static Item NewItem(string source, SourceItem src, DateTime now) => new()
    {
        Title = src.Title,
        Type = src.Type,
        Priority = src.Priority,
        DueAt = src.DueAt,
        Requester = src.Requester,
        Trust = src.Trust,
        Source = source,
        ExternalId = src.ExternalId,
        SourceUrl = src.Url,
        Details = src.Details,
        SourceContent = src.SourceContent,
        RepoHint = src.RepoHint,
        ExternalUpdatedAt = src.UpdatedAt,
        DueConfidence = src.DueConfidence,
        Reasoning = src.Reasoning,
        NeedsReview = src.NeedsReview,
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>Open and snoozed items from every source, the ones a new item could repeat.</summary>
    private Task<List<Item>> OpenItemsAsync(CancellationToken cancellationToken) =>
        db.Items.Include(i => i.Notes).Include(i => i.Artifacts)
            .Where(i => i.Status == ItemStatus.Open || i.Status == ItemStatus.Snoozed)
            .OrderByDescending(i => i.UpdatedAt)
            .Take(MaxDuplicateCandidates)
            .ToListAsync(cancellationToken);

    private async Task<Item?> PrimaryAsync(Item duplicate, CancellationToken cancellationToken) =>
        duplicate.MergedIntoId is { } id
            ? await db.Items.Include(i => i.Notes).FirstOrDefaultAsync(i => i.Id == id, cancellationToken)
            : null;

    private static void Close(Item item, string note, string source, DateTime now)
    {
        item.Status = ItemStatus.Done;
        item.ClosedBySync = true;
        item.SnoozedUntil = null;
        item.CompletedAt = now;
        item.UpdatedAt = now;
        item.Notes.Add(Note(note, source, now));
    }

    private static void Reopen(Item item, string note, string source, DateTime now)
    {
        item.Status = ItemStatus.Open;
        item.ClosedBySync = false;
        item.CompletedAt = null;
        item.UpdatedAt = now;
        item.Notes.Add(Note(note, source, now));
    }

    /// <summary>Copies source-owned fields. Your own edits to title, priority, or due date are overwritten only when the source changes.</summary>
    private static bool CopyFields(SourceItem src, Item item)
    {
        if (item.ExternalUpdatedAt == src.UpdatedAt && item.Type == src.Type)
        {
            return false;
        }

        item.Title = src.Title;
        item.Type = src.Type;
        item.Priority = src.Priority;
        item.DueAt = src.DueAt;
        item.Requester = src.Requester;
        item.Trust = src.Trust;
        item.SourceUrl = src.Url;
        item.Details = src.Details;
        item.SourceContent = src.SourceContent;
        item.RepoHint = src.RepoHint ?? item.RepoHint;
        item.ExternalUpdatedAt = src.UpdatedAt;
        item.DueConfidence = src.DueConfidence;
        item.Reasoning = src.Reasoning ?? item.Reasoning;

        // Once you keep an item it stays kept, even if the source is still unsure.
        item.NeedsReview = item.NeedsReview && src.NeedsReview;
        return true;
    }

    private static bool InFailedScope(Item item, ConnectorSnapshot snapshot) =>
        snapshot.FailedScopes.Count > 0 &&
        snapshot.FailedScopes.Any(scope => item.ExternalId!.StartsWith(scope + ":", StringComparison.OrdinalIgnoreCase));

    private static ItemNote Note(string text, string source, DateTime now) =>
        new() { Text = text, Author = source, CreatedAt = now };
}
