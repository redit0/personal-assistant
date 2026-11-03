namespace Assistant.Core.Signals;

/// <summary>
/// One raw incoming message from a conversational source, such as an email. The mail connector stores
/// these as they arrive, and triage reads them a thread at a time.
/// </summary>
public class Signal
{
    public int Id { get; set; }

    public required string Source { get; set; }

    public required string ExternalId { get; set; }

    /// <summary>Groups messages into a thread, such as an Outlook conversation ID.</summary>
    public string? ThreadKey { get; set; }

    /// <summary>Where it came from within the source, such as "inbox", "sent", or a folder name.</summary>
    public string? Kind { get; set; }

    /// <summary>Sender, as "Name &lt;address&gt;".</summary>
    public string? Author { get; set; }

    public string? AuthorAddress { get; set; }

    /// <summary>To and Cc, as "Name &lt;address&gt;" separated by "; ".</summary>
    public string? Recipients { get; set; }

    public bool FromMe { get; set; }

    /// <summary>How much the sender is trusted, decided by the connector (your domain, your Teams tenant, your Slack workspace).</summary>
    public Items.SenderTrust? AuthorTrust { get; set; }

    public string? Subject { get; set; }

    public string? Body { get; set; }

    public string? Url { get; set; }

    public DateTime ReceivedAt { get; set; }

    /// <summary>Why the rule filter dropped this message before AI triage, or null if it passed.</summary>
    public string? FilteredReason { get; set; }

    public DateTime? TriagedAt { get; set; }

    /// <summary>Item created or updated from this signal, if any.</summary>
    public int? ItemId { get; set; }
}

/// <summary>
/// The latest AI judgment for a thread or calendar event. Kept so a thread is only re-read when it
/// changes, and so the connector can rebuild its list of open items without calling the model again.
/// </summary>
public class TriageDecision
{
    public int Id { get; set; }

    /// <summary>"mail" or "calendar".</summary>
    public required string Source { get; set; }

    /// <summary>Thread or event ID.</summary>
    public required string Key { get; set; }

    /// <summary>For calendar events: the event's last-modified stamp, so edits are re-judged.</summary>
    public string? Version { get; set; }

    public TriageOutcome Outcome { get; set; }

    public string? Title { get; set; }

    public string? Summary { get; set; }

    public DateTime? DueAt { get; set; }

    public double? DueConfidence { get; set; }

    public Items.Priority Priority { get; set; } = Items.Priority.Normal;

    public string? Requester { get; set; }

    public Items.SenderTrust Trust { get; set; } = Items.SenderTrust.Unknown;

    /// <summary>0..1. Below the review threshold, the item lands in Needs a look.</summary>
    public double Confidence { get; set; }

    public string? Reasoning { get; set; }

    public string? Url { get; set; }

    public string? SourceContent { get; set; }

    /// <summary>Newest message (or event change) this judgment covered.</summary>
    public DateTime LastSeenAt { get; set; }

    /// <summary>Newest message of yours outside the thread (same channel, or a DM with the asker) that triage has seen.</summary>
    public DateTime? ElsewhereSeenAt { get; set; }

    /// <summary>When a different request last started in this thread, so an item you already closed isn't reused for it.</summary>
    public DateTime? RequestStartedAt { get; set; }

    public DateTime TriagedAt { get; set; }

    public string? Model { get; set; }
}

public enum TriageOutcome
{
    Ignore,
    Fyi,
    Ask,
    Commitment,
    WaitingOn,
    Resolved,
    MeetingPrep,
    Deadline,
}

/// <summary>Small key/value store for connector state such as Graph delta links.</summary>
public class ConnectorState
{
    public required string Key { get; set; }

    public string? Value { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>Your thumbs up or down on an AI-created item. Recent feedback is shown to triage as examples.</summary>
public class ItemFeedback
{
    public int Id { get; set; }

    public int ItemId { get; set; }

    public bool Useful { get; set; }

    public required string ItemTitle { get; set; }

    public string? Requester { get; set; }

    public required string Source { get; set; }

    public DateTime CreatedAt { get; set; }
}
