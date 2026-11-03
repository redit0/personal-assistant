using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Assistant.Core.Connectors;
using Microsoft.Identity.Client;

namespace Assistant.Connectors.Microsoft;

public sealed record GraphAddress(string? Name, string? Address)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Address ?? "" : $"{Name} <{Address}>";
}

public sealed record GraphMessage(
    string Id,
    string? ConversationId,
    string? Subject,
    GraphAddress? From,
    IReadOnlyList<GraphAddress> Recipients,
    DateTime ReceivedAt,
    string? Body,
    string? WebLink,
    string? InferenceClassification,
    bool IsMeetingMessage,
    string? FlagStatus,
    DateTime? FlagDueAt,
    bool Removed);

public sealed record GraphEvent(
    string Id,
    string? Subject,
    DateTime Start,
    DateTime End,
    bool IsAllDay,
    bool IsCancelled,
    bool IsOrganizer,
    string? Organizer,
    IReadOnlyList<string> Attendees,
    string? Location,
    string? BodyPreview,
    string? WebLink,
    string? ResponseStatus,
    DateTime LastModified,
    string? ShowAs = null);

public sealed record GraphFolder(string Id, string Name);

/// <summary>
/// The Microsoft Graph calls the Outlook and calendar connectors need, using your Microsoft sign-in.
/// Your Entra app needs the delegated Mail.Read, Calendars.Read, and MailboxSettings.Read permissions.
/// </summary>
public sealed partial class GraphClient(IHttpClientFactory httpClients, IMicrosoftTokenProvider auth)
{
    public const string HttpClientName = "graph";

    public static readonly string[] Scopes =
    [
        "https://graph.microsoft.com/User.Read",
        "https://graph.microsoft.com/Mail.Read",
        "https://graph.microsoft.com/Calendars.Read",
        "https://graph.microsoft.com/MailboxSettings.Read",
    ];

    private static readonly Uri Base = new("https://graph.microsoft.com/v1.0/");

    private const string MessageFields =
        "id,conversationId,subject,from,toRecipients,ccRecipients,receivedDateTime,body,webLink,inferenceClassification,flag";

