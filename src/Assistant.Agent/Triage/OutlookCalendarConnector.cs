using Assistant.Connectors.Microsoft;
using Assistant.Core.Connectors;
using Assistant.Core.Items;
using Assistant.Core.Settings;
using Assistant.Core.Signals;
using Assistant.Core.Text;
using Assistant.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Assistant.Agent.Triage;

/// <summary>
/// Upcoming calendar events that need action: meetings to prepare for, and events that are really
/// deadlines. Each event is judged once (again only if it changes), and the item closes once the
/// event has passed.
/// </summary>
public sealed class OutlookCalendarConnector(
    GraphClient graph,
    TriageService triage,
    IMicrosoftTokenProvider auth,
    UserSettingsStore settings,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<OutlookCalendarConnector> logger) : IConnector
{
    public const string Scope = "calendar";
    private const int MaxEventsPerSync = 60;
    private const int EventsPerRequest = 15;

    public string Id => "calendar";

    public string DisplayName => "Calendar";

    public TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(5, settings.Current.Outlook.IntervalMinutes * 3));

    public string? NotReadyReason()
    {
        if (!settings.Current.Outlook.CalendarEnabled) return "Turned off.";
        if (!auth.IsConfigured) return "Set up Microsoft sign-in in Settings first.";
        return triage.UnavailableReason is { } ai ? $"Needs an AI provider for triage. {ai}" : null;
    }

    public async Task<ConnectorSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var events = (await graph.GetCalendarViewAsync(now, now.AddDays(Math.Max(1, settings.Current.Outlook.CalendarDaysAhead)), cancellationToken))
            .Where(e => !e.IsCancelled && !string.Equals(e.ResponseStatus, "declined", StringComparison.OrdinalIgnoreCase))
            .ToList();

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
        var ids = events.Select(e => e.Id).ToList();
        var decisions = await db.TriageDecisions.Where(d => d.Source == Scope && ids.Contains(d.Key)).ToDictionaryAsync(d => d.Key, cancellationToken);

        var warnings = new List<string>();
        var toJudge = events.Where(e => !decisions.TryGetValue(e.Id, out var d) || d.Version != Version(e)).ToList();
        var judged = 0;
        foreach (var batch in toJudge.Take(MaxEventsPerSync).Chunk(EventsPerRequest))
        {
            try
            {
                // The model lists only events that need action; anything it leaves out is a plain meeting.
                var judgments = (await triage.JudgeEventsAsync(batch, cancellationToken))
                    .GroupBy(j => j.EventId, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

                foreach (var e in batch)
                {
                    if (!decisions.TryGetValue(e.Id, out var decision))
                    {
                        decision = new TriageDecision { Source = Scope, Key = e.Id };
                        db.TriageDecisions.Add(decision);
                        decisions[e.Id] = decision;
                    }

                    var j = judgments.GetValueOrDefault(e.Id);
                    decision.Version = Version(e);
                    decision.Outcome = j?.Kind?.Trim().ToLowerInvariant() switch
                    {
                        "prep" => TriageOutcome.MeetingPrep,
                        "deadline" => TriageOutcome.Deadline,
                        _ => TriageOutcome.Ignore,
                    };
                    decision.Title = string.IsNullOrWhiteSpace(j?.Title) ? e.Subject : j.Title;
                    decision.Summary = j?.Summary;
                    decision.Confidence = Math.Clamp(j?.Confidence ?? 0, 0, 1);
                    decision.DueAt = e.Start;
                    decision.Requester = e.IsOrganizer ? null : e.Organizer;
                    decision.Trust = SenderTrust.Organization;
                    decision.Url = e.WebLink;
                    decision.SourceContent = SafeLinks.Unwrap(e.BodyPreview);
                    decision.LastSeenAt = e.LastModified;
                    decision.TriagedAt = now;
                    decision.Model = triage.ModelName;
                }

                await db.SaveChangesAsync(cancellationToken);
                judged += batch.Length;
            }
            catch (TriageRefusedException)
            {
                // Record these as plain meetings so the same batch isn't refused every sync.
                logger.LogWarning("Triage model declined a batch of {Count} calendar events", batch.Length);
                foreach (var e in batch)
                {
                    if (!decisions.TryGetValue(e.Id, out var decision))
                    {
                        decision = new TriageDecision { Source = Scope, Key = e.Id };
                        db.TriageDecisions.Add(decision);
                        decisions[e.Id] = decision;
                    }

                    decision.Version = Version(e);
                    decision.Outcome = TriageOutcome.Ignore;
                    decision.Reasoning = "The triage model declined to process this event.";
                    decision.LastSeenAt = e.LastModified;
                    decision.TriagedAt = now;
                }

                await db.SaveChangesAsync(cancellationToken);
                warnings.Add($"The triage model declined {batch.Length} events, so they weren't checked for prep or deadlines.");
            }
            catch (TriageResponseException ex)
            {
                // One unusable answer; the other batches can still go ahead. These events are retried next sync.
                logger.LogWarning("Calendar triage answer unusable for {Count} events: {Message}", batch.Length, ex.Message);
                warnings.Add($"Couldn't judge {batch.Length} events this round ({ex.Message}); they'll be retried.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not ConnectorException)
            {
                logger.LogWarning(ex, "Calendar triage failed");
                warnings.Add($"Calendar triage stopped early: {ex.Message}");
                break;
            }
        }

        if (toJudge.Count > judged)
        {
            logger.LogInformation("Calendar: judged {Judged} of {Total} new or changed events", judged, toJudge.Count);
        }

        if (toJudge.Count > MaxEventsPerSync)
        {
            warnings.Add($"{toJudge.Count - MaxEventsPerSync} more events will be judged on the next sync.");
        }

        var threshold = settings.Current.Triage.ReviewThreshold;
        var byId = events.ToDictionary(e => e.Id);
        var items = decisions.Values
            .Where(d => d.Outcome is TriageOutcome.MeetingPrep or TriageOutcome.Deadline && byId.ContainsKey(d.Key))
            .Select(d =>
            {
                var e = byId[d.Key];
                return new SourceItem(
                    Scope: Scope,
                    ExternalId: $"{Scope}:{d.Key}",
                    Title: d.Outcome == TriageOutcome.MeetingPrep ? $"Prep: {d.Title}" : d.Title ?? e.Subject ?? "(no title)",
                    Type: ItemType.Event,
                    Url: e.WebLink ?? "https://outlook.office.com/calendar/",
                    UpdatedAt: e.LastModified,
                    Priority: d.Outcome == TriageOutcome.Deadline ? Priority.High : Priority.Normal,
                    DueAt: e.Start,
                    Requester: d.Requester,
                    Trust: SenderTrust.Organization,
                    Details: d.Summary ?? $"{e.Subject} at {e.Start.ToLocalTime():ddd MMM d h:mm tt}.",
                    SourceContent: e.BodyPreview,
                    DueConfidence: 1,
                    Reasoning: d.Summary,
                    NeedsReview: d.Confidence < threshold);
            })
            .ToList();

        return new ConnectorSnapshot(items, new HashSet<string>(), warnings);
    }

    private static string Version(GraphEvent e) => e.LastModified.ToString("O");
}
