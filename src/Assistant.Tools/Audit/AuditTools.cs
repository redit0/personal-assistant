using System.ComponentModel;
using Assistant.Core.Audit;
using Assistant.Core.Tools;
using Assistant.Data;
using Assistant.Tools.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Tools.Audit;

public sealed record AuditEntryDto(
    DateTime Timestamp,
    string Caller,
    string Tool,
    AuditDecision Decision,
    bool Succeeded,
    string? Arguments,
    string? Error,
    long DurationMs)
{
    public static AuditEntryDto From(AuditEntry e) =>
        new(e.Timestamp, e.Caller, e.Tool, e.Decision, e.Succeeded, e.ArgumentsJson, e.Error, e.DurationMs);
}

public sealed class AuditTools(AssistantDbContext db, TimeProvider time)
{
    [AssistantTool("get_audit_log", ToolCategory.Read, Title = "Get audit log")]
    [Description("Lists recent tool calls made through the assistant by any agent, newest first.")]
    public async Task<IReadOnlyList<AuditEntryDto>> GetAuditLogAsync(
        [Description("How many hours back to look.")] int hours = 24,
        [Description("Only calls to this tool.")] string? tool = null,
        [Description("Maximum number of entries (1-500).")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var since = time.GetUtcNow().UtcDateTime.AddHours(-Math.Clamp(hours, 1, 24 * 90));
        var query = db.AuditEntries.AsNoTracking().Where(e => e.Timestamp >= since);
        if (!string.IsNullOrWhiteSpace(tool))
        {
            query = query.Where(e => e.Tool == tool);
        }

        var entries = await query.OrderByDescending(e => e.Timestamp).Take(Math.Clamp(limit, 1, 500)).ToListAsync(cancellationToken);
        return [.. entries.Select(AuditEntryDto.From)];
    }
}
