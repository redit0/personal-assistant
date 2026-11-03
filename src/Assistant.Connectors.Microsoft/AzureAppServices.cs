using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Assistant.Core.Connectors;
using Microsoft.Extensions.Logging;

namespace Assistant.Connectors.Microsoft;

/// <summary>An App Service app or deployment slot.</summary>
/// <param name="Id">The ARM resource ID.</param>
/// <param name="Name">The app's name, such as "contoso-records-uat".</param>
/// <param name="Slot">The slot name, or null for the production slot.</param>
public sealed record AppServiceSite(string TenantId, string Id, string Name, string? Slot, string DefaultHost)
{
    public string Url => $"https://{DefaultHost}";

    /// <summary>"app" or "app/slot", the way the portal shows it.</summary>
    public string DisplayName => Slot is null ? Name : $"{Name}/{Slot}";
}

/// <summary>Where an app deploys from when it uses App Service Deployment Center.</summary>
public sealed record AppServiceSource(string RepoUrl, string Branch, bool UsesGitHubActions);

/// <summary>One App Service deployment. Deployment Center names them by commit hash.</summary>
public sealed record AppServiceDeployment(
    string Id, string Status, bool Active, DateTime? StartedAt, DateTime? EndedAt, string? Deployer, string? Author, string? Message);

/// <summary>
/// Reads App Service apps, their Deployment Center settings, and their deployments through Azure Resource Manager,
/// across every tenant the Azure CLI is signed in to. Read only.
/// </summary>
public sealed class AzureAppServices(IHttpClientFactory httpClients, IAzureCliToken azureCli, TimeProvider time, ILogger<AzureAppServices> logger)
{
    public const string HttpClientName = "azure-management";

    private const string ApiVersion = "api-version=2023-12-01";
    private static readonly Uri Management = new("https://management.azure.com/");
    private static readonly TimeSpan SiteListLifetime = TimeSpan.FromHours(6);

    private readonly SemaphoreSlim _listLock = new(1, 1);
    private readonly ConcurrentDictionary<string, AppServiceSource?> _sources = new(StringComparer.OrdinalIgnoreCase);
    private (DateTimeOffset At, IReadOnlyList<AppServiceSite> Sites)? _sites;
    private IReadOnlyList<string> _skipped = [];

    public bool IsAvailable => azureCli.IsAvailable;

    /// <summary>Tenants the last site listing couldn't read, usually because the CLI isn't signed in to them.</summary>
    public IReadOnlyList<string> SkippedTenants => _skipped;

    /// <summary>Every app and slot you can see. Cached for six hours; tenants you can't get a token for are skipped.</summary>
    public async Task<IReadOnlyList<AppServiceSite>> ListSitesAsync(CancellationToken cancellationToken, bool refresh = false)
    {
        await _listLock.WaitAsync(cancellationToken);
        try
        {
            if (!refresh && _sites is { } cached && time.GetUtcNow() - cached.At < SiteListLifetime)
            {
                return cached.Sites;
            }

            var tenants = await azureCli.ListTenantsAsync(cancellationToken);
            var skipped = new System.Collections.Concurrent.ConcurrentBag<string>();
            var perTenant = await Task.WhenAll(tenants.Select(async tenant =>
            {
                try
                {
                    return await ListTenantSitesAsync(tenant, cancellationToken);
                }
                catch (ConnectorException ex)
                {
                    logger.LogInformation("Skipping App Services in {Tenant}: {Error}", tenant, ex.Message);
                    skipped.Add(tenant);
                    return [];
                }
            }));

            var sites = perTenant.SelectMany(s => s).ToList();
            _sites = (time.GetUtcNow(), sites);
            _skipped = [.. skipped.Order()];
            return sites;
        }
        finally
        {
            _listLock.Release();
        }
    }

