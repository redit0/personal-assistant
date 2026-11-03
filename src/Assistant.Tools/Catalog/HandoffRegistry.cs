using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Assistant.Tools.Catalog;

/// <summary>
/// Keys for Claude Code sessions started with "Hand to Claude". The launcher gives each session its own key, which
/// the session sends with every MCP request, so the assistant knows which item you handed it. Calls about that
/// item skip the approval prompt, because you're watching the session and its results still get reviewed: a pull
/// request waits for its reviewers and an email reply is only a draft. Keys live in memory, so restarting the app
/// ends them and the session's calls ask again.
/// </summary>
public sealed class HandoffRegistry(TimeProvider time)
{
    public const string HeaderName = "X-Assistant-Handoff";

    /// <summary>How long a session can keep acting without prompts. Long enough for a working day.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    private readonly ConcurrentDictionary<string, (int ItemId, DateTimeOffset Expires)> _keys = new(StringComparer.Ordinal);

    public string Issue(int itemId)
    {
        var now = time.GetUtcNow();
        foreach (var (key, grant) in _keys)
        {
            if (grant.Expires <= now)
            {
                _keys.TryRemove(key, out _);
            }
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _keys[token] = (itemId, now + Lifetime);
        return token;
    }

    /// <summary>The item a key was issued for, or null if the key is unknown or expired.</summary>
    public int? Find(string? token) =>
        !string.IsNullOrEmpty(token) && _keys.TryGetValue(token, out var grant) && grant.Expires > time.GetUtcNow()
            ? grant.ItemId
            : null;
}
