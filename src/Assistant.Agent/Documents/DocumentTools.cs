using System.ComponentModel;
using Assistant.Connectors.Microsoft;
using Assistant.Core.Connectors;
using Assistant.Core.Settings;
using Assistant.Core.Tools;
using Assistant.Tools.Catalog;

namespace Assistant.Agent.Documents;

public sealed record DocumentSearchResult(string Name, string Link, string? Snippet, DateTime? LastModified, string? LastModifiedBy);

public sealed record DocumentText(string Name, string Link, DateTime? LastModified, string? LastModifiedBy, string? Note, string? Text);

public sealed record WorkIqReply(string Answer, string ConversationId, IReadOnlyList<WorkIqSource> Sources);

/// <summary>SharePoint and OneDrive files, and Work IQ, for the chat and MCP clients. Each is off until you turn it on.</summary>
public sealed class DocumentTools(GraphClient graph, WorkIqClient workIq, UserSettingsStore settings)
{
    [AssistantTool("search_documents", ToolCategory.Read, Title = "Search documents", OpenWorld = true)]
    [Description("Searches the SharePoint and OneDrive files the user can open, by keywords. Returns names, links, and matching snippets. " +
        "Use read_document with a link to get a file's text.")]
    public async Task<IReadOnlyList<DocumentSearchResult>> SearchDocumentsAsync(
        [Description("Keywords, as in the SharePoint search box. KQL such as filetype:docx or author:\"Ann Lee\" also works.")] string query,
        [Description("How many results, 1 to 25.")] int count = 10,
        CancellationToken cancellationToken = default)
    {
        RequireDocuments();
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ToolException("query is required.");
        }

        var hits = await Run(() => graph.SearchDocumentsAsync(query, count, cancellationToken));
        return [.. hits.Select(h => new DocumentSearchResult(h.Name, h.WebUrl, UntrustedContent.Wrap("sharepoint", h.Summary), h.LastModified, h.LastModifiedBy))];
    }

    [AssistantTool("read_document", ToolCategory.Read, Title = "Read document", OpenWorld = true)]
    [Description("Reads the text of a SharePoint or OneDrive file from its link (a search result, or a link from an email or chat). " +
        "Works for Word, PowerPoint, Excel (cell text only), and plain-text files, up to 30,000 characters.")]
    public async Task<DocumentText> ReadDocumentAsync(
        [Description("The file's SharePoint or OneDrive link.")] string link,
        CancellationToken cancellationToken = default)
    {
        RequireDocuments();
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("onedrive.live.com", StringComparison.OrdinalIgnoreCase) || uri.Host == "1drv.ms"))
        {
            throw new ToolException("link must be a SharePoint or OneDrive https link.");
        }

        var doc = await Run(() => graph.ReadDocumentAsync(link, cancellationToken));
        return new DocumentText(doc.Name, doc.WebUrl, doc.LastModified, doc.LastModifiedBy, doc.Note, UntrustedContent.Wrap("sharepoint", doc.Text));
    }

    [AssistantTool("ask_work_iq", ToolCategory.Read, Title = "Ask Work IQ", OpenWorld = true)]
    [Description("Asks Microsoft Work IQ a question about the user's work: meetings and what was decided in them, mail, Teams chats, and files. " +
        "Good for meeting recaps, \"what did Ann say about the launch\", or finding a document by what's in it. Each call is billed to the " +
        "user's organization, so ask focused questions and reuse conversationId for follow-ups.")]
    public async Task<WorkIqReply> AskWorkIqAsync(
        [Description("The question, in plain language.")] string question,
        [Description("conversationId from an earlier answer, to ask a follow-up.")] string? conversationId = null,
        CancellationToken cancellationToken = default)
    {
        var w = settings.Current.WorkIq;
        if (!w.Enabled)
        {
            throw new ToolException("Work IQ is turned off. The user can turn it on from the Connectors page once their admin has set it up.");
        }

        if (string.IsNullOrWhiteSpace(question))
        {
            throw new ToolException("question is required.");
        }

        var answer = await Run(() => workIq.AskAsync(question, conversationId, w.WebGrounding, cancellationToken));
        return new WorkIqReply(UntrustedContent.Wrap("workiq", answer.Text) ?? "(no answer)", answer.ConversationId, answer.Sources);
    }

    private void RequireDocuments()
    {
        if (!settings.Current.SharePoint.DocumentsEnabled)
        {
            throw new ToolException("SharePoint documents are turned off. The user can turn them on from the Connectors page.");
        }
    }

    private static async Task<T> Run<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (ConnectorException ex)
        {
            throw new ToolException(ex.Message);
        }
    }
}
