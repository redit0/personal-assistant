using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Assistant.Core.Connectors;
using Assistant.Core.Items;
using Assistant.Core.Secrets;
using Assistant.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;

namespace Assistant.Connectors.Microsoft;

/// <summary>What a Find organizations run turned up.</summary>
public sealed record DiscoveryResult(int Found, IReadOnlyList<string> Errors);

/// <summary>
/// Azure DevOps Services, across every organization you belong to: PRs waiting on your vote, your own
/// open PRs, work items assigned to you, and work items that recently @mentioned you. New organizations
/// are discovered automatically and watched or not according to your setting.
///
/// Organizations can sign in three ways: your home tenant through Microsoft sign-in, a client tenant where
/// you're a guest through the Azure CLI, or a personal access token entered for that organization.
/// </summary>
public sealed partial class AzureDevOpsConnector(
    IHttpClientFactory httpClients,
    UserSettingsStore settings,
    IMicrosoftTokenProvider auth,
    IAzureCliToken azureCli,
    ISecretStore secrets,
    TimeProvider time,
    ILogger<AzureDevOpsConnector> logger) : IConnector
{
    public const string HttpClientName = "azuredevops";

    /// <summary>The Azure DevOps resource. Your Entra app needs its user_impersonation permission.</summary>
    public static readonly string[] Scopes = ["499b84ac-1321-427f-aa17-267ca6975798/.default"];

    private const string ApiVersion = "api-version=7.1";
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromHours(1);

    private DateTime _lastDiscovery = DateTime.MinValue;

    public string Id => "azuredevops";

    public string DisplayName => "Azure DevOps";

    public TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, settings.Current.AzureDevOps.IntervalMinutes));

    public string? NotReadyReason()
    {
        var ado = settings.Current.AzureDevOps;
        if (!ado.Enabled) return "Turned off.";

        var hasTokenOrgs = ado.OrganizationSources.Values.Any(s => s == AzureDevOpsSettings.TokenSource);
        return auth.IsConfigured || ado.Tenants.Count > 0 || hasTokenOrgs
            ? null
            : "Set up Microsoft sign-in, add a client tenant, or add an organization with a token.";
    }

    public async Task<ConnectorSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        if (time.GetUtcNow().UtcDateTime - _lastDiscovery > DiscoveryInterval)
        {
            warnings.AddRange((await RefreshOrganizationsAsync(cancellationToken)).Errors);
        }

        var ado = settings.Current.AzureDevOps;
        var watched = ado.Organizations.Where(o => o.Value).Select(o => o.Key).ToList();
        if (watched.Count == 0)
        {
            warnings.Add("No organizations are being watched. Pick some under Organizations.");
            return new ConnectorSnapshot([], new HashSet<string>(), warnings);
        }

        var items = new Dictionary<string, SourceItem>(StringComparer.OrdinalIgnoreCase);
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clients = new Dictionary<string, HttpClient>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var org in watched)
            {
                var source = ado.OrganizationSources.GetValueOrDefault(org, AzureDevOpsSettings.HomeSource);
                try
                {
                    // Organizations from the same tenant share one client and token. Token organizations each have their own.
                    var key = source == AzureDevOpsSettings.TokenSource ? $"{source}:{org}" : source;
                    if (!clients.TryGetValue(key, out var http))
                    {
                        http = clients[key] = CreateClient(await GetAuthorizationAsync(source, org, cancellationToken));
                    }

                    foreach (var item in await FetchOrganizationAsync(http, org, cancellationToken))
                    {
                        items.TryAdd(item.ExternalId, item);
                    }
                }
                catch (Exception ex) when (ex is ConnectorException or HttpRequestException or JsonException)
                {
                    logger.LogWarning(ex, "Azure DevOps organization {Org} failed", org);
                    failed.Add(org);
                    warnings.Add($"{org}: {ex.Message}");
                }
            }
        }
        finally
        {
            foreach (var client in clients.Values)
            {
                client.Dispose();
            }
        }

        return new ConnectorSnapshot([.. items.Values], failed, warnings);
    }

    /// <summary>
    /// Finds the organizations you belong to, in your home tenant and each client tenant, and records any
    /// new ones. A tenant that fails is reported without stopping the others.
    /// </summary>
    public async Task<DiscoveryResult> RefreshOrganizationsAsync(CancellationToken cancellationToken)
    {
        var ado = settings.Current.AzureDevOps;
        var sources = new List<(string Source, string Name)>();
        if (auth.IsConfigured)
        {
            sources.Add((AzureDevOpsSettings.HomeSource, "your home tenant"));
        }

        sources.AddRange(ado.Tenants.Select(t => (AzureDevOpsSettings.TenantSource(t.TenantId), t.Label ?? t.TenantId)));

        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        foreach (var (source, name) in sources)
        {
            try
            {
                using var http = CreateClient(await GetAuthorizationAsync(source, null, cancellationToken));
                foreach (var org in await ListOrganizationsAsync(http, cancellationToken))
                {
                    found.TryAdd(org, source);
                }
            }
            catch (Exception ex) when (ex is ConnectorException or HttpRequestException or JsonException)
            {
                logger.LogWarning(ex, "Azure DevOps discovery failed for {Source}", source);
                errors.Add($"Couldn't list organizations for {name}: {ex.Message}");
            }
        }

        settings.Update(s =>
        {
            var current = s.AzureDevOps;
            foreach (var (org, source) in found)
            {
                if (!current.Organizations.ContainsKey(org))
                {
                    current.Organizations[org] = current.WatchNewOrganizations;
                }

                // An organization you gave a token to keeps using it.
                if (current.OrganizationSources.GetValueOrDefault(org) != AzureDevOpsSettings.TokenSource)
                {
                    current.OrganizationSources[org] = source;
                }
            }
        });

        _lastDiscovery = time.GetUtcNow().UtcDateTime;
        return new DiscoveryResult(found.Count, errors);
    }

    private static async Task<List<string>> ListOrganizationsAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using var profile = await GetJsonAsync(http, $"https://app.vssps.visualstudio.com/_apis/profile/profiles/me?{ApiVersion}", cancellationToken);
        var memberId = profile.RootElement.GetProperty("id").GetString();

        using var accounts = await GetJsonAsync(http, $"https://app.vssps.visualstudio.com/_apis/accounts?memberId={memberId}&{ApiVersion}", cancellationToken);
        return [.. accounts.RootElement.GetProperty("value").EnumerateArray()
            .Select(a => a.GetProperty("accountName").GetString())
            .OfType<string>()];
    }

    private async Task<List<SourceItem>> FetchOrganizationAsync(HttpClient http, string org, CancellationToken cancellationToken)
    {
        var baseUrl = $"https://dev.azure.com/{Uri.EscapeDataString(org)}";

        using var connection = await GetJsonAsync(http, $"{baseUrl}/_apis/connectionData", cancellationToken);
        var me = connection.RootElement.GetProperty("authenticatedUser").GetProperty("id").GetString()
            ?? throw new ConnectorException($"Azure DevOps didn't return your identity in {org}.");

        var items = new List<SourceItem>();

        foreach (var pr in await GetPullRequestsAsync(http, baseUrl, $"searchCriteria.reviewerId={me}", cancellationToken))
        {
            if (MyVote(pr, me) == 0 && pr.GetProperty("createdBy").GetProperty("id").GetString() != me)
            {
                items.Add(ToPullRequestItem(org, pr, ItemType.Review));
            }
        }

        foreach (var pr in await GetPullRequestsAsync(http, baseUrl, $"searchCriteria.creatorId={me}", cancellationToken))
        {
            items.Add(ToPullRequestItem(org, pr, ItemType.WaitingOn));
        }

        // Stock processes use these state names for finished work. Custom states still show up, which is
        // the safer mistake.
        const string notFinished = "[System.State] NOT IN ('Closed', 'Done', 'Removed', 'Resolved', 'Completed', 'Cut')";
        var assigned = await QueryWorkItemIdsAsync(http, baseUrl,
            $"SELECT [System.Id] FROM WorkItems WHERE [System.AssignedTo] = @Me AND {notFinished} ORDER BY [System.ChangedDate] DESC", cancellationToken);
        // Mentions are a nice-to-have; if this query fails, keep the rest of the organization's items.
        HashSet<int> mentioned;
        try
        {
            mentioned = await QueryWorkItemIdsAsync(http, baseUrl,
                $"SELECT [System.Id] FROM WorkItems WHERE [System.Id] IN (@RecentMentions) AND [System.AssignedTo] <> @Me AND {notFinished}", cancellationToken);
        }
        catch (ConnectorException ex)
        {
            logger.LogWarning(ex, "Azure DevOps mention query failed in {Org}", org);
            mentioned = [];
        }

        foreach (var wi in await GetWorkItemsAsync(http, baseUrl, [.. assigned, .. mentioned], cancellationToken))
        {
            var id = wi.GetProperty("id").GetInt32();
            items.Add(ToWorkItem(org, wi, assigned.Contains(id) ? ItemType.Assigned : ItemType.Ask));
        }

        return items;
    }

    private async Task<List<JsonElement>> GetPullRequestsAsync(HttpClient http, string baseUrl, string criteria, CancellationToken cancellationToken)
    {
        var query = $"_apis/git/pullrequests?{criteria}&searchCriteria.status=active&$top=200&{ApiVersion}";
        try
        {
            using var doc = await GetJsonAsync(http, $"{baseUrl}/{query}", cancellationToken);
            return [.. doc.RootElement.GetProperty("value").EnumerateArray().Select(e => e.Clone())];
        }
        catch (ConnectorException ex) when (ex.Data["status"] is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            // The organization-wide form isn't documented; fall back to asking each project.
            logger.LogInformation("Org-level PR search unavailable at {Url}; querying each project", baseUrl);
            using var projects = await GetJsonAsync(http, $"{baseUrl}/_apis/projects?$top=500&{ApiVersion}", cancellationToken);

            var results = new List<JsonElement>();
            foreach (var project in projects.RootElement.GetProperty("value").EnumerateArray())
            {
                using var doc = await GetJsonAsync(http, $"{baseUrl}/{Uri.EscapeDataString(project.GetProperty("name").GetString()!)}/{query}", cancellationToken);
                results.AddRange(doc.RootElement.GetProperty("value").EnumerateArray().Select(e => e.Clone()));
            }

            return results;
        }
    }

    private async Task<HashSet<int>> QueryWorkItemIdsAsync(HttpClient http, string baseUrl, string wiql, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/_apis/wit/wiql?$top=200&{ApiVersion}")
        {
            Content = JsonContent.Create(new { query = wiql }),
        };

        using var doc = await SendAsync(http, request, cancellationToken);
        return [.. doc.RootElement.GetProperty("workItems").EnumerateArray().Select(w => w.GetProperty("id").GetInt32())];
    }

    private async Task<List<JsonElement>> GetWorkItemsAsync(HttpClient http, string baseUrl, IReadOnlyCollection<int> ids, CancellationToken cancellationToken)
    {
        const string fields = "System.Title,System.State,System.WorkItemType,System.TeamProject,System.CreatedBy,System.ChangedDate," +
            "System.Description,Microsoft.VSTS.Common.Priority,Microsoft.VSTS.Scheduling.DueDate,Microsoft.VSTS.Scheduling.TargetDate";

        var results = new List<JsonElement>();
        foreach (var batch in ids.Distinct().Chunk(200))
        {
            using var doc = await GetJsonAsync(http,
                $"{baseUrl}/_apis/wit/workitems?ids={string.Join(',', batch)}&fields={fields}&errorPolicy=Omit&{ApiVersion}", cancellationToken);
            results.AddRange(doc.RootElement.GetProperty("value").EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Object)
                .Select(e => e.Clone()));
        }

        return results;
    }

    private static int MyVote(JsonElement pr, string me) =>
        pr.TryGetProperty("reviewers", out var reviewers)
            ? reviewers.EnumerateArray().Where(r => r.GetProperty("id").GetString() == me).Select(r => r.GetProperty("vote").GetInt32()).FirstOrDefault()
            : 0;

    private SourceItem ToPullRequestItem(string org, JsonElement pr, ItemType type)
    {
        var id = pr.GetProperty("pullRequestId").GetInt32();
        var repo = pr.GetProperty("repository");
        var repoName = repo.GetProperty("name").GetString()!;
        var project = repo.GetProperty("project").GetProperty("name").GetString()!;
        var title = pr.GetProperty("title").GetString() ?? $"PR {id}";
        var author = pr.GetProperty("createdBy").GetProperty("displayName").GetString();
        var draft = pr.TryGetProperty("isDraft", out var d) && d.GetBoolean();

        return new SourceItem(
            Scope: org,
            ExternalId: $"{org}:pr:{id}",
            Title: type == ItemType.Review ? $"Review: {title}" : $"Your PR: {title}",
            Type: type,
            Url: $"https://dev.azure.com/{Uri.EscapeDataString(org)}/{Uri.EscapeDataString(project)}/_git/{Uri.EscapeDataString(repoName)}/pullrequest/{id}",
            UpdatedAt: pr.TryGetProperty("creationDate", out var created) ? created.GetDateTime().ToUniversalTime() : time.GetUtcNow().UtcDateTime,
            Priority: type == ItemType.Review ? Priority.High : Priority.Low,
            Requester: type == ItemType.Review ? author : null,
            Trust: type == ItemType.Review ? SenderTrust.Organization : SenderTrust.Self,
            Details: type == ItemType.Review
                ? $"Review requested on {project}/{repoName} PR {id} by {author}{(draft ? " (draft)" : "")}."
                : $"Your pull request {project}/{repoName} {id} is open, waiting on review or completion.",
            SourceContent: pr.TryGetProperty("description", out var desc) ? desc.GetString() : null,
            RepoHint: repoName);
    }

    private SourceItem ToWorkItem(string org, JsonElement wi, ItemType type)
    {
        var id = wi.GetProperty("id").GetInt32();
        var f = wi.GetProperty("fields");
        var title = Str(f, "System.Title") ?? $"Work item {id}";
        var project = Str(f, "System.TeamProject") ?? "";
        var kind = Str(f, "System.WorkItemType") ?? "Work item";
        var state = Str(f, "System.State");
        var createdBy = f.TryGetProperty("System.CreatedBy", out var cb) && cb.ValueKind == JsonValueKind.Object
            ? cb.GetProperty("displayName").GetString()
            : null;
        var due = Date(f, "Microsoft.VSTS.Scheduling.DueDate") ?? Date(f, "Microsoft.VSTS.Scheduling.TargetDate");

        return new SourceItem(
            Scope: org,
            ExternalId: $"{org}:wi:{id}",
            Title: $"{kind} {id}: {title}",
            Type: type,
            Url: $"https://dev.azure.com/{Uri.EscapeDataString(org)}/{Uri.EscapeDataString(project)}/_workitems/edit/{id}",
            UpdatedAt: Date(f, "System.ChangedDate") ?? time.GetUtcNow().UtcDateTime,
            Priority: f.TryGetProperty("Microsoft.VSTS.Common.Priority", out var p) && p.TryGetInt32(out var pri)
                ? pri switch { 1 => Priority.High, 2 => Priority.Normal, _ => Priority.Low }
                : Priority.Normal,
            DueAt: due,
            Requester: createdBy,
            Trust: SenderTrust.Organization,
            Details: type == ItemType.Assigned
                ? $"{kind} {id} in {project} is assigned to you ({state})."
                : $"You were mentioned on {kind} {id} in {project} ({state}).",
            SourceContent: HtmlToText(Str(f, "System.Description")));
    }

    private static string? Str(JsonElement fields, string name) =>
        fields.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTime? Date(JsonElement fields, string name) =>
        fields.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var d)
            ? d.UtcDateTime
            : null;

    private static string? HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var text = WebUtility.HtmlDecode(Tags().Replace(BlockTags().Replace(html, "\n"), ""));
        text = text.Trim();
        return text.Length <= 8000 ? text : text[..8000] + "\n…";
    }

    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/h\d)\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTags();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    /// <summary>The Authorization header for an organization's sign-in source.</summary>
    private async Task<AuthenticationHeaderValue> GetAuthorizationAsync(string source, string? org, CancellationToken cancellationToken)
    {
        if (source == AzureDevOpsSettings.TokenSource)
        {
            var pat = org is null ? null : secrets.Get(SecretNames.AzureDevOpsToken(org));
            if (string.IsNullOrEmpty(pat))
            {
                throw new ConnectorException($"No access token saved for {org}. Add one on the Connectors page.");
            }

            // Personal access tokens use Basic auth with an empty user name.
            return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(":" + pat)));
        }

        if (source.StartsWith("tenant:", StringComparison.Ordinal))
        {
            return new AuthenticationHeaderValue("Bearer", await azureCli.GetTokenAsync(source["tenant:".Length..], cancellationToken));
        }

        try
        {
            return new AuthenticationHeaderValue("Bearer", await auth.GetAccessTokenAsync(Scopes, cancellationToken));
        }
        catch (MsalUiRequiredException)
        {
            throw new ConnectorException("Azure DevOps needs your consent. Click Connect on the Connectors page.");
        }
        catch (MsalServiceException ex) when (ex.Message.Contains("AADSTS650057") || ex.Message.Contains("AADSTS65001"))
        {
            throw new ConnectorException("Your Entra app is missing the Azure DevOps permission. Add it under API permissions, then click Connect.");
        }
    }

    /// <summary>An HTTP client signed in the way this organization is configured: home tenant, client tenant, or token.</summary>
    public async Task<HttpClient> CreateOrganizationClientAsync(string organization, CancellationToken cancellationToken)
    {
        var source = settings.Current.AzureDevOps.OrganizationSources.GetValueOrDefault(organization, AzureDevOpsSettings.HomeSource);
        return CreateClient(await GetAuthorizationAsync(source, organization, cancellationToken));
    }

    private HttpClient CreateClient(AuthenticationHeaderValue authorization)
    {
        var http = httpClients.CreateClient(HttpClientName);
        http.DefaultRequestHeaders.Authorization = authorization;
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    }

    internal static Task<JsonDocument> GetJsonAsync(HttpClient http, string url, CancellationToken cancellationToken) =>
        SendAsync(http, new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);

    internal static async Task<JsonDocument> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            // Azure DevOps answers an expired or missing token with a 203 sign-in page instead of a 401.
            var isSignInPage = response.StatusCode == HttpStatusCode.NonAuthoritativeInformation ||
                response.Content.Headers.ContentType?.MediaType == "text/html";

            if (response.IsSuccessStatusCode && !isSignInPage)
            {
                return JsonDocument.Parse(body);
            }

            var ex = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.NonAuthoritativeInformation =>
                    new ConnectorException("Azure DevOps didn't accept your sign-in. Click Connect on the Connectors page."),
                HttpStatusCode.Forbidden => new ConnectorException("Azure DevOps refused access. Your account may not have access to this organization."),
                _ when isSignInPage => new ConnectorException("Azure DevOps returned a sign-in page. Click Connect on the Connectors page."),
                _ => new ConnectorException($"Azure DevOps returned {(int)response.StatusCode} for {request.RequestUri?.AbsolutePath}: {(body.Length > 200 ? body[..200] : body)}"),
            };
            ex.Data["status"] = response.StatusCode;
            throw ex;
        }
    }
}
