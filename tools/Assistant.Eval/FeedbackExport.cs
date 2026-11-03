using Assistant.Core.Signals;
using Assistant.Data;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Eval;

/// <summary>
/// Turns your thumbs up and down into eval cases, so you can check a prompt or model change against your own
/// mail and chats. The cases hold message text, so they're written to your data folder, never to the repo.
/// </summary>
public static class FeedbackExport
{
    private const int MessagesPerCase = 8;

    private static readonly Dictionary<string, (string SignalSource, string Medium)> Scopes = new()
    {
        ["mail"] = ("outlook", "email"),
        ["teams"] = ("teams", "Teams chat"),
        ["slack"] = ("slack", "Slack conversation"),
    };

    public static async Task<List<EvalCase>> ExportAsync(AssistantDbContext db, CancellationToken cancellationToken)
    {
        var feedback = await db.ItemFeedback.AsNoTracking().OrderByDescending(f => f.CreatedAt).ToListAsync(cancellationToken);
        var itemIds = feedback.Select(f => f.ItemId).Distinct().ToList();
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, cancellationToken);
        var decisions = await db.TriageDecisions.AsNoTracking().ToListAsync(cancellationToken);

        var cases = new List<EvalCase>();
        foreach (var f in feedback.DistinctBy(f => f.ItemId))
        {
            if (!items.TryGetValue(f.ItemId, out var item) || item.ExternalId?.Split(':', 2) is not [var scope, var key] || !Scopes.TryGetValue(scope, out var where))
            {
                continue;
            }

            var signals = await db.Signals.AsNoTracking()
                .Where(s => s.Source == where.SignalSource && s.ThreadKey == key && s.FilteredReason == null)
                .OrderByDescending(s => s.ReceivedAt)
                .Take(MessagesPerCase)
                .ToListAsync(cancellationToken);
            if (signals.Count == 0)
            {
                continue;
            }

            signals.Reverse();
            var original = decisions.FirstOrDefault(d => d.Source == scope && d.Key == key);
            var newest = signals[^1].ReceivedAt;
            cases.Add(new EvalCase
            {
                Name = $"{scope} item {item.Id}: {item.Title}",
                Medium = where.Medium,
                Expected = f.Useful
                    ? [Agent.Triage.TriageService.Snake(original?.Outcome ?? TriageOutcome.Ask)]
                    : ["fyi", "ignore"],
                Why = f.Useful ? "You marked this item useful." : "You marked this item not useful.",
                Messages = [.. signals.Select(s => new EvalMessage
                {
                    From = s.Author ?? "?",
                    To = s.Recipients ?? "",
                    FromMe = s.FromMe,
                    Ago = $"-{Math.Max(1, (newest - s.ReceivedAt).TotalMinutes + 60):0}m",
                    Subject = s.Subject,
                    Body = s.Body ?? "",
                })],
            });
        }

        return cases;
    }
}
