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
/// Teams chats and channels. Chats with new activity are read in full. In channels, only threads you posted in or
/// were mentioned in are kept. Both go through the same triage as email.
/// </summary>
public sealed class TeamsConnector(
    GraphClient graph,
    TriageService triage,
    ThreadTriageEngine engine,
    IMicrosoftTokenProvider auth,
    UserSettingsStore settings,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<TeamsConnector> logger) : IConnector
{
    public const string Scope = "teams";

    public static readonly ConversationSource Source = new("teams", Scope, "Teams chat", "https://teams.microsoft.com",
        new ElsewhereRule(PlaceOf, new HashSet<string>(["chat:oneOnOne", "chat:group"])));

    /// <summary>
    /// Chat thread keys are the chat ID ("19:abc@thread.v2"). Channel thread keys are "channelId:rootId", so the
    /// channel is everything before the first colon after the "@".
    /// </summary>
    public static string PlaceOf(string threadKey)
    {
        var colon = threadKey.IndexOf(':', Math.Max(0, threadKey.IndexOf('@')));
        return colon > 0 ? threadKey[..colon] : threadKey;
    }

    private const string ChatsWatermark = "teams:chats:since";
    private static readonly TimeSpan ChannelListLifetime = TimeSpan.FromHours(1);

    private (string Id, string? Name, string? TenantId)? _me;
    private List<TeamsChannel>? _channels;
    private DateTime _channelsLoadedAt;

    /// <summary>
    /// Graph allows about one Teams request per second per user, so calls are spaced out. Tests set this to zero.
    /// </summary>
    public TimeSpan Pace { get; set; } = TimeSpan.FromSeconds(1.1);

    public string Id => "teams";

    public string DisplayName => "Teams";

    public TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, settings.Current.Teams.IntervalMinutes));

    public string? NotReadyReason()
    {
        var t = settings.Current.Teams;
        if (!t.ChatsEnabled && !t.ChannelsEnabled) return "Turned off.";
        if (!auth.IsConfigured) return "Set up Microsoft sign-in in Settings first.";
        return triage.UnavailableReason is { } ai ? $"Needs an AI provider for triage. {ai}" : null;
    }

    public async Task<ConnectorSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var t = settings.Current.Teams;
        var warnings = new List<string>();
        _me ??= await graph.GetMyIdAsync(cancellationToken);

        if (t.ChatsEnabled)
        {
            warnings.AddRange(await PullChatsAsync(t, cancellationToken));
        }

        if (t.ChannelsEnabled)
        {
            try
            {
                await PullChannelsAsync(t, cancellationToken);
            }
            catch (ConnectorException ex)
            {
                // Channels need admin consent. Chats keep working without it.
                warnings.Add($"Channels: {ex.Message}");
            }
        }

        var me = new[] { _me.Value.Name ?? "you" };
        var run = await engine.TriageChangedThreadsAsync(Source, t.MaxThreadsPerSync, me, cancellationToken);
        warnings.AddRange(run.Warnings());
        logger.LogInformation("Teams: triaged {Count} threads, {Backlog} waiting", run.Triaged, run.Backlog);
        await engine.PruneAsync(Source.SignalSource, cancellationToken);

        return new ConnectorSnapshot(await engine.BuildItemsAsync(Source, cancellationToken), new HashSet<string>(), warnings);
    }

    /// <summary>Reads chats with activity since the last sync, oldest first, so a capped sync picks up where it stopped.</summary>
    private async Task<List<string>> PullChatsAsync(TeamsSettings t, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
        var now = time.GetUtcNow().UtcDateTime;
        var state = await db.ConnectorStates.FindAsync([ChatsWatermark], cancellationToken);
        var since = state?.Value is { } v && DateTime.TryParse(v, null, System.Globalization.DateTimeStyles.RoundtripKind, out var s)
            ? s
            : now.AddDays(-Math.Max(1, t.BackfillDays));

        var chats = await graph.GetChatsAsync(500, since, cancellationToken);
        var active = chats.Where(c => c.LastMessageAt > since).OrderBy(c => c.LastMessageAt).ToList();
        var batch = active.Take(Math.Max(1, t.MaxChatsPerSync)).ToList();

        var refused = new List<string>();
        ConnectorException? refusal = null;

        foreach (var chat in batch)
        {
            await Task.Delay(Pace, cancellationToken);
            List<TeamsMessage> messages;
            try
            {
                // A little overlap covers clock skew between the chat preview and its messages.
                messages = await graph.GetChatMessagesAsync(chat.Id, since.AddMinutes(-5), cancellationToken);
            }
            catch (ConnectorException ex) when (ex.Data["status"] is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
            {
                // Graph lists some chats it won't let you read, like a meeting chat you've left or one another
                // organization owns. Skipping it lets the rest sync, and moves past it so it doesn't fail every time.
                refused.Add(ChatSubject(chat));
                refusal = ex;
                logger.LogWarning("Teams wouldn't let chat {ChatId} be read; skipped", chat.Id);
                continue;
            }

            await StoreAsync(db, chat.Id, "chat:" + chat.ChatType, ChatSubject(chat), chat.WebUrl, messages, chat.MemberNames, cancellationToken);
        }

        // Every chat refused means the permission itself is missing, which skipping would hide.
        if (refusal is not null && batch.Count > 1 && refused.Count == batch.Count)
        {
            throw refusal;
        }

        if (refused.Count > 0)
        {
            warnings.Add($"Skipped {(refused.Count == 1 ? "a chat" : $"{refused.Count} chats")} Teams wouldn't let me read: {string.Join("; ", refused.Take(3))}{(refused.Count > 3 ? "; and more" : "")}. "
                + "This usually means a meeting chat you've left, or one owned by another organization.");
        }

        var watermark = batch.Count > 0 ? batch[^1].LastMessageAt!.Value : since;
        if (state is null)
        {
            db.ConnectorStates.Add(new ConnectorState { Key = ChatsWatermark, Value = watermark.ToString("O"), UpdatedAt = now });
        }
        else
        {
            state.Value = watermark.ToString("O");
            state.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        var left = active.Count - batch.Count;
        if (left > 0)
        {
            warnings.Add($"{left} more chat{(left == 1 ? "" : "s")} with new messages will be read on the next syncs.");
        }

        return warnings;
    }

    private async Task PullChannelsAsync(TeamsSettings t, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (_channels is null || now - _channelsLoadedAt > ChannelListLifetime)
        {
            _channels = await graph.GetMyChannelsAsync(cancellationToken);
            _channelsLoadedAt = now;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
        var cutoff = now.AddDays(-Math.Max(1, t.BackfillDays));

        foreach (var channel in _channels)
        {
            await Task.Delay(Pace, cancellationToken);
            foreach (var (root, replies) in await graph.GetChannelThreadsAsync(channel, cancellationToken))
            {
                var all = replies.Prepend(root).ToList();
                if (all.Max(m => m.LastModifiedAt) < cutoff || !Involves(all))
                {
                    continue;
                }

                var subject = $"{channel.TeamName} › {channel.Name}: {Headline(root)}";
                await StoreAsync(db, $"{channel.Id}:{root.Id}", "channel", subject, root.WebUrl, all, [], cancellationToken);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A channel thread is yours if you posted in it or someone mentioned you.</summary>
    private bool Involves(IEnumerable<TeamsMessage> messages) =>
        messages.Any(m => m.FromId == _me!.Value.Id || m.MentionedUserIds.Contains(_me!.Value.Id));

    private async Task StoreAsync(
        AssistantDbContext db, string threadKey, string kind, string subject, string? threadUrl,
        IReadOnlyList<TeamsMessage> messages, IReadOnlyList<string> members, CancellationToken cancellationToken)
    {
        var ids = messages.Select(m => $"{threadKey}:{m.Id}").ToList();
        var existing = await db.Signals.Where(s => s.Source == Source.SignalSource && ids.Contains(s.ExternalId)).ToDictionaryAsync(s => s.ExternalId, cancellationToken);
        ThreadTriageEngine.AddUnsaved(db, Source.SignalSource, existing, ids);
        var recipients = string.Join("; ", members.Where(n => n != _me!.Value.Name));

        foreach (var m in messages)
        {
            var externalId = $"{threadKey}:{m.Id}";
            existing.TryGetValue(externalId, out var signal);
            var body = HtmlText.ToText(m.BodyHtml);

            // Join/leave notices, call records, and the like aren't conversation.
            if (m.MessageType != "message" || m.Deleted || body is null)
            {
                if (signal is not null)
                {
                    db.Signals.Remove(signal);
                    existing.Remove(externalId);
                }

                continue;
            }

            if (signal is null)
            {
                signal = new Signal { Source = Source.SignalSource, ExternalId = externalId };
                db.Signals.Add(signal);
                existing[externalId] = signal;
            }

            var fromMe = m.FromId is not null && m.FromId == _me!.Value.Id;
            signal.ThreadKey = threadKey;
            signal.Kind = kind;
            signal.Author = m.FromName ?? "an app";
            signal.AuthorAddress = m.FromId;
            signal.Recipients = recipients;
            signal.FromMe = fromMe;
            signal.AuthorTrust = fromMe ? SenderTrust.Self : TrustOf(m);
            signal.Subject = subject;
            signal.Body = SafeLinks.Unwrap(MentionNote(m) + body);
            signal.Url = m.WebUrl ?? threadUrl;
            signal.ReceivedAt = m.CreatedAt;
            signal.FilteredReason = null;
        }
    }

    /// <summary>People in your own tenant count as your organization. Guests, federated users, and apps don't.</summary>
    private SenderTrust TrustOf(TeamsMessage m) =>
        m.FromId is null || m.FromTenantId is null || _me!.Value.TenantId is null ? SenderTrust.Unknown
        : string.Equals(m.FromTenantId, _me.Value.TenantId, StringComparison.OrdinalIgnoreCase) ? SenderTrust.Organization
        : SenderTrust.External;

    /// <summary>Tells triage plainly when the user was @mentioned, since names alone can be ambiguous.</summary>
    private string MentionNote(TeamsMessage m) =>
        m.MentionedUserIds.Contains(_me!.Value.Id) ? "[This message @mentions the user.]\n" : "";

    private string ChatSubject(TeamsChat chat)
    {
        if (!string.IsNullOrWhiteSpace(chat.Topic))
        {
            return chat.Topic;
        }

        var others = chat.MemberNames.Where(n => n != _me!.Value.Name).ToList();
        return others.Count switch
        {
            0 => "Chat",
            <= 3 => "Chat with " + string.Join(", ", others),
            _ => $"Chat with {string.Join(", ", others.Take(3))} and {others.Count - 3} more",
        };
    }

    private static string Headline(TeamsMessage root)
    {
        var text = HtmlText.ToText(root.BodyHtml) ?? "(no text)";
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 80 ? line[..80] + "…" : line;
    }
}