    public async Task<(string? DisplayName, IReadOnlyList<string> Addresses)> GetMeAsync(CancellationToken cancellationToken)
    {
        using var doc = await GetAsync("me?$select=displayName,mail,userPrincipalName,otherMails", cancellationToken);
        var root = doc.RootElement;
        var addresses = new[] { Str(root, "mail"), Str(root, "userPrincipalName") }
            .Concat(root.TryGetProperty("otherMails", out var other) ? other.EnumerateArray().Select(e => e.GetString()) : [])
            .OfType<string>()
            .Where(a => a.Contains('@'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (Str(root, "displayName"), addresses);
    }

    /// <summary>Your Outlook working hours, or null if none are set.</summary>
    public async Task<WorkingHours?> GetWorkingHoursAsync(CancellationToken cancellationToken)
    {
        using var doc = await GetAsync("me/mailboxSettings?$select=workingHours,timeZone", cancellationToken);
        if (!doc.RootElement.TryGetProperty("workingHours", out var wh) || wh.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var days = wh.TryGetProperty("daysOfWeek", out var d) && d.ValueKind == JsonValueKind.Array
            ? d.EnumerateArray().Select(x => Enum.TryParse<DayOfWeek>(x.GetString(), ignoreCase: true, out var day) ? day : (DayOfWeek?)null).OfType<DayOfWeek>().ToHashSet()
            : [];
        var zone = wh.TryGetProperty("timeZone", out var tz) && tz.ValueKind == JsonValueKind.Object ? Str(tz, "name") : Str(doc.RootElement, "timeZone");

        return TimeOnly.TryParse(Str(wh, "startTime")?.Split('.')[0], out var start) && TimeOnly.TryParse(Str(wh, "endTime")?.Split('.')[0], out var end)
            ? new WorkingHours(days, start, end, zone)
            : null;
    }

    /// <summary>Your Outlook time zone, as a Windows or IANA ID.</summary>
    public async Task<string?> GetTimeZoneAsync(CancellationToken cancellationToken)
    {
        using var doc = await GetAsync("me/mailboxSettings?$select=timeZone", cancellationToken);
        return Str(doc.RootElement, "timeZone");
    }

    /// <summary>
    /// New and changed messages in a folder since the last delta link. The first call (no link) reads
    /// messages received since <paramref name="since"/>. Returns the next delta link to store.
    /// </summary>
    public async Task<(List<GraphMessage> Messages, string DeltaLink)> GetMessageDeltaAsync(
        string folder, string? deltaLink, DateTime since, CancellationToken cancellationToken)
    {
        var messages = new List<GraphMessage>();
        var url = deltaLink ??
            $"me/mailFolders/{Uri.EscapeDataString(folder)}/messages/delta?$select={MessageFields}" +
            $"&$filter=receivedDateTime+ge+{since:yyyy-MM-ddTHH:mm:ssZ}";

        try
        {
            while (true)
            {
                using var doc = await GetAsync(url, cancellationToken, preferTextBody: true);
                messages.AddRange(doc.RootElement.GetProperty("value").EnumerateArray().Select(ParseMessage));

                if (Str(doc.RootElement, "@odata.nextLink") is { } next)
                {
                    url = next;
                }
                else
                {
                    return (messages, Str(doc.RootElement, "@odata.deltaLink") ?? throw new ConnectorException("Graph didn't return a delta link."));
                }
            }
        }
        catch (ConnectorException ex) when (deltaLink is not null && ex.Data["status"] is HttpStatusCode.Gone)
        {
            // The stored delta link expired; start over from the backfill window.
            return await GetMessageDeltaAsync(folder, null, since, cancellationToken);
        }
    }

    /// <summary>Internet headers for one message, used to spot newsletters and automated mail.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetHeadersAsync(string messageId, CancellationToken cancellationToken)
    {
        using var doc = await GetAsync($"me/messages/{Uri.EscapeDataString(messageId)}?$select=internetMessageHeaders", cancellationToken);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("internetMessageHeaders", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var h in list.EnumerateArray())
            {
                if (Str(h, "name") is { } name)
                {
                    headers[name] = Str(h, "value") ?? "";
                }
            }
        }

        return headers;
    }

    public async Task<List<GraphMessage>> GetFlaggedAsync(CancellationToken cancellationToken)
    {
        var messages = new List<GraphMessage>();
        string? url = $"me/messages?$filter=flag/flagStatus eq 'flagged'&$select={MessageFields}&$top=100";
        for (var page = 0; url is not null && page < 5; page++)
        {
            using var doc = await GetAsync(url, cancellationToken, preferTextBody: true);
            messages.AddRange(doc.RootElement.GetProperty("value").EnumerateArray().Select(ParseMessage));
            url = Str(doc.RootElement, "@odata.nextLink");
        }

        return messages;
    }

    /// <summary>Your mail folders and their direct subfolders, for picking extra folders to watch.</summary>
    public async Task<List<GraphFolder>> GetFoldersAsync(CancellationToken cancellationToken)
    {
        var folders = new List<GraphFolder>();
        using var top = await GetAsync("me/mailFolders?$top=100&$select=id,displayName,childFolderCount", cancellationToken);
        foreach (var f in top.RootElement.GetProperty("value").EnumerateArray())
        {
            var id = Str(f, "id")!;
            var name = Str(f, "displayName") ?? id;
            folders.Add(new GraphFolder(id, name));

            if (f.TryGetProperty("childFolderCount", out var c) && c.GetInt32() > 0)
            {
                using var children = await GetAsync($"me/mailFolders/{Uri.EscapeDataString(id)}/childFolders?$top=100&$select=id,displayName", cancellationToken);
                folders.AddRange(children.RootElement.GetProperty("value").EnumerateArray()
                    .Select(ch => new GraphFolder(Str(ch, "id")!, $"{name} / {Str(ch, "displayName")}")));
            }
        }

        return folders;
    }

    public async Task<List<GraphEvent>> GetCalendarViewAsync(DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken)
    {
        var events = new List<GraphEvent>();
        string? url = $"me/calendarView?startDateTime={startUtc:yyyy-MM-ddTHH:mm:ssZ}&endDateTime={endUtc:yyyy-MM-ddTHH:mm:ssZ}" +
            "&$select=id,subject,start,end,isAllDay,isCancelled,isOrganizer,organizer,attendees,location,bodyPreview,webLink,responseStatus,lastModifiedDateTime,showAs" +
            "&$orderby=start/dateTime&$top=200";

        for (var page = 0; url is not null && page < 5; page++)
        {
            using var doc = await GetAsync(url, cancellationToken, preferUtc: true);
            events.AddRange(doc.RootElement.GetProperty("value").EnumerateArray().Select(ParseEvent));
            url = Str(doc.RootElement, "@odata.nextLink");
        }

        return events;
    }

    private static GraphMessage ParseMessage(JsonElement m)
    {
        var removed = m.TryGetProperty("@removed", out _);
        var recipients = new List<GraphAddress>();
        foreach (var field in new[] { "toRecipients", "ccRecipients" })
        {
            if (m.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array)
            {
                recipients.AddRange(list.EnumerateArray().Select(r => Address(r)).OfType<GraphAddress>());
            }
        }

        var flag = m.TryGetProperty("flag", out var f) && f.ValueKind == JsonValueKind.Object ? f : (JsonElement?)null;

        return new GraphMessage(
            Id: Str(m, "id")!,
            ConversationId: Str(m, "conversationId"),
            Subject: Str(m, "subject"),
            From: m.TryGetProperty("from", out var from) ? Address(from) : null,
            Recipients: recipients,
            ReceivedAt: Date(m, "receivedDateTime") ?? DateTime.UtcNow,
            Body: m.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object ? Str(body, "content") : null,
            WebLink: Str(m, "webLink"),
            InferenceClassification: Str(m, "inferenceClassification"),
            IsMeetingMessage: Str(m, "@odata.type") is "#microsoft.graph.eventMessage" or "#microsoft.graph.eventMessageRequest" or "#microsoft.graph.eventMessageResponse",
            FlagStatus: flag is { } fl ? Str(fl, "flagStatus") : null,
            FlagDueAt: flag is { } fd && fd.TryGetProperty("dueDateTime", out var due) && due.ValueKind == JsonValueKind.Object ? GraphDateTime(due) : null,
            Removed: removed);
    }

    private static GraphEvent ParseEvent(JsonElement e) => new(
        Id: Str(e, "id")!,
        Subject: Str(e, "subject"),
        Start: GraphDateTime(e.GetProperty("start")) ?? DateTime.UtcNow,
        End: GraphDateTime(e.GetProperty("end")) ?? DateTime.UtcNow,
        IsAllDay: e.TryGetProperty("isAllDay", out var ad) && ad.GetBoolean(),
        IsCancelled: e.TryGetProperty("isCancelled", out var ic) && ic.GetBoolean(),
        IsOrganizer: e.TryGetProperty("isOrganizer", out var io) && io.GetBoolean(),
        Organizer: e.TryGetProperty("organizer", out var org) ? Address(org)?.ToString() : null,
        Attendees: e.TryGetProperty("attendees", out var att) && att.ValueKind == JsonValueKind.Array
            ? [.. att.EnumerateArray().Select(a => Address(a)?.ToString()).OfType<string>()]
            : [],
        Location: e.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object ? Str(loc, "displayName") : null,
        BodyPreview: Str(e, "bodyPreview"),
        WebLink: Str(e, "webLink"),
        ResponseStatus: e.TryGetProperty("responseStatus", out var rs) && rs.ValueKind == JsonValueKind.Object ? Str(rs, "response") : null,
        LastModified: Date(e, "lastModifiedDateTime") ?? DateTime.UtcNow,
        ShowAs: Str(e, "showAs"));

    /// <summary>Reads {"emailAddress":{"name":..,"address":..}}.</summary>
    private static GraphAddress? Address(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("emailAddress", out var ea) && ea.ValueKind == JsonValueKind.Object
            ? new GraphAddress(Str(ea, "name"), Str(ea, "address"))
            : null;

    /// <summary>Reads Graph's {"dateTime":..,"timeZone":..}. Requests ask for UTC, so other zones are rare.</summary>
    private static DateTime? GraphDateTime(JsonElement e)
    {
        if (Str(e, "dateTime") is not { } value || !DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
        {
            return null;
        }

        var zone = Str(e, "timeZone");
        if (zone is null or "UTC" or "Etc/UTC" or "tzone://Microsoft/Utc")
        {
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(zone, out var tz)
            ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), tz)
            : DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTime? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d.UtcDateTime : null;

    /// <summary>
    /// Creates a reply to a message as a draft in your Drafts folder. Nothing is sent. Needs Mail.ReadWrite,
    /// which is asked for separately so reading mail keeps working without it.
    /// </summary>
    public async Task<(string DraftId, string? WebLink)> CreateReplyDraftAsync(string messageId, string comment, bool replyAll, CancellationToken cancellationToken)
    {
        var action = replyAll ? "createReplyAll" : "createReply";
        using var doc = await SendAsync(HttpMethod.Post, $"me/messages/{Uri.EscapeDataString(messageId)}/{action}",
            new { comment }, WriteScopes, cancellationToken);
        return (Str(doc.RootElement, "id")!, Str(doc.RootElement, "webLink"));
    }

    /// <summary>Extra permission for drafting replies. Requested by the "Allow drafting replies" button.</summary>
    public static readonly string[] WriteScopes = ["https://graph.microsoft.com/Mail.ReadWrite"];

    private Task<JsonDocument> GetAsync(string url, CancellationToken cancellationToken, bool preferTextBody = false, bool preferUtc = false) =>
        SendAsync(HttpMethod.Get, url, null, Scopes, cancellationToken, preferTextBody, preferUtc);

    private async Task<JsonDocument> SendAsync(
        HttpMethod method, string url, object? body, string[] scopes, CancellationToken cancellationToken, bool preferTextBody = false, bool preferUtc = false)
    {
        var (connect, permissions) = Consent(scopes);
        string token;
        try
        {
            token = await auth.GetAccessTokenAsync(scopes, cancellationToken);
        }
        catch (MsalUiRequiredException)
        {
            throw new ConnectorException($"This needs your consent. Click {connect} on the Connectors page.");
        }
        catch (MsalServiceException ex) when (ex.Message.Contains("AADSTS65001") || ex.Message.Contains("AADSTS650057") || ex.Message.Contains("AADSTS90094"))
        {
            throw new ConnectorException($"Your Entra app is missing {permissions}, or it needs admin consent. Add them under API permissions (and grant admin consent if required), then click {connect}.");
        }

        using var http = httpClients.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(method, url.StartsWith("http", StringComparison.Ordinal) ? new Uri(url) : new Uri(Base, url));
        if (body is not null)
        {
            request.Content = System.Net.Http.Json.JsonContent.Create(body);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        if (preferTextBody)
        {
            request.Headers.Add("Prefer", "outlook.body-content-type=\"text\"");
        }

        if (preferUtc)
        {
            request.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
        }

        using var response = await http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        }

        var error = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new ConnectorException($"Microsoft Graph didn't accept your sign-in. Click {connect} on the Connectors page."),
            HttpStatusCode.Forbidden => new ConnectorException($"Microsoft Graph refused access. Check that your Entra app has {permissions} (with admin consent if required), then click {connect}."),
            HttpStatusCode.TooManyRequests => new ConnectorException("Microsoft Graph is throttling requests. It will retry on the next sync."),
            _ => new ConnectorException($"Microsoft Graph returned {(int)response.StatusCode}: {(text.Length > 300 ? text[..300] : text)}"),
        };
        error.Data["status"] = response.StatusCode;
        throw error;
    }

    /// <summary>Which Connectors-page button grants these scopes, and which permissions they need, for error messages.</summary>
    private static (string Connect, string Permissions) Consent(string[] scopes) => scopes switch
    {
        _ when scopes == WriteScopes => ("Allow drafting replies", "Mail.ReadWrite"),
        _ when scopes == TeamsChatScopes => ("Connect Teams", "Chat.Read"),
        _ when scopes == TeamsChannelScopes => ("Connect Teams channels", "ChannelMessage.Read.All, Team.ReadBasic.All, and Channel.ReadBasic.All"),
        _ when scopes == FileScopes => ("Connect SharePoint", "Files.Read.All and Sites.Read.All"),
        _ when scopes == TeamsSendScopes => ("Allow posting to Teams", "ChatMessage.Send and ChannelMessage.Send"),
        _ => ("Connect Outlook", "Mail.Read, Calendars.Read, and MailboxSettings.Read"),
    };
}

/// <summary>Outlook working hours. <see cref="TimeZone"/> is a Windows or IANA ID, or null for the computer's zone.</summary>
public sealed record WorkingHours(IReadOnlySet<DayOfWeek> Days, TimeOnly Start, TimeOnly End, string? TimeZone)
{
    public TimeZoneInfo Zone =>
        TimeZone is not null && TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out var zone) ? zone : TimeZoneInfo.Local;
}
