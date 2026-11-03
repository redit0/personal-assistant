using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Assistant.Core.Actions;
using Assistant.Core.Connectors;

namespace Assistant.Connectors.Microsoft;

/// <summary>
/// Changes in Azure DevOps: pull requests, PR comments and votes, and work item comments and states.
/// Each organization signs in the way the connector is set up for it, including client tenants.
/// </summary>
public sealed class AzureDevOpsActions(AzureDevOpsConnector connector)
{
    private const string ApiVersion = "api-version=7.1";

    public async Task<ActionResult> CreatePullRequestAsync(
        AzureDevOpsRepo repo, string sourceBranch, string? targetBranch, string title, string? description, bool draft, CancellationToken cancellationToken)
    {
        using var http = await connector.CreateOrganizationClientAsync(repo.Organization, cancellationToken);
        var repoUrl = $"{Base(repo.Organization)}/{Escape(repo.Project)}/_apis/git/repositories/{Escape(repo.Name)}";

        string targetRef;
        if (targetBranch is null)
        {
            using var info = await AzureDevOpsConnector.GetJsonAsync(http, $"{repoUrl}?{ApiVersion}", cancellationToken);
            targetRef = info.RootElement.GetProperty("defaultBranch").GetString()!;
        }
        else
        {
            targetRef = BranchRef(targetBranch);
        }

        using var doc = await SendJsonAsync(http, HttpMethod.Post, $"{repoUrl}/pullrequests?{ApiVersion}", new
        {
            sourceRefName = BranchRef(sourceBranch),
            targetRefName = targetRef,
            title,
            description = description ?? "",
            isDraft = draft,
        }, "application/json", cancellationToken);

        var id = doc.RootElement.GetProperty("pullRequestId").GetInt32();
        return new ActionResult(
            $"{Base(repo.Organization)}/{Escape(repo.Project)}/_git/{Escape(repo.Name)}/pullrequest/{id}",
            $"Opened pull request {id} in {repo.Project}/{repo.Name} ({sourceBranch} into {targetRef.Replace("refs/heads/", "")}).",
            "pull_request");
    }

    public async Task<ActionResult> CommentOnPullRequestAsync(AzureDevOpsPull pull, string body, CancellationToken cancellationToken)
    {
        using var http = await connector.CreateOrganizationClientAsync(pull.Organization, cancellationToken);
        (await SendJsonAsync(http, HttpMethod.Post, $"{PullApi(pull)}/threads?{ApiVersion}", new
        {
            comments = new[] { new { parentCommentId = 0, content = body, commentType = 1 } },
            status = 1,
        }, "application/json", cancellationToken)).Dispose();

        return new ActionResult(PullWeb(pull), $"Commented on pull request {pull.Id} in {pull.Project}/{pull.Repo}.", "comment");
    }

    /// <summary>Approve votes 10; request changes votes -5 ("waiting for author"); comment only adds the comment.</summary>
    public async Task<ActionResult> ReviewPullRequestAsync(AzureDevOpsPull pull, ReviewDecision decision, string? body, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            await CommentOnPullRequestAsync(pull, body, cancellationToken);
        }

        if (decision != ReviewDecision.Comment)
        {
            using var http = await connector.CreateOrganizationClientAsync(pull.Organization, cancellationToken);
            using var connection = await AzureDevOpsConnector.GetJsonAsync(http, $"{Base(pull.Organization)}/_apis/connectionData", cancellationToken);
            var me = connection.RootElement.GetProperty("authenticatedUser").GetProperty("id").GetString();

            (await SendJsonAsync(http, HttpMethod.Put, $"{PullApi(pull)}/reviewers/{me}?{ApiVersion}",
                new { vote = decision == ReviewDecision.Approve ? 10 : -5 }, "application/json", cancellationToken)).Dispose();
        }

