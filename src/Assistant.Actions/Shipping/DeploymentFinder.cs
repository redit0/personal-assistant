using System.Text.RegularExpressions;
using Assistant.Connectors.Microsoft;
using Assistant.Core.Actions;
using Assistant.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Assistant.Actions.Shipping;

/// <summary>One branch of a repo and the App Service it deploys to.</summary>
/// <param name="Environment">dev, qa, uat, prod, or unknown.</param>
/// <param name="FoundBy">saved (you or an agent confirmed it), deployment-center, or pipeline-file.</param>
/// <param name="BehindApproval">A later stage of a pipeline that deploys several environments from one branch. Those stages wait for
/// the pipeline's approval gates, so a production stage here doesn't make the branch off limits.</param>
public sealed record DeploymentTarget(string Branch, string App, string Url, string Environment, string FoundBy, string? Notes = null, bool BehindApproval = false);

/// <summary>An App Service whose name looks related to the repo but isn't tied to a branch yet.</summary>
public sealed record AppServiceCandidate(string App, string Url, string Environment, string? DeploysFrom);

/// <summary>Where a repo's branches deploy, and which one to use for QA.</summary>
/// <param name="QaTarget">Where finished work should go before Ready for QA: the lowest non-production environment.
/// Null when none is known; merge nothing then, and say so.</param>
/// <param name="ProductionBranches">Branches that deploy production. The shipping tools never merge into these.</param>
/// <param name="PipelineFiles">CI files in the clone, relative to it, for reading when the map is incomplete.</param>
/// <param name="Flow">"merge" (merge into the QA branch) or "feature-slot" (each feature branch deploys to its own slot of
/// FeatureSlotApp and isn't merged; see prepare_feature_slot).</param>
public sealed record DeploymentMap(
    string Repo,
    IReadOnlyList<DeploymentTarget> Targets,
    DeploymentTarget? QaTarget,
    DeploymentTarget? UatTarget,
    IReadOnlyList<string> ProductionBranches,
    IReadOnlyList<AppServiceCandidate> Candidates,
    IReadOnlyList<string> PipelineFiles,
    IReadOnlyList<string> Notes,
    string Flow = "merge",
    string? FeatureSlotApp = null);

/// <summary>
/// Works out where a repo's branches deploy. Saved targets come first; then App Service Deployment Center (each app
/// names its repo and branch); then the repo's pipeline files (GitHub Actions and Azure Pipelines that deploy with
/// azure/webapps-deploy or AzureWebApp); then App Service names that look related, as candidates to confirm.
/// </summary>
public sealed partial class DeploymentFinder(AzureAppServices appServices, UserSettingsStore settings, ILogger<DeploymentFinder> logger)
{
    private const int MaxCandidates = 10;

    /// <summary>How many Deployment Center lookups run at once. Each app is looked up once and remembered.</summary>
    private const int ParallelLookups = 16;

    private static readonly string[] EnvironmentWords = ["uat", "stage", "staging", "preprod", "prod", "production", "qa", "test", "dev"];

