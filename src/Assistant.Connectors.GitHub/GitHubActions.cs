using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Assistant.Core.Actions;
using Assistant.Core.Connectors;

namespace Assistant.Connectors.GitHub;

/// <summary>Changes on GitHub: pull requests, reviews, comments, and issue state. Uses the connector's token.</summary>
public sealed class GitHubActions(IHttpClientFactory httpClients, GitHubConnector connector)
{
    public async Task<ActionResult> CreatePullRequestAsync(
        GitHubRepo repo, string head, string? baseBranch, string title, string? body, bool draft, CancellationToken cancellationToken)
    {
        baseBranch ??= (await SendAsync(HttpMethod.Get, $"repos/{repo.Owner}/{repo.Name}", null, cancellationToken))
            .RootElement.GetProperty("default_branch").GetString();

        using var doc = await SendAsync(HttpMethod.Post, $"repos/{repo.Owner}/{repo.Name}/pulls",
            new { title, head, @base = baseBranch, body, draft }, cancellationToken);
        var number = doc.RootElement.GetProperty("number").GetInt32();
        return new ActionResult(
            doc.RootElement.GetProperty("html_url").GetString()!,
            $"Opened pull request #{number} in {repo.Owner}/{repo.Name} ({head} into {baseBranch}).",
            "pull_request");
    }

    /// <summary>Comments on an issue or pull request's conversation.</summary>
    public async Task<ActionResult> CommentAsync(GitHubIssueOrPull target, string body, CancellationToken cancellationToken)
    {
        using var doc = await SendAsync(HttpMethod.Post, $"repos/{target.Owner}/{target.Repo}/issues/{target.Number}/comments", new { body }, cancellationToken);
        return new ActionResult(doc.RootElement.GetProperty("html_url").GetString()!,
            $"Commented on {target.Owner}/{target.Repo}#{target.Number}.", "comment");
    }

    public async Task<ActionResult> ReviewAsync(GitHubIssueOrPull pull, ReviewDecision decision, string? body, CancellationToken cancellationToken)
    {
        if (!pull.IsPull)
        {
            throw new ConnectorException($"{pull.Owner}/{pull.Repo}#{pull.Number} is an issue, not a pull request.");
        }

        var reviewEvent = decision switch
        {
            ReviewDecision.Approve => "APPROVE",
            ReviewDecision.RequestChanges => "REQUEST_CHANGES",
            _ => "COMMENT",
        };

        using var doc = await SendAsync(HttpMethod.Post, $"repos/{pull.Owner}/{pull.Repo}/pulls/{pull.Number}/reviews",
            new { @event = reviewEvent, body = body ?? "" }, cancellationToken);
        return new ActionResult(doc.RootElement.GetProperty("html_url").GetString()!,
            $"Submitted a {decision} review on {pull.Owner}/{pull.Repo}#{pull.Number}.", "review");
    }

    /// <summary>Closes or reopens an issue or pull request. GitHub has no other states.</summary>
    public async Task<ActionResult> SetStateAsync(GitHubIssueOrPull target, bool open, CancellationToken cancellationToken)
    {
        using var doc = await SendAsync(HttpMethod.Patch, $"repos/{target.Owner}/{target.Repo}/issues/{target.Number}",
            new { state = open ? "open" : "closed" }, cancellationToken);
        return new ActionResult(doc.RootElement.GetProperty("html_url").GetString()!,
            $"{(open ? "Reopened" : "Closed")} {target.Owner}/{target.Repo}#{target.Number}.", "status_change");
    }

    public async Task<PullRequestInfo> GetPullRequestAsync(GitHubIssueOrPull pull, CancellationToken cancellationToken)
    {
        var path = $"repos/{pull.Owner}/{pull.Repo}/pulls/{pull.Number}";
        using var doc = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
        var pr = doc.RootElement;
        var head = pr.GetProperty("head").GetProperty("sha").GetString();
        var merged = pr.TryGetProperty("merged", out var m) && m.ValueKind == JsonValueKind.True;

        using var reviews = await SendAsync(HttpMethod.Get, $"{path}/reviews?per_page=100", null, cancellationToken);
        var latest = reviews.RootElement.EnumerateArray()
            .Where(r => r.GetProperty("state").GetString() is "APPROVED" or "CHANGES_REQUESTED" or "DISMISSED")
            .GroupBy(r => r.GetProperty("user").GetProperty("login").GetString()!)
            .Select(g => (User: g.Key, State: g.Last().GetProperty("state").GetString()))
            .ToList();

        var mergeableState = pr.TryGetProperty("mergeable_state", out var ms) ? ms.GetString() : null;
        var blockers = new List<string>();
        if (pr.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True)
        {
            blockers.Add("It's a draft.");
        }

        switch (mergeableState)
        {
            case "dirty":
                blockers.Add("It has merge conflicts with the target branch.");
                break;
            case "behind":
                blockers.Add("The source branch is behind the target branch, and the branch rules require it to be up to date.");
                break;
            case "blocked":
                blockers.Add("Branch protection is blocking it: required reviews or required checks haven't passed.");
                break;
        }

        return new PullRequestInfo(
            pr.GetProperty("html_url").GetString()!,
            pr.GetProperty("title").GetString() ?? "",
            merged ? "merged" : pr.GetProperty("state").GetString() ?? "open",
            pr.GetProperty("head").GetProperty("ref").GetString() ?? "",
            pr.GetProperty("base").GetProperty("ref").GetString() ?? "",
            head,
            merged && pr.TryGetProperty("merge_commit_sha", out var mc) ? mc.GetString() : null,
            merged || pr.GetProperty("state").GetString() != "open"
                ? false
                : pr.TryGetProperty("mergeable", out var ok) && ok.ValueKind is JsonValueKind.True or JsonValueKind.False ? ok.GetBoolean() && blockers.Count == 0 : null,
            blockers,
            head is null ? [] : await GetCommitChecksAsync(new GitHubRepo(pull.Owner, pull.Repo), head, cancellationToken),
            [.. latest.Where(r => r.State == "APPROVED").Select(r => r.User)],
            [.. latest.Where(r => r.State == "CHANGES_REQUESTED").Select(r => r.User)],
            pr.GetProperty("user").GetProperty("login").GetString());
    }

