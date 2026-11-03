using System.Globalization;
using Assistant.Connectors.Slack;
using Assistant.Core.Connectors;
using Assistant.Core.Items;
using Assistant.Core.Settings;
using Assistant.Core.Signals;
using Assistant.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Assistant.Agent.Triage;

/// <summary>
/// Slack direct messages, chosen channels, and mentions anywhere, triaged like email. Slack doesn't say which DMs
/// have new messages, so each sync checks the recently active ones and works through the quiet ones in turn.
/// </summary>
public sealed class SlackConnector(
    SlackClient slack,
    TriageService triage,
    ThreadTriageEngine engine,
    UserSettingsStore settings,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<SlackConnector> logger) : IConnector
{
    public const string Scope = "slack";

    public static readonly ConversationSource Source = new("slack", Scope, "Slack conversation", "https://app.slack.com",
        new ElsewhereRule(key => key.Split(':')[0], new HashSet<string>(["dm", "group-dm"])));

    private const string SeenPrefix = "slack:seen:";
    private const string MineSinceKey = "slack:mine:since";
    private const string ContextOnly = "Your message outside the conversations being read; used only to spot answers.";
    private const int ConversationsPerSync = 40;
    private const int MentionThreadsPerSync = 20;
    private static readonly TimeSpan RecentlyActive = TimeSpan.FromDays(3);
    private static readonly TimeSpan ListLifetime = TimeSpan.FromHours(1);

    /// <summary>Message types that are conversation. Joins, topic changes, pins, and the like are skipped.</summary>
    private static readonly HashSet<string?> KeptSubtypes = [null, "bot_message", "thread_broadcast", "file_share", "me_message"];

    private SlackIdentity? _me;
    private List<SlackConversation>? _dms;
    private DateTime _dmsLoadedAt;
    private readonly Dictionary<string, SlackUser?> _users = new(StringComparer.Ordinal);

    /// <summary>Spacing between history calls, well under Slack's per-minute limits. Tests set this to zero.</summary>
    public TimeSpan Pace { get; set; } = TimeSpan.FromSeconds(1.3);

    public string Id => "slack";

    public string DisplayName => "Slack";

    public TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, settings.Current.Slack.IntervalMinutes));

    public string? NotReadyReason()
    {
        if (!settings.Current.Slack.Enabled) return "Turned off.";
        if (!slack.HasToken) return "Add your Slack user token first.";
        return triage.UnavailableReason is { } ai ? $"Needs an AI provider for triage. {ai}" : null;
    }

    public async Task<ConnectorSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var s = settings.Current.Slack;
        var warnings = new List<string>();
        _me ??= await slack.AuthTestAsync(cancellationToken);

        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
            var now = time.GetUtcNow().UtcDateTime;
            var backfill = now.AddDays(-Math.Max(1, s.BackfillDays));
            await RepairMovedThreadsAsync(db, cancellationToken);

            try
            {
                await PullConversationsAsync(db, s, now, backfill, warnings, cancellationToken);
            }
            catch (ConnectorException ex) when (ex.Message.Contains("rate limit", StringComparison.Ordinal))
            {
                warnings.Add(ex.Message); // Keep what was read so far.
            }

            if (s.IncludeMentions)
            {
                try
                {
                    await PullMentionsAsync(db, s, backfill, cancellationToken);
                }
                catch (ConnectorException ex)
                {
                    warnings.Add($"Mentions: {ex.Message}");
                }
            }

            try
            {
                await PullMyMessagesAsync(db, backfill, cancellationToken);
            }
            catch (ConnectorException ex)
            {
                warnings.Add($"Your replies elsewhere: {ex.Message}");
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        var run = await engine.TriageChangedThreadsAsync(Source, s.MaxThreadsPerSync, [_me.User], cancellationToken);
        warnings.AddRange(run.Warnings());
        logger.LogInformation("Slack: triaged {Count} threads, {Backlog} waiting", run.Triaged, run.Backlog);
        await engine.PruneAsync(Source.SignalSource, cancellationToken);

        return new ConnectorSnapshot(await engine.BuildItemsAsync(Source, cancellationToken), new HashSet<string>(), warnings);
    }

    private async Task PullConversationsAsync(
        AssistantDbContext db, SlackSettings s, DateTime now, DateTime backfill, List<string> warnings, CancellationToken cancellationToken)
    {
        var conversations = new List<SlackConversation>();
        if (s.IncludeDirectMessages)
        {
            if (_dms is null || now - _dmsLoadedAt > ListLifetime)
            {
                _dms = await slack.GetMyConversationsAsync("im,mpim", cancellationToken);
                _dmsLoadedAt = now;
            }

            conversations.AddRange(_dms);
        }

        var chosen = s.Channels.Select(c => new SlackConversation(c.Id, c.Name, false, false, false, null)).ToList();
        var seen = await db.ConnectorStates.Where(x => x.Key.StartsWith(SeenPrefix)).ToDictionaryAsync(x => x.Key[SeenPrefix.Length..], cancellationToken);

        // Chosen channels and recently active DMs every time; the rest in turn, least recently checked first.
        var latest = (SlackConversation c) => seen.TryGetValue(c.Id, out var st) && Parse(st.Value) is { } at ? at : (DateTime?)null;
        var hot = conversations.Where(c => latest(c) is { } at && now - at < RecentlyActive).ToList();
        var cold = conversations.Except(hot)
            .OrderBy(c => seen.TryGetValue(c.Id, out var st) ? st.UpdatedAt : DateTime.MinValue)
            .ToList();
        var batch = chosen.Concat(hot).Concat(cold).DistinctBy(c => c.Id).Take(Math.Max(ConversationsPerSync, chosen.Count + hot.Count)).ToList();

        foreach (var conversation in batch)
        {
            await Task.Delay(Pace, cancellationToken);
            var oldest = latest(conversation) ?? backfill;
            List<SlackMessage> messages;
            try
            {
                messages = await slack.GetHistoryAsync(conversation.Id, oldest, cancellationToken);
            }
            catch (ConnectorException ex) when (SlackClient.IsUnreadableConversation(ex))
            {
                // One conversation you can't read anymore (left, archived, or deleted) shouldn't stop the rest.
                var error = ex.Data[SlackClient.ErrorKey];
                logger.LogInformation("Slack: skipped {Conversation} ({Error})", conversation.Name ?? conversation.Id, error);
                if (s.Channels.FirstOrDefault(c => c.Id == conversation.Id) is { } picked)
                {
                    warnings.Add($"Couldn't read #{picked.Name} (Slack says {error}). If you left it or it was archived, untick it below.");
                }

                messages = [];
            }

            var subject = await SubjectAsync(conversation, cancellationToken);
            var kind = conversation.IsIm ? "dm" : conversation.IsMpim ? "group-dm" : "channel";

            foreach (var root in messages.Where(m => m.ReplyCount > 0 && Parse(m.LatestReply) > oldest))
            {
                // Thread replies aren't in the history, so read each thread with new replies.
                await Task.Delay(Pace, cancellationToken);
                List<SlackMessage> thread;
                try
                {
                    thread = await slack.GetRepliesAsync(conversation.Id, root.Ts, cancellationToken);
                }
                catch (ConnectorException ex) when (SlackClient.IsUnreadableConversation(ex))
                {
                    continue; // Deleted while we were reading.
                }

                await StoreAsync(db, conversation.Id, $"{conversation.Id}:{root.Ts}", kind, subject, thread, cancellationToken);
            }

            // Replies sent "also to the channel" show up in the history too, but they belong to their thread.
            await StoreAsync(db, conversation.Id, conversation.Id, kind, subject, [.. messages.Where(m => m.ReplyCount == 0 && !m.IsReply)], cancellationToken);

            var newest = messages.Select(m => (DateTime?)m.At).Max();
            var key = SeenPrefix + conversation.Id;
            if (!seen.TryGetValue(conversation.Id, out var state))
            {
                state = new ConnectorState { Key = key, Value = (newest ?? oldest).ToString("O") };
                db.ConnectorStates.Add(state);
                seen[conversation.Id] = state;
            }
            else if (newest is not null)
            {
                state.Value = newest.Value.ToString("O");
            }

            state.UpdatedAt = now;
        }

        var waiting = conversations.Count + chosen.Count - batch.Count;
        if (waiting > 0 && seen.Count < conversations.Count)
        {
            warnings.Add($"Still reading older Slack conversations for the first time ({waiting} left).");
        }
    }

    /// <summary>
    /// Threads where someone mentioned you, found through search. DMs are skipped when they're already read in full.
    /// </summary>
    private async Task PullMentionsAsync(AssistantDbContext db, SlackSettings s, DateTime backfill, CancellationToken cancellationToken)
    {
        var after = backfill.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var hits = await slack.SearchAsync($"<@{_me!.UserId}> after:{after}", 50, cancellationToken);
        var chosen = s.Channels.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var known = (await db.Signals.AsNoTracking()
                .Where(x => x.Source == Source.SignalSource)
                .Select(x => x.ExternalId)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        var fresh = hits
            .Where(h => !known.Contains($"{h.ChannelId}:{h.Ts}"))
            .Where(h => !chosen.Contains(h.ChannelId) && !(s.IncludeDirectMessages && _dms?.Any(d => d.Id == h.ChannelId) == true))
            .DistinctBy(h => (h.ChannelId, h.ThreadTs ?? h.Ts))
            .Take(MentionThreadsPerSync);

        foreach (var hit in fresh)
        {
            await Task.Delay(Pace, cancellationToken);
            var threadTs = hit.ThreadTs ?? hit.Ts;
            List<SlackMessage> thread;
            try
            {
                thread = await slack.GetRepliesAsync(hit.ChannelId, threadTs, cancellationToken);
            }
            catch (ConnectorException ex) when (SlackClient.IsUnreadableConversation(ex))
            {
                logger.LogInformation("Slack: skipped mention in {Channel} ({Error})", hit.ChannelName ?? hit.ChannelId, ex.Data[SlackClient.ErrorKey]);
                continue;
            }

            var subject = $"#{hit.ChannelName ?? hit.ChannelId}: {Headline(thread.FirstOrDefault())}";
            await StoreAsync(db, hit.ChannelId, $"{hit.ChannelId}:{threadTs}", "mention", subject, thread, cancellationToken);
        }
    }

    /// <summary>
    /// Earlier versions moved an @mention's message into its channel's conversation when you started reading
    /// that channel in full, leaving the mention's item with no messages. Puts those messages back.
    /// </summary>
    private static async Task RepairMovedThreadsAsync(AssistantDbContext db, CancellationToken cancellationToken)
    {
        var orphans = await db.TriageDecisions.AsNoTracking()
            .Where(d => d.Source == Scope && d.Key.Contains(":")
                && !db.Signals.Any(x => x.Source == Source.SignalSource && x.ThreadKey == d.Key))
            .Select(d => d.Key)
            .ToListAsync(cancellationToken);
        if (orphans.Count == 0)
        {
            return;
        }

        foreach (var signal in await db.Signals.Where(x => x.Source == Source.SignalSource && orphans.Contains(x.ExternalId)).ToListAsync(cancellationToken))
        {
            signal.ThreadKey = signal.ExternalId;
        }
    }

    /// <summary>
    /// Everything you've posted lately, found with one search, so an answer you gave in a channel's main
    /// timeline or in a DM can close an item asked somewhere else. Messages in threads already being read join
    /// them; the rest are kept only as possible answers and aren't triaged on their own.
    /// </summary>
    private async Task PullMyMessagesAsync(AssistantDbContext db, DateTime backfill, CancellationToken cancellationToken)
    {
        var state = await db.ConnectorStates.FirstOrDefaultAsync(x => x.Key == MineSinceKey, cancellationToken);
        var since = Parse(state?.Value) is { } at && at > backfill ? at : backfill;
        var after = since.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var hits = await slack.SearchAsync($"from:<@{_me!.UserId}> after:{after}", 100, cancellationToken);
        if (hits.Count == 0)
        {
            return;
        }

        var ids = hits.Select(h => $"{h.ChannelId}:{h.Ts}").ToList();
        var known = (await db.Signals.AsNoTracking()
                .Where(x => x.Source == Source.SignalSource && ids.Contains(x.ExternalId))
                .Select(x => x.ExternalId)
                .ToListAsync(cancellationToken))
            .Concat(db.Signals.Local.Where(x => x.Source == Source.SignalSource).Select(x => x.ExternalId))
            .ToHashSet(StringComparer.Ordinal);

        var fresh = hits.Where(h => !known.Contains($"{h.ChannelId}:{h.Ts}")).ToList();
        var keys = fresh.Select(ThreadKeyOf).Distinct().ToList();
        var threads = (await db.Signals.AsNoTracking()
                .Where(x => x.Source == Source.SignalSource && x.FilteredReason == null && keys.Contains(x.ThreadKey!))
                .Select(x => new { x.ThreadKey, x.Subject })
                .ToListAsync(cancellationToken))
            .Concat(db.Signals.Local.Where(x => x.Source == Source.SignalSource && x.FilteredReason == null && keys.Contains(x.ThreadKey!))
                .Select(x => new { x.ThreadKey, x.Subject }))
            .DistinctBy(x => x.ThreadKey)
            .ToDictionary(x => x.ThreadKey!, x => x.Subject, StringComparer.Ordinal);

        foreach (var hit in fresh)
        {
            var threadKey = ThreadKeyOf(hit);
            var tracked = threads.TryGetValue(threadKey, out var subject);
            subject ??= hit.IsIm ? "Slack DM" : hit.IsMpim ? "Slack group DM" : $"#{hit.ChannelName ?? hit.ChannelId}";
            var kind = hit.IsIm ? "dm" : hit.IsMpim ? "group-dm" : "channel";
            var message = new SlackMessage(hit.Ts, hit.ThreadTs, null, _me.UserId, _me.TeamId, null, hit.Text, 0, null);
            await StoreAsync(db, hit.ChannelId, threadKey, kind, subject, [message], cancellationToken, tracked ? null : ContextOnly);
        }

        var newest = hits.Max(h => h.At).ToString("O");
        if (state is null)
        {
            db.ConnectorStates.Add(new ConnectorState { Key = MineSinceKey, Value = newest, UpdatedAt = time.GetUtcNow().UtcDateTime });
        }
        else
        {
            state.Value = newest;
            state.UpdatedAt = time.GetUtcNow().UtcDateTime;
        }

        static string ThreadKeyOf(SlackSearchHit h) => h.ThreadTs is { } t && t != h.Ts ? $"{h.ChannelId}:{t}" : h.ChannelId;
    }

    private async Task StoreAsync(
        AssistantDbContext db, string channelId, string threadKey, string kind, string subject, IReadOnlyList<SlackMessage> messages,
        CancellationToken cancellationToken, string? filteredReason = null)
    {
        var ids = messages.Select(m => $"{channelId}:{m.Ts}").ToList();
        var existing = await db.Signals.Where(x => x.Source == Source.SignalSource && ids.Contains(x.ExternalId)).ToDictionaryAsync(x => x.ExternalId, cancellationToken);
        ThreadTriageEngine.AddUnsaved(db, Source.SignalSource, existing, ids);

        foreach (var m in messages)
        {
            if (!KeptSubtypes.Contains(m.Subtype))
            {
                continue;
            }

            var text = await TextAsync(m.Text, cancellationToken);
            if (text.Length == 0)
            {
                continue;
            }

            var externalId = $"{channelId}:{m.Ts}";
            if (!existing.TryGetValue(externalId, out var signal))
            {
                signal = new Signal { Source = Source.SignalSource, ExternalId = externalId };
                db.Signals.Add(signal);
                existing[externalId] = signal;
            }

            var fromMe = m.UserId == _me!.UserId;
            var user = m.UserId is null ? null : await UserAsync(m.UserId, cancellationToken);
            // A message already read as its own thread (an @mention, say) stays there when its channel is later
            // read in full. Moving it would leave that thread's item with nothing behind it.
            if (signal.ThreadKey is null || !(signal.ThreadKey.Contains(':') && !threadKey.Contains(':')))
            {
                signal.ThreadKey = threadKey;
            }

            signal.Kind = kind;
            signal.Author = m.BotName ?? user?.Name ?? m.UserId ?? "someone";
            signal.AuthorAddress = m.UserId;
            signal.FromMe = fromMe;
            signal.AuthorTrust = fromMe ? SenderTrust.Self : TrustOf(m, user);
            signal.Subject = subject;
            signal.Body = (m.Text.Contains($"<@{_me.UserId}>", StringComparison.Ordinal) ? "[This message @mentions the user.]\n" : "") + text;
            signal.Url = SlackClient.Permalink(_me.Url, channelId, m.Ts, m.ThreadTs);
            signal.ReceivedAt = m.At;
            signal.FilteredReason = filteredReason;
        }
    }

    /// <summary>Members of your workspace count as your organization. Slack Connect users and apps don't.</summary>
    private SenderTrust TrustOf(SlackMessage m, SlackUser? user)
    {
        if (m.BotName is not null || user is null || user.IsBot)
        {
            return SenderTrust.Unknown;
        }

        var team = m.UserTeam ?? user.TeamId;
        return team is null ? SenderTrust.Unknown
            : team == _me!.TeamId ? SenderTrust.Organization
            : SenderTrust.External;
    }

    private async Task<SlackUser?> UserAsync(string userId, CancellationToken cancellationToken)
    {
        if (!_users.TryGetValue(userId, out var user))
        {
            user = await slack.GetUserAsync(userId, cancellationToken);
            _users[userId] = user;
        }

        return user;
    }

    private async Task<string> TextAsync(string text, CancellationToken cancellationToken)
    {
        // Look up everyone referenced first, since the converter is synchronous.
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, "<@([A-Z0-9]+)"))
        {
            await UserAsync(match.Groups[1].Value, cancellationToken);
        }

        return SlackText.ToText(text, id => _users.GetValueOrDefault(id)?.Name);
    }

    private async Task<string> SubjectAsync(SlackConversation c, CancellationToken cancellationToken)
    {
        if (c.IsIm)
        {
            var other = c.ImUserId is null ? null : await UserAsync(c.ImUserId, cancellationToken);
            return $"Slack DM with {other?.Name ?? "someone"}";
        }

        if (c.IsMpim)
        {
            // Group DM names look like "mpdm-ann--bob--cy-1".
            var names = System.Text.RegularExpressions.Regex.Replace(c.Name ?? "", @"^mpdm-|-\d+$", "").Split("--");
            return "Slack group DM with " + string.Join(", ", names.Where(n => n.Length > 0 && n != _me!.User));
        }

        return "#" + (c.Name ?? c.Id);
    }

    private static string Headline(SlackMessage? m)
    {
        var line = SlackText.ToText(m?.Text ?? "", _ => null).Split('\n', 2)[0].Trim();
        return line.Length == 0 ? "(no text)" : line.Length > 80 ? line[..80] + "…" : line;
    }

    private static DateTime? Parse(string? value)
    {
        if (value is null) return null;
        // Slack timestamps ("1712345678.000100") or the ISO dates this connector stores.
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return SlackClient.FromTs(value);
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;
    }
}