        return new ActionResult(PullWeb(pull), $"Submitted a {decision} review on pull request {pull.Id} in {pull.Project}/{pull.Repo}.", "review");
    }

    /// <summary>Adds a discussion comment by writing to System.History, which works on every process template.</summary>
    public async Task<ActionResult> CommentOnWorkItemAsync(AzureDevOpsWorkItem item, string body, CancellationToken cancellationToken)
    {
        var html = string.Join("<br>", WebUtility.HtmlEncode(body).Replace("\r\n", "\n").Split('\n'));
        await PatchWorkItemAsync(item, "/fields/System.History", html, cancellationToken);
        return new ActionResult(WorkItemWeb(item), $"Commented on work item {item.Id}.", "comment");
    }

    public async Task<ActionResult> SetWorkItemStateAsync(AzureDevOpsWorkItem item, string state, CancellationToken cancellationToken)
    {
        await PatchWorkItemAsync(item, "/fields/System.State", state, cancellationToken);
        return new ActionResult(WorkItemWeb(item), $"Moved work item {item.Id} to {state}.", "status_change");
    }

    public async Task<PullRequestInfo> GetPullRequestAsync(AzureDevOpsPull pull, CancellationToken cancellationToken)
    {
        using var http = await connector.CreateOrganizationClientAsync(pull.Organization, cancellationToken);
        using var doc = await AzureDevOpsConnector.GetJsonAsync(http, $"{PullApi(pull)}?{ApiVersion}", cancellationToken);
        var pr = doc.RootElement;

        var reviewers = pr.TryGetProperty("reviewers", out var r) ? r.EnumerateArray().ToList() : [];
        string Name(JsonElement e) => e.TryGetProperty("displayName", out var n) ? n.GetString() ?? "someone" : "someone";
        int Vote(JsonElement e) => e.TryGetProperty("vote", out var v) && v.TryGetInt32(out var n) ? n : 0;

        var checks = new List<CheckStatus>();
        var blockers = new List<string>();
        var projectId = pr.GetProperty("repository").GetProperty("project").GetProperty("id").GetString();
        using (var evaluations = await AzureDevOpsConnector.GetJsonAsync(http,
            $"{Base(pull.Organization)}/{Escape(pull.Project)}/_apis/policy/evaluations?artifactId={Uri.EscapeDataString($"vstfs:///CodeReview/CodeReviewId/{projectId}/{pull.Id}")}&api-version=7.1-preview.1",
            cancellationToken))
        {
            foreach (var e in evaluations.RootElement.GetProperty("value").EnumerateArray())
            {
                var config = e.GetProperty("configuration");
                var name = config.GetProperty("type").GetProperty("displayName").GetString() ?? "policy";
                if (config.TryGetProperty("settings", out var settings) && settings.TryGetProperty("displayName", out var display) && display.GetString() is { Length: > 0 } custom)
                {
                    name = custom;
                }

                var state = e.GetProperty("status").GetString() switch
                {
                    "approved" => CheckStates.Success,
                    "rejected" or "broken" => CheckStates.Failure,
                    "notApplicable" => CheckStates.Skipped,
                    _ => CheckStates.Pending,
                };
                checks.Add(new CheckStatus(name, state));

                if (state == CheckStates.Failure && config.TryGetProperty("isBlocking", out var blocking) && blocking.ValueKind == JsonValueKind.True)
                {
                    blockers.Add($"The \"{name}\" policy isn't met.");
                }
            }
        }

        var mergeStatus = pr.TryGetProperty("mergeStatus", out var mst) ? mst.GetString() : null;
        if (mergeStatus == "conflicts")
        {
            blockers.Add("It has merge conflicts with the target branch.");
        }

        if (pr.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True)
        {
            blockers.Add("It's a draft.");
        }

        var status = pr.GetProperty("status").GetString() switch
        {
            "completed" => "merged",
            "abandoned" => "closed",
            _ => "open",
        };

        return new PullRequestInfo(
            PullWeb(pull),
            pr.GetProperty("title").GetString() ?? "",
            status,
            Branch(pr.GetProperty("sourceRefName").GetString()),
            Branch(pr.GetProperty("targetRefName").GetString()),
            pr.TryGetProperty("lastMergeSourceCommit", out var head) ? head.GetProperty("commitId").GetString() : null,
            status == "merged" && pr.TryGetProperty("lastMergeCommit", out var merge) ? merge.GetProperty("commitId").GetString() : null,
            status == "open" ? blockers.Count == 0 && mergeStatus == "succeeded" && checks.All(c => c.State != CheckStates.Pending) : false,
            blockers,
            checks,
            [.. reviewers.Where(x => Vote(x) >= 5).Select(Name)],
            [.. reviewers.Where(x => Vote(x) < 0).Select(Name)],
            pr.TryGetProperty("createdBy", out var by) ? Name(by) : null);
    }

    /// <summary>Completes (merges) a pull request. Policies still apply; if one blocks, the error says which.</summary>
    public async Task<ActionResult> CompletePullRequestAsync(AzureDevOpsPull pull, MergeStrategy? strategy, bool deleteSourceBranch, CancellationToken cancellationToken)
    {
        var info = await GetPullRequestAsync(pull, cancellationToken);
        using var http = await connector.CreateOrganizationClientAsync(pull.Organization, cancellationToken);
        (await SendJsonAsync(http, HttpMethod.Patch, $"{PullApi(pull)}?{ApiVersion}", new
        {
            status = "completed",
            lastMergeSourceCommit = new { commitId = info.HeadCommit },
            completionOptions = new
            {
                mergeStrategy = strategy switch
                {
                    MergeStrategy.Squash => "squash",
                    MergeStrategy.Rebase => "rebase",
                    _ => "noFastForward",
                },
                deleteSourceBranch,
                transitionWorkItems = false,
            },
        }, "application/json", cancellationToken)).Dispose();

        return new ActionResult(info.Url, $"Completed pull request {pull.Id} in {pull.Project}/{pull.Repo} ({info.SourceBranch} into {info.TargetBranch}).", "merge");
    }

    /// <summary>Pipeline runs for a commit in this repo, newest first.</summary>
    public async Task<IReadOnlyList<CheckStatus>> GetCommitBuildsAsync(AzureDevOpsRepo repo, string sha, CancellationToken cancellationToken)
    {
        using var http = await connector.CreateOrganizationClientAsync(repo.Organization, cancellationToken);
        using var info = await AzureDevOpsConnector.GetJsonAsync(http,
            $"{Base(repo.Organization)}/{Escape(repo.Project)}/_apis/git/repositories/{Escape(repo.Name)}?{ApiVersion}", cancellationToken);
        var repoId = info.RootElement.GetProperty("id").GetString();

        using var builds = await AzureDevOpsConnector.GetJsonAsync(http,
            $"{Base(repo.Organization)}/{Escape(repo.Project)}/_apis/build/builds?repositoryId={repoId}&repositoryType=TfsGit&queryOrder=queueTimeDescending&$top=50&{ApiVersion}",
            cancellationToken);

        return [.. builds.RootElement.GetProperty("value").EnumerateArray()
            .Where(b => b.TryGetProperty("sourceVersion", out var v) && string.Equals(v.GetString(), sha, StringComparison.OrdinalIgnoreCase))
            .Select(b => new CheckStatus(
                b.GetProperty("definition").GetProperty("name").GetString() ?? "pipeline",
                b.GetProperty("status").GetString() != "completed"
                    ? CheckStates.Pending
                    : b.TryGetProperty("result", out var result) && result.GetString() is "succeeded" or "partiallySucceeded" ? CheckStates.Success : CheckStates.Failure,
                b.TryGetProperty("_links", out var links) && links.TryGetProperty("web", out var web) ? web.GetProperty("href").GetString() : null))];
    }

    /// <summary>The work item's current state and every state its type defines.</summary>
    public async Task<TicketStatuses> GetWorkItemStatesAsync(AzureDevOpsWorkItem item, CancellationToken cancellationToken)
    {
        using var http = await connector.CreateOrganizationClientAsync(item.Organization, cancellationToken);
        using var doc = await AzureDevOpsConnector.GetJsonAsync(http,
            $"{Base(item.Organization)}/{Escape(item.Project)}/_apis/wit/workitems/{item.Id}?fields=System.WorkItemType,System.State&{ApiVersion}", cancellationToken);
        var fields = doc.RootElement.GetProperty("fields");
        var type = fields.GetProperty("System.WorkItemType").GetString()!;
        var state = fields.TryGetProperty("System.State", out var s) ? s.GetString() : null;

        using var states = await AzureDevOpsConnector.GetJsonAsync(http,
            $"{Base(item.Organization)}/{Escape(item.Project)}/_apis/wit/workitemtypes/{Escape(type)}/states?{ApiVersion}", cancellationToken);
        var options = states.RootElement.GetProperty("value").EnumerateArray()
            .Select(e => new StatusOption(e.GetProperty("name").GetString()!, e.TryGetProperty("category", out var c) ? c.GetString() : null))
            .Where(o => !string.Equals(o.Status, state, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new TicketStatuses(state, options);
    }

    /// <summary>Assigns a work item. With no name, to whoever created it.</summary>
    public async Task<ActionResult> AssignWorkItemAsync(AzureDevOpsWorkItem item, string? assignee, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(assignee))
        {
            using var http = await connector.CreateOrganizationClientAsync(item.Organization, cancellationToken);
            using var doc = await AzureDevOpsConnector.GetJsonAsync(http,
                $"{Base(item.Organization)}/{Escape(item.Project)}/_apis/wit/workitems/{item.Id}?fields=System.CreatedBy&{ApiVersion}", cancellationToken);
            var createdBy = doc.RootElement.GetProperty("fields").GetProperty("System.CreatedBy");
            assignee = createdBy.ValueKind == JsonValueKind.Object
                ? createdBy.TryGetProperty("uniqueName", out var u) ? u.GetString() : createdBy.GetProperty("displayName").GetString()
                : createdBy.GetString();
        }

        await PatchWorkItemAsync(item, "/fields/System.AssignedTo", assignee ?? throw new ConnectorException($"Work item {item.Id} has no creator to assign it to."), cancellationToken);
        return new ActionResult(WorkItemWeb(item), $"Assigned work item {item.Id} to {assignee}.", "assignment");
    }

    private static string Branch(string? refName) => refName?.Replace("refs/heads/", "") ?? "";

    private async Task PatchWorkItemAsync(AzureDevOpsWorkItem item, string path, string value, CancellationToken cancellationToken)
    {
        using var http = await connector.CreateOrganizationClientAsync(item.Organization, cancellationToken);
        (await SendJsonAsync(http, HttpMethod.Patch,
            $"{Base(item.Organization)}/{Escape(item.Project)}/_apis/wit/workitems/{item.Id}?{ApiVersion}",
            new[] { new { op = "add", path, value } }, "application/json-patch+json", cancellationToken)).Dispose();
    }

    private static async Task<JsonDocument> SendJsonAsync(HttpClient http, HttpMethod method, string url, object body, string contentType, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, contentType),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return await AzureDevOpsConnector.SendAsync(http, request, cancellationToken);
    }

    private static string BranchRef(string branch) => branch.StartsWith("refs/", StringComparison.Ordinal) ? branch : $"refs/heads/{branch}";

    private static string Base(string organization) => $"https://dev.azure.com/{Escape(organization)}";

    private static string Escape(string segment) => Uri.EscapeDataString(segment);

    private static string PullApi(AzureDevOpsPull pull) =>
        $"{Base(pull.Organization)}/{Escape(pull.Project)}/_apis/git/repositories/{Escape(pull.Repo)}/pullRequests/{pull.Id}";

    private static string PullWeb(AzureDevOpsPull pull) =>
        $"{Base(pull.Organization)}/{Escape(pull.Project)}/_git/{Escape(pull.Repo)}/pullrequest/{pull.Id}";

    private static string WorkItemWeb(AzureDevOpsWorkItem item) =>
        $"{Base(item.Organization)}/{Escape(item.Project)}/_workitems/edit/{item.Id}";
}
