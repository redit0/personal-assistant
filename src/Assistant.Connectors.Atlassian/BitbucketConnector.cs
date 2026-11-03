using System.Net.Http.Headers;
using System.Text.Json;
using Assistant.Core.Connectors;
using Assistant.Core.Items;
using Assistant.Core.Secrets;
using Assistant.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Assistant.Connectors.Atlassian;

/// <summary>
/// Bitbucket Cloud: open PRs where you're a reviewer and haven't responded yet, and your own open PRs.
/// Bitbucket has no cross-repo "review requested" search, so it checks each recently active repo in
/// your workspaces.
/// </summary>
public sealed class BitbucketConnector(
    IHttpClientFactory httpClients,
    UserSettingsStore settings,
    ISecretStore secrets,
    TimeProvider time,
    ILogger<BitbucketConnector> logger) : IConnector
{
    public const string HttpClientName = "bitbucket";

    private static readonly Uri Api = new("https://api.bitbucket.org/2.0/");
    private const int MaxPages = 10;

    public string Id => "bitbucket";

    public string DisplayName => "Bitbucket";

    public TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, settings.Current.Atlassian.IntervalMinutes));

    public string? NotReadyReason()
    {
        var a = settings.Current.Atlassian;
        if (!a.BitbucketEnabled) return "Turned off.";
        if (string.IsNullOrWhiteSpace(a.Email)) return "Needs your Atlassian email.";
        return string.IsNullOrEmpty(secrets.Get(SecretNames.BitbucketApiToken)) ? "Needs a Bitbucket API token." : null;
    }

    public async Task<ConnectorSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var a = settings.Current.Atlassian;
        var auth = AtlassianHttp.Basic(a.Email!, secrets.Get(SecretNames.BitbucketApiToken)!);
        using var http = httpClients.CreateClient(HttpClientName);

        using var me = await GetAsync(http, auth, new Uri(Api, "user"), cancellationToken);
        var myUuid = me.RootElement.GetString("uuid") ?? throw new ConnectorException("Bitbucket didn't return your account id.");

        var workspaces = new List<string>();
        await foreach (var w in PagesAsync(http, auth, new Uri(Api, "user/workspaces?pagelen=100"), cancellationToken))
        {
            if (w.Get("workspace")?.GetString("slug") is { } slug)
            {
                workspaces.Add(slug);
            }
        }

        var activeSince = time.GetUtcNow().UtcDateTime.AddDays(-Math.Max(1, a.BitbucketActiveRepoDays));
        var items = new Dictionary<string, SourceItem>(StringComparer.OrdinalIgnoreCase);
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var workspace in workspaces)
        {
            try
            {
                await FetchWorkspaceAsync(http, auth, workspace, myUuid, activeSince, items, cancellationToken);
            }
            catch (Exception ex) when (ex is ConnectorException or HttpRequestException or JsonException)
            {
                logger.LogWarning(ex, "Bitbucket workspace {Workspace} failed", workspace);
                failed.Add(workspace);
            }
        }

        return new ConnectorSnapshot([.. items.Values], failed, []);
    }

    private async Task FetchWorkspaceAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        string workspace,
        string myUuid,
        DateTime activeSince,
        Dictionary<string, SourceItem> items,
        CancellationToken cancellationToken)
    {
        // PRs waiting on your review, repo by repo.
        var repoQuery = Uri.EscapeDataString($"updated_on >= {activeSince:yyyy-MM-ddTHH:mm:ss}Z");
        await foreach (var repo in PagesAsync(http, auth, new Uri(Api, $"repositories/{workspace}?role=member&pagelen=100&sort=-updated_on&q={repoQuery}"), cancellationToken))
        {
            var fullName = repo.GetString("full_name");
            if (fullName is null)
            {
                continue;
            }

            var prQuery = Uri.EscapeDataString($"state=\"OPEN\" AND reviewers.uuid=\"{myUuid}\"");
            var url = new Uri(Api, $"repositories/{fullName}/pullrequests?pagelen=50&q={prQuery}&fields=%2Bvalues.participants,%2Bvalues.summary");
            await foreach (var pr in PagesAsync(http, auth, url, cancellationToken))
            {
                if (!AlreadyResponded(pr, myUuid))
                {
                    var item = ToSourceItem(pr, workspace, ItemType.Review);
                    items.TryAdd(item.ExternalId, item);
                }
            }
        }

        // Your own open PRs.
        var mine = new Uri(Api, $"workspaces/{workspace}/pullrequests/{Uri.EscapeDataString(myUuid)}?state=OPEN&pagelen=50&fields=%2Bvalues.summary");
        await foreach (var pr in PagesAsync(http, auth, mine, cancellationToken))
        {
            var item = ToSourceItem(pr, workspace, ItemType.WaitingOn);
            items.TryAdd(item.ExternalId, item);
        }
    }

    /// <summary>A review you approved or requested changes on isn't waiting on you any more.</summary>
    private static bool AlreadyResponded(JsonElement pr, string myUuid) =>
        pr.Get("participants") is { ValueKind: JsonValueKind.Array } participants &&
        participants.EnumerateArray().Any(p =>
            string.Equals(p.Get("user")?.GetString("uuid"), myUuid, StringComparison.OrdinalIgnoreCase) &&
            (p.Get("approved") is { ValueKind: JsonValueKind.True } || p.GetString("state") is "approved" or "changes_requested"));

    private SourceItem ToSourceItem(JsonElement pr, string workspace, ItemType type)
    {
        var id = pr.GetProperty("id").GetInt32();
        var repo = pr.Get("destination")?.Get("repository")?.GetString("full_name") ?? workspace;
        var title = pr.GetString("title") ?? $"{repo} PR {id}";
        var author = pr.Get("author")?.GetString("display_name");
        var url = pr.Get("links")?.Get("html")?.GetString("href") ?? $"https://bitbucket.org/{repo}/pull-requests/{id}";

        return new SourceItem(
            Scope: workspace,
            ExternalId: $"{workspace}:{repo}#{id}",
            Title: type == ItemType.Review ? $"Review: {title}" : $"Your PR: {title}",
            Type: type,
            Url: url,
            UpdatedAt: pr.Get("updated_on")?.GetDateTimeOffset().UtcDateTime ?? time.GetUtcNow().UtcDateTime,
            Priority: type == ItemType.Review ? Priority.High : Priority.Low,
            Requester: type == ItemType.Review ? author : null,
            Trust: type == ItemType.Review ? SenderTrust.Organization : SenderTrust.Self,
            Details: type == ItemType.Review
                ? $"Review requested on {repo} PR #{id} by {author ?? "someone"}."
                : $"Your pull request {repo} #{id} is open, waiting on review or merge.",
            SourceContent: AtlassianHttp.Truncate(pr.Get("summary")?.GetString("raw")),
            RepoHint: repo[(repo.IndexOf('/') + 1)..]);
    }

    private static async IAsyncEnumerable<JsonElement> PagesAsync(
        HttpClient http,
        AuthenticationHeaderValue auth,
        Uri first,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Uri? next = first;
        for (var page = 0; next is not null && page < MaxPages; page++)
        {
            using var doc = await GetAsync(http, auth, next, cancellationToken);
            if (doc.RootElement.Get("values") is { ValueKind: JsonValueKind.Array } values)
            {
                foreach (var value in values.EnumerateArray())
                {
                    yield return value.Clone();
                }
            }

            next = doc.RootElement.GetString("next") is { } n ? new Uri(n) : null;
        }
    }

    private static Task<JsonDocument> GetAsync(HttpClient http, AuthenticationHeaderValue auth, Uri url, CancellationToken cancellationToken) =>
        AtlassianHttp.SendAsync(http, new HttpRequestMessage(HttpMethod.Get, url), auth, "Bitbucket", cancellationToken);
}
