using Assistant.Connectors.Microsoft;
using Assistant.Core.Connectors;
using Assistant.Core.Items;
using Assistant.Core.Settings;
using Assistant.Core.Signals;
using Assistant.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Assistant.Agent.Triage;

/// <summary>Where a conversation connector keeps its messages and judgments.</summary>
/// <param name="SignalSource">Signal.Source for its messages, such as "outlook" or "teams".</param>
/// <param name="DecisionScope">TriageDecision.Source and item scope, such as "mail" or "teams".</param>
/// <param name="Medium">How the messages are described to the model, such as "email" or "Teams chat".</param>
/// <param name="DefaultUrl">Link for items when a message has none.</param>
/// <param name="Elsewhere">For chat: how to spot your replies outside the thread. Null for email.</param>
public sealed record ConversationSource(string SignalSource, string DecisionScope, string Medium, string DefaultUrl, ElsewhereRule? Elsewhere = null);

/// <summary>
/// In chat, people often answer in the main channel or in a DM rather than in the thread they were asked in.
/// Your later messages count as possible answers when they're in the same place (channel or chat) as the ask,
/// or in a direct conversation with someone who asked.
/// </summary>
/// <param name="PlaceOf">The channel or chat a thread key belongs to.</param>
/// <param name="DirectKinds">Signal kinds for DMs and group chats.</param>
public sealed record ElsewhereRule(Func<string, string> PlaceOf, IReadOnlySet<string> DirectKinds);

public sealed record TriageRun(int Triaged, int Backlog, string? Error)
{
    public IEnumerable<string> Warnings()
    {
        if (Error is not null)
        {
            yield return Error;
        }

        if (Backlog > 0)
        {
            yield return $"{Backlog} thread{(Backlog == 1 ? "" : "s")} still waiting for triage; they'll be read over the next syncs.";
        }
    }
}

