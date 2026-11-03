using Assistant.Core.Audit;
using Assistant.Core.Items;
using Assistant.Core.Repos;
using Assistant.Core.Signals;
using Assistant.Core.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Assistant.Data;

public class AssistantDbContext(DbContextOptions<AssistantDbContext> options) : DbContext(options)
{
    public DbSet<Item> Items => Set<Item>();

    public DbSet<ItemNote> ItemNotes => Set<ItemNote>();

    public DbSet<ItemArtifact> ItemArtifacts => Set<ItemArtifact>();

    public DbSet<Signal> Signals => Set<Signal>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<RepoMapping> RepoMappings => Set<RepoMapping>();

    public DbSet<ToolPolicySetting> ToolPolicies => Set<ToolPolicySetting>();

    public DbSet<TriageDecision> TriageDecisions => Set<TriageDecision>();

    public DbSet<ConnectorState> ConnectorStates => Set<ConnectorState>();

    public DbSet<ItemFeedback> ItemFeedback => Set<ItemFeedback>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Enums read better as text when you open the database in a SQLite browser.
        configurationBuilder.Properties<ItemType>().HaveConversion<string>();
        // Priority stays numeric so ORDER BY sorts Urgent above Low.
        configurationBuilder.Properties<ItemStatus>().HaveConversion<string>();
        configurationBuilder.Properties<SenderTrust>().HaveConversion<string>();
        configurationBuilder.Properties<AuditDecision>().HaveConversion<string>();
        configurationBuilder.Properties<CodeHost>().HaveConversion<string>();
        configurationBuilder.Properties<ToolPolicy>().HaveConversion<string>();
        configurationBuilder.Properties<TriageOutcome>().HaveConversion<string>();

        // Everything is stored in UTC. SQLite drops DateTime.Kind, so restore it on read.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v == null ? v : v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime(),
        v => v == null ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Item>(item =>
        {
            item.HasIndex(i => new { i.Status, i.DueAt });
            item.HasIndex(i => new { i.Source, i.ExternalId });
            item.HasMany(i => i.Notes).WithOne().HasForeignKey(n => n.ItemId).OnDelete(DeleteBehavior.Cascade);
            item.HasMany(i => i.Artifacts).WithOne().HasForeignKey(a => a.ItemId).OnDelete(DeleteBehavior.Cascade);
            item.HasMany(i => i.Duplicates).WithOne().HasForeignKey(i => i.MergedIntoId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Signal>(signal =>
        {
            signal.HasIndex(s => new { s.Source, s.ExternalId }).IsUnique();
            signal.HasIndex(s => s.TriagedAt);
        });

        modelBuilder.Entity<AuditEntry>().HasIndex(a => a.Timestamp);

        modelBuilder.Entity<RepoMapping>().HasIndex(r => r.LocalPath).IsUnique();

        modelBuilder.Entity<ToolPolicySetting>().HasKey(p => p.ToolName);

        modelBuilder.Entity<Signal>().HasIndex(s => new { s.Source, s.ThreadKey });

        modelBuilder.Entity<TriageDecision>().HasIndex(d => new { d.Source, d.Key }).IsUnique();

        modelBuilder.Entity<ConnectorState>().HasKey(s => s.Key);

        modelBuilder.Entity<ItemFeedback>().HasIndex(f => f.CreatedAt);
    }
}
