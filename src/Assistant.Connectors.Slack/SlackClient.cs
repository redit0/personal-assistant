using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Assistant.Core.Connectors;
using Assistant.Core.Secrets;

namespace Assistant.Connectors.Slack;

public sealed record SlackIdentity(string UserId, string User, string TeamId, string Team, string Url);

public sealed record SlackConversation(string Id, string? Name, bool IsIm, bool IsMpim, bool IsPrivate, string? ImUserId);

public sealed record SlackMessage(
    string Ts,
    string? ThreadTs,
    string? Subtype,
    string? UserId,
    string? UserTeam,
    string? BotName,
    string Text,
    int ReplyCount,
    string? LatestReply)
{
    public DateTime At => SlackClient.FromTs(Ts);

    /// <summary>A reply inside a thread, as opposed to the thread's first message or a plain channel message.</summary>
    public bool IsReply => ThreadTs is not null && ThreadTs != Ts;
}

public sealed record SlackSearchHit(
    string ChannelId, string? ChannelName, string Ts, string? ThreadTs, string? Permalink,
    string Text = "", string? UserId = null, bool IsIm = false, bool IsMpim = false)
{
    public DateTime At => SlackClient.FromTs(Ts);
}

public sealed record SlackUser(string Id, string Name, string? TeamId, bool IsBot);

/// <summary>
/// The Slack Web API calls the Slack connector needs, using your user token (xoxp-) from an internal app.
/// Internal apps keep the normal rate limits; the 2025 limits apply only to apps distributed outside the Marketplace.
/// </summary>
public sealed class SlackClient(IHttpClientFactory httpClients, ISecretStore secrets)
{
    public const string HttpClientName = "slack";

    /// <summary>User token scopes the internal app needs.</summary>
    public const string RequiredScopes = "channels:history, channels:read, groups:history, groups:read, im:history, im:read, mpim:history, mpim:read, search:read, users:read";

    /// <summary>Only needed if agents may post for you (post_message).</summary>
    public const string PostScope = "chat:write";

    private static readonly Uri Base = new("https://slack.com/api/");

    public bool HasToken => !string.IsNullOrWhiteSpace(secrets.Get(SecretNames.SlackToken));

    public async Task<SlackIdentity> AuthTestAsync(CancellationToken cancellationToken, string? token = null)
    {
        using var doc = await CallAsync("auth.test", [], cancellationToken, token);
        var r = doc.RootElement;
        return new SlackIdentity(Str(r, "user_id")!, Str(r, "user") ?? "", Str(r, "team_id")!, Str(r, "team") ?? "", Str(r, "url") ?? "https://slack.com/");
    }

    /// <summary>Conversations you're a member of, of the given types (im, mpim, public_channel, private_channel).</summary>
    public async Task<List<SlackConversation>> GetMyConversationsAsync(string types, CancellationToken cancellationToken)
    {
        var list = new List<SlackConversation>();
        string? cursor = null;
        do
        {
            var args = new Dictionary<string, string> { ["types"] = types, ["exclude_archived"] = "true", ["limit"] = "200" };
            if (cursor is not null) args["cursor"] = cursor;
            using var doc = await CallAsync("users.conversations", args, cancellationToken);
            list.AddRange(doc.RootElement.GetProperty("channels").EnumerateArray().Select(c => new SlackConversation(
                Str(c, "id")!, Str(c, "name"), Bool(c, "is_im"), Bool(c, "is_mpim"), Bool(c, "is_private"), Str(c, "user"))));
            cursor = NextCursor(doc.RootElement);
        }
        while (cursor is not null && list.Count < 5000);

        return list;
    }

    /// <summary>Top-level messages in a conversation after <paramref name="oldest"/>, newest first.</summary>
    public async Task<List<SlackMessage>> GetHistoryAsync(string channelId, DateTime oldest, CancellationToken cancellationToken)
    {
        using var doc = await CallAsync("conversations.history",
            new() { ["channel"] = channelId, ["oldest"] = ToTs(oldest), ["limit"] = "100" }, cancellationToken);
        return [.. doc.RootElement.GetProperty("messages").EnumerateArray().Select(Parse)];
    }