/// <summary>
/// The shared half of every conversation connector (email, Teams, Slack): find threads with new messages, ask
/// the triage model about them, and turn the open asks, promises, and waits into items. Connectors only need to
/// store messages as signals.
/// </summary>
public sealed class ThreadTriageEngine(
    GraphClient graph,
    TriageService triage,
    UserSettingsStore settings,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<ThreadTriageEngine> logger)
{
    private const int MessagesPerThread = 8;
    private const int MessagesPerChat = 30;
    private const int MessagesElsewhere = 6;
    private const int MaxAttemptsPerThread = 3;
    private static readonly TimeSpan SignalRetention = TimeSpan.FromDays(60);

    private readonly Dictionary<string, int> _failures = new(StringComparer.Ordinal);
    private readonly Lock _failuresLock = new();

    public async Task<TriageRun> TriageChangedThreadsAsync(ConversationSource source, int max, IReadOnlyList<string> me, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();

        // A thread needs triage when it has an unfiltered message newer than the last judgment.
        var latestByThread = await db.Signals.AsNoTracking()
            .Where(s => s.Source == source.SignalSource && s.FilteredReason == null && s.ThreadKey != null)
            .GroupBy(s => s.ThreadKey!)
            .Select(g => new { Thread = g.Key, Latest = g.Max(s => s.ReceivedAt), HasInbound = g.Any(s => !s.FromMe) })
            .ToListAsync(cancellationToken);

        var decisions = await db.TriageDecisions.Where(d => d.Source == source.DecisionScope).ToDictionaryAsync(d => d.Key, cancellationToken);
        await MoveWholeChatJudgmentsToTopicsAsync(db, source, decisions, cancellationToken);

        // Threads with only your own messages matter only if they contain a promise, so they're still judged,
        // but threads with replies from others go first.
        var elsewhere = source.Elsewhere is null ? [] : await ElsewhereAsync(db, source, source.Elsewhere, decisions.Values, cancellationToken);

        // Open items also need another look when you've posted something that might answer them.
        var pending = latestByThread
            .Where(t => !decisions.TryGetValue(t.Thread, out var d) || t.Latest > d.LastSeenAt)
            .Select(t => (t.Thread, t.Latest, t.HasInbound))
            .Concat(elsewhere
                .Where(e => e.Value[^1].ReceivedAt > (decisions[e.Key].ElsewhereSeenAt ?? DateTime.MinValue))
                .Select(e => (Thread: ConversationTopic.ConversationOf(e.Key), Latest: e.Value[^1].ReceivedAt, HasInbound: true)))
            .DistinctBy(t => t.Thread)
            .OrderByDescending(t => t.HasInbound)
            .ThenByDescending(t => t.Latest)
            .ToList();

        if (pending.Count == 0)
        {
            return new TriageRun(0, 0, null);
        }

        var calendar = await CalendarSummaryAsync(cancellationToken);
        var history = await SenderHistory.LoadAsync(db, time.GetUtcNow().UtcDateTime, cancellationToken);
        var done = 0;
        var skipped = 0;

        foreach (var thread in pending.Take(Math.Max(1, max)))
        {
            var isChat = IsWholeChat(source, thread.Thread);
            var messages = await db.Signals.AsNoTracking()
                .Where(s => s.Source == source.SignalSource && s.ThreadKey == thread.Thread && s.FilteredReason == null)
                .OrderByDescending(s => s.ReceivedAt)
                .Take(isChat ? MessagesPerChat : MessagesPerThread)
                .ToListAsync(cancellationToken);
            messages.Reverse();

            decisions.TryGetValue(thread.Thread, out var previous);
            var failureKey = $"{source.DecisionScope}:{thread.Thread}";
            var outsideKeys = elsewhere.Keys.Where(k => ConversationTopic.ConversationOf(k) == thread.Thread).ToList();
            var outside = outsideKeys.SelectMany(k => elsewhere[k]).DistinctBy(s => s.Id).OrderBy(s => s.ReceivedAt).ToList();
            if (messages.Count == 0)
            {
                // An open item whose messages have since been pruned. There's nothing left to judge it against.
                MarkElsewhereSeen(outsideKeys, elsewhere, decisions);
                continue;
            }

            var topics = isChat ? await OpenTopicsAsync(db, source, thread.Thread, decisions, cancellationToken) : [];
            var readUpTo = previous?.LastSeenAt ?? DateTime.MinValue;

            ThreadJudgment? judgment = null;
            ChatJudgment? chat = null;
            try
            {
                if (isChat)
                {
                    chat = await triage.JudgeChatAsync(
                        [.. messages.Select(ToThreadMessage)], messages.Count(m => m.ReceivedAt > readUpTo),
                        topics, me, calendar, source.Medium, cancellationToken, [.. outside.Select(ToThreadMessage)]);
                }
                else
                {
                    judgment = await triage.JudgeThreadAsync(
                        [.. messages.Select(ToThreadMessage)],
                        previous, me, calendar, source.Medium, cancellationToken, [.. outside.Select(ToThreadMessage)]);
                }
            }
            catch (TriageRefusedException)
            {
                // Retrying gets the same refusal, so put it in front of you instead.
                logger.LogWarning("Triage model declined {Scope} thread {Thread}; sending it to Needs a look", source.DecisionScope, thread.Thread);
                judgment = new ThreadJudgment
                {
                    Decision = "ask",
                    Title = $"Check this conversation: {messages[^1].Subject}",
                    Summary = "The triage model's safety filter declined to read this conversation, so it couldn't tell whether you need to act. Take a look yourself.",
                    Confidence = 0,
                    Reasoning = "The triage model declined to process this conversation.",
                };
            }
            catch (TriageResponseException ex)
            {
                // A bad answer for one thread shouldn't hold up the rest. Retry on later syncs, and give up after
                // a few tries so it can't block the backlog forever.
                int failures;
                lock (_failuresLock)
                {
                    failures = _failures[failureKey] = _failures.GetValueOrDefault(failureKey) + 1;
                }

                logger.LogWarning("Triage answer unusable for {Scope} thread {Thread} (attempt {Attempt}): {Message}", source.DecisionScope, thread.Thread, failures, ex.Message);
                if (failures < MaxAttemptsPerThread)
                {
                    skipped++;
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Network, quota, or key problems affect every thread, so stop for this round.
                logger.LogWarning(ex, "Triage failed for {Scope} thread {Thread}", source.DecisionScope, thread.Thread);
                await db.SaveChangesAsync(cancellationToken);
                return new TriageRun(done, pending.Count - done, $"Triage stopped early: {ex.Message}");
            }

            var decision = previous ?? new TriageDecision { Source = source.DecisionScope, Key = thread.Thread };
            if (previous is null)
            {
                db.TriageDecisions.Add(decision);
                decisions[thread.Thread] = decision;
            }

            lock (_failuresLock)
            {
                _failures.Remove(failureKey);
            }

            if (isChat)
            {
                // A refusal has no topics to report, so it becomes a new one that puts the conversation in front of you.
                chat ??= judgment is null
                    ? new ChatJudgment()
                    : new ChatJudgment { Topics = [new ChatTopic { Messages = [messages.Count], Judgment = judgment }] };
                ApplyChat(db, source, thread.Thread, chat, topics, messages, readUpTo, decisions, history);

                // The conversation's own decision only records how far triage has read.
                decision.Outcome = TriageOutcome.Ignore;
                decision.LastSeenAt = messages.Max(m => m.ReceivedAt);
                decision.TriagedAt = time.GetUtcNow().UtcDateTime;
                decision.Model = triage.ModelName;
            }
            else
            {
                // Gave up on this thread until a new message arrives. FYI keeps it off your list.
                judgment ??= new ThreadJudgment { Decision = "fyi", Confidence = 0, Reasoning = "Couldn't be triaged after several tries." };
                Apply(judgment, decision, messages);
                WeighSender(decision, history);
            }

            MarkElsewhereSeen(outsideKeys, elsewhere, decisions);
            done++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new TriageRun(done, pending.Count - done,
            skipped > 0 ? $"{skipped} thread{(skipped == 1 ? "" : "s")} got an unusable answer and will be retried." : null);
    }

    public async Task<List<SourceItem>> BuildItemsAsync(ConversationSource source, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
        var threshold = settings.Current.Triage.ReviewThreshold;

        var open = await db.TriageDecisions.AsNoTracking()
            .Where(d => d.Source == source.DecisionScope && (d.Outcome == TriageOutcome.Ask || d.Outcome == TriageOutcome.Commitment || d.Outcome == TriageOutcome.WaitingOn))
            .ToListAsync(cancellationToken);

        return [.. open.Select(d => new SourceItem(
            Scope: source.DecisionScope,
            ExternalId: $"{source.DecisionScope}:{d.Key}",
            Title: d.Title ?? "(no subject)",
            Type: d.Outcome switch { TriageOutcome.Commitment => ItemType.Commitment, TriageOutcome.WaitingOn => ItemType.WaitingOn, _ => ItemType.Ask },
            Url: d.Url ?? source.DefaultUrl,
            UpdatedAt: d.TriagedAt,
            Priority: d.Priority,
            DueAt: d.DueAt,
            Requester: d.Requester,
            Trust: d.Trust,
            Details: d.Summary,
            SourceContent: d.SourceContent,
            DueConfidence: d.DueConfidence,
            Reasoning: d.Reasoning,
            NeedsReview: d.Confidence < threshold,
            RequestStartedAt: d.RequestStartedAt ?? DateTime.MinValue))];
    }

    /// <summary>A DM, group chat, or channel timeline, as opposed to one thread within it. Email has only threads.</summary>
    private static bool IsWholeChat(ConversationSource source, string key) =>
        source.Elsewhere is { } rule && rule.PlaceOf(key) == key && ConversationTopic.ConversationOf(key) == key;

    private static bool IsOpen(TriageOutcome outcome) => outcome is TriageOutcome.Ask or TriageOutcome.Commitment or TriageOutcome.WaitingOn;

    private static void MarkElsewhereSeen(List<string> keys, Dictionary<string, List<Signal>> elsewhere, Dictionary<string, TriageDecision> decisions)
    {
        foreach (var key in keys)
        {
            decisions[key].ElsewhereSeenAt = elsewhere[key][^1].ReceivedAt;
        }
    }

    /// <summary>
    /// Earlier versions kept one judgment and one item for a whole DM or chat. Moves each open one, and the items
    /// behind it, to a topic of its own, so later messages in that chat can become items of their own.
    /// </summary>
    private static async Task MoveWholeChatJudgmentsToTopicsAsync(
        AssistantDbContext db, ConversationSource source, Dictionary<string, TriageDecision> decisions, CancellationToken cancellationToken)
    {
        var whole = decisions.Values.Where(d => IsWholeChat(source, d.Key) && IsOpen(d.Outcome)).ToList();
        foreach (var d in whole)
        {
            var key = ConversationTopic.KeyFor(d.Key, d.TriagedAt.Ticks);
            var topic = new TriageDecision
            {
                Source = d.Source,
                Key = key,
                Outcome = d.Outcome,
                Title = d.Title,
                Summary = d.Summary,
                DueAt = d.DueAt,
                DueConfidence = d.DueConfidence,
                Priority = d.Priority,
                Requester = d.Requester,
                Trust = d.Trust,
                Confidence = d.Confidence,
                Reasoning = d.Reasoning,
                Url = d.Url,
                SourceContent = d.SourceContent,
                LastSeenAt = d.LastSeenAt,
                ElsewhereSeenAt = d.ElsewhereSeenAt,
                RequestStartedAt = d.RequestStartedAt,
                TriagedAt = d.TriagedAt,
                Model = d.Model,
            };
            db.TriageDecisions.Add(topic);
            decisions[key] = topic;
            d.Outcome = TriageOutcome.Ignore;

            var oldId = $"{source.DecisionScope}:{d.Key}";
            foreach (var item in await db.Items.Where(i => i.Source == source.SignalSource && i.ExternalId == oldId).ToListAsync(cancellationToken))
            {
                item.ExternalId = $"{source.DecisionScope}:{key}";
            }
        }

        if (whole.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The topics new messages in a chat could belong to. A topic whose item you closed is finished, so anything
    /// said after that is new.
    /// </summary>
    private static async Task<List<TriageDecision>> OpenTopicsAsync(
        AssistantDbContext db, ConversationSource source, string conversation, Dictionary<string, TriageDecision> decisions, CancellationToken cancellationToken)
    {
        var topics = decisions.Values
            .Where(d => d.Key != conversation && ConversationTopic.ConversationOf(d.Key) == conversation && IsOpen(d.Outcome))
            .OrderBy(d => d.LastSeenAt)
            .ToList();
        var ids = topics.Select(d => $"{source.DecisionScope}:{d.Key}").ToList();
        var closed = await db.Items.AsNoTracking()
            .Where(i => i.Source == source.SignalSource && ids.Contains(i.ExternalId!) && !i.ClosedBySync
                && (i.Status == ItemStatus.Done || i.Status == ItemStatus.Dismissed))
            .Select(i => i.ExternalId!)
            .ToListAsync(cancellationToken);

        foreach (var topic in topics.Where(d => closed.Contains($"{source.DecisionScope}:{d.Key}")).ToList())
        {
            topic.Outcome = TriageOutcome.Resolved;
            topics.Remove(topic);
        }

        return topics;
    }

    private void ApplyChat(
        AssistantDbContext db, ConversationSource source, string conversation, ChatJudgment chat, List<TriageDecision> openTopics,
        List<Signal> messages, DateTime readUpTo, Dictionary<string, TriageDecision> decisions, SenderHistory history)
    {
        foreach (var topic in chat.Topics)
        {
            var cited = topic.Messages.Where(n => n >= 1 && n <= messages.Count).Distinct().Order().Select(n => messages[n - 1]).ToList();
            var decision = topic.Topic is { } number && number >= 1 && number <= openTopics.Count ? openTopics[number - 1] : null;
            if (decision is null)
            {
                // Only something open that the new messages actually say starts a topic, so an old request
                // shown as context can't come back.
                if (!IsOpen(TriageService.ParseDecision(topic.Judgment.Decision)) || !cited.Any(m => m.ReceivedAt > readUpTo))
                {
                    continue;
                }

                var id = cited[0].ReceivedAt.Ticks;
                while (decisions.ContainsKey(ConversationTopic.KeyFor(conversation, id)))
                {
                    id++;
                }

                decision = new TriageDecision { Source = source.DecisionScope, Key = ConversationTopic.KeyFor(conversation, id) };
                db.TriageDecisions.Add(decision);
                decisions[decision.Key] = decision;
                Apply(topic.Judgment, decision, cited);
            }
            else
            {
                // A reply like "Sure" says nothing about the request, so the item keeps pointing at what was asked.
                var (title, url, content, trust, requester) = (decision.Title, decision.Url, decision.SourceContent, decision.Trust, decision.Requester);
                Apply(topic.Judgment, decision, cited.Count > 0 ? cited : messages);
                if (cited.All(m => m.FromMe))
                {
                    (decision.Url, decision.SourceContent) = (url, content);
                    decision.Trust = decision.Outcome == TriageOutcome.Commitment ? SenderTrust.Self : trust;
                    decision.Requester = string.IsNullOrWhiteSpace(topic.Judgment.Requester) ? requester : topic.Judgment.Requester;
                }

                decision.Title = string.IsNullOrWhiteSpace(topic.Judgment.Title) ? title : decision.Title;
            }

            WeighSender(decision, history);
        }
    }

    /// <summary>
    /// For each open item, your messages since it was last judged that sit outside its thread but might answer
    /// it: in the same channel or chat, or in a DM or group chat with someone who asked. Oldest first.
    /// </summary>
    private static async Task<Dictionary<string, List<Signal>>> ElsewhereAsync(
        AssistantDbContext db, ConversationSource source, ElsewhereRule rule, IEnumerable<TriageDecision> decisions, CancellationToken cancellationToken)
    {
        var open = decisions.Where(d => IsOpen(d.Outcome)).ToList();
        if (open.Count == 0)
        {
            return [];
        }

        var since = open.Min(d => d.LastSeenAt);
        var mine = await db.Signals.AsNoTracking()
            .Where(s => s.Source == source.SignalSource && s.FromMe && s.ReceivedAt > since && s.ThreadKey != null)
            .OrderBy(s => s.ReceivedAt)
            .ToListAsync(cancellationToken);
        if (mine.Count == 0)
        {
            return [];
        }

        // Who has written in each thread with an open item, and in each direct conversation.
        var openKeys = open.Select(d => ConversationTopic.ConversationOf(d.Key)).Distinct().ToList();
        var directKinds = rule.DirectKinds.ToList();
        var others = await db.Signals.AsNoTracking()
            .Where(s => s.Source == source.SignalSource && !s.FromMe && s.AuthorAddress != null
                && (openKeys.Contains(s.ThreadKey!) || directKinds.Contains(s.Kind!)))
            .Select(s => new { s.ThreadKey, s.AuthorAddress, s.Kind })
            .Distinct()
            .ToListAsync(cancellationToken);

        var askers = others.Where(o => openKeys.Contains(o.ThreadKey!)).ToLookup(o => o.ThreadKey!, o => o.AuthorAddress!);
        var directPeople = others.Where(o => directKinds.Contains(o.Kind!))
            .GroupBy(o => rule.PlaceOf(o.ThreadKey!))
            .ToDictionary(g => g.Key, g => g.Select(o => o.AuthorAddress!).ToHashSet(StringComparer.OrdinalIgnoreCase));

        var result = new Dictionary<string, List<Signal>>(StringComparer.Ordinal);
        foreach (var d in open)
        {
            var conversation = ConversationTopic.ConversationOf(d.Key);
            var place = rule.PlaceOf(conversation);
            var asked = askers[conversation].ToList();
            var related = mine
                .Where(m => m.ThreadKey != conversation && m.ReceivedAt > d.LastSeenAt)
                .Where(m =>
                {
                    var where = rule.PlaceOf(m.ThreadKey!);
                    return where == place || (directPeople.TryGetValue(where, out var people) && asked.Any(people.Contains));
                })
                .TakeLast(MessagesElsewhere)
                .ToList();

            if (related.Count > 0)
            {
                result[d.Key] = related;
            }
        }

        return result;
    }

    private static ThreadMessage ToThreadMessage(Signal m) => new(m.Author ?? "?", m.Recipients ?? "", m.ReceivedAt, m.FromMe, m.Subject, m.Body);

    /// <summary>
    /// Adds signals from earlier in the same sync to <paramref name="existing"/>. They aren't saved yet, so a database
    /// query can't see them, and adding them a second time breaks the unique index.
    /// </summary>
    public static void AddUnsaved(AssistantDbContext db, string signalSource, Dictionary<string, Signal> existing, IReadOnlyCollection<string> ids)
    {
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        foreach (var s in db.Signals.Local.Where(x => x.Source == signalSource && wanted.Contains(x.ExternalId)))
        {
            existing.TryAdd(s.ExternalId, s);
        }
    }

    public async Task PruneAsync(string signalSource, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
        var cutoff = time.GetUtcNow().UtcDateTime - SignalRetention;
        await db.Signals.Where(s => s.Source == signalSource && s.ReceivedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
    }

    private void Apply(ThreadJudgment judgment, TriageDecision decision, List<Signal> messages)
    {
        var latest = messages[^1];
        var latestInbound = messages.LastOrDefault(m => !m.FromMe);
        var wasResolved = decision.Outcome == TriageOutcome.Resolved;

        decision.Outcome = TriageService.ParseDecision(judgment.Decision);
        if (IsOpen(decision.Outcome) && (wasResolved || judgment.NewRequest))
        {
            decision.RequestStartedAt = time.GetUtcNow().UtcDateTime;
        }

        decision.Title = string.IsNullOrWhiteSpace(judgment.Title) ? latest.Subject : judgment.Title.Trim();
        decision.Summary = judgment.Summary;
        decision.DueAt = TriageService.ParseDue(judgment.Due);
        decision.DueConfidence = decision.DueAt is null ? null : Math.Clamp(judgment.DueConfidence ?? 0.5, 0, 1);
        decision.Priority = TriageService.ParsePriority(judgment.Priority);
        decision.Requester = string.IsNullOrWhiteSpace(judgment.Requester) ? latestInbound?.Author : judgment.Requester;
        decision.Trust = decision.Outcome == TriageOutcome.Commitment ? SenderTrust.Self : latestInbound?.AuthorTrust ?? SenderTrust.Unknown;
        decision.Confidence = Math.Clamp(judgment.Confidence, 0, 1);
        decision.Reasoning = judgment.Reasoning;
        decision.Url = latest.Url;
        decision.SourceContent = Truncate((latestInbound ?? latest).Body);
        decision.LastSeenAt = messages.Max(m => m.ReceivedAt);
        decision.TriagedAt = time.GetUtcNow().UtcDateTime;
        decision.Model = triage.ModelName;
    }

    /// <summary>
    /// Asks from someone whose items you keep dismissing go to Needs a look instead of your list. The model sees
    /// the same history, but this holds even when it doesn't follow it.
    /// </summary>
    private void WeighSender(TriageDecision decision, SenderHistory history)
    {
        var threshold = settings.Current.Triage.ReviewThreshold;
        if (decision.Outcome is TriageOutcome.Ask or TriageOutcome.WaitingOn
            && history.For(decision.Requester) is { MostlyNoise: true } sender
            && decision.Confidence >= threshold)
        {
            decision.Confidence = Math.Max(0, threshold - 0.05);
            decision.Reasoning = $"{decision.Reasoning} You dismissed {sender.NotUseful} earlier items from {sender.Name} and kept none, so check this one.".Trim();
        }
    }

    /// <summary>The next week of meetings, so triage can resolve "before the review". Optional.</summary>
    private async Task<string?> CalendarSummaryAsync(CancellationToken cancellationToken)
    {
        if (!settings.Current.Outlook.CalendarEnabled)
        {
            return null;
        }

        try
        {
            var now = time.GetUtcNow().UtcDateTime;
            var events = await graph.GetCalendarViewAsync(now, now.AddDays(7), cancellationToken);
            return string.Join("\n", events.Where(e => !e.IsCancelled).Take(40)
                .Select(e => $"- {e.Start.ToLocalTime():ddd MMM d h:mm tt}: {e.Subject}"));
        }
        catch (ConnectorException ex)
        {
            logger.LogDebug(ex, "Calendar summary unavailable");
            return null;
        }
    }

    private static string? Truncate(string? text) => text is { Length: > 8000 } ? text[..8000] + "\n…" : text;
}
