using System.ComponentModel;
using Assistant.Core.Items;
using Assistant.Core.Tools;
using Assistant.Data;
using Assistant.Tools.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Tools.Items;

/// <summary>Read and manage the assistant's item list.</summary>
public sealed class ItemTools(AssistantDbContext db, ToolCallContext context, TimeProvider time)
{
    private const int MaxLimit = 1000;
    private const string Escape = "\\";

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    [AssistantTool("list_items", ToolCategory.Read, Title = "List items")]
    [Description("Lists tracked items (asks, commitments, reviews, assigned tickets, events, things you're waiting on, tasks). " +
        "Overdue and soonest-due items come first, then higher priority. Use get_item for full details.")]
    public async Task<IReadOnlyList<ItemSummary>> ListItemsAsync(
        [Description("Which items to include. Active means open or snoozed. NeedsReview means open items the triage agent was unsure about.")] ItemListStatus status = ItemListStatus.Active,
        [Description("Only items of this type.")] ItemType? type = null,
        [Description("Only items from this source, such as github or outlook.")] string? source = null,
        [Description("Only items due within this many days from now, including overdue ones.")] int? dueWithinDays = null,
        [Description("Only items whose requester contains this text.")] string? person = null,
        [Description("Maximum number of items to return (1-1000).")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var query = db.Items.AsNoTracking();

        query = status switch
        {
            ItemListStatus.Active => query.Where(i => i.Status == ItemStatus.Open || i.Status == ItemStatus.Snoozed),
            ItemListStatus.Open => query.Where(i => i.Status == ItemStatus.Open),
            ItemListStatus.Snoozed => query.Where(i => i.Status == ItemStatus.Snoozed),
            ItemListStatus.Done => query.Where(i => i.Status == ItemStatus.Done),
            ItemListStatus.Dismissed => query.Where(i => i.Status == ItemStatus.Dismissed),
            ItemListStatus.NeedsReview => query.Where(i => i.Status == ItemStatus.Open && i.NeedsReview),
            _ => query.Where(i => i.Status != ItemStatus.Merged),
        };

        if (type is not null)
        {
            query = query.Where(i => i.Type == type);
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            query = query.Where(i => i.Source == source);
        }

        if (dueWithinDays is not null)
        {
            var cutoff = Now.AddDays(dueWithinDays.Value);
            query = query.Where(i => i.DueAt != null && i.DueAt <= cutoff);
        }

        if (!string.IsNullOrWhiteSpace(person))
        {
            query = query.Where(i => i.Requester != null && EF.Functions.Like(i.Requester, Like(person), Escape));
        }

        var items = await query
            .OrderBy(i => i.DueAt == null)
            .ThenBy(i => i.DueAt)
            .ThenByDescending(i => i.Priority)
            .ThenByDescending(i => i.UpdatedAt)
            .Take(Math.Clamp(limit, 1, MaxLimit))
            .ToListAsync(cancellationToken);

        return [.. items.Select(ItemSummary.From)];
    }

    [AssistantTool("get_item", ToolCategory.Read, Title = "Get item")]
    [Description("Gets one item with its details, notes, linked artifacts, and the original source text. " +
        UntrustedContent.Notice)]
    public async Task<ItemDetail> GetItemAsync(
        [Description("The item's ID.")] int itemId,
        CancellationToken cancellationToken = default) =>
        ItemDetail.From(await LoadAsync(itemId, tracking: false, cancellationToken));

    [AssistantTool("search", ToolCategory.Read, Title = "Search items")]
    [Description("Searches item titles, details, requesters, notes, and source text. Every word must match somewhere.")]
    public async Task<IReadOnlyList<ItemSummary>> SearchAsync(
        [Description("Words to search for.")] string query,
        [Description("Include done and dismissed items too.")] bool includeClosed = false,
        [Description("Maximum number of results (1-1000).")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            throw new ToolException("Search needs at least one word.");
        }

        var items = db.Items.AsNoTracking();
        if (!includeClosed)
        {
            items = items.Where(i => i.Status == ItemStatus.Open || i.Status == ItemStatus.Snoozed);
        }
        else
        {
            items = items.Where(i => i.Status != ItemStatus.Merged);
        }

        foreach (var term in terms)
        {
            var pattern = Like(term);
            items = items.Where(i =>
                EF.Functions.Like(i.Title, pattern, Escape) ||
                (i.Details != null && EF.Functions.Like(i.Details, pattern, Escape)) ||
                (i.Requester != null && EF.Functions.Like(i.Requester, pattern, Escape)) ||
                (i.SourceContent != null && EF.Functions.Like(i.SourceContent, pattern, Escape)) ||
                i.Notes.Any(n => EF.Functions.Like(n.Text, pattern, Escape)));
        }

        var results = await items
            .OrderByDescending(i => i.UpdatedAt)
            .Take(Math.Clamp(limit, 1, MaxLimit))
            .ToListAsync(cancellationToken);

        return [.. results.Select(ItemSummary.From)];
    }

    [AssistantTool("create_item", ToolCategory.ItemManagement, Title = "Create item")]
    [Description("Adds an item to the user's list, for example a follow-up you discovered while working.")]
    public async Task<ItemDetail> CreateItemAsync(
        [Description("Short, specific title.")] string title,
        [Description("Kind of item.")] ItemType type = ItemType.Task,
        [Description("Longer description.")] string? details = null,
        [Description("When it is due, as an ISO 8601 date and time with an offset.")] DateTimeOffset? dueAt = null,
        Priority priority = Priority.Normal,
        [Description("Who asked for it, if anyone.")] string? requester = null,
        [Description("Repo name this relates to, used by get_work_context.")] string? repoHint = null,
        [Description("Link to the source, such as an email or ticket URL.")] string? sourceUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ToolException("Title is required.");
        }

        var item = new Item
        {
            Title = title.Trim(),
            Type = type,
            Details = details,
            DueAt = dueAt?.UtcDateTime,
            Priority = priority,
            Requester = requester,
            RepoHint = repoHint,
            SourceUrl = sourceUrl,
            Source = context.Caller.Id,
            Trust = SenderTrust.Self,
            CreatedAt = Now,
            UpdatedAt = Now,
        };

        db.Items.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        return ItemDetail.From(item);
    }

