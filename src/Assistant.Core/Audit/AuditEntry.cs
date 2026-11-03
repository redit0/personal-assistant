namespace Assistant.Core.Audit;

/// <summary>One tool call made through the tool catalog, from any caller.</summary>
public class AuditEntry
{
    public long Id { get; set; }

    public DateTime Timestamp { get; set; }

    /// <summary>Who called, such as "chat" or "mcp:claude-code".</summary>
    public required string Caller { get; set; }

    public required string Tool { get; set; }

    public string? ArgumentsJson { get; set; }

    public AuditDecision Decision { get; set; }

    public bool Succeeded { get; set; }

    public string? ResultSummary { get; set; }

    public string? Error { get; set; }

    public long DurationMs { get; set; }
}

public enum AuditDecision
{
    /// <summary>Policy allowed the call without asking.</summary>
    Allowed,

    /// <summary>You approved the call when asked.</summary>
    Approved,

    /// <summary>You denied the call, or the approval prompt timed out.</summary>
    Denied,

    /// <summary>Policy is set to Deny for this tool.</summary>
    Blocked,
}
