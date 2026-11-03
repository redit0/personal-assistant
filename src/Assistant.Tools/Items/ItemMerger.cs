using Assistant.Core.Items;

namespace Assistant.Tools.Items;

/// <summary>
/// Folds a duplicate into the item it repeats. The duplicate stays in the database, hidden, so its source keeps
/// updating it in place; the kept item takes the earlier due date, the higher priority, and the less trusted sender.
/// </summary>
public static class ItemMerger
{
    public static void Merge(Item duplicate, Item into, string author, string sourceName, string? addedContext, DateTime now)
    {
        duplicate.Status = ItemStatus.Merged;
        duplicate.MergedIntoId = into.Id;
        duplicate.SnoozedUntil = null;
        duplicate.UpdatedAt = now;

        if (duplicate.DueAt is { } due && (into.DueAt is null || due < into.DueAt))
        {
            into.DueAt = due;
            into.DueConfidence = duplicate.DueConfidence;
        }

        if (duplicate.Priority > into.Priority)
        {
            into.Priority = duplicate.Priority;
        }

        // Anything from a less trusted sender makes the whole item less trusted, so approvals stay as strict.
        if (duplicate.Trust > into.Trust)
        {
            into.Trust = duplicate.Trust;
        }

        into.Requester ??= duplicate.Requester;
        into.RepoHint ??= duplicate.RepoHint;

        var text = $"Merged in the same request from {sourceName}: \"{duplicate.Title}\"" +
            (duplicate.Requester is { } who && who != into.Requester ? $" (from {who})." : ".") +
            (string.IsNullOrWhiteSpace(addedContext) ? "" : $" New there: {addedContext.Trim()}");
        into.Notes.Add(new ItemNote { Text = text, Author = author, CreatedAt = now });

        if (duplicate.SourceUrl is { } url && into.SourceUrl != url && !into.Artifacts.Any(a => a.Url == url))
        {
            into.Artifacts.Add(new ItemArtifact
            {
                Url = url,
                Kind = "duplicate",
                Description = $"{sourceName}: {duplicate.Title}",
                AddedBy = author,
                CreatedAt = now,
            });
        }

        into.UpdatedAt = now;
    }

    /// <summary>Undoes a merge: the duplicate becomes its own open item again.</summary>
    public static void Split(Item duplicate, Item? from, string author, DateTime now)
    {
        duplicate.Status = ItemStatus.Open;
        duplicate.MergedIntoId = null;
        duplicate.CompletedAt = null;
        duplicate.ClosedBySync = false;
        duplicate.UpdatedAt = now;
        duplicate.Notes.Add(new ItemNote { Text = from is null ? "Split out as its own item." : $"Split out of item {from.Id}.", Author = author, CreatedAt = now });

        if (from is not null)
        {
            from.Artifacts.RemoveAll(a => a.Kind == "duplicate" && a.Url == duplicate.SourceUrl);
            from.Notes.Add(new ItemNote { Text = $"Split \"{duplicate.Title}\" back out as item {duplicate.Id}.", Author = author, CreatedAt = now });
            from.UpdatedAt = now;
        }
    }
}
