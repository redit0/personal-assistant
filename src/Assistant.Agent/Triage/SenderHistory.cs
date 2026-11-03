using System.Text;
using Assistant.Core.Items;
using Assistant.Data;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Agent.Triage;

/// <summary>How items from one sender (or one source) have turned out.</summary>
public sealed record SenderRecord(string Name, int Useful, int NotUseful)
{
    public int Total => Useful + NotUseful;

    /// <summary>You've dismissed several of their items and kept none.</summary>
    public bool MostlyNoise => NotUseful >= SenderHistory.NoiseThreshold && Useful == 0;
}

/// <summary>
/// What the user has done with past triage items, per sender and per source, so triage can learn who matters.
/// Thumbs up and down count, and so does what happened to items without them: finished counts as useful,
/// dismissed as not.
/// </summary>
public sealed class SenderHistory
{
    public const int NoiseThreshold = 3;

    private static readonly TimeSpan Lookback = TimeSpan.FromDays(90);
    private static readonly string[] TriagedSources = ["outlook", "calendar", "teams", "slack"];
    private const int MaxSendersShown = 25;

    private readonly Dictionary<string, SenderRecord> _bySender;

    private SenderHistory(Dictionary<string, SenderRecord> bySender, IReadOnlyList<SenderRecord> bySource)
    {
        _bySender = bySender;
        BySource = bySource;
    }

    public IReadOnlyList<SenderRecord> BySource { get; }

    public IEnumerable<SenderRecord> Senders => _bySender.Values;

    public SenderRecord? For(string? sender) => sender is null ? null : _bySender.GetValueOrDefault(sender.Trim());

    public static async Task<SenderHistory> LoadAsync(AssistantDbContext db, DateTime now, CancellationToken cancellationToken)
    {
        var since = now - Lookback;
        var explicitFeedback = await db.ItemFeedback.AsNoTracking()
            .Where(f => f.CreatedAt >= since)
            .Select(f => new { f.ItemId, f.Requester, f.Source, f.Useful })
            .ToListAsync(cancellationToken);
        var rated = explicitFeedback.Select(f => f.ItemId).ToHashSet();

        var outcomes = await db.Items.AsNoTracking()
            .Where(i => TriagedSources.Contains(i.Source) && i.CompletedAt >= since
                && (i.Status == ItemStatus.Done || i.Status == ItemStatus.Dismissed))
            .Select(i => new { i.Id, i.Requester, i.Source, i.Status })
            .ToListAsync(cancellationToken);

        var all = explicitFeedback.Select(f => (f.Requester, f.Source, f.Useful))
            .Concat(outcomes.Where(o => !rated.Contains(o.Id)).Select(o => (o.Requester, o.Source, Useful: o.Status == ItemStatus.Done)))
            .ToList();

        var bySender = all.Where(x => !string.IsNullOrWhiteSpace(x.Requester))
            .GroupBy(x => x.Requester!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => new SenderRecord(g.Key, g.Count(x => x.Useful), g.Count(x => !x.Useful)), StringComparer.OrdinalIgnoreCase);
        var bySource = all.GroupBy(x => x.Source)
            .Select(g => new SenderRecord(g.Key, g.Count(x => x.Useful), g.Count(x => !x.Useful)))
            .OrderByDescending(r => r.Total)
            .ToList();

        return new SenderHistory(bySender, bySource);
    }

    /// <summary>The part of the triage prompt that describes this history, or null when there's none worth showing.</summary>
    public string? Describe()
    {
        var senders = _bySender.Values.Where(r => r.Total >= 2).OrderByDescending(r => r.Total).Take(MaxSendersShown).ToList();
        if (senders.Count == 0 && BySource.Count == 0)
        {
            return null;
        }

        var text = new StringBuilder("How the user has treated past items (last 90 days; useful = kept or finished, not useful = dismissed):\n");
        foreach (var s in BySource)
        {
            text.AppendLine($"- Source {s.Name}: {s.Useful} useful, {s.NotUseful} not useful");
        }

        foreach (var s in senders)
        {
            text.AppendLine($"- {s.Name}: {s.Useful} useful, {s.NotUseful} not useful");
        }

        return text.ToString();
    }
}