    /// <summary>A thread's first message and its replies, oldest first.</summary>
    public async Task<List<SlackMessage>> GetRepliesAsync(string channelId, string threadTs, CancellationToken cancellationToken)
    {
        using var doc = await CallAsync("conversations.replies",
            new() { ["channel"] = channelId, ["ts"] = threadTs, ["limit"] = "100" }, cancellationToken);
        return [.. doc.RootElement.GetProperty("messages").EnumerateArray().Select(Parse)];
    }

    /// <summary>
    /// Posts as you, in a thread when <paramref name="threadTs"/> is given. Needs the chat:write user scope.
    /// Returns the new message's timestamp. A <paramref name="mentionUserId"/> is tagged at the start.
    /// </summary>
    public async Task<string> PostMessageAsync(string channelId, string text, string? threadTs, CancellationToken cancellationToken, string? mentionUserId = null)
    {
        // Slack reads &, <, and > as markup, so escape them to post the text as written.
        var escaped = text.Trim().Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        if (mentionUserId is not null)
        {
            escaped = $"<@{mentionUserId}> {escaped}";
        }

        var args = new Dictionary<string, string> { ["channel"] = channelId, ["text"] = escaped };
        if (threadTs is not null)
        {
            args["thread_ts"] = threadTs;
        }

        using var doc = await CallAsync("chat.postMessage", args, cancellationToken);
        return Str(doc.RootElement, "ts")!;
    }

    /// <summary>Messages matching a search, newest first. Slack's search runs as you, so it covers what you can see.</summary>
    public async Task<List<SlackSearchHit>> SearchAsync(string query, int count, CancellationToken cancellationToken)
    {
        using var doc = await CallAsync("search.messages",
            new() { ["query"] = query, ["count"] = Math.Clamp(count, 1, 100).ToString(CultureInfo.InvariantCulture), ["sort"] = "timestamp", ["sort_dir"] = "desc" },
            cancellationToken);
        if (!doc.RootElement.TryGetProperty("messages", out var messages) || !messages.TryGetProperty("matches", out var matches))
        {
            return [];
        }

        return [.. matches.EnumerateArray().Select(m =>
        {
            var channel = m.TryGetProperty("channel", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
            var permalink = Str(m, "permalink");
            return new SlackSearchHit(
                channel.ValueKind == JsonValueKind.Object ? Str(channel, "id")! : "",
                channel.ValueKind == JsonValueKind.Object ? Str(channel, "name") : null,
                Str(m, "ts")!,
                ThreadTsFromPermalink(permalink),
                permalink,
                Str(m, "text") ?? "",
                Str(m, "user"),
                channel.ValueKind == JsonValueKind.Object && Bool(channel, "is_im"),
                channel.ValueKind == JsonValueKind.Object && Bool(channel, "is_mpim"));
        }).Where(h => h.ChannelId.Length > 0)];
    }

    public async Task<SlackUser?> GetUserAsync(string userId, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = await CallAsync("users.info", new() { ["user"] = userId }, cancellationToken);
            var u = doc.RootElement.GetProperty("user");
            var profile = u.TryGetProperty("profile", out var p) ? p : default;
            var name = profile.ValueKind == JsonValueKind.Object ? Str(profile, "display_name") is { Length: > 0 } dn ? dn : Str(profile, "real_name") : null;
            return new SlackUser(userId, name ?? Str(u, "real_name") ?? Str(u, "name") ?? userId, Str(u, "team_id"), Bool(u, "is_bot"));
        }
        catch (ConnectorException ex) when (ex.Message.Contains("user_not_found", StringComparison.Ordinal))
        {
            return null;
        }
    }

    /// <summary>Slack's standard message link, built locally so every message doesn't cost a chat.getPermalink call.</summary>
    public static string Permalink(string workspaceUrl, string channelId, string ts, string? threadTs = null)
    {
        var link = $"{workspaceUrl.TrimEnd('/')}/archives/{channelId}/p{ts.Replace(".", "", StringComparison.Ordinal)}";
        return threadTs is not null && threadTs != ts ? $"{link}?thread_ts={threadTs}&cid={channelId}" : link;
    }

