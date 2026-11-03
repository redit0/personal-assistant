using System.ComponentModel;
using System.Diagnostics;
using Assistant.Connectors.Atlassian;
using Assistant.Connectors.GitHub;
using Assistant.Connectors.Microsoft;
using Assistant.Core.Actions;
using Assistant.Core.Settings;
using Assistant.Core.Tools;
using Assistant.Data;
using Assistant.Tools.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Actions.Shipping;

/// <summary>Where a merged commit is on its way to the site.</summary>
/// <param name="Stage">waiting_for_ci, ci_failed, deploying, deployed, deploy_failed, or not_seen (no deployment for it yet).</param>
public sealed record DeploymentStatus(
    string Commit,
    string Stage,
    string Summary,
    IReadOnlyList<CheckStatus> Ci,
    string? App,
    string? Url,
    AppServiceDeployment? Deployment,
    int? SiteStatusCode,
    long? SiteResponseMs);

/// <summary>
/// Tools for taking finished work past the pull request: checking and merging it, finding where the branch deploys,
/// watching the deployment, and handing the ticket to QA. Merging and assigning are source actions, so they ask for
/// your approval unless a Hand to Claude session is working on that item. Nothing here merges into a branch that
/// deploys production.
/// </summary>
public sealed class ShippingTools(
    AssistantDbContext db,
    ToolCallContext context,
    TimeProvider time,
    GitHubActions github,
    AtlassianActions atlassian,
    AzureDevOpsActions azureDevOps,
    AzureAppServices appServices,
    DeploymentFinder finder,
    UserSettingsStore settings,
    IHttpClientFactory httpClients)
{
    public const string ProbeClientName = "deployment-probe";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    [AssistantTool("get_pull_request", ToolCategory.Read, Title = "Get a pull request", OpenWorld = true)]
    [Description("Gets a pull request's state, branches, latest commit, CI checks, reviews, and what (if anything) blocks merging it. " +
        "Works for GitHub, Bitbucket, and Azure DevOps. After merging, mergeCommit is the commit to track with get_deployment_status.")]
    public async Task<PullRequestInfo> GetPullRequestAsync(
        [Description("Web link to the pull request.")] string url,
        CancellationToken cancellationToken = default)
    {
        var target = SourceLinks.Parse(url) ?? throw new ToolException($"{url} isn't a pull request link.");
        return await ActionSupport.RunAsync(() => target switch
        {
            GitHubIssueOrPull { IsPull: true } g => github.GetPullRequestAsync(g, cancellationToken),
            BitbucketPull b => atlassian.GetPullRequestAsync(b, cancellationToken),
            AzureDevOpsPull a => azureDevOps.GetPullRequestAsync(a, cancellationToken),
            _ => throw new ToolException($"{url} isn't a pull request link."),
        });
    }

    [AssistantTool("merge_pull_request", ToolCategory.SourceAction, Title = "Merge a pull request", OpenWorld = true)]
    [Description("Merges a pull request on GitHub, Bitbucket, or Azure DevOps once its checks have passed. Refuses when the target branch " +
        "deploys production directly (per find_deployments; a production stage behind a pipeline's approval gates doesn't count), when checks are failing or still running, or when the host reports blockers. " +
        "Branch rules on the host still apply.")]
    public async Task<ActionResult> MergePullRequestAsync(
        [Description("Web link to the pull request.")] string url,
        [Description("merge, squash, or rebase. Leave empty for the repo's default.")] string? strategy = null,
        [Description("Delete the source branch after merging.")] bool deleteSourceBranch = true,
        [Description("The item this work is for. The merge is recorded on it.")] int? itemId = null,
        CancellationToken cancellationToken = default)
    {
        var target = SourceLinks.Parse(url) ?? throw new ToolException($"{url} isn't a pull request link.");
        var merge = strategy?.Trim().ToLowerInvariant() switch
        {
            null or "" => (MergeStrategy?)null,
            "merge" or "merge_commit" => MergeStrategy.Merge,
            "squash" => MergeStrategy.Squash,
            "rebase" or "fast_forward" => MergeStrategy.Rebase,
            _ => throw new ToolException("strategy must be merge, squash, or rebase."),
        };

        var info = await GetPullRequestAsync(url, cancellationToken);
        if (info.State != "open")
        {
            throw new ToolException($"The pull request is {info.State}, so there's nothing to merge.");
        }

        var repo = RepoOf(target);
        var map = await FindForAsync(repo, cancellationToken);
        if (map.ProductionBranches.Contains(info.TargetBranch, StringComparer.OrdinalIgnoreCase) || DeploymentFinder.IsProductionBranchName(info.TargetBranch))
        {
            throw new ToolException($"{info.TargetBranch} deploys production, and the assistant never merges into it. Leave this merge to a person.");
        }

        var overall = CheckStates.Overall(info.Checks);
        if (overall == CheckStates.Pending)
        {
            throw new ToolException("Checks are still running. Wait for them with get_pull_request, then merge.");
        }

        if (overall == CheckStates.Failure)
        {
            var failed = string.Join(", ", info.Checks.Where(c => c.State == CheckStates.Failure).Select(c => c.Name));
            throw new ToolException($"Checks failed ({failed}). Fix them and push before merging.");
        }

        if (info.Blockers.Count > 0)
        {
            throw new ToolException("The host won't merge it yet: " + string.Join(" ", info.Blockers));
        }

        var result = await ActionSupport.RunAsync(() => target switch
        {
            GitHubIssueOrPull g => github.MergeAsync(g, merge, deleteSourceBranch, cancellationToken),
            BitbucketPull b => atlassian.MergePullRequestAsync(b, merge, deleteSourceBranch, cancellationToken),
            AzureDevOpsPull a => azureDevOps.CompletePullRequestAsync(a, merge, deleteSourceBranch, cancellationToken),
            _ => throw new ToolException($"{url} isn't a pull request link."),
        });

        var deploysTo = map.Targets.Where(t => string.Equals(t.Branch, info.TargetBranch, StringComparison.OrdinalIgnoreCase)).ToList();
        if (deploysTo.Count > 0)
        {
            result = result with { Summary = result.Summary + $" {info.TargetBranch} deploys to {string.Join(", ", deploysTo.Select(t => $"{t.App} ({t.Url}){(t.BehindApproval ? ", after the pipeline's approval" : "")}"))}." };
        }

        return await ActionSupport.LinkAsync(db, context, time, itemId, result, cancellationToken);
    }

    [AssistantTool("find_deployments", ToolCategory.Read, Title = "Find where a repo deploys", OpenWorld = true)]
    [Description("Maps a repo's branches to the Azure App Services they deploy to, using saved targets, App Service Deployment Center, " +
        "the repo's pipeline files, and App Service names, across every tenant the Azure CLI is signed in to. qaTarget is where finished " +
        "work goes before Ready for QA; uatTarget is the UAT environment if there is one; productionBranches are off limits. " +
        "When the map is incomplete, read the listed pipeline files, then record what you confirm with save_deployment_target.")]
    public async Task<DeploymentMap> FindDeploymentsAsync(
        [Description("Repo name from list_repos, or its remote URL.")] string repo,
        CancellationToken cancellationToken = default)
    {
        var (repoRef, clone) = await ActionSupport.ResolveRepoAsync(db, repo, cancellationToken);
        return await finder.FindAsync(clone?.Name ?? repo, repoRef, clone?.LocalPath, cancellationToken);
    }

    [AssistantTool("save_deployment_target", ToolCategory.ItemManagement, Title = "Remember where a branch deploys")]
    [Description("Records that a repo's branch deploys to an App Service, so find_deployments knows it next time. Use it once you've " +
        "confirmed the mapping from a pipeline file, the app's deployment settings, or the user. Saving can't unmark a production branch.")]
    public async Task<DeploymentTarget> SaveDeploymentTargetAsync(
        [Description("Repo name from list_repos.")] string repo,
        [Description("The branch that deploys.")] string branch,
        [Description("The App Service app, or app/slot.")] string app,
        [Description("dev, qa, uat, or prod.")] string environment,
        [Description("The site's URL. Defaults to the app's azurewebsites.net address.")] string? url = null,
        [Description("How you confirmed it, for next time.")] string? notes = null,
        CancellationToken cancellationToken = default)
    {
        var (_, clone) = await ActionSupport.ResolveRepoAsync(db, repo, cancellationToken);
        var name = clone?.Name ?? repo;
        var env = environment.Trim().ToLowerInvariant();
        if (env is not ("dev" or "qa" or "uat" or "prod"))
        {
            throw new ToolException("environment must be dev, qa, uat, or prod.");
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            var site = (await appServices.ListSitesAsync(cancellationToken)).FirstOrDefault(s => string.Equals(s.DisplayName, app, StringComparison.OrdinalIgnoreCase));
            url = site?.Url ?? $"https://{app.Replace('/', '-')}.azurewebsites.net";
        }

        var saved = new DeploymentTargetSetting { Repo = name, Branch = branch.Trim(), App = app.Trim(), Url = url.Trim(), Environment = env, Notes = notes };
        settings.Update(s =>
        {
            s.Deployments.Targets.RemoveAll(t => string.Equals(t.Repo, saved.Repo, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Branch, saved.Branch, StringComparison.OrdinalIgnoreCase) && string.Equals(t.App, saved.App, StringComparison.OrdinalIgnoreCase));
            s.Deployments.Targets.Add(saved);
        });

        return new DeploymentTarget(saved.Branch, saved.App, saved.Url, env, "saved", notes);
    }

    [AssistantTool("get_deployment_status", ToolCategory.Read, Title = "Check a deployment", OpenWorld = true)]
    [Description("Follows a merged commit to the site: the CI run on the commit, then the App Service deployment, then whether the site " +
        "responds. Pass waitSeconds to keep checking (every 20 seconds) until it's deployed or failed. Call it again if it's still going.")]
    public async Task<DeploymentStatus> GetDeploymentStatusAsync(
        [Description("Repo name from list_repos, or its remote URL.")] string repo,
        [Description("The commit to follow, usually the pull request's mergeCommit.")] string commit,
        [Description("The branch it was merged into. Used to find the App Service when app isn't given.")] string? branch = null,
        [Description("The App Service app or app/slot, if you already know it.")] string? app = null,
        [Description("When it was merged (ISO 8601), so deployments that don't record the commit can still be matched. Defaults to an hour ago.")] DateTimeOffset? mergedAt = null,
        [Description("Keep checking for up to this many seconds (0 to 600).")] int waitSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(commit) || commit.Length < 7)
        {
            throw new ToolException("commit must be at least the first 7 characters of the commit hash.");
        }

        var (repoRef, clone) = await ActionSupport.ResolveRepoAsync(db, repo, cancellationToken);
        if (app is null)
        {
            var map = await finder.FindAsync(clone?.Name ?? repo, repoRef, clone?.LocalPath, cancellationToken);
            var targets = branch is null
                ? map.QaTarget is null ? [] : [map.QaTarget]
                : map.Targets.Where(t => string.Equals(t.Branch, branch, StringComparison.OrdinalIgnoreCase)).ToList();
            app = targets.FirstOrDefault(t => t.Environment != "prod")?.App ?? targets.FirstOrDefault()?.App;
        }

        var since = (mergedAt ?? time.GetUtcNow().AddHours(-1)).UtcDateTime;
        var deadline = time.GetUtcNow().AddSeconds(Math.Clamp(waitSeconds, 0, 600));
        while (true)
        {
            var status = await CheckOnceAsync(repoRef, commit.Trim(), app, since, cancellationToken);
            if (status.Stage is "deployed" or "ci_failed" or "deploy_failed" || time.GetUtcNow() + PollInterval > deadline)
            {
                return status;
            }

            await Task.Delay(PollInterval, time, cancellationToken);
        }
    }

    [AssistantTool("prepare_feature_slot", ToolCategory.SourceAction, Title = "Deploy a feature branch to its own slot", OpenWorld = true)]
    [Description("For repos whose find_deployments flow is feature-slot: makes sure the pushed feature branch deploys to its own App Service " +
        "slot, named after the branch (feature/SUP-1580 becomes sup-1580). Uses the slot that already deploys the branch, or " +
        "creates one with the app's settings and points its Deployment Center at the branch, which starts a deployment. Then follow it with " +
        "get_deployment_status (app is the returned slot, commit is the branch's latest commit).")]
    public async Task<ActionResult> PrepareFeatureSlotAsync(
        [Description("Repo name from list_repos, or its remote URL.")] string repo,
        [Description("The pushed feature branch.")] string branch,
        [Description("The item this work is for. The slot is recorded on it.")] int? itemId = null,
        CancellationToken cancellationToken = default)
    {
        var (repoRef, clone) = await ActionSupport.ResolveRepoAsync(db, repo, cancellationToken);
        var name = clone?.Name ?? repo;
        var rule = settings.Current.Deployments.Repos.FirstOrDefault(r => string.Equals(r.Repo, name, StringComparison.OrdinalIgnoreCase));
        if (rule?.FeatureSlotApp is not { Length: > 0 } appName)
        {
            throw new ToolException($"{name} doesn't deploy feature branches to slots. Use find_deployments and merge into its QA branch instead.");
        }

        var map = await finder.FindAsync(name, repoRef, clone?.LocalPath, cancellationToken);
        if (map.ProductionBranches.Contains(branch, StringComparer.OrdinalIgnoreCase) || DeploymentFinder.IsProductionBranchName(branch))
        {
            throw new ToolException($"{branch} deploys production. Push the work to a feature branch instead.");
        }

        var existing = map.Targets.FirstOrDefault(t => string.Equals(t.Branch, branch, StringComparison.OrdinalIgnoreCase) && t.App.Contains('/'));
        if (existing is not null)
        {
            return await ActionSupport.LinkAsync(db, context, time, itemId,
                new ActionResult(existing.Url, $"{branch} already deploys to the slot {existing.App} ({existing.Url}).", "deployment_slot"), cancellationToken);
        }

        var sites = await ActionSupport.RunAsync(() => appServices.ListSitesAsync(cancellationToken));
        var app = sites.FirstOrDefault(s => s.Slot is null && string.Equals(s.Name, appName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ToolException($"The App Service {appName} isn't visible to the Azure CLI. Sign in to its tenant with az login --tenant.");

        var slot = SlotNameFor(branch, app.Name);
        if (sites.Any(s => string.Equals(s.Name, app.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(s.Slot, slot, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ToolException($"{app.Name} already has a slot named {slot}, but it doesn't deploy {branch}. Ask the user which to use.");
        }

        // Use the same repo address the app's own Deployment Center uses, so its Bitbucket or GitHub connection applies.
        var repoUrl = (await appServices.GetSourceAsync(app, cancellationToken))?.RepoUrl ?? RemoteOf(repoRef);
        var created = await ActionSupport.RunAsync(() => appServices.CreateSlotAsync(app, slot, repoUrl, branch, cancellationToken));

        return await ActionSupport.LinkAsync(db, context, time, itemId,
            new ActionResult(created.Url, $"Created the slot {created.DisplayName} for {branch} and started its first deployment. It will be at {created.Url}.", "deployment_slot"),
            cancellationToken);
    }

    /// <summary>
    /// feature/SUP-1580 becomes sup-1580: the branch's last part, lowercase, letters, digits, and dashes. App
    /// Service host names are limited to 60 characters, app and slot together.
    /// </summary>
    public static string SlotNameFor(string branch, string app)
    {
        var last = branch.Trim().TrimEnd('/').Split('/')[^1].ToLowerInvariant();
        var slot = string.Concat(last.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')).Trim('-');
        while (slot.Contains("--", StringComparison.Ordinal))
        {
            slot = slot.Replace("--", "-", StringComparison.Ordinal);
        }

        var max = Math.Max(1, 59 - app.Length);
        slot = slot.Length > max ? slot[..max].TrimEnd('-') : slot;
        return slot.Length == 0 ? throw new ToolException($"Can't make a slot name from {branch}.") : slot;
    }

    [AssistantTool("assign_ticket", ToolCategory.SourceAction, Title = "Assign a ticket", OpenWorld = true)]
    [Description("Assigns a Jira issue, Azure DevOps work item, or GitHub issue to one person. With no assignee, it goes back to the " +
        "reporter (Jira), creator (Azure DevOps), or author (GitHub). For Jira, an assignee is matched by name or email among the people who can be assigned.")]
    public async Task<ActionResult> AssignTicketAsync(
        [Description("Name, email, or GitHub login. Leave empty to assign it back to the reporter.")] string? assignee = null,
        [Description("Web link to the ticket. Optional when itemId is given.")] string? url = null,
        [Description("The item this work is for. Its link is used when no url is given.")] int? itemId = null,
        CancellationToken cancellationToken = default)
    {
        var target = await ActionSupport.ResolveTargetAsync(db, url, itemId, cancellationToken);
        var result = await ActionSupport.RunAsync(() => target switch
        {
            JiraIssue j => atlassian.AssignIssueAsync(j, assignee, cancellationToken),
            AzureDevOpsWorkItem w => azureDevOps.AssignWorkItemAsync(w, assignee, cancellationToken),
            GitHubIssueOrPull { IsPull: false } g => github.AssignAsync(g, assignee, cancellationToken),
            _ => throw new ToolException("That link isn't a Jira issue, Azure DevOps work item, or GitHub issue."),
        });

        return await ActionSupport.LinkAsync(db, context, time, itemId, result, cancellationToken);
    }

    private async Task<DeploymentStatus> CheckOnceAsync(RepoRef repo, string commit, string? app, DateTime since, CancellationToken cancellationToken)
    {
        IReadOnlyList<CheckStatus> ci = [];
        try
        {
            ci = repo switch
            {
                GitHubRepo g => await github.GetCommitChecksAsync(g, commit, cancellationToken),
                BitbucketRepo b => await atlassian.GetCommitChecksAsync(b, commit, cancellationToken),
                AzureDevOpsRepo a => await azureDevOps.GetCommitBuildsAsync(a, commit, cancellationToken),
                _ => [],
            };
        }
        catch (Core.Connectors.ConnectorException)
        {
            // CI status is a hint; the App Service deployment is what counts.
        }

        var ciState = CheckStates.Overall(ci);
        var site = app is null ? null : (await appServices.ListSitesAsync(cancellationToken))
            .FirstOrDefault(s => string.Equals(s.DisplayName, app, StringComparison.OrdinalIgnoreCase));

        if (site is null)
        {
            var why = app is null
                ? "No App Service is known for this branch. Call find_deployments, then pass app."
                : $"The App Service {app} isn't visible to the Azure CLI. Sign in to its tenant with az login --tenant.";
            var stage = ciState == CheckStates.Failure ? "ci_failed" : ciState == CheckStates.Pending ? "waiting_for_ci" : "not_seen";
            return new DeploymentStatus(commit, stage, why, ci, app, null, null, null, null);
        }

        var deployments = await ActionSupport.RunAsync(() => appServices.ListDeploymentsAsync(site, cancellationToken));
        var deployment = deployments.FirstOrDefault(d => SameCommit(d.Id, commit) || (d.Message?.Contains(commit[..7], StringComparison.OrdinalIgnoreCase) ?? false))
            ?? deployments.FirstOrDefault(d => (d.StartedAt ?? d.EndedAt) >= since);

        if (deployment is null)
        {
            var stage = ciState == CheckStates.Failure ? "ci_failed" : ciState == CheckStates.Pending ? "waiting_for_ci" : "not_seen";
            var summary = stage switch
            {
                "ci_failed" => $"The build failed: {string.Join(", ", ci.Where(c => c.State == CheckStates.Failure).Select(c => c.Name))}.",
                "waiting_for_ci" => "The build is still running.",
                _ => $"{site.DisplayName} hasn't started a deployment for {commit[..7]} yet.",
            };
            return new DeploymentStatus(commit, stage, summary, ci, site.DisplayName, site.Url, null, null, null);
        }

        if (deployment.Status is "pending" or "building" or "deploying")
        {
            return new DeploymentStatus(commit, "deploying", $"{site.DisplayName} is {deployment.Status}.", ci, site.DisplayName, site.Url, deployment, null, null);
        }

        if (deployment.Status == "failed")
        {
            return new DeploymentStatus(commit, "deploy_failed", $"The deployment to {site.DisplayName} failed.", ci, site.DisplayName, site.Url, deployment, null, null);
        }

        var (code, ms) = await ProbeAsync(site.Url, cancellationToken);
        var health = code switch
        {
            null => "didn't respond",
            401 or 403 => $"answered {code} in {ms} ms, so it likely needs a sign-in or only allows certain IP addresses; test it the way its users reach it",
            >= 500 => $"answered {code} in {ms} ms, a server error; check the app's logs before calling it deployed",
            _ => $"answered {code} in {ms} ms",
        };
        return new DeploymentStatus(commit, "deployed",
            $"Deployed to {site.DisplayName} at {deployment.EndedAt:u}. {site.Url} {health}.", ci, site.DisplayName, site.Url, deployment, code, ms);
    }

    /// <summary>Deployment Center names deployments by the full hash; Bitbucket links often carry a 12-character one.</summary>
    private static bool SameCommit(string deploymentId, string commit) =>
        deploymentId.Length >= 7 && (deploymentId.StartsWith(commit, StringComparison.OrdinalIgnoreCase) || commit.StartsWith(deploymentId, StringComparison.OrdinalIgnoreCase));

    private async Task<(int? Code, long? Ms)> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        using var http = httpClients.CreateClient(ProbeClientName);
        http.Timeout = TimeSpan.FromSeconds(60);
        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return ((int)response.StatusCode, watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return (null, null);
        }
    }

    private async Task<DeploymentMap> FindForAsync(RepoRef repo, CancellationToken cancellationToken)
    {
        var clones = await db.RepoMappings.AsNoTracking().ToListAsync(cancellationToken);
        var clone = clones.FirstOrDefault(m => SourceLinks.ParseRemote(m.RemoteUrl) == repo);
        return await finder.FindAsync(clone?.Name ?? RemoteOf(repo), repo, clone?.LocalPath, cancellationToken);
    }

    private static RepoRef RepoOf(SourceRef pull) => pull switch
    {
        GitHubIssueOrPull g => new GitHubRepo(g.Owner, g.Repo),
        BitbucketPull b => new BitbucketRepo(b.Workspace, b.Repo),
        AzureDevOpsPull a => new AzureDevOpsRepo(a.Organization, a.Project, a.Repo),
        _ => throw new ToolException("That isn't a pull request."),
    };

    private static string RemoteOf(RepoRef repo) => repo switch
    {
        GitHubRepo g => $"https://github.com/{g.Owner}/{g.Name}",
        BitbucketRepo b => $"https://bitbucket.org/{b.Workspace}/{b.Name}",
        AzureDevOpsRepo a => $"https://dev.azure.com/{a.Organization}/{a.Project}/_git/{a.Name}",
        _ => "",
    };
}
