using System.Text.Json;

namespace Assistant.Connectors.Microsoft;

public sealed record TeamsChat(string Id, string? Topic, string ChatType, string? WebUrl, DateTime? LastMessageAt, IReadOnlyList<string> MemberNames);

public sealed record TeamsChannel(string TeamId, string TeamName, string Id, string Name);

public sealed record TeamsMessage(
    string Id,
    string? ReplyToId,
    string MessageType,
    string? FromId,
    string? FromName,
    string? FromTenantId,
    string? BodyHtml,
    string? WebUrl,
    DateTime CreatedAt,
    DateTime LastModifiedAt,
    IReadOnlyList<string> MentionedUserIds,
    bool Deleted);

/// <summary>Microsoft Graph calls for Teams chats and channels.</summary>
public sealed partial class GraphClient
{
    /// <summary>Chats: you can consent to this yourself.</summary>
    public static readonly string[] TeamsChatScopes = ["https://graph.microsoft.com/Chat.Read", "https://graph.microsoft.com/User.Read"];

    /// <summary>Channel messages: ChannelMessage.Read.All needs admin consent.</summary>
    public static readonly string[] TeamsChannelScopes =
    [
        "https://graph.microsoft.com/ChannelMessage.Read.All",
        "https://graph.microsoft.com/Team.ReadBasic.All",
        "https://graph.microsoft.com/Channel.ReadBasic.All",
    ];

    /// <summary>Posting as you: chats and channel replies. Neither needs admin consent.</summary>
    public static readonly string[] TeamsSendScopes =
    [
        "https://graph.microsoft.com/ChatMessage.Send",
        "https://graph.microsoft.com/ChannelMessage.Send",
    ];

    /// <summary>Posts to a 1:1, group, or meeting chat. Returns the new message's link, when Graph gives one.</summary>
    public async Task<string?> PostChatMessageAsync(string chatId, string text, CancellationToken cancellationToken)
    {
        using var doc = await SendAsync(HttpMethod.Post, $"chats/{Uri.EscapeDataString(chatId)}/messages",
            MessageBody(text), TeamsSendScopes, cancellationToken);
        return Str(doc.RootElement, "webUrl");
    }

    /// <summary>Replies in a channel thread.</summary>
    public async Task<string?> ReplyInChannelAsync(string teamId, string channelId, string rootId, string text, CancellationToken cancellationToken)
    {
        using var doc = await SendAsync(HttpMethod.Post,
            $"teams/{Uri.EscapeDataString(teamId)}/channels/{Uri.EscapeDataString(channelId)}/messages/{Uri.EscapeDataString(rootId)}/replies",
            MessageBody(text), TeamsSendScopes, cancellationToken);
        return Str(doc.RootElement, "webUrl");
    }

    /// <summary>Plain text as Teams HTML: escaped, with line breaks kept.</summary>
    public static object MessageBody(string text) => new
    {
        body = new
        {
            contentType = "html",
            content = System.Net.WebUtility.HtmlEncode(text.Trim()).Replace("\r\n", "\n").Replace("\n", "<br>"),
        },
    };

    /// <summary>Your user ID, name, and home tenant (read from the token, since /me doesn't include it).</summary>
    public async Task<(string Id, string? DisplayName, string? TenantId)> GetMyIdAsync(CancellationToken cancellationToken)
    {
        using var doc = await SendAsync(HttpMethod.Get, "me?$select=id,displayName", null, TeamsChatScopes, cancellationToken);
        var token = await auth.GetAccessTokenAsync(TeamsChatScopes, cancellationToken);
        return (Str(doc.RootElement, "id")!, Str(doc.RootElement, "displayName"), TenantOf(token));
    }