    [AssistantTool("update_item", ToolCategory.ItemManagement, Title = "Update item", Idempotent = true)]
    [Description("Changes an item's fields. Only the fields you pass are changed.")]
    public async Task<ItemDetail> UpdateItemAsync(
        [Description("The item's ID.")] int itemId,
        string? title = null,
        string? details = null,
        ItemType? type = null,
        Priority? priority = null,
        [Description("New due date and time, ISO 8601 with an offset.")] DateTimeOffset? dueAt = null,
        [Description("Set true to remove the due date.")] bool clearDueAt = false,
        string? requester = null,
        string? repoHint = null,
        CancellationToken cancellationToken = default)
    {
        var item = await LoadAsync(itemId, tracking: true, cancellationToken);

        if (!string.IsNullOrWhiteSpace(title)) item.Title = title.Trim();
        if (details is not null) item.Details = details;
        if (type is not null) item.Type = type.Value;
        if (priority is not null) item.Priority = priority.Value;
        if (requester is not null) item.Requester = requester;
        if (repoHint is not null) item.RepoHint = repoHint;

        if (clearDueAt)
        {
            item.DueAt = null;
            item.DueConfidence = null;
        }
        else if (dueAt is not null)
        {
            item.DueAt = dueAt.Value.UtcDateTime;
            item.DueConfidence = null;
        }

        item.UpdatedAt = Now;
        await db.SaveChangesAsync(cancellationToken);
        return ItemDetail.From(item);
    }

    [AssistantTool("mark_done", ToolCategory.ItemManagement, Title = "Mark item done", Idempotent = true)]
    [Description("Marks an item done, optionally with a note saying how it was resolved.")]
    public Task<ItemDetail> MarkDoneAsync(
        [Description("The item's ID.")] int itemId,
        [Description("How it was resolved.")] string? note = null,
        CancellationToken cancellationToken = default) =>
        SetStatusAsync(itemId, ItemStatus.Done, note, cancellationToken);

    [AssistantTool("dismiss_item", ToolCategory.ItemManagement, Title = "Dismiss item", Idempotent = true)]
    [Description("Dismisses an item that doesn't need doing, such as a false positive.")]
    public Task<ItemDetail> DismissItemAsync(
        [Description("The item's ID.")] int itemId,
        [Description("Why it doesn't need doing.")] string? reason = null,
        CancellationToken cancellationToken = default) =>
        SetStatusAsync(itemId, ItemStatus.Dismissed, reason, cancellationToken);

