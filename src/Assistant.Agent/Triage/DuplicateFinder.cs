using Assistant.Core.Connectors;
using Assistant.Core.Items;
using Assistant.Tools.Sync;
using Microsoft.Extensions.Logging;

namespace Assistant.Agent.Triage;

/// <summary>
/// Asks the triage model whether a new item repeats an open or lately finished one. Without an AI provider, or when
/// the model can't answer, the new item simply stays its own.
/// </summary>
public sealed class DuplicateFinder(TriageService triage, ILogger<DuplicateFinder> logger) : IDuplicateDetector
{
    /// <summary>Below this the model is too unsure; a wrong merge hides work, so keep the items apart.</summary>
    private const double MinConfidence = 0.75;

    public async Task<DuplicateMatch?> FindAsync(SourceItem candidate, string sourceName, IReadOnlyList<Item> candidates, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0 || triage.UnavailableReason is not null)
        {
            return null;
        }

        try
        {
            var judgment = await triage.JudgeDuplicateAsync(candidate, sourceName, candidates, cancellationToken);
            if (judgment.DuplicateOf is not { } id || judgment.Confidence < MinConfidence || candidates.FirstOrDefault(i => i.Id == id) is not { } match)
            {
                return null;
            }

            var reopen = match.Status == ItemStatus.Done && judgment.Reopen;
            logger.LogInformation("'{Title}' from {Source} repeats {State} item {ItemId}{Reopen}: {Reason}",
                candidate.Title, sourceName, match.Status == ItemStatus.Done ? "finished" : "open", id, reopen ? " and reopens it" : "", judgment.Reasoning);
            return new DuplicateMatch(id, judgment.AddedContext, judgment.Reasoning, reopen);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't check '{Title}' from {Source} for duplicates; keeping it as its own item.", candidate.Title, sourceName);
            return null;
        }
    }
}
