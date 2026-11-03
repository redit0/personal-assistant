using System.ComponentModel;
using Assistant.Connectors.Atlassian;
using Assistant.Connectors.GitHub;
using Assistant.Connectors.Microsoft;
using Assistant.Connectors.Slack;
using Assistant.Core.Actions;
using Assistant.Core.Connectors;
using Assistant.Core.Items;
using Assistant.Core.Settings;
using Assistant.Core.Signals;
using Assistant.Core.Tools;
using Assistant.Data;
using Assistant.Tools.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Actions;

/// <summary>
/// Tools that change things outside the assistant: pull requests, reviews, comments, ticket status, and
/// email drafts. All are source actions, so they ask for your approval by default, and always ask when the
/// item came from someone outside your organization. Passing itemId links the result back to the item.
/// </summary>
public sealed class ActionTools(
    AssistantDbContext db,
    ToolCallContext context,
    TimeProvider time,
    GitHubActions github,
    AtlassianActions atlassian,
    AzureDevOpsActions azureDevOps,
    GraphClient graph,
    SlackClient slack,
    UserSettingsStore settings)
{
    private const string ItemIdHelp = "The item this work is for. The result is linked to it, and its link is used when no url is given.";

    [AssistantTool("create_pull_request", ToolCategory.SourceAction, Title = "Open a pull request", OpenWorld = true)]
    [Description("Opens a pull request on GitHub, Bitbucket, or Azure DevOps for a branch you've already pushed. " +
        "The repo is a name from list_repos (or its remote URL); the host is worked out from the repo's remote.")]
    public async Task<ActionResult> CreatePullRequestAsync(
        [Description("Repo name from list_repos, or its remote URL.")] string repo,
        [Description("The pushed branch with the changes.")] string sourceBranch,
        [Description("Pull request title.")] string title,
        [Description("Pull request description. Mention what was asked and how it was addressed.")] string? description = null,
        [Description("Branch to merge into. Defaults to the repo's default branch.")] string? targetBranch = null,
        [Description("Open as a draft.")] bool draft = false,
        [Description(ItemIdHelp)] int? itemId = null,
        CancellationToken cancellationToken = default)
    {
        var target = await ResolveRepoAsync(repo, cancellationToken);
        var result = await RunAsync(() => target switch
        {
            GitHubRepo g => github.CreatePullRequestAsync(g, sourceBranch, targetBranch, title, description, draft, cancellationToken),
            BitbucketRepo b => atlassian.CreatePullRequestAsync(b, sourceBranch, targetBranch, title, description, draft, cancellationToken),
            AzureDevOpsRepo a => azureDevOps.CreatePullRequestAsync(a, sourceBranch, targetBranch, title, description, draft, cancellationToken),
            _ => throw new ToolException($"Can't open pull requests for {repo}."),
        });

        return await LinkAsync(itemId, result, cancellationToken);
    }

    [AssistantTool("comment", ToolCategory.SourceAction, Title = "Comment on a PR or ticket", OpenWorld = true)]
    [Description("Adds a comment to a GitHub issue or pull request, Bitbucket or Azure DevOps pull request, Azure DevOps work item, or Jira issue. " +
        "On a Jira issue the comment starts with an @mention of the reporter, so don't open with their name yourself.")]
    public async Task<ActionResult> CommentAsync(
        [Description("The comment text. Plain text; blank lines separate paragraphs.")] string body,
        [Description("Web link to the PR, issue, work item, or ticket. Optional when itemId is given.")] string? url = null,
        [Description(ItemIdHelp)] int? itemId = null,
        [Description("Tag the Jira reporter at the start of the comment. Defaults to true.")] bool mentionRequester = true,
        CancellationToken cancellationToken = default)
    {
        var target = await ResolveTargetAsync(url, itemId, cancellationToken);
        var result = await RunAsync(() => target switch
        {
            GitHubIssueOrPull g => github.CommentAsync(g, body, cancellationToken),
            BitbucketPull b => atlassian.CommentOnPullRequestAsync(b, body, cancellationToken),
            AzureDevOpsPull a => azureDevOps.CommentOnPullRequestAsync(a, body, cancellationToken),
            AzureDevOpsWorkItem w => azureDevOps.CommentOnWorkItemAsync(w, body, cancellationToken),
            JiraIssue j => atlassian.CommentOnIssueAsync(j, body, mentionRequester, cancellationToken),
            _ => throw new ToolException("Can't comment there."),
        });

        return await LinkAsync(itemId, result, cancellationToken);
    }

    [AssistantTool("review_pull_request", ToolCategory.SourceAction, Title = "Review a pull request", OpenWorld = true)]
    [Description("Approves, requests changes on, or comments on a pull request on GitHub, Bitbucket, or Azure DevOps.")]
    public async Task<ActionResult> ReviewPullRequestAsync(
        [Description("approve, request_changes, or comment.")] string decision,
        [Description("Review comment. Required for request_changes.")] string? body = null,
        [Description("Web link to the pull request. Optional when itemId is given.")] string? url = null,
        [Description(ItemIdHelp)] int? itemId = null,
        CancellationToken cancellationToken = default)
    {
        var review = decision.Trim().ToLowerInvariant().Replace('-', '_') switch
        {
            "approve" or "approved" => ReviewDecision.Approve,
            "request_changes" or "changes_requested" or "reject" => ReviewDecision.RequestChanges,
            "comment" => ReviewDecision.Comment,
            _ => throw new ToolException("decision must be approve, request_changes, or comment."),
        };

        if (review != ReviewDecision.Approve && string.IsNullOrWhiteSpace(body))
        {
            throw new ToolException("Explain what needs to change in the body.");
        }

        var target = await ResolveTargetAsync(url, itemId, cancellationToken);
        var result = await RunAsync(() => target switch
        {
            GitHubIssueOrPull g => github.ReviewAsync(g, review, body, cancellationToken),
            BitbucketPull b => atlassian.ReviewPullRequestAsync(b, review, body, cancellationToken),
            AzureDevOpsPull a => azureDevOps.ReviewPullRequestAsync(a, review, body, cancellationToken),
            _ => throw new ToolException("That link isn't a pull request."),
        });

        return await LinkAsync(itemId, result, cancellationToken);
    }

    [AssistantTool("list_statuses", ToolCategory.Read, Title = "List ticket statuses", Idempotent = true, OpenWorld = true)]
    [Description("Lists the statuses a Jira issue, Azure DevOps work item, or GitHub issue can move to from where it is now, with each " +
        "one's category, so you can pick the one that fits before calling change_status.")]
    public async Task<TicketStatuses> ListStatusesAsync(
        [Description("Web link to the issue, work item, or ticket. Optional when itemId is given.")] string? url = null,
        [Description("The item the ticket belongs to; its link is used when no url is given.")] int? itemId = null,
        CancellationToken cancellationToken = default)
    {
        var target = await ResolveTargetAsync(url, itemId, cancellationToken);
        return await RunAsync(() => target switch
        {
            JiraIssue j => atlassian.GetStatusesAsync(j, cancellationToken),
            AzureDevOpsWorkItem w => azureDevOps.GetWorkItemStatesAsync(w, cancellationToken),
            GitHubIssueOrPull { IsPull: false } => Task.FromResult(new TicketStatuses(null, [new StatusOption("open", "To Do"), new StatusOption("closed", "Done")])),
            _ => throw new ToolException("That link isn't a ticket, issue, or work item."),
        });
    }

    [AssistantTool("change_status", ToolCategory.SourceAction, Title = "Change ticket status", OpenWorld = true)]
    [Description("Moves a Jira issue (by transition or status name), changes an Azure DevOps work item's state, or closes/reopens a GitHub issue. " +
        "If the status isn't available, the error lists the ones that are.")]
    public async Task<ActionResult> ChangeStatusAsync(
        [Description("Target status, such as \"In Progress\", \"Done\", \"Resolved\", or for GitHub \"closed\"/\"open\".")] string status,
        [Description("Web link to the issue, work item, or ticket. Optional when itemId is given.")] string? url = null,
        [Description(ItemIdHelp)] int? itemId = null,
        CancellationToken cancellationToken = default)
    {
        var target = await ResolveTargetAsync(url, itemId, cancellationToken);
        var result = await RunAsync(() => target switch
        {
            JiraIssue j => atlassian.TransitionIssueAsync(j, status, cancellationToken),
            AzureDevOpsWorkItem w => azureDevOps.SetWorkItemStateAsync(w, status, cancellationToken),
            GitHubIssueOrPull g => status.Trim().ToLowerInvariant() switch
            {
                "closed" or "close" or "done" => github.SetStateAsync(g, open: false, cancellationToken),
                "open" or "reopen" or "reopened" => github.SetStateAsync(g, open: true, cancellationToken),
                _ => throw new ToolException("GitHub issues can only be open or closed."),
            },
            _ => throw new ToolException("Can't change the status of that link. Pull requests are completed on the host."),
        });

        return await LinkAsync(itemId, result, cancellationToken);
    }

    [AssistantTool("draft_email_reply", ToolCategory.SourceAction, Title = "Draft an email reply", OpenWorld = true)]
    [Description("Writes a reply to an email item as a draft in the user's Outlook Drafts folder. It is never sent; the user reviews and sends it. " +
        "Replies to the latest message in the thread from someone other than the user. Format it like a real email: a greeting on its " +
        "own line, a blank line, short paragraphs separated by blank lines, and lists on their own lines. Stop after the last paragraph, " +
        "because the user's sign-off is added for you.")]
    public async Task<ActionResult> DraftEmailReplyAsync(
        [Description("The email item to reply to.")] int itemId,
        [Description("The reply text, as the user, in plain text. Line breaks and blank lines are kept. Leave out the sign-off.")] string body,
        [Description("Reply to everyone on the thread rather than just the sender.")] bool replyAll = true,
        CancellationToken cancellationToken = default)
    {
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken)
            ?? throw new ToolException($"Item {itemId} does not exist.");

        var messageId = await FindMessageToReplyToAsync(item, cancellationToken);
        var (_, webLink) = await RunAsync(async () =>
        {
            var signOff = settings.Current.Outlook.EmailSignOff;
            if (string.IsNullOrWhiteSpace(signOff))
            {
                signOff = EmailBody.DefaultSignOff((await graph.GetMeAsync(cancellationToken)).DisplayName);
            }

            return await graph.CreateReplyDraftAsync(messageId, EmailBody.ToHtml(EmailBody.WithSignOff(body, signOff)), replyAll, cancellationToken);
        });

        var result = new ActionResult(webLink ?? "https://outlook.office.com/mail/drafts",
            $"Saved a draft {(replyAll ? "reply-all" : "reply")} in Outlook Drafts. Nothing was sent.", "email_draft");
        return await LinkAsync(itemId, result, cancellationToken);
    }

    [AssistantTool("post_message", ToolCategory.SourceAction, Title = "Post a chat message", OpenWorld = true, AlwaysAsk = true)]
    [Description("Posts a message as the user in the Teams or Slack conversation an item came from. In a Teams channel or a Slack " +
        "thread it replies in the thread; in a chat or DM it posts to the chat. The user always approves the exact text first, " +
        "so write it as the final message, in the user's voice, in plain text. In Slack outside a DM, the message starts with an " +
        "@mention of the person who asked, so don't open with their name yourself.")]
    public async Task<ActionResult> PostMessageAsync(
        [Description("The Teams or Slack item to reply to.")] int itemId,
        [Description("The message, as the user. Plain text; line breaks are kept.")] string text,
        [Description("In Slack, tag the person who asked at the start of the message. Defaults to true.")] bool mentionRequester = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ToolException("text is required.");
        }

        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken)
            ?? throw new ToolException($"Item {itemId} does not exist.");
        var key = item.ExternalId switch
        {
            { } id when id.StartsWith("teams:", StringComparison.Ordinal) => id["teams:".Length..],
            { } id when id.StartsWith("slack:", StringComparison.Ordinal) => id["slack:".Length..],
            _ => throw new ToolException($"Item {itemId} didn't come from Teams or Slack. Use draft_email_reply or comment instead."),
        };

        key = ConversationTopic.ConversationOf(key);
        var result = item.ExternalId!.StartsWith("teams:", StringComparison.Ordinal)
            ? await RunAsync(() => PostToTeamsAsync(key, text, item.SourceUrl, cancellationToken))
            : await RunAsync(() => PostToSlackAsync(key, text, mentionRequester, cancellationToken));
        return await LinkAsync(itemId, result, cancellationToken);
    }

    /// <summary>Teams thread keys are a chat ID, or "channelId:rootId" for a channel thread.</summary>
    private async Task<ActionResult> PostToTeamsAsync(string key, string text, string? fallbackUrl, CancellationToken cancellationToken)
    {
        var colon = key.IndexOf(':', Math.Max(0, key.IndexOf('@')));
        if (colon < 0)
        {
            var link = await graph.PostChatMessageAsync(key, text, cancellationToken);
            return new ActionResult(link ?? fallbackUrl ?? "https://teams.microsoft.com", "Posted a reply in the Teams chat.", "chat_message");
        }

        var (channelId, rootId) = (key[..colon], key[(colon + 1)..]);
        var channel = (await graph.GetMyChannelsAsync(cancellationToken)).FirstOrDefault(c => c.Id == channelId)
            ?? throw new ToolException("That Teams channel isn't in a team you belong to anymore.");
        var url = await graph.ReplyInChannelAsync(channel.TeamId, channelId, rootId, text, cancellationToken);
        return new ActionResult(url ?? fallbackUrl ?? "https://teams.microsoft.com", $"Replied in the {channel.TeamName} › {channel.Name} thread.", "chat_message");
    }

    /// <summary>Slack thread keys are a channel ID, or "channelId:threadTs" for a thread.</summary>
    private async Task<ActionResult> PostToSlackAsync(string key, string text, bool mentionRequester, CancellationToken cancellationToken)
    {
        var parts = key.Split(':', 2);
        var threadTs = parts.Length == 2 ? parts[1] : null;
        var requester = mentionRequester ? await SlackRequesterAsync(key, cancellationToken) : null;
        var ts = await slack.PostMessageAsync(parts[0], text, threadTs, cancellationToken, requester?.UserId);
        var me = await slack.AuthTestAsync(cancellationToken);
        var tagged = requester is null ? "" : $" and tagged {requester.Name}";
        return new ActionResult(SlackClient.Permalink(me.Url, parts[0], ts, threadTs),
            threadTs is null ? $"Posted a message in Slack{tagged}." : $"Replied in the Slack thread{tagged}.", "chat_message");
    }

    /// <summary>
    /// The person who asked: the latest message in the conversation from someone other than you. Nobody is
    /// tagged in a DM, where it would only add noise, or when that message came from an app.
    /// </summary>
    private async Task<SlackRequester?> SlackRequesterAsync(string key, CancellationToken cancellationToken)
    {
        if (key.StartsWith('D'))
        {
            return null;
        }

        var last = await db.Signals.AsNoTracking()
            .Where(s => s.Source == "slack" && s.ThreadKey == key && !s.FromMe && s.AuthorAddress != null)
            .OrderByDescending(s => s.ReceivedAt)
            .Select(s => new { s.AuthorAddress, s.Author, s.Kind })
            .FirstOrDefaultAsync(cancellationToken);

        // Slack user IDs start with U or W; apps post as B.
        return last is null || last.Kind == "dm" || last.AuthorAddress![0] is not ('U' or 'W')
            ? null
            : new SlackRequester(last.AuthorAddress, last.Author ?? last.AuthorAddress);
    }

    private sealed record SlackRequester(string UserId, string Name);

    private async Task<string> FindMessageToReplyToAsync(Item item, CancellationToken cancellationToken)
    {
        if (item.Source == "outlook-flagged" && item.ExternalId?.StartsWith("flagged:", StringComparison.Ordinal) == true)
        {
            return item.ExternalId["flagged:".Length..];
        }

        if (item.Source != "outlook" || item.ExternalId?.StartsWith("mail:", StringComparison.Ordinal) != true)
        {
            throw new ToolException($"Item {item.Id} didn't come from email, so there's nothing to reply to.");
        }

        var thread = item.ExternalId["mail:".Length..];
        var message = await db.Signals.AsNoTracking()
            .Where(s => s.Source == "outlook" && s.ThreadKey == thread)
            .OrderByDescending(s => !s.FromMe)
            .ThenByDescending(s => s.ReceivedAt)
            .Select(s => s.ExternalId)
            .FirstOrDefaultAsync(cancellationToken);

        return message ?? throw new ToolException($"The messages for item {item.Id} are no longer stored. Reply from Outlook instead.");
    }

    private async Task<RepoRef> ResolveRepoAsync(string repo, CancellationToken cancellationToken) =>
        (await ActionSupport.ResolveRepoAsync(db, repo, cancellationToken)).Repo;

    private Task<SourceRef> ResolveTargetAsync(string? url, int? itemId, CancellationToken cancellationToken) =>
        ActionSupport.ResolveTargetAsync(db, url, itemId, cancellationToken);

    private Task<ActionResult> LinkAsync(int? itemId, ActionResult result, CancellationToken cancellationToken) =>
        ActionSupport.LinkAsync(db, context, time, itemId, result, cancellationToken);

    private static Task<T> RunAsync<T>(Func<Task<T>> action) => ActionSupport.RunAsync(action);
}
