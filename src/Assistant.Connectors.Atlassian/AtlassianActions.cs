using System.Net.Http.Json;
using System.Text.Json;
using Assistant.Core.Actions;
using Assistant.Core.Connectors;
using Assistant.Core.Secrets;
using Assistant.Core.Settings;

namespace Assistant.Connectors.Atlassian;

/// <summary>
/// Changes on Bitbucket Cloud (pull requests, comments, reviews) and Jira Cloud (comments, transitions).
/// Bitbucket needs an API token with write:pullrequest and write:repository scopes; Jira's API token acts
/// with your own permissions.
/// </summary>
public sealed class AtlassianActions(IHttpClientFactory httpClients, UserSettingsStore settings, ISecretStore secrets)
{
    private static readonly Uri BitbucketApi = new("https://api.bitbucket.org/2.0/");

    public async Task<ActionResult> CreatePullRequestAsync(
        BitbucketRepo repo, string sourceBranch, string? destinationBranch, string title, string? description, bool draft, CancellationToken cancellationToken)
    {
        var repoPath = $"repositories/{repo.Workspace}/{repo.Name}";
        if (destinationBranch is null)
        {
            using var info = await BitbucketAsync(HttpMethod.Get, repoPath, null, cancellationToken);
            destinationBranch = info.RootElement.Get("mainbranch")?.GetString("name") ?? "main";
        }

        using var doc = await BitbucketAsync(HttpMethod.Post, $"{repoPath}/pullrequests", new
        {
            title,
            description = description ?? "",
            draft,
            source = new { branch = new { name = sourceBranch } },
            destination = new { branch = new { name = destinationBranch } },
        }, cancellationToken);

        var id = doc.RootElement.GetProperty("id").GetInt32();
        return new ActionResult(
            doc.RootElement.Get("links")?.Get("html")?.GetString("href") ?? $"https://bitbucket.org/{repo.Workspace}/{repo.Name}/pull-requests/{id}",
            $"Opened pull request #{id} in {repo.Workspace}/{repo.Name} ({sourceBranch} into {destinationBranch}).",
            "pull_request");
    }

    public async Task<ActionResult> CommentOnPullRequestAsync(BitbucketPull pull, string body, CancellationToken cancellationToken)
    {
        using var doc = await BitbucketAsync(HttpMethod.Post, $"repositories/{pull.Workspace}/{pull.Repo}/pullrequests/{pull.Id}/comments",
            new { content = new { raw = body } }, cancellationToken);
        return new ActionResult(
            doc.RootElement.Get("links")?.Get("html")?.GetString("href") ?? PullUrl(pull),
            $"Commented on {pull.Workspace}/{pull.Repo} pull request #{pull.Id}.", "comment");
    }

    public async Task<ActionResult> ReviewPullRequestAsync(BitbucketPull pull, ReviewDecision decision, string? body, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            await CommentOnPullRequestAsync(pull, body, cancellationToken);
        }

        var basePath = $"repositories/{pull.Workspace}/{pull.Repo}/pullrequests/{pull.Id}";
        switch (decision)
        {
            case ReviewDecision.Approve:
                (await BitbucketAsync(HttpMethod.Post, $"{basePath}/approve", null, cancellationToken)).Dispose();
                break;
            case ReviewDecision.RequestChanges:
                (await BitbucketAsync(HttpMethod.Post, $"{basePath}/request-changes", null, cancellationToken)).Dispose();
                break;
        }