    public static DateTime FromTs(string ts) =>
        DateTime.UnixEpoch.AddSeconds(double.Parse(ts, CultureInfo.InvariantCulture));

    public static string ToTs(DateTime utc) =>
        ((utc - DateTime.UnixEpoch).TotalSeconds).ToString("F6", CultureInfo.InvariantCulture);

    public static SlackMessage Parse(JsonElement m)
    {
        var bot = m.TryGetProperty("bot_profile", out var bp) && bp.ValueKind == JsonValueKind.Object ? Str(bp, "name") : Str(m, "username");
        return new SlackMessage(
            Ts: Str(m, "ts")!,
            ThreadTs: Str(m, "thread_ts"),
            Subtype: Str(m, "subtype"),
            UserId: Str(m, "user"),
            UserTeam: Str(m, "user_team") ?? Str(m, "team"),
            BotName: Str(m, "bot_id") is null ? null : bot ?? "an app",
            Text: Str(m, "text") ?? "",
            ReplyCount: m.TryGetProperty("reply_count", out var rc) && rc.TryGetInt32(out var n) ? n : 0,
            LatestReply: Str(m, "latest_reply"));
    }

    private static string? ThreadTsFromPermalink(string? permalink)
    {
        if (permalink is null || !Uri.TryCreate(permalink, UriKind.Absolute, out var uri))
        {
            return null;
        }

        foreach (var part in uri.Query.TrimStart('?').Split('&'))
        {
            if (part.StartsWith("thread_ts=", StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(part["thread_ts=".Length..]);
            }
        }

        return null;
    }

    private async Task<JsonDocument> CallAsync(string method, Dictionary<string, string> args, CancellationToken cancellationToken, string? token = null)
    {
        token ??= secrets.Get(SecretNames.SlackToken)
            ?? throw new ConnectorException("Add your Slack user token on the Connectors page.");

        using var http = httpClients.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Base, method)) { Content = new FormUrlEncodedContent(args) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var wait = response.Headers.RetryAfter?.Delta?.TotalSeconds;
            throw new ConnectorException($"Slack rate limit reached on {method}{(wait is null ? "" : $" (retry after {wait:0}s)")}. It will continue on the next sync.");
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ConnectorException($"Slack returned {(int)response.StatusCode} for {method}.");
        }

        var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
        {
            return doc;
        }

        var error = Str(doc.RootElement, "error") ?? "unknown_error";
        var needed = Str(doc.RootElement, "needed");
        doc.Dispose();
        var failure = new ConnectorException(error switch
        {
            "invalid_auth" or "not_authed" or "token_revoked" or "account_inactive" or "token_expired" =>
                "Slack didn't accept the token. Copy the User OAuth Token (xoxp-) from your Slack app again.",
            "missing_scope" =>
                $"Your Slack app is missing the {needed ?? "required"} user scope. Add it under OAuth & Permissions, reinstall the app, and paste the new token.",
            "not_allowed_token_type" =>
                "That looks like a bot token. Use the User OAuth Token, which starts with xoxp-.",
            _ => $"Slack {method} failed: {error}",
        });
        failure.Data[ErrorKey] = error;
        throw failure;
    }

    /// <summary>Key in <see cref="Exception.Data"/> holding Slack's error code, such as "channel_not_found".</summary>
    public const string ErrorKey = "slack_error";

    /// <summary>Errors that mean one conversation can't be read (you left it, it was archived, or it's gone), not that Slack is down.</summary>
    public static bool IsUnreadableConversation(ConnectorException ex) =>
        ex.Data[ErrorKey] is "channel_not_found" or "not_in_channel" or "is_archived" or "thread_not_found";

    private static string? NextCursor(JsonElement root) =>
        root.TryGetProperty("response_metadata", out var meta) && Str(meta, "next_cursor") is { Length: > 0 } c ? c : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