    [AssistantTool("merge_items", ToolCategory.ItemManagement, Title = "Merge duplicate items")]
    [Description("Folds an item into another one that is the same request, for example an email ask and the Slack message " +
        "repeating it. The duplicate is hidden and its source keeps feeding the kept item; the kept item takes the earlier due " +
        "date and higher priority. New items are checked for duplicates automatically, so use this for ones that were missed.")]
    public async Task<ItemDetail> MergeItemsAsync(
        [Description("The duplicate to fold in.")] int itemId,
        [Description("The item to keep.")] int intoItemId,
        [Description("What the duplicate adds that the kept item doesn't say, for the note.")] string? addedContext = null,
        CancellationToken cancellationToken = default)
    {
        if (itemId == intoItemId)
        {
            throw new ToolException("An item can't be merged into itself.");
        }

        var duplicate = await LoadAsync(itemId, tracking: true, cancellationToken);
        var into = await LoadAsync(intoItemId, tracking: true, cancellationToken);
        if (into.Status == ItemStatus.Merged)
        {
            throw new ToolException($"Item {intoItemId} was itself merged into item {into.MergedIntoId}. Merge into that one.");
        }

        if (duplicate.Status == ItemStatus.Merged)
        {
            throw new ToolException($"Item {itemId} is already merged into item {duplicate.MergedIntoId}.");
        }

        // Anything already merged into the duplicate moves to the kept item with it.
        foreach (var nested in duplicate.Duplicates.ToList())
        {
            nested.MergedIntoId = into.Id;
        }

        ItemMerger.Merge(duplicate, into, context.Caller.Id, duplicate.Source, addedContext, Now);
        await db.SaveChangesAsync(cancellationToken);
        return ItemDetail.From(await LoadAsync(intoItemId, tracking: false, cancellationToken));
    }

    [AssistantTool("split_item", ToolCategory.ItemManagement, Title = "Split out a merged item")]
    [Description("Undoes a merge: a duplicate that was folded into another item becomes its own open item again. " +
        "Use it when two items were merged but are really different requests.")]
    public async Task<ItemDetail> SplitItemAsync(
        [Description("The merged duplicate's ID, from the kept item's mergedFrom list.")] int itemId,
        CancellationToken cancellationToken = default)
    {
        var duplicate = await LoadAsync(itemId, tracking: true, cancellationToken);
        if (duplicate.Status != ItemStatus.Merged)
        {
            throw new ToolException($"Item {itemId} isn't merged into anything.");
        }

        var from = duplicate.MergedIntoId is { } id ? await LoadAsync(id, tracking: true, cancellationToken) : null;
        ItemMerger.Split(duplicate, from, context.Caller.Id, Now);
        await db.SaveChangesAsync(cancellationToken);
        return ItemDetail.From(duplicate);
    }

    [AssistantTool("reopen_item", ToolCategory.ItemManagement, Title = "Reopen item", Idempotent = true)]
    [Description("Reopens a done, dismissed, or snoozed item.")]
    public Task<ItemDetail> ReopenItemAsync(
        [Description("The item's ID.")] int itemId,
        CancellationToken cancellationToken = default) =>
        SetStatusAsync(itemId, ItemStatus.Open, null, cancellationToken);

    [AssistantTool("snooze", ToolCategory.ItemManagement, Title = "Snooze item", Idempotent = true)]
    [Description("Hides an item until a later time, when it comes back with a reminder.")]
    public async Task<ItemDetail> SnoozeAsync(
        [Description("The item's ID.")] int itemId,
        [Description("When to bring it back, ISO 8601 with an offset.")] DateTimeOffset until,
        CancellationToken cancellationToken = default)
    {
        if (until.UtcDateTime <= Now)
        {
            throw new ToolException("The snooze time must be in the future.");
        }

        var item = await LoadAsync(itemId, tracking: true, cancellationToken);
        RequireNotMerged(item);
        item.Status = ItemStatus.Snoozed;
        item.SnoozedUntil = until.UtcDateTime;
        item.UpdatedAt = Now;
        await db.SaveChangesAsync(cancellationToken);
        return ItemDetail.From(item);
    }