    /// <summary>The Deployment Center source, or null when the app is deployed some other way (a pipeline, zip deploy).</summary>
    public async Task<AppServiceSource?> GetSourceAsync(AppServiceSite site, CancellationToken cancellationToken)
    {
        if (_sources.TryGetValue(site.Id, out var known))
        {
            return known;
        }

        AppServiceSource? source = null;
        try
        {
            using var doc = await GetAsync(site.TenantId, $"{site.Id}/sourcecontrols/web?{ApiVersion}", cancellationToken);
            var p = doc.RootElement.GetProperty("properties");
            var repoUrl = p.TryGetProperty("repoUrl", out var r) ? r.GetString() : null;
            if (!string.IsNullOrWhiteSpace(repoUrl))
            {
                source = new AppServiceSource(
                    repoUrl,
                    p.TryGetProperty("branch", out var b) ? b.GetString() ?? "" : "",
                    p.TryGetProperty("gitHubActionConfiguration", out var g) && g.ValueKind == JsonValueKind.Object);
            }
        }
        catch (ConnectorException ex)
        {
            // 404 means no Deployment Center source; 403 means you can see the app but not its settings. Either way, remember it.
            logger.LogDebug("No Deployment Center source for {Site}: {Error}", site.DisplayName, ex.Message);
        }

        _sources[site.Id] = source;
        return source;
    }

    /// <summary>The slot this one swaps into automatically after each deployment (usually "production"), or null when swaps are manual.</summary>
    public async Task<string?> GetAutoSwapTargetAsync(AppServiceSite slot, CancellationToken cancellationToken)
    {
        using var doc = await GetAsync(slot.TenantId, $"{slot.Id}/config/web?{ApiVersion}", cancellationToken);
        return doc.RootElement.GetProperty("properties").TryGetProperty("autoSwapSlotName", out var s) && s.GetString() is { Length: > 0 } name ? name : null;
    }

    /// <summary>
    /// Creates a deployment slot with the app's settings, then points its Deployment Center at a branch, which starts the
    /// first deployment. Waits for the slot to be running. The new slot is added to the cached site list.
    /// </summary>
    public async Task<AppServiceSite> CreateSlotAsync(AppServiceSite app, string slot, string repoUrl, string branch, CancellationToken cancellationToken)
    {
        if (app.Slot is not null)
        {
            throw new ConnectorException($"{app.DisplayName} is already a slot; create slots on the app itself.");
        }

        string location, plan;
        using (var info = await GetAsync(app.TenantId, $"{app.Id}?{ApiVersion}", cancellationToken))
        {
            location = info.RootElement.GetProperty("location").GetString()!;
            plan = info.RootElement.GetProperty("properties").GetProperty("serverFarmId").GetString()!;
        }

        var slotId = $"{app.Id}/slots/{Uri.EscapeDataString(slot)}";
        (await SendAsync(app.TenantId, HttpMethod.Put, $"{slotId.TrimStart('/')}?{ApiVersion}", new
        {
            location,
            properties = new
            {
                serverFarmId = plan,
                cloneInfo = new { sourceWebAppId = app.Id, sourceWebAppLocation = location, cloneCustomHostNames = false, cloneSourceControl = false, overwrite = true },
            },
        }, cancellationToken)).Dispose();

        // Creating a slot that clones settings runs in the background; wait until it's up.
        string? host = null;
        var deadline = time.GetUtcNow().AddMinutes(5);
        while (host is null)
        {
            try
            {
                using var created = await GetAsync(app.TenantId, $"{slotId}?{ApiVersion}", cancellationToken);
                var p = created.RootElement.GetProperty("properties");
                if (p.TryGetProperty("state", out var state) && state.GetString() == "Running")
                {
                    host = p.GetProperty("defaultHostName").GetString();
                }
            }
            catch (ConnectorException ex) when (ex.Message.Contains("404", StringComparison.Ordinal))
            {
            }

            if (host is null)
            {
                if (time.GetUtcNow() > deadline)
                {
                    throw new ConnectorException($"The slot {app.Name}/{slot} didn't finish starting within five minutes. Check it in the portal.");
                }

                await Task.Delay(TimeSpan.FromSeconds(5), time, cancellationToken);
            }
        }

        (await SendAsync(app.TenantId, HttpMethod.Put, $"{slotId.TrimStart('/')}/sourcecontrols/web?{ApiVersion}", new
        {
            properties = new { repoUrl, branch, isManualIntegration = false, isMercurial = false, deploymentRollbackEnabled = false },
        }, cancellationToken)).Dispose();

        var site = new AppServiceSite(app.TenantId, slotId, app.Name, slot, host);
        _sources[site.Id] = new AppServiceSource(repoUrl, branch, UsesGitHubActions: false);
        if (_sites is { } cached)
        {
            _sites = (cached.At, [.. cached.Sites, site]);
        }

        return site;
    }

