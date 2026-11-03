using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Assistant.Core.Connectors;
using Assistant.Core.Items;
using Assistant.Core.Secrets;
using Assistant.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Assistant.Connectors.GitHub;

/// <summary>
/// github.com: PRs waiting on your review, issues and PRs assigned to you, recent mentions, and your
/// own open PRs. Uses the issue search API, which covers every repo you can see in one call per query.
/// </summary>
public sealed class GitHubConnector(
    IHttpClientFactory httpClients,
    UserSettingsStore settings,
    ISecretStore secrets,
    IGitHubCliToken ghCli,
    TimeProvider time,
    ILogger<GitHubConnector> logger) : IConnector
{
    public const string HttpClientName = "github";
    public const string Scope = "github.com";

    private const int MaxPages = 3;
    private const int MaxBodyLength = 8000;
    private static readonly TimeSpan MentionWindow = TimeSpan.FromDays(14);

    public string Id => "github";

    public string DisplayName => "GitHub";

    public TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, settings.Current.GitHub.IntervalMinutes));

    public string? NotReadyReason()
    {
        var gh = settings.Current.GitHub;
        if (!gh.Enabled)
        {
            return "Turned off.";
        }

        return string.IsNullOrEmpty(secrets.Get(SecretNames.GitHubToken)) && !(gh.UseGhCli && ghCli.IsAvailable)
            ? "Needs a personal access token, or a signed-in GitHub CLI."
            : null;
    }

    public async Task<ConnectorSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(cancellationToken);
        using var http = httpClients.CreateClient(HttpClientName);
        http.BaseAddress ??= new Uri("https://api.github.com/");

        var since = time.GetUtcNow().UtcDateTime - MentionWindow;

        // Order matters: an item matching several queries keeps the first (most urgent) kind.
        var queries = new (string Query, ItemType Type)[]
        {
            ("is:open is:pr archived:false review-requested:@me", ItemType.Review),
            ("is:open archived:false assignee:@me", ItemType.Assigned),
            ($"is:open archived:false mentions:@me updated:>={since:yyyy-MM-dd}", ItemType.Ask),
            ("is:open is:pr archived:false author:@me", ItemType.WaitingOn),
        };

        var items = new Dictionary<string, SourceItem>(StringComparer.OrdinalIgnoreCase);
        var incomplete = false;

        foreach (var (query, type) in queries)
        {
            var (results, complete) = await SearchAsync(http, token, query, cancellationToken);
            incomplete |= !complete;

            foreach (var result in results)
            {
                var item = ToSourceItem(result, type);
                items.TryAdd(item.ExternalId, item);
            }
        }

        // GitHub sometimes times out and returns partial search results; don't close items based on those.
        return incomplete
            ? new ConnectorSnapshot([.. items.Values], new HashSet<string> { Scope }, ["GitHub returned partial results; nothing was closed this round."])
            : ConnectorSnapshot.Complete([.. items.Values]);
    }

    private async Task<(List<JsonElement> Items, bool Complete)> SearchAsync(HttpClient http, string token, string query, CancellationToken cancellationToken)
    {
        var results = new List<JsonElement>();
        var complete = true;

        for (var page = 1; page <= MaxPages; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"search/issues?q={Uri.EscapeDataString(query)}&per_page=100&page={page}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            request.Headers.UserAgent.ParseAdd("PersonalAssistant/0.1");

            using var response = await http.SendAsync(request, cancellationToken);
            await ThrowIfFailedAsync(response, cancellationToken);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = doc.RootElement;
            complete &= !root.TryGetProperty("incomplete_results", out var inc) || !inc.GetBoolean();

            var pageItems = root.GetProperty("items").EnumerateArray().Select(e => e.Clone()).ToList();
            results.AddRange(pageItems);

            var total = root.TryGetProperty("total_count", out var t) ? t.GetInt32() : results.Count;
            if (pageItems.Count < 100 || results.Count >= total)
            {
                return (results, complete);
            }
        }

        logger.LogWarning("GitHub query '{Query}' has more than {Max} results; the rest were skipped", query, MaxPages * 100);
        return (results, false);
    }

    private static SourceItem ToSourceItem(JsonElement e, ItemType type)
    {
        var repo = RepoFromApiUrl(e.GetProperty("repository_url").GetString()!);
        var number = e.GetProperty("number").GetInt32();
        var isPr = e.TryGetProperty("pull_request", out _);
        var author = e.GetProperty("user").GetProperty("login").GetString();
        var title = e.GetProperty("title").GetString() ?? $"{repo}#{number}";
        var body = e.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;

        var (prefix, details, priority) = type switch
        {
            ItemType.Review => ("Review", $"Review requested on {repo}#{number} by {author}.", Priority.High),
            ItemType.Assigned => (isPr ? "PR" : "Issue", $"{(isPr ? "Pull request" : "Issue")} {repo}#{number} is assigned to you.", Priority.Normal),
            ItemType.Ask => ("Mention", $"You were mentioned on {repo}#{number}.", Priority.Normal),
            _ => ("Your PR", $"Your pull request {repo}#{number} is open, waiting on review or merge.", Priority.Low),
        };

        return new SourceItem(
            Scope: Scope,
            ExternalId: $"{Scope}:{repo}#{number}",
            Title: $"{prefix}: {title}",
            Type: type,
            Url: e.GetProperty("html_url").GetString()!,
            UpdatedAt: e.GetProperty("updated_at").GetDateTime().ToUniversalTime(),
            Priority: priority,
            Requester: type == ItemType.WaitingOn ? null : author,
            Trust: type == ItemType.WaitingOn ? SenderTrust.Self : TrustFrom(e),
            Details: details,
            SourceContent: Truncate(body),
            RepoHint: repo[(repo.IndexOf('/') + 1)..]);
    }

    /// <summary>"owner/repo" from "https://api.github.com/repos/owner/repo".</summary>
    private static string RepoFromApiUrl(string url)
    {
        var parts = new Uri(url).AbsolutePath.Trim('/').Split('/');
        return $"{parts[^2]}/{parts[^1]}";
    }

    private static SenderTrust TrustFrom(JsonElement e) =>
        (e.TryGetProperty("author_association", out var a) ? a.GetString() : null) switch
        {
            "OWNER" or "MEMBER" or "COLLABORATOR" => SenderTrust.Organization,
            "CONTRIBUTOR" => SenderTrust.KnownContact,
            _ => SenderTrust.External,
        };

    private static string? Truncate(string? text) =>
        text is null || text.Length <= MaxBodyLength ? text : text[..MaxBodyLength] + "\n…";

    /// <summary>The token used for API calls: a saved token, or the GitHub CLI login. Also used by GitHub actions.</summary>
    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        var saved = secrets.Get(SecretNames.GitHubToken);
        if (!string.IsNullOrEmpty(saved))
        {
            return saved;
        }

        if (settings.Current.GitHub.UseGhCli && await ghCli.GetTokenAsync(cancellationToken) is { Length: > 0 } fromCli)
        {
            return fromCli;
        }

        throw new ConnectorException("No GitHub token. Add one in Settings, or sign in with the GitHub CLI (gh auth login).");
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new ConnectorException("GitHub rejected the token. Create a new one, or run gh auth login again."),
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests when response.Headers.TryGetValues("x-ratelimit-remaining", out var left) && left.FirstOrDefault() == "0" =>
                new ConnectorException("GitHub rate limit reached. It will retry on the next sync."),
            HttpStatusCode.Forbidden => new ConnectorException($"GitHub refused the request. If your org uses SSO, authorize the token for it. ({Short(body)})"),
            _ => new ConnectorException($"GitHub returned {(int)response.StatusCode}: {Short(body)}"),
        };
    }

    private static string Short(string text) => text.Length <= 200 ? text : text[..200] + "…";
}
