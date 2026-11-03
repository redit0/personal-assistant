using Assistant.Core.Tools;

namespace Assistant.Tools.Catalog;

/// <summary>What you see in an approval prompt.</summary>
public sealed record ApprovalRequest(
    string ToolName,
    string ToolTitle,
    ToolCategory Category,
    ToolCaller Caller,
    string ArgumentsJson,
    string? Reason);

/// <summary>
/// Asks you to approve a tool call. The tray app shows its own window for this, so the agent that
/// made the call has no way to answer on your behalf.
/// </summary>
public interface IApprovalBroker
{
    /// <summary>True if you approved. Implementations deny on timeout.</summary>
    Task<bool> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>Denies everything. Used when no UI is attached.</summary>
public sealed class DenyAllApprovalBroker : IApprovalBroker
{
    public Task<bool> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}
