namespace Assistant.Core.Signals;

/// <summary>
/// A DM, group chat, or channel timeline is every message in that place, so it can hold many unrelated requests.
/// Each one is tracked as a topic, with the key "conversationKey|id". The conversation's own key stays on its
/// messages, and its own decision only records how far triage has read.
/// </summary>
public static class ConversationTopic
{
    private const char Separator = '|';

    public static string KeyFor(string conversationKey, long id) => $"{conversationKey}{Separator}{id}";

    /// <summary>The conversation a topic belongs to. Any other key is returned as it is.</summary>
    public static string ConversationOf(string key)
    {
        var at = key.IndexOf(Separator);
        return at < 0 ? key : key[..at];
    }
}