    /// <summary>Check runs (GitHub Actions and apps) plus commit statuses from older integrations.</summary>
    public async Task<IReadOnlyList<CheckStatus>> GetCommitChecksAsync(GitHubRepo repo, string sha, CancellationToken cancellationToken)
    {
        var checks = new List<CheckStatus>();
        using (var runs = await SendAsync(HttpMethod.Get, $"repos/{repo.Owner}/{repo.Name}/commits/{sha}/check-runs?per_page=100", null, cancellationToken))
        {
            foreach (var run in runs.RootElement.GetProperty("check_runs").EnumerateArray())
            {
                var state = run.GetProperty("status").GetString() != "completed"
                    ? CheckStates.Pending
                    : run.GetProperty("conclusion").GetString() switch
                    {
                        "success" or "neutral" => CheckStates.Success,
                        "skipped" => CheckStates.Skipped,
                        _ => CheckStates.Failure,
                    };
                checks.Add(new CheckStatus(run.GetProperty("name").GetString() ?? "check", state, run.TryGetProperty("html_url", out var u) ? u.GetString() : null));
            }
        }

        using (var statuses = await SendAsync(HttpMethod.Get, $"repos/{repo.Owner}/{repo.Name}/commits/{sha}/status", null, cancellationToken))
        {
            foreach (var status in statuses.RootElement.GetProperty("statuses").EnumerateArray())
            {
                var state = status.GetProperty("state").GetString() switch
                {
                    "success" => CheckStates.Success,
                    "pending" => CheckStates.Pending,
                    _ => CheckStates.Failure,
                };
                checks.Add(new CheckStatus(status.GetProperty("context").GetString() ?? "status", state, status.TryGetProperty("target_url", out var u) ? u.GetString() : null));
            }
        }

        return checks;
    }

    public async Task<ActionResult> MergeAsync(GitHubIssueOrPull pull, MergeStrategy? strategy, bool deleteSourceBranch, CancellationToken cancellationToken)
    {
        var info = await GetPullRequestAsync(pull, cancellationToken);
        using var doc = await SendAsync(HttpMethod.Put, $"repos/{pull.Owner}/{pull.Repo}/pulls/{pull.Number}/merge",
            strategy is null ? new { } : new { merge_method = strategy.ToString()!.ToLowerInvariant() }, cancellationToken);
        var sha = doc.RootElement.TryGetProperty("sha", out var s) ? s.GetString() : null;

        if (deleteSourceBranch)
        {
            try
            {
                (await SendAsync(HttpMethod.Delete, $"repos/{pull.Owner}/{pull.Repo}/git/refs/heads/{Uri.EscapeDataString(info.SourceBranch)}", null, cancellationToken)).Dispose();
            }
            catch (ConnectorException)
            {
                // The branch may be from a fork, protected, or already gone. The merge still happened.
            }
        }

        return new ActionResult(info.Url,
            $"Merged {pull.Owner}/{pull.Repo}#{pull.Number} ({info.SourceBranch} into {info.TargetBranch}){(sha is null ? "" : $" as {sha[..Math.Min(7, sha.Length)]}")}.",
            "merge");
    }

    /// <summary>Assigns an issue to one person, replacing whoever had it. With no login, the issue's author.</summary>
    public async Task<ActionResult> AssignAsync(GitHubIssueOrPull issue, string? login, CancellationToken cancellationToken)
    {
        var path = $"repos/{issue.Owner}/{issue.Repo}/issues/{issue.Number}";
        if (string.IsNullOrWhiteSpace(login))
        {
            using var current = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
            login = current.RootElement.GetProperty("user").GetProperty("login").GetString();
        }

        using var doc = await SendAsync(HttpMethod.Patch, path, new { assignees = new[] { login } }, cancellationToken);
        return new ActionResult(doc.RootElement.GetProperty("html_url").GetString()!,
            $"Assigned {issue.Owner}/{issue.Repo}#{issue.Number} to {login}.", "assignment");
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var token = await connector.GetTokenAsync(cancellationToken);
        using var http = httpClients.CreateClient(GitHubConnector.HttpClientName);
        http.BaseAddress ??= new Uri("https://api.github.com/");

        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd("PersonalAssistant/0.1");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        }

        if (GitHubConnector.RateLimitMessage(response, text) is { } limited)
        {
            throw new ConnectorException($"{limited} Try again in a few minutes.");
        }

        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new ConnectorException("GitHub rejected the token."),
            HttpStatusCode.MethodNotAllowed or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity =>
                new ConnectorException($"GitHub: {Message(text)}"),
            HttpStatusCode.Forbidden or HttpStatusCode.NotFound =>
                new ConnectorException($"GitHub refused or couldn't find {path}. The token may lack write access to this repo. ({Message(text)})"),
            _ => new ConnectorException($"GitHub: {Message(text)}"),
        };
    }

    /// <summary>GitHub's error text, including the details in "errors" (like "A pull request already exists").</summary>
    private static string Message(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
            var details = doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
                ? string.Join("; ", errors.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var em) ? em.GetString() : e.ToString()))
                : null;
            return string.IsNullOrEmpty(details) ? message ?? body : $"{message}: {details}";
        }
        catch (JsonException)
        {
            return body.Length > 200 ? body[..200] : body;
        }
    }
}
