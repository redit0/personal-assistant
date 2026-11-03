using Assistant.Tools.Catalog;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Assistant.Agent;

/// <summary>One step of a streaming chat reply: some text, or the name of a tool the assistant is using.</summary>
public sealed record ChatUpdate(string? Text, string? ToolTitle);

/// <summary>
/// One conversation in the tray chat window. The model gets the same tools MCP clients get, wrapped
/// so every call goes through policy checks, approvals, and the audit log.
/// </summary>
public sealed class ChatSession(ChatClientFactory clients, ToolCatalog catalog, ToolInvoker invoker, TimeProvider time, ILoggerFactory loggerFactory)
{
    private readonly List<ChatMessage> _history = [];

    public IReadOnlyList<ChatMessage> History => _history;

    public string? UnavailableReason => clients.UnavailableReason();

    public void Clear() => _history.Clear();

    /// <summary>Streams the assistant's reply, including which tools it calls along the way.</summary>
    public async IAsyncEnumerable<ChatUpdate> SendAsync(string userMessage, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = clients.GetClient() ?? throw new InvalidOperationException(UnavailableReason);

        using var withTools = client.AsBuilder()
            .UseFunctionInvocation(loggerFactory, f => f.MaximumIterationsPerRequest = 20)
            .Build();

        _history.Add(new ChatMessage(ChatRole.User, userMessage));

        var options = new ChatOptions
        {
            Tools = [.. catalog.Tools.Select(t => (AITool)new GuardedAIFunction(t, invoker, ToolCaller.Chat))],
            ToolMode = ChatToolMode.Auto,
        };

        var messages = new List<ChatMessage>(_history.Count + 1) { new(ChatRole.System, SystemPrompt()) };
        messages.AddRange(_history);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in withTools.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            updates.Add(update);

            foreach (var call in update.Contents.OfType<FunctionCallContent>())
            {
                yield return new ChatUpdate(null, catalog.Find(call.Name)?.Title ?? call.Name);
            }

            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return new ChatUpdate(update.Text, null);
            }
        }

        _history.AddMessages(updates);
    }

    private string SystemPrompt()
    {
        var now = time.GetLocalNow();
        return $"""
            You are the user's personal assistant, running in a Windows tray app. You keep track of what they have been
            asked to do, what they have committed to, what is waiting on their review, their calendar, and their deadlines.

            The current local time is {now:dddd, MMMM d, yyyy h:mm tt} ({now:zzz}).

            Use the tools to look things up rather than guessing, and to update items when the user asks:
            - For "what's my day like" questions, get_digest gives meetings, due, overdue, and new items in one call.
            - get_calendar and find_free_time read their Outlook calendar and working hours.
            - remind_me sets a reminder. For "remind me to follow up with X if they haven't replied", attach it to the
              matching waiting-on item so it's skipped if they reply first; create one if none exists.
            - Actions that change things outside the assistant ask the user for approval in a separate window.

            When you mention an item, include its ID like #136 so the user can find it. Keep answers short and
            scannable; Markdown lists and bold are fine. Pass dates to tools as ISO 8601 with the user's UTC offset.

            {UntrustedContent.Notice}
            """;
    }
}
