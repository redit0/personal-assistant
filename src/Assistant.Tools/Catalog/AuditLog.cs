using Assistant.Core.Audit;
using Assistant.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.Tools.Catalog;

public interface IAuditLog
{
    Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>Writes audit entries in their own scope so they are saved even when the tool's own work fails.</summary>
public sealed class DbAuditLog(IServiceScopeFactory scopes) : IAuditLog
{
    private const int MaxArgumentsLength = 4000;
    private const int MaxSummaryLength = 500;

    public async Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        entry.ArgumentsJson = Truncate(entry.ArgumentsJson, MaxArgumentsLength);
        entry.ResultSummary = Truncate(entry.ResultSummary, MaxSummaryLength);
        entry.Error = Truncate(entry.Error, MaxSummaryLength);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
        db.AuditEntries.Add(entry);

        // Not cancellable: a client disconnecting mid-call should still leave a record.
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max] + "…";
}
