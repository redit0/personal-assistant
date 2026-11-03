using System.Diagnostics;
using Assistant.Core;
using Assistant.Data;
using Assistant.Mcp;
using Assistant.Tools.Catalog;
using Assistant.Tools.Repos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Assistant.Web.Services;

/// <summary>
/// "Hand to Claude": opens Claude Code in its own terminal window, in the item's local clone, already asked to
/// handle the item. It runs interactively, so you watch it work and approve its steps there, and it records what
/// it did back on the item through the MCP tools. The session gets a handoff key (see <see cref="HandoffRegistry"/>), so
/// its calls about that item don't also wait for the assistant's approval prompt.
/// </summary>
public sealed class ClaudeLauncher(
    IServiceScopeFactory scopes,
    HandoffRegistry handoffs,
    McpTokenProvider tokens,
    McpHostStatus mcp,
    ILogger<ClaudeLauncher> logger)
{
    /// <summary>Where Claude Code is installed, or null. The native installer uses ~/.local/bin; npm adds claude.cmd to PATH.</summary>
    public static string? FindClaude()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dirs = new[] { Path.Combine(home, ".local", "bin") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        foreach (var dir in dirs)
        {
            foreach (var name in new[] { "claude.exe", "claude.cmd" })
            {
                var path = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    /// <summary>The first message Claude gets. Plain words only, since it passes through a command line.</summary>
    public static string Prompt(int itemId) =>
        $"Handle item {itemId} from my personal assistant. Call the handle_item tool with itemId {itemId} and follow its instructions.";

    /// <summary>Starts the session. Returns where it runs, for the item's notes.</summary>
    public async Task<string> HandToClaudeAsync(int itemId, CancellationToken cancellationToken = default)
    {
        var claude = FindClaude()
            ?? throw new InvalidOperationException("Claude Code isn't installed, or isn't on your PATH. Install it from claude.com/code and try again.");

        string folder;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var context = await new RepoTools(scope.ServiceProvider.GetRequiredService<AssistantDbContext>()).GetWorkContextAsync(itemId, cancellationToken);
            folder = context.CandidateRepos is [var only] && Directory.Exists(only.LocalPath)
                ? only.LocalPath
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        var config = WriteHandoffConfig(itemId);
        // The prompt goes first: --mcp-config takes any number of files, so anything after it would be read as one.
        string[] arguments = config is null ? [Prompt(itemId)] : [Prompt(itemId), "--mcp-config", config];

        var start = claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            // cmd strips the outer pair of quotes after /k, so wrap the whole command once more.
            ? new ProcessStartInfo("cmd.exe", $"/k \"\"{claude}\" {string.Join(' ', arguments.Select(a => $"\"{a}\""))}\"")
            : new ProcessStartInfo(claude, arguments);

        start.WorkingDirectory = folder;
        start.UseShellExecute = false; // A console program started from the tray app gets its own window.

        Process.Start(start)?.Dispose();
        logger.LogInformation("Handed item {ItemId} to Claude Code in {Folder}", itemId, folder);
        return folder;
    }

    /// <summary>
    /// Writes the session's MCP config to a file, which keeps the tokens off the command line. Returns null when
    /// the MCP server is off, and the session then uses whatever you registered. Old files are removed once their
    /// keys have expired.
    /// </summary>
    private string? WriteHandoffConfig(int itemId)
    {
        if (!mcp.IsRunning || mcp.Port == 0)
        {
            return null;
        }

        var dir = Path.Combine(AppPaths.DataDirectory, "handoffs");
        Directory.CreateDirectory(dir);
        foreach (var old in new DirectoryInfo(dir).EnumerateFiles("*.json"))
        {
            if (old.LastWriteTimeUtc < DateTime.UtcNow - HandoffRegistry.Lifetime)
            {
                try
                {
                    old.Delete();
                }
                catch (IOException)
                {
                }
            }
        }

        var path = Path.Combine(dir, $"item-{itemId}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, McpClientConfig.ClaudeCodeHandoff(mcp.Port, tokens.GetToken(), handoffs.Issue(itemId)));
        return path;
    }
}
