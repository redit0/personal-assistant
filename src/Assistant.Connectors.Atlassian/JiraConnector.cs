using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Assistant.Core.Connectors;
using Assistant.Core.Items;
using Assistant.Core.Secrets;
using Assistant.Core.Settings;

namespace Assistant.Connectors.Atlassian;

/// <summary>Jira Cloud: unresolved issues assigned to you, and issues you reported that someone else holds.</summary>
public sealed class JiraConnector(
    IHttpClientFactory httpClients,
    UserSettingsStore settings,
    ISecretStore secrets,
    TimeProvider time) : IConnector
{
    public const string HttpClientName = "jira";

    private const int MaxIssuesPerQuery = 300;
    private static readonly string[] Fields =
        ["summary", "status", "duedate", "priority", "reporter", "assignee", "updated", "description", "issuetype", "project"];

    public string Id => "jira";

    public string DisplayName => "Jira";

    public TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, settings.Current.Atlassian.IntervalMinutes));

    public string? NotReadyReason()
    {
        var a = settings.Current.Atlassian;
        if (!a.JiraEnabled) return "Turned off.";
        if (!Uri.TryCreate(a.JiraSiteUrl, UriKind.Absolute, out _)) return "Needs your Jira site URL.";
        if (string.IsNullOrWhiteSpace(a.Email)) return "Needs your Atlassian email.";
        return string.IsNullOrEmpty(secrets.Get(SecretNames.JiraApiToken)) ? "Needs a Jira API token." : null;
    }

    public async Task<ConnectorSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var a = settings.Current.Atlassian;
        var site = new Uri(a.JiraSiteUrl!.TrimEnd('/') + "/");
        var auth = AtlassianHttp.Basic(a.Email!, secrets.Get(SecretNames.JiraApiToken)!);
        using var http = httpClients.CreateClient(HttpClientName);

        var queries = new (string Jql, ItemType Type)[]
        {
            ($"assignee = currentUser() AND statusCategory != Done{SprintFilter(a)} ORDER BY updated DESC", ItemType.Assigned),
            ($"reporter = currentUser() AND (assignee != currentUser() OR assignee is EMPTY) AND statusCategory != Done{SprintFilter(a)} ORDER BY updated DESC", ItemType.WaitingOn),
        };

        var items = new Dictionary<string, SourceItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var (jql, type) in queries)
        {
            foreach (var issue in await SearchAsync(http, site, auth, jql, cancellationToken))
            {
                var item = ToSourceItem(issue, type, site);
                items.TryAdd(item.ExternalId, item);
            }
        }

        return ConnectorSnapshot.Complete([.. items.Values]);
    }

    private static async Task<List<JsonElement>> SearchAsync(HttpClient http, Uri site, System.Net.Http.Headers.AuthenticationHeaderValue auth, string jql, CancellationToken cancellationToken)
    {
        var issues = new List<JsonElement>();
        string? nextPageToken = null;

        do
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(site, "rest/api/3/search/jql"))
            {
                Content = JsonContent.Create(new Dictionary<string, object?>
                {
                    ["jql"] = jql,
                    ["fields"] = Fields,
                    ["maxResults"] = 100,
                    ["nextPageToken"] = nextPageToken,
                }),
            };

            using var doc = await AtlassianHttp.SendAsync(http, request, auth, "Jira", cancellationToken);
            var root = doc.RootElement;
            issues.AddRange(root.GetProperty("issues").EnumerateArray().Select(i => i.Clone()));

            var isLast = root.Get("isLast") is { ValueKind: JsonValueKind.True };
            nextPageToken = isLast ? null : root.GetString("nextPageToken");
        }
        while (nextPageToken is not null && issues.Count < MaxIssuesPerQuery);

        return issues;
    }

    private SourceItem ToSourceItem(JsonElement issue, ItemType type, Uri site)
    {
        var key = issue.GetProperty("key").GetString()!;
        var f = issue.GetProperty("fields");
        var summary = f.GetString("summary") ?? key;
        var status = f.Get("status")?.GetString("name");
        var reporter = f.Get("reporter")?.GetString("displayName");
        var assignee = f.Get("assignee")?.GetString("displayName");
        var issueType = f.Get("issuetype")?.GetString("name") ?? "Issue";

        var details = type == ItemType.Assigned
            ? $"{issueType} {key} is assigned to you ({status}). Reported by {reporter ?? "unknown"}."
            : $"{issueType} {key} that you reported is {status}, {(assignee is null ? "unassigned" : $"with {assignee}")}.";

        return new SourceItem(
            Scope: site.Host,
            ExternalId: $"{site.Host}:{key}",
            Title: $"{key}: {summary}",
            Type: type,
            Url: new Uri(site, $"browse/{key}").ToString(),
            UpdatedAt: ParseTimestamp(f.GetString("updated")) ?? time.GetUtcNow().UtcDateTime,
            Priority: MapPriority(f.Get("priority")?.GetString("name")),
            DueAt: ParseDueDate(f.GetString("duedate")),
            Requester: type == ItemType.Assigned ? reporter : assignee,
            Trust: SenderTrust.Organization,
            Details: details,
            SourceContent: AtlassianHttp.Truncate(AdfToText(f.Get("description"))));
    }

    private static string SprintFilter(AtlassianSettings atlassian) =>
        atlassian.JiraActiveSprintOnly ? " AND sprint in openSprints()" : "";

    /// <summary>Jira writes offsets without a colon ("-0500"), which the strict JSON date parser rejects.</summary>
    private static DateTime? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d.UtcDateTime : null;

    /// <summary>Jira due dates have no time, so treat them as due at 5 PM your time that day.</summary>
    private static DateTime? ParseDueDate(string? value) =>
        DateOnly.TryParse(value, out var date)
            ? new DateTimeOffset(date.ToDateTime(new TimeOnly(17, 0)), TimeZoneInfo.Local.GetUtcOffset(date.ToDateTime(new TimeOnly(17, 0)))).UtcDateTime
            : null;

    private static Priority MapPriority(string? name) => name?.ToLowerInvariant() switch
    {
        "highest" or "blocker" => Priority.Urgent,
        "high" or "critical" or "major" => Priority.High,
        "low" or "lowest" or "minor" or "trivial" => Priority.Low,
        _ => Priority.Normal,
    };

    /// <summary>Flattens Atlassian Document Format into plain text.</summary>
    public static string? AdfToText(JsonElement? adf)
    {
        if (adf is not { } root)
        {
            return null;
        }

        if (root.ValueKind == JsonValueKind.String)
        {
            return root.GetString();
        }

        var text = new StringBuilder();
        Walk(root, text);
        var result = text.ToString().Trim();
        return result.Length == 0 ? null : result;

        static void Walk(JsonElement node, StringBuilder text)
        {
            if (node.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var type = node.GetString("type");
            switch (type)
            {
                case "text":
                    text.Append(node.GetString("text"));
                    return;
                case "hardBreak":
                    text.AppendLine();
                    return;
                case "mention":
                    text.Append(node.Get("attrs")?.GetString("text") ?? "@someone");
                    return;
            }

            if (node.Get("content") is { ValueKind: JsonValueKind.Array } content)
            {
                foreach (var child in content.EnumerateArray())
                {
                    Walk(child, text);
                }
            }

            if (type is "paragraph" or "heading" or "listItem" or "codeBlock" or "blockquote")
            {
                text.AppendLine();
            }
        }
    }
}