        return new ActionResult(PullUrl(pull), $"Submitted a {decision} review on {pull.Workspace}/{pull.Repo} pull request #{pull.Id}.", "review");
    }

    /// <summary>Comments on a Jira issue, starting with an @mention of the reporter when asked and the reporter isn't you.</summary>
    public async Task<ActionResult> CommentOnIssueAsync(JiraIssue issue, string body, bool mentionReporter, CancellationToken cancellationToken)
    {
        var mention = mentionReporter ? await ReporterToMentionAsync(issue, cancellationToken) : null;
        using var doc = await JiraAsync(issue.Site, HttpMethod.Post, $"rest/api/3/issue/{issue.Key}/comment", new { body = ToAdf(body, mention) }, cancellationToken);
        var id = doc.RootElement.GetString("id");
        return new ActionResult(new Uri(issue.Site, $"browse/{issue.Key}?focusedCommentId={id}").ToString(),
            $"Commented on {issue.Key}{(mention is null ? "" : $" and tagged {mention.Value.Name}")}.", "comment");
    }

    private async Task<(string AccountId, string Name)?> ReporterToMentionAsync(JiraIssue issue, CancellationToken cancellationToken)
    {
        using var doc = await JiraAsync(issue.Site, HttpMethod.Get, $"rest/api/3/issue/{issue.Key}?fields=reporter", null, cancellationToken);
        var reporter = doc.RootElement.Get("fields")?.Get("reporter");
        if (reporter?.GetString("accountId") is not { } accountId)
        {
            return null;
        }

        using var me = await JiraAsync(issue.Site, HttpMethod.Get, "rest/api/3/myself", null, cancellationToken);
        return me.RootElement.GetString("accountId") == accountId ? null : (accountId, reporter.Value.GetString("displayName") ?? "the reporter");
    }

    /// <summary>The issue's current status and the ones its workflow allows moving to from there.</summary>
    public async Task<TicketStatuses> GetStatusesAsync(JiraIssue issue, CancellationToken cancellationToken)
    {
        using var current = await JiraAsync(issue.Site, HttpMethod.Get, $"rest/api/3/issue/{issue.Key}?fields=status", null, cancellationToken);
        using var transitions = await JiraAsync(issue.Site, HttpMethod.Get, $"rest/api/3/issue/{issue.Key}/transitions", null, cancellationToken);
        var options = transitions.RootElement.GetProperty("transitions").EnumerateArray()
            .Select(t => (Name: t.GetString("name") ?? "", To: t.Get("to")))
            .Select(t => new StatusOption(
                t.To?.GetString("name") ?? t.Name,
                t.To?.Get("statusCategory")?.GetString("name"),
                t.To?.GetString("name") is { } to && !string.Equals(to, t.Name, StringComparison.OrdinalIgnoreCase) ? t.Name : null))
            .ToList();

        return new TicketStatuses(current.RootElement.Get("fields")?.Get("status")?.GetString("name"), options);
    }

    /// <summary>Moves an issue by transition name or target status name, whichever matches.</summary>
    public async Task<ActionResult> TransitionIssueAsync(JiraIssue issue, string status, CancellationToken cancellationToken)
    {
        using var transitions = await JiraAsync(issue.Site, HttpMethod.Get, $"rest/api/3/issue/{issue.Key}/transitions", null, cancellationToken);
        var options = transitions.RootElement.GetProperty("transitions").EnumerateArray()
            .Select(t => (Id: t.GetString("id"), Name: t.GetString("name"), To: t.Get("to")?.GetString("name")))
            .ToList();

        var match = options.FirstOrDefault(t => string.Equals(t.Name, status, StringComparison.OrdinalIgnoreCase))
            is { Id: not null } byName ? byName : options.FirstOrDefault(t => string.Equals(t.To, status, StringComparison.OrdinalIgnoreCase));

        if (match.Id is null)
        {
            var available = string.Join(", ", options.Select(t => t.To is null || t.To == t.Name ? t.Name : $"{t.Name} (to {t.To})"));
            throw new ConnectorException($"{issue.Key} can't move to '{status}' from where it is. Available: {available}.");
        }

        (await JiraAsync(issue.Site, HttpMethod.Post, $"rest/api/3/issue/{issue.Key}/transitions", new { transition = new { id = match.Id } }, cancellationToken)).Dispose();
        return new ActionResult(new Uri(issue.Site, $"browse/{issue.Key}").ToString(), $"Moved {issue.Key} to {match.To ?? match.Name}.", "status_change");
    }

    public async Task<PullRequestInfo> GetPullRequestAsync(BitbucketPull pull, CancellationToken cancellationToken)
    {
        var basePath = $"repositories/{pull.Workspace}/{pull.Repo}/pullrequests/{pull.Id}";
        using var doc = await BitbucketAsync(HttpMethod.Get, basePath, null, cancellationToken);
        var pr = doc.RootElement;
        var participants = pr.Get("participants")?.EnumerateArray().ToList() ?? [];
        string Name(JsonElement p) => p.Get("user")?.GetString("display_name") ?? "someone";

        var approved = participants.Where(p => p.Get("approved")?.ValueKind == JsonValueKind.True).Select(Name).ToList();
        var changes = participants.Where(p => p.GetString("state") == "changes_requested").Select(Name).ToList();

        var checks = new List<CheckStatus>();
        using (var statuses = await BitbucketAsync(HttpMethod.Get, $"{basePath}/statuses?pagelen=50", null, cancellationToken))
        {
            checks.AddRange(ParseStatuses(statuses));
        }

        var state = pr.GetString("state")?.ToLowerInvariant() switch
        {
            "merged" => "merged",
            "declined" => "declined",
            "superseded" => "closed",
            _ => "open",
        };

        var blockers = new List<string>();
        if (pr.Get("draft")?.ValueKind == JsonValueKind.True)
        {
            blockers.Add("It's a draft.");
        }

        if (changes.Count > 0)
        {
            blockers.Add($"Changes requested by {string.Join(", ", changes)}.");
        }

        if (CheckStates.Overall(checks) == CheckStates.Failure)
        {
            blockers.Add("A build or check failed.");
        }

        return new PullRequestInfo(
            pr.Get("links")?.Get("html")?.GetString("href") ?? PullUrl(pull),
            pr.GetString("title") ?? "",
            state,
            pr.Get("source")?.Get("branch")?.GetString("name") ?? "",
            pr.Get("destination")?.Get("branch")?.GetString("name") ?? "",
            pr.Get("source")?.Get("commit")?.GetString("hash"),
            pr.Get("merge_commit")?.GetString("hash"),
            // Bitbucket doesn't say up front whether merge checks pass; the merge call reports what blocks it.
            state == "open" && blockers.Count == 0 ? null : false,
            blockers,
            checks,
            approved,
            changes,
            pr.Get("author")?.GetString("display_name"));
    }

    /// <summary>Build statuses on a commit; Bitbucket Pipelines and most CI tools report here.</summary>
    public async Task<IReadOnlyList<CheckStatus>> GetCommitChecksAsync(BitbucketRepo repo, string sha, CancellationToken cancellationToken)
    {
        using var statuses = await BitbucketAsync(HttpMethod.Get, $"repositories/{repo.Workspace}/{repo.Name}/commit/{sha}/statuses?pagelen=50", null, cancellationToken);
        return [.. ParseStatuses(statuses)];
    }

    public async Task<ActionResult> MergePullRequestAsync(BitbucketPull pull, MergeStrategy? strategy, bool closeSourceBranch, CancellationToken cancellationToken)
    {
        var info = await GetPullRequestAsync(pull, cancellationToken);
        var body = new Dictionary<string, object> { ["type"] = "pullrequest", ["close_source_branch"] = closeSourceBranch };
        if (strategy is not null)
        {
            body["merge_strategy"] = strategy switch
            {
                MergeStrategy.Squash => "squash",
                MergeStrategy.Rebase => "fast_forward",
                _ => "merge_commit",
            };
        }

        using var doc = await BitbucketAsync(HttpMethod.Post, $"repositories/{pull.Workspace}/{pull.Repo}/pullrequests/{pull.Id}/merge", body, cancellationToken);
        var sha = doc.RootElement.Get("merge_commit")?.GetString("hash");
        return new ActionResult(info.Url,
            $"Merged {pull.Workspace}/{pull.Repo} pull request #{pull.Id} ({info.SourceBranch} into {info.TargetBranch}){(sha is null ? "" : $" as {sha[..Math.Min(7, sha.Length)]}")}.",
            "merge");
    }

    /// <summary>Assigns a Jira issue. With no name, to the reporter; otherwise to the first assignable person matching the name or email.</summary>
    public async Task<ActionResult> AssignIssueAsync(JiraIssue issue, string? assignee, CancellationToken cancellationToken)
    {
        string? accountId;
        string? name;
        if (string.IsNullOrWhiteSpace(assignee))
        {
            using var doc = await JiraAsync(issue.Site, HttpMethod.Get, $"rest/api/3/issue/{issue.Key}?fields=reporter", null, cancellationToken);
            var reporter = doc.RootElement.Get("fields")?.Get("reporter");
            accountId = reporter?.GetString("accountId");
            name = reporter?.GetString("displayName");
            if (accountId is null)
            {
                throw new ConnectorException($"{issue.Key} has no reporter to assign it to.");
            }
        }
        else
        {
            using var users = await JiraAsync(issue.Site, HttpMethod.Get,
                $"rest/api/3/user/assignable/search?issueKey={Uri.EscapeDataString(issue.Key)}&query={Uri.EscapeDataString(assignee)}", null, cancellationToken);
            var match = users.RootElement.ValueKind == JsonValueKind.Array ? users.RootElement.EnumerateArray().FirstOrDefault() : default;
            accountId = match.ValueKind == JsonValueKind.Object ? match.GetString("accountId") : null;
            name = match.ValueKind == JsonValueKind.Object ? match.GetString("displayName") : null;
            if (accountId is null)
            {
                throw new ConnectorException($"Nobody matching '{assignee}' can be assigned {issue.Key}.");
            }
        }

        (await JiraAsync(issue.Site, HttpMethod.Put, $"rest/api/3/issue/{issue.Key}/assignee", new { accountId }, cancellationToken)).Dispose();
        return new ActionResult(new Uri(issue.Site, $"browse/{issue.Key}").ToString(), $"Assigned {issue.Key} to {name}.", "assignment");
    }

    private static IEnumerable<CheckStatus> ParseStatuses(JsonDocument statuses) =>
        statuses.RootElement.Get("values")?.EnumerateArray().Select(s => new CheckStatus(
            s.GetString("name") ?? s.GetString("key") ?? "build",
            s.GetString("state") switch
            {
                "SUCCESSFUL" => CheckStates.Success,
                "INPROGRESS" => CheckStates.Pending,
                "STOPPED" => CheckStates.Skipped,
                _ => CheckStates.Failure,
            },
            s.GetString("url"))) ?? [];

    /// <summary>
    /// Jira's API wants Atlassian Document Format; blank lines become paragraphs. A mention goes at the start of
    /// the first paragraph.
    /// </summary>
    public static object ToAdf(string text, (string AccountId, string Name)? mention = null)
    {
        var paragraphs = text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(paragraph => paragraph.Split('\n')
                .SelectMany((line, i) => i == 0
                    ? new object[] { new { type = "text", text = line } }
                    : [new { type = "hardBreak" }, new { type = "text", text = line }])
                .ToList())
            .ToList();

        if (mention is { } m)
        {
            if (paragraphs.Count == 0)
            {
                paragraphs.Add([]);
            }

            paragraphs[0].InsertRange(0, [new { type = "mention", attrs = new { id = m.AccountId, text = $"@{m.Name}" } }, new { type = "text", text = " " }]);
        }

        return new
        {
            type = "doc",
            version = 1,
            content = paragraphs.Select(content => new { type = "paragraph", content = content.ToArray() }).ToArray(),
        };
    }

    private static string PullUrl(BitbucketPull pull) => $"https://bitbucket.org/{pull.Workspace}/{pull.Repo}/pull-requests/{pull.Id}";

    private async Task<JsonDocument> BitbucketAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var a = settings.Current.Atlassian;
        var token = secrets.Get(SecretNames.BitbucketApiToken);
        if (string.IsNullOrWhiteSpace(a.Email) || string.IsNullOrEmpty(token))
        {
            throw new ConnectorException("Bitbucket isn't set up. Add your Atlassian email and a Bitbucket API token on the Connectors page.");
        }

        using var http = httpClients.CreateClient(BitbucketConnector.HttpClientName);
        using var request = new HttpRequestMessage(method, new Uri(BitbucketApi, path)) { Content = body is null ? null : JsonContent.Create(body) };
        return await SendOrEmptyAsync(http, request, AtlassianHttp.Basic(a.Email, token), "Bitbucket", cancellationToken);
    }

    private async Task<JsonDocument> JiraAsync(Uri site, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var a = settings.Current.Atlassian;
        var token = secrets.Get(SecretNames.JiraApiToken);
        if (string.IsNullOrWhiteSpace(a.Email) || string.IsNullOrEmpty(token))
        {
            throw new ConnectorException("Jira isn't set up. Add your Atlassian email and a Jira API token on the Connectors page.");
        }

        using var http = httpClients.CreateClient(JiraConnector.HttpClientName);
        using var request = new HttpRequestMessage(method, new Uri(site, path)) { Content = body is null ? null : JsonContent.Create(body) };
        return await SendOrEmptyAsync(http, request, AtlassianHttp.Basic(a.Email, token), "Jira", cancellationToken);
    }

    /// <summary>Some Atlassian write calls answer 204 with no body; treat that as an empty object.</summary>
    private static async Task<JsonDocument> SendOrEmptyAsync(HttpClient http, HttpRequestMessage request, System.Net.Http.Headers.AuthenticationHeaderValue auth, string product, CancellationToken cancellationToken)
    {
        try
        {
            return await AtlassianHttp.SendAsync(http, request, auth, product, cancellationToken);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}");
        }
    }
}