    private static readonly HashSet<string> GenericWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "web", "website", "site", "app", "apps", "webapp", "core", "the", "and", "new", "api", "func", "function", "service",
        "portal", "cms", "admin", "poc", "azure", "dev", "qa", "uat", "prod", "test", "stage", "staging", "slot",
    };

    public async Task<DeploymentMap> FindAsync(string repoName, RepoRef repo, string? localPath, CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var targets = new List<DeploymentTarget>();

        foreach (var saved in settings.Current.Deployments.Targets.Where(t => string.Equals(t.Repo, repoName, StringComparison.OrdinalIgnoreCase)))
        {
            targets.Add(new DeploymentTarget(saved.Branch, saved.App, saved.Url, Normalize(saved.Environment) ?? Classify(saved.App, saved.Branch), "saved", saved.Notes));
        }

        var pipelineFiles = localPath is not null && Directory.Exists(localPath) ? FindPipelineFiles(localPath) : [];
        var fromPipelines = localPath is null ? [] : pipelineFiles.SelectMany(f => ParsePipelineFile(Path.Combine(localPath, f), f)).ToList();

        IReadOnlyList<AppServiceSite> sites = [];
        if (appServices.IsAvailable)
        {
            try
            {
                sites = await appServices.ListSitesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogInformation(ex, "Couldn't list App Services");
                notes.Add($"Couldn't list App Services: {ex.Message}");
            }
        }
        else
        {
            notes.Add("The Azure CLI isn't installed, so App Service settings couldn't be read. Install it and run az login.");
        }

        if (sites.Count == 0 && appServices.IsAvailable)
        {
            notes.Add("No App Services were visible. Run az login (once per tenant with --tenant) so the assistant can see them.");
        }

        foreach (var tenant in appServices.SkippedTenants)
        {
            notes.Add($"Couldn't read App Services in tenant {tenant}. If this repo deploys there, run: az login --tenant {tenant}");
        }

        // Deployment Center: each app says which repo and branch it deploys from, so every visible app is checked. Names
        // don't always match (doe-website deploys to doe-rampac-dev). The first call takes a while; the answers are cached.
        using var gate = new SemaphoreSlim(ParallelLookups);
        var sources = await Task.WhenAll(sites.Select(async s =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return (Site: s, Source: await appServices.GetSourceAsync(s, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (Site: s, Source: (AppServiceSource?)null);
            }
            finally
            {
                gate.Release();
            }
        }));

        foreach (var (site, source) in sources)
        {
            if (source is not null && SourceLinks.ParseRemote(source.RepoUrl) == repo && source.Branch.Length > 0)
            {
                targets.Add(new DeploymentTarget(source.Branch, site.DisplayName, site.Url, Classify(site.DisplayName, source.Branch), "deployment-center"));
            }
        }

        foreach (var p in fromPipelines)
        {
            var site = sites.FirstOrDefault(s => string.Equals(s.Name, p.App, StringComparison.OrdinalIgnoreCase) && string.Equals(s.Slot, p.Slot, StringComparison.OrdinalIgnoreCase));
            var display = p.Slot is null ? p.App : $"{p.App}/{p.Slot}";
            targets.Add(new DeploymentTarget(p.Branch, display, site?.Url ?? $"https://{p.App}{(p.Slot is null ? "" : "-" + p.Slot)}.azurewebsites.net",
                Classify(display, p.Branch), "pipeline-file",
                p.LaterStage ? $"From {p.File}, a later stage behind the pipeline's approvals." : $"From {p.File}.", p.LaterStage));
        }

        targets = [.. await Task.WhenAll(targets.Select(t => CheckProductionSlotAsync(t, sites, cancellationToken)))];

        targets = [.. targets
            .GroupBy(t => (Branch: t.Branch.ToLowerInvariant(), App: t.App.ToLowerInvariant()))
            .Select(g => g.OrderBy(t => t.FoundBy switch { "saved" => 0, "deployment-center" => 1, _ => 2 }).First())
            .OrderBy(t => Rank(t.Environment))
            .ThenBy(t => t.Branch)];

        var rule = settings.Current.Deployments.Repos.FirstOrDefault(r => string.Equals(r.Repo, repoName, StringComparison.OrdinalIgnoreCase));
        var production = targets.Where(t => t.Environment == "prod" && !t.BehindApproval).Select(t => t.Branch)
            .Concat(rule?.ProductionBranches ?? [])
            .Concat(targets.Select(t => t.Branch).Where(IsProductionBranchName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var safe = targets.Where(t => t.Environment != "prod" && !production.Contains(t.Branch, StringComparer.OrdinalIgnoreCase)).ToList();
        // Feature, release, and ticket branches (anything with a slash) have their own preview sites, but finished work goes
        // to a long-lived branch like master, main, dev, or uat. Prefer what deploys right away over a stage that waits for an
        // approval, an app over one of its slots, and a branch named for its environment (uat for UAT).
        safe = [.. safe
            .Where(t => !t.Branch.Contains('/'))
            .OrderBy(t => t.BehindApproval)
            .ThenBy(t => t.App.Contains('/'))
            .ThenBy(t => Classify("", t.Branch) != t.Environment)];
        var qa = safe.FirstOrDefault(t => t.Environment is "dev" or "qa") ?? safe.FirstOrDefault(t => t.Environment == "uat");
        var uat = safe.FirstOrDefault(t => t.Environment == "uat");

        var tied = targets.Select(t => t.App).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loose = sources
            .Where(s => !tied.Contains(s.Site.DisplayName) && LooksRelated(repoName, localPath, s.Site.Name))
            .Select(s => new AppServiceCandidate(s.Site.DisplayName, s.Site.Url, Classify(s.Site.DisplayName, null),
                s.Source is null ? null : $"{s.Source.RepoUrl} ({s.Source.Branch})"))
            .Where(c => c.DeploysFrom is null) // Apps that deploy from another repo aren't candidates.
            .Take(MaxCandidates)
            .ToList();

        if (targets.Count == 0)
        {
            notes.Add(loose.Count > 0
                ? "No branch is tied to an App Service yet. Check the candidates (read the pipeline files or the app's deployment settings), then call save_deployment_target."
                : "Nothing ties this repo to an App Service. It may deploy some other way; ask the user before merging.");
        }

        if (rule?.FeatureSlotApp is { Length: > 0 } slotApp)
        {
            notes.Add($"Feature branches deploy to their own slot of {slotApp}, named after the branch. Push the feature branch, call " +
                "prepare_feature_slot, and test there. Open the pull request for review, but don't merge it.");
            return new DeploymentMap(repoName, targets, null, null, production, loose, pipelineFiles, notes, "feature-slot", slotApp);
        }

        return new DeploymentMap(repoName, targets, qa, uat, production, loose, pipelineFiles, notes);
    }

    /// <summary>
    /// A slot of a production app, like shop-site-prod/staging, goes live only when someone swaps it into production, so
    /// it's production behind a gate. If the slot auto-swaps after each deployment, it's production outright.
    /// </summary>
    private async Task<DeploymentTarget> CheckProductionSlotAsync(DeploymentTarget target, IReadOnlyList<AppServiceSite> sites, CancellationToken cancellationToken)
    {
        var slash = target.App.IndexOf('/');
        if (slash < 0 || target.FoundBy == "saved" || target.BehindApproval || Classify(target.App[..slash], null) != "prod")
        {
            return target;
        }

        var slot = sites.FirstOrDefault(s => string.Equals(s.DisplayName, target.App, StringComparison.OrdinalIgnoreCase));
        var autoSwap = slot is null ? "unknown" : null;
        if (slot is not null)
        {
            try
            {
                autoSwap = await appServices.GetAutoSwapTargetAsync(slot, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Can't tell, so assume the worst.
                logger.LogInformation(ex, "Couldn't read auto-swap for {Slot}", target.App);
                autoSwap = "unknown";
            }
        }

        return autoSwap is null
            ? target with { Environment = "prod", BehindApproval = true, Notes = $"A slot of the production app; it goes live only when someone swaps it in." }
            : target with { Environment = "prod", BehindApproval = false, Notes = autoSwap == "unknown"
                ? "A slot of the production app, and its swap settings couldn't be read, so it's treated as production."
                : $"A slot of the production app that swaps into {autoSwap} automatically." };
    }

    /// <summary>dev, qa, uat, or prod from an app, slot, or branch name; unknown when nothing matches.</summary>
    public static string Classify(string app, string? branch)
    {
        foreach (var text in new[] { app, branch })
        {
            var words = Words(text ?? "");
            // "websiteprod" and "websitestage" run the environment into the name, so a word ending in one counts too.
            words.UnionWith(words.SelectMany(w => EnvironmentWords.Where(e => w.Length > e.Length + 2 && w.EndsWith(e, StringComparison.Ordinal))).ToList());
            if (words.Overlaps(["uat", "stage", "staging", "stg", "preprod", "preproduction"]))
            {
                return "uat";
            }

            if (words.Overlaps(["prod", "production", "live", "prd"]))
            {
                return "prod";
            }

            if (words.Overlaps(["qa", "test", "testing"]))
            {
                return "qa";
            }

            if (words.Overlaps(["dev", "develop", "development", "sandbox", "int", "integration"]))
            {
                return "dev";
            }
        }

        return "unknown";
    }

    public static bool IsProductionBranchName(string branch) =>
        Words(branch).Overlaps(["prod", "production", "live", "prd"]);

    /// <summary>A deployment found in a pipeline file: which branch triggers it and which app it deploys.</summary>
/// <param name="LaterStage">Not the first app the file deploys from this branch, so it runs after an earlier stage and its approvals.</param>
    public sealed record PipelineDeployment(string Branch, string App, string? Slot, string File, bool LaterStage = false);

    /// <summary>Reads a GitHub Actions or Azure Pipelines file for literal App Service names and the branches that trigger them.</summary>
    public static IEnumerable<PipelineDeployment> ParsePipelineFile(string path, string relativePath)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            yield break;
        }

        var deploysAppService = Regex.IsMatch(text, @"azure/webapps-deploy|AzureWebApp|AzureRmWebAppDeployment|az\s+webapp\s+deploy", RegexOptions.IgnoreCase);
        if (!deploysAppService)
        {
            yield break;
        }

        var branches = TriggerBranches(text);
        var apps = AppName().Matches(text).Select(m => m.Groups["app"].Value).Where(a => !a.Contains('$')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var slot = SlotName().Match(text) is { Success: true } s && !s.Groups["slot"].Value.Contains('$') && !s.Groups["slot"].Value.Equals("production", StringComparison.OrdinalIgnoreCase)
            ? s.Groups["slot"].Value
            : null;

        // App Service's Deployment Center writes workflows named "<branch>_<app>.yml".
        var generated = GeneratedWorkflow().Match(Path.GetFileName(path));
        if (generated.Success && apps.Count == 0 && branches.Count <= 1)
        {
            apps.Add(generated.Groups["app"].Value);
            if (branches.Count == 0)
            {
                branches.Add(generated.Groups["branch"].Value);
            }
        }

        // Only pair a branch with an app when the file is unambiguous about it. A file that deploys several apps from one
        // branch is a staged pipeline (dev, then QA, UAT, and prod), and the stages after the first wait for its approval gates.
        if (branches.Count == 1)
        {
            for (var i = 0; i < apps.Count; i++)
            {
                yield return new PipelineDeployment(branches[0], apps[i], slot, relativePath, LaterStage: i > 0);
            }
        }
        else if (apps.Count == 1)
        {
            foreach (var branch in branches)
            {
                yield return new PipelineDeployment(branch, apps[0], slot, relativePath);
            }
        }
    }

    private static List<string> TriggerBranches(string text)
    {
        var found = new List<string>();

        // GitHub: on: push: branches: [a, b] or a list. Azure Pipelines: trigger: [a] or a list, or trigger: branches: include: a list.
        foreach (Match block in BranchBlock().Matches(text))
        {
            found.AddRange(ListItem().Matches(block.Groups["items"].Value).Select(m => m.Groups["v"].Value));
        }

        foreach (Match inline in InlineBranches().Matches(text))
        {
            found.AddRange(inline.Groups["items"].Value.Split(',').Select(v => v.Trim().Trim('\'', '"')));
        }

        return [.. found.Where(b => b.Length > 0 && !b.Contains('*') && b != "none").Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static List<string> FindPipelineFiles(string root)
    {
        var files = new List<string>();
        var workflows = Path.Combine(root, ".github", "workflows");
        if (Directory.Exists(workflows))
        {
            files.AddRange(Directory.EnumerateFiles(workflows).Where(f => f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)));
        }

        files.AddRange(Directory.EnumerateFiles(root, "*pipeline*.yml", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)));

        return [.. files.Distinct(StringComparer.OrdinalIgnoreCase).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Order()];
    }

    /// <summary>The app's name shares a distinctive word with the repo or its folder (the clone's own folder, not its parents, which are often a client's name shared by every app they have), like contoso-records-uat and recordscore.</summary>
    private static bool LooksRelated(string repoName, string? localPath, string appName)
    {
        var repoText = Squash(repoName + " " + (localPath is null ? "" : string.Join(" ", localPath.Split(Path.DirectorySeparatorChar, '/').TakeLast(1))));
        var appWords = Words(appName).Where(w => w.Length >= 4 && !GenericWords.Contains(w)).ToList();
        if (appWords.Any(w => repoText.Contains(w, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var app = Squash(appName);
        return Words(repoName).Where(w => w.Length >= 5 && !GenericWords.Contains(w)).Any(w => app.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private static string? Normalize(string? environment) => environment?.Trim().ToLowerInvariant() switch
    {
        "dev" or "development" => "dev",
        "qa" or "test" => "qa",
        "uat" or "stage" or "staging" => "uat",
        "prod" or "production" or "live" => "prod",
        _ => null,
    };

    private static int Rank(string environment) => environment switch
    {
        "dev" => 0,
        "qa" => 1,
        "uat" => 2,
        "unknown" => 3,
        _ => 4,
    };

    private static HashSet<string> Words(string text) =>
        new(WordSplit().Split(CamelSplit().Replace(text, "$1 $2")).Where(w => w.Length > 0).Select(w => w.ToLowerInvariant()));

    private static string Squash(string text) => WordSplit().Replace(text, "").ToLowerInvariant();

    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex WordSplit();

    [GeneratedRegex(@"([a-z0-9])([A-Z])")]
    private static partial Regex CamelSplit();

    [GeneratedRegex(@"(?im)^\s*(?:app-name|appName|WebAppName)\s*:\s*['""]?(?<app>[\w${}.-]+)")]
    private static partial Regex AppName();

    [GeneratedRegex(@"(?im)^\s*(?:slot-name|SlotName|deployToSlotOrASEName)\s*:\s*['""]?(?<slot>[\w${}.-]+)")]
    private static partial Regex SlotName();

    [GeneratedRegex(@"^(?<branch>[\w.-]+)_(?<app>[\w-]+)\.ya?ml$", RegexOptions.IgnoreCase)]
    private static partial Regex GeneratedWorkflow();

    [GeneratedRegex(@"(?m)^(?:\s*(?:push|pull_request)\s*:\s*\n)?(?<indent>\s*)(?:trigger|branches|include)\s*:\s*\n(?<items>(?:\k<indent>\s*-\s*.+\n?)+)")]
    private static partial Regex BranchBlock();

    [GeneratedRegex(@"(?m)^\s*(?:trigger|branches)\s*:\s*\[(?<items>[^\]]*)\]")]
    private static partial Regex InlineBranches();

    [GeneratedRegex(@"(?m)^\s*-\s*['""]?(?<v>[^'""\s#]+)")]
    private static partial Regex ListItem();
}