    [AssistantTool("remind_me", ToolCategory.ItemManagement, Title = "Set a reminder")]
    [Description("Shows the user a reminder at a specific time. Attach it to an existing item with itemId (for \"remind me " +
        "Thursday to follow up with Dana if she hasn't replied\", attach it to the waiting-on item: if that item is done by " +
        "then, the reminder is skipped). Without itemId, a new task is created with the reminder and due at that time.")]
    public async Task<ItemDetail> RemindMeAsync(
        [Description("When to remind, ISO 8601 with the user's UTC offset.")] DateTimeOffset at,
        [Description("What the reminder is about. Required when no itemId is given.")] string? title = null,
        [Description("Existing item to attach the reminder to.")] int? itemId = null,
        [Description("Extra detail shown in the reminder, such as the condition.")] string? note = null,
        CancellationToken cancellationToken = default)
    {
        if (at.UtcDateTime <= Now)
        {
            throw new ToolException("The reminder time must be in the future.");
        }

        Item item;
        if (itemId is not null)
        {
            item = await LoadAsync(itemId.Value, tracking: true, cancellationToken);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                throw new ToolException("Give the reminder a title, or attach it to an item with itemId.");
            }

            item = new Item
            {
                Title = title.Trim(),
                Type = ItemType.Task,
                DueAt = at.UtcDateTime,
                Source = context.Caller.Id,
                Trust = SenderTrust.Self,
                CreatedAt = Now,
            };
            db.Items.Add(item);
        }

        item.RemindAt = at.UtcDateTime;
        item.ReminderNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        item.UpdatedAt = Now;
        await db.SaveChangesAsync(cancellationToken);
        return ItemDetail.From(item);
    }

    [AssistantTool("add_note", ToolCategory.ItemManagement, Title = "Add note")]
    [Description("Adds a note to an item, such as progress or what you found.")]
    public async Task<ItemDetail> AddNoteAsync(
        [Description("The item's ID.")] int itemId,
        [Description("The note.")] string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ToolException("Note text is required.");
        }

        var item = await LoadAsync(itemId, tracking: true, cancellationToken);
        item.Notes.Add(new ItemNote { Text = text.Trim(), Author = context.Caller.Id, CreatedAt = Now });
        item.UpdatedAt = Now;
        await db.SaveChangesAsync(cancellationToken);
        return ItemDetail.From(item);
    }

    [AssistantTool("link_artifact", ToolCategory.ItemManagement, Title = "Link artifact")]
    [Description("Attaches a link to something that resolves or relates to an item: a commit, pull request, reply, or document.")]
    public async Task<ItemDetail> LinkArtifactAsync(
        [Description("The item's ID.")] int itemId,
        [Description("Absolute http or https URL.")] string url,
        [Description("What it is, such as pull_request, commit, email, or document.")] string? kind = null,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ToolException("The URL must be an absolute http or https URL.");
        }

        var item = await LoadAsync(itemId, tracking: true, cancellationToken);
        if (!item.Artifacts.Any(a => a.Url == uri.ToString()))
        {
            item.Artifacts.Add(new ItemArtifact
            {
                Url = uri.ToString(),
                Kind = kind,
                Description = description,
                AddedBy = context.Caller.Id,
                CreatedAt = Now,
            });
            item.UpdatedAt = Now;
            await db.SaveChangesAsync(cancellationToken);
        }

        return ItemDetail.From(item);
    }

    private async Task<ItemDetail> SetStatusAsync(int itemId, ItemStatus status, string? note, CancellationToken cancellationToken)
    {
        var item = await LoadAsync(itemId, tracking: true, cancellationToken);
        RequireNotMerged(item);
        item.Status = status;
        item.SnoozedUntil = null;
        item.CompletedAt = status is ItemStatus.Done or ItemStatus.Dismissed ? Now : null;
        item.UpdatedAt = Now;

        if (!string.IsNullOrWhiteSpace(note))
        {
            item.Notes.Add(new ItemNote { Text = note.Trim(), Author = context.Caller.Id, CreatedAt = Now });
        }

        await db.SaveChangesAsync(cancellationToken);
        return ItemDetail.From(item);
    }

    private static void RequireNotMerged(Item item)
    {
        if (item.Status == ItemStatus.Merged)
        {
            throw new ToolException($"Item {item.Id} was merged into item {item.MergedIntoId}. Act on that item, or split this one out with split_item first.");
        }
    }

    private async Task<Item> LoadAsync(int itemId, bool tracking, CancellationToken cancellationToken)
    {
        var query = db.Items.Include(i => i.Notes).Include(i => i.Artifacts).Include(i => i.Duplicates).AsQueryable();
        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return await query.FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken)
            ?? throw new ToolException($"Item {itemId} does not exist.");
    }

    private static string Like(string text) =>
        "%" + text.Replace(Escape, Escape + Escape).Replace("%", Escape + "%").Replace("_", Escape + "_") + "%";
}
