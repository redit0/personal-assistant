namespace Assistant.Core.Items;

/// <summary>Something the assistant tracks on your behalf.</summary>
public class Item
{
    public int Id { get; set; }

    public required string Title { get; set; }

    /// <summary>Short description written by you, the triage agent, or a calling agent.</summary>
    public string? Details { get; set; }

    public ItemType Type { get; set; } = ItemType.Task;

    public ItemStatus Status { get; set; } = ItemStatus.Open;

    public Priority Priority { get; set; } = Priority.Normal;

    /// <summary>When the item is due, in UTC.</summary>
    public DateTime? DueAt { get; set; }

    /// <summary>0..1. How sure the triage agent is about <see cref="DueAt"/>. Null when you set it.</summary>
    public double? DueConfidence { get; set; }

    /// <summary>Who asked for this, if anyone.</summary>
    public string? Requester { get; set; }

    public SenderTrust Trust { get; set; } = SenderTrust.Self;

    /// <summary>Where the item came from, such as "manual", "outlook", "github", or "mcp:claude-code".</summary>
    public required string Source { get; set; }

    /// <summary>Identifier of the item in its source system, used to dedupe and reconcile.</summary>
    public string? ExternalId { get; set; }

    public string? SourceUrl { get; set; }

    /// <summary>
    /// Raw text from the source (email body, message, ticket description). This is untrusted input
    /// and must always be labeled as such when it is handed to a model.
    /// </summary>
    public string? SourceContent { get; set; }

    /// <summary>Repo name or URL fragment this item probably relates to.</summary>
    public string? RepoHint { get; set; }

    /// <summary>One-line explanation of why the triage agent created or prioritized this item.</summary>
    public string? Reasoning { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? SnoozedUntil { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// When you asked to be reminded about this item, in UTC. Cleared once the reminder is shown. Skipped if the
    /// item is done or dismissed by then, so "remind me if she hasn't replied" works on waiting-on items.
    /// </summary>
    public DateTime? RemindAt { get; set; }

    /// <summary>What to say in the reminder, if you gave a reason.</summary>
    public string? ReminderNote { get; set; }

    /// <summary>Set once a due-soon reminder has been shown, so it only fires once per due date.</summary>
    public DateTime? RemindedForDueAt { get; set; }

    /// <summary>When the source last changed this item, for connector items.</summary>
    public DateTime? ExternalUpdatedAt { get; set; }

    /// <summary>
    /// True when a connector closed the item because it disappeared at the source. Such items reopen
    /// if they come back. Items you closed yourself stay closed.
    /// </summary>
    public bool ClosedBySync { get; set; }

    /// <summary>The triage agent wasn't sure about this one. It shows under Needs a look until you keep or dismiss it.</summary>
    public bool NeedsReview { get; set; }

    /// <summary>
    /// The item this one was found to duplicate. Merged items stay hidden behind it (status <see cref="ItemStatus.Merged"/>)
    /// so their source can keep updating them without creating a second item.
    /// </summary>
    public int? MergedIntoId { get; set; }

    public List<ItemNote> Notes { get; set; } = [];

    public List<ItemArtifact> Artifacts { get; set; } = [];

    /// <summary>Items merged into this one, from other emails, chats, or tickets about the same request.</summary>
    public List<Item> Duplicates { get; set; } = [];
}

public class ItemNote
{
    public int Id { get; set; }

    public int ItemId { get; set; }

    public required string Text { get; set; }

    public required string Author { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>A commit, pull request, reply, or document that resolved or relates to an item.</summary>
public class ItemArtifact
{
    public int Id { get; set; }

    public int ItemId { get; set; }

    public required string Url { get; set; }

    public string? Kind { get; set; }

    public string? Description { get; set; }

    public required string AddedBy { get; set; }

    public DateTime CreatedAt { get; set; }
}

public enum ItemType
{
    /// <summary>Someone needs something from you.</summary>
    Ask,

    /// <summary>You said you would do something.</summary>
    Commitment,

    /// <summary>A pull request or approval waiting on you.</summary>
    Review,

    /// <summary>A ticket or issue assigned to you.</summary>
    Assigned,

    /// <summary>A meeting or fixed deadline.</summary>
    Event,

    /// <summary>You are blocked on someone else.</summary>
    WaitingOn,

    /// <summary>A plain to-do you added yourself.</summary>
    Task,
}

public enum ItemStatus
{
    Open,
    Snoozed,
    Done,
    Dismissed,

    /// <summary>A duplicate folded into another item; see <see cref="Item.MergedIntoId"/>.</summary>
    Merged,
}

public enum Priority
{
    Low,
    Normal,
    High,
    Urgent,
}

/// <summary>How much the person or system behind an item is trusted to direct actions.</summary>
public enum SenderTrust
{
    /// <summary>You created it.</summary>
    Self,

    /// <summary>Someone in your organization's domain.</summary>
    Organization,

    /// <summary>An external contact you have corresponded with before.</summary>
    KnownContact,

    /// <summary>An external sender with no history.</summary>
    External,

    Unknown,
}