    /// <summary>The "tid" claim of a JWT access token, or null if it can't be read.</summary>
    public static string? TenantOf(string accessToken)
    {
        var parts = accessToken.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var claims = JsonDocument.Parse(Convert.FromBase64String(payload));
            return Str(claims.RootElement, "tid");
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Your chats, most recently active first, up to <paramref name="max"/>, stopping at chats quiet since <paramref name="activeSince"/>.</summary>
    public async Task<List<TeamsChat>> GetChatsAsync(int max, DateTime? activeSince, CancellationToken cancellationToken)
    {
        var chats = new List<TeamsChat>();
        string? url = "me/chats?$expand=lastMessagePreview,members&$orderby=lastMessagePreview/createdDateTime desc&$top=50";
        while (url is not null && chats.Count < max)
        {
            using var doc = await SendAsync(HttpMethod.Get, url, null, TeamsChatScopes, cancellationToken);
            foreach (var c in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                var preview = c.TryGetProperty("lastMessagePreview", out var p) && p.ValueKind == JsonValueKind.Object ? p : (JsonElement?)null;
                var members = c.TryGetProperty("members", out var m) && m.ValueKind == JsonValueKind.Array
                    ? m.EnumerateArray().Select(x => Str(x, "displayName")).OfType<string>().ToList()
                    : [];
                chats.Add(new TeamsChat(Str(c, "id")!, Str(c, "topic"), Str(c, "chatType") ?? "oneOnOne", Str(c, "webUrl"),
                    preview is { } pv ? Date(pv, "createdDateTime") : null, members));
            }

            url = activeSince is { } cutoff && chats.Count > 0 && chats[^1].LastMessageAt < cutoff ? null : Str(doc.RootElement, "@odata.nextLink");
        }

        return chats;
    }

    /// <summary>Messages in a chat changed since a time, newest first, up to 50.</summary>
    public async Task<List<TeamsMessage>> GetChatMessagesAsync(string chatId, DateTime sinceUtc, CancellationToken cancellationToken)
    {
        var url = $"chats/{Uri.EscapeDataString(chatId)}/messages?$top=50&$orderby=lastModifiedDateTime desc" +
            $"&$filter=lastModifiedDateTime gt {sinceUtc:yyyy-MM-ddTHH:mm:ssZ}";
        using var doc = await SendAsync(HttpMethod.Get, url, null, TeamsChatScopes, cancellationToken);
        return [.. doc.RootElement.GetProperty("value").EnumerateArray().Select(ParseTeamsMessage)];
    }

    public async Task<List<TeamsChannel>> GetMyChannelsAsync(CancellationToken cancellationToken)
    {
        var channels = new List<TeamsChannel>();
        using var teams = await SendAsync(HttpMethod.Get, "me/joinedTeams?$select=id,displayName", null, TeamsChannelScopes, cancellationToken);
        foreach (var team in teams.RootElement.GetProperty("value").EnumerateArray())
        {
            var teamId = Str(team, "id")!;
            using var list = await SendAsync(HttpMethod.Get, $"teams/{Uri.EscapeDataString(teamId)}/channels?$select=id,displayName", null, TeamsChannelScopes, cancellationToken);
            channels.AddRange(list.RootElement.GetProperty("value").EnumerateArray()
                .Select(ch => new TeamsChannel(teamId, Str(team, "displayName") ?? teamId, Str(ch, "id")!, Str(ch, "displayName") ?? "channel")));
        }

        return channels;
    }

    /// <summary>
    /// The 50 most recently active threads in a channel (Graph sorts by the latest activity in each thread), with up
    /// to 200 replies each. This list has no date filter, so callers drop old threads themselves.
    /// </summary>
    public async Task<List<(TeamsMessage Root, List<TeamsMessage> Replies)>> GetChannelThreadsAsync(TeamsChannel channel, CancellationToken cancellationToken)
    {
        using var doc = await SendAsync(HttpMethod.Get,
            $"teams/{Uri.EscapeDataString(channel.TeamId)}/channels/{Uri.EscapeDataString(channel.Id)}/messages?$top=50&$expand=replies",
            null, TeamsChannelScopes, cancellationToken);
        return [.. doc.RootElement.GetProperty("value").EnumerateArray().Select(root => (
            ParseTeamsMessage(root),
            root.TryGetProperty("replies", out var r) && r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray().Select(ParseTeamsMessage).ToList()
                : new List<TeamsMessage>()))];
    }

    /// <summary>Parses one chatMessage. Public for tests.</summary>
    public static TeamsMessage ParseTeamsMessage(JsonElement m)
    {
        var user = m.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.Object
            && from.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? u : (JsonElement?)null;
        var body = m.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.Object ? Str(b, "content") : null;
        var mentions = m.TryGetProperty("mentions", out var ms) && ms.ValueKind == JsonValueKind.Array
            ? ms.EnumerateArray()
                .Select(x => x.TryGetProperty("mentioned", out var mm) && mm.ValueKind == JsonValueKind.Object
                    && mm.TryGetProperty("user", out var mu) && mu.ValueKind == JsonValueKind.Object ? Str(mu, "id") : null)
                .OfType<string>()
                .ToList()
            : [];

        return new TeamsMessage(
            Id: Str(m, "id")!,
            ReplyToId: Str(m, "replyToId"),
            MessageType: Str(m, "messageType") ?? "message",
            FromId: user is { } us ? Str(us, "id") : null,
            FromName: user is { } un ? Str(un, "displayName") : null,
            FromTenantId: user is { } ut ? Str(ut, "tenantId") : null,
            BodyHtml: body,
            WebUrl: Str(m, "webUrl"),
            CreatedAt: Date(m, "createdDateTime") ?? DateTime.UtcNow,
            LastModifiedAt: Date(m, "lastModifiedDateTime") ?? Date(m, "createdDateTime") ?? DateTime.UtcNow,
            MentionedUserIds: mentions,
            Deleted: m.TryGetProperty("deletedDateTime", out var del) && del.ValueKind == JsonValueKind.String);
    }
}