    /// <summary>The most recent deployments, newest first.</summary>
    public async Task<IReadOnlyList<AppServiceDeployment>> ListDeploymentsAsync(AppServiceSite site, CancellationToken cancellationToken)
    {
        using var doc = await GetAsync(site.TenantId, $"{site.Id}/deployments?{ApiVersion}", cancellationToken);
        return [.. doc.RootElement.GetProperty("value").EnumerateArray()
            .Select(d =>
            {
                var p = d.GetProperty("properties");
                var name = d.GetProperty("name").GetString() ?? "";
                return new AppServiceDeployment(
                    name[(name.LastIndexOf('/') + 1)..],
                    StatusName(p.TryGetProperty("status", out var s) && s.TryGetInt32(out var n) ? n : -1),
                    p.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True,
                    Date(p, "start_time"),
                    Date(p, "end_time"),
                    Text(p, "deployer"),
                    Text(p, "author"),
                    Text(p, "message"));
            })
            .OrderByDescending(d => d.EndedAt ?? d.StartedAt ?? DateTime.MaxValue)];
    }

    private async Task<IReadOnlyList<AppServiceSite>> ListTenantSitesAsync(string tenant, CancellationToken cancellationToken)
    {
        const string query = "resources | where type =~ 'microsoft.web/sites' or type =~ 'microsoft.web/sites/slots' " +
            "| project id, name, type, host = tostring(properties.defaultHostName)";

        var sites = new List<AppServiceSite>();
        string? skipToken = null;
        do
        {
            var options = new Dictionary<string, object> { ["$top"] = 1000 };
            if (skipToken is not null)
            {
                options["$skipToken"] = skipToken;
            }

            using var doc = await SendAsync(tenant, HttpMethod.Post, "providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01",
                new { query, options }, cancellationToken);

            foreach (var row in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = row.GetProperty("id").GetString()!;
                var name = row.GetProperty("name").GetString()!;
                var host = row.TryGetProperty("host", out var h) ? h.GetString() : null;
                if (string.IsNullOrEmpty(host))
                {
                    continue;
                }

                var isSlot = row.GetProperty("type").GetString()!.EndsWith("/slots", StringComparison.OrdinalIgnoreCase);
                var parts = name.Split('/', 2);
                sites.Add(new AppServiceSite(tenant, id, parts[0], isSlot && parts.Length == 2 ? parts[1] : null, host));
            }

            skipToken = doc.RootElement.TryGetProperty("$skipToken", out var next) ? next.GetString() : null;
        }
        while (skipToken is not null);

        return sites;
    }

    private Task<JsonDocument> GetAsync(string tenant, string path, CancellationToken cancellationToken) =>
        SendAsync(tenant, HttpMethod.Get, path.TrimStart('/'), null, cancellationToken);

    private async Task<JsonDocument> SendAsync(string tenant, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var token = await azureCli.GetTokenAsync(tenant, AzureCliToken.ManagementResource, cancellationToken);
        using var http = httpClients.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(method, new Uri(Management, path)) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ConnectorException(response.StatusCode == HttpStatusCode.NotFound
                ? $"Azure: 404 for {path}"
                : $"Azure: {(int)response.StatusCode} {Message(text)}");
        }

        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    /// <summary>Kudu's deployment status codes.</summary>
    private static string StatusName(int status) => status switch
    {
        0 => "pending",
        1 => "building",
        2 => "deploying",
        3 => "failed",
        4 => "succeeded",
        _ => "unknown",
    };

    private static DateTime? Date(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && DateTime.TryParse(v.GetString(), null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static string? Text(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Message(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? m.GetString() ?? body : body;
        }
        catch (JsonException)
        {
            return body.Length > 200 ? body[..200] : body;
        }
    }
}
