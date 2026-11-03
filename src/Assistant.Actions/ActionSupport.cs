using Assistant.Core.Actions;
using Assistant.Core.Connectors;
using Assistant.Core.Items;
using Assistant.Core.Repos;
using Assistant.Core.Tools;
using Assistant.Data;
using Assistant.Tools.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Actions;

/// <summary>Lookups and bookkeeping shared by the action and shipping tools.</summary>
internal static class ActionSupport
{
    /// <summary>A repo by its list_repos name or remote URL, plus its local clone when there is one.</summary>
    public static async Task<(RepoRef Repo, RepoMapping? Clone)> ResolveRepoAsync(AssistantDbContext db, string repo, CancellationToken cancellationToken)
    {
        var mappings = await db.RepoMappings.AsNoTracking().ToListAsync(cancellationToken);
        if (SourceLinks.ParseRemote(repo) is { } direct)
        {
            return (direct, mappings.FirstOrDefault(m => SourceLinks.ParseRemote(m.RemoteUrl) == direct));
        }

        var matches = mappings.Where(m => string.Equals(m.Name, repo, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            matches = [.. mappings.Where(m => m.RemoteUrl.Contains(repo, StringComparison.OrdinalIgnoreCase))];
        }

        var remotes = matches.Select(m => m.RemoteUrl).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return remotes.Count switch
        {
            0 => throw new ToolException($"No local clone matches '{repo}'. Call list_repos, or pass the remote URL."),
            > 1 => throw new ToolException($"'{repo}' matches several repos: {string.Join(", ", remotes)}. Pass the remote URL instead."),
            _ => (SourceLinks.ParseRemote(remotes[0]) ?? throw new ToolException($"{remotes[0]} isn't on GitHub, Bitbucket, or Azure DevOps."), matches[0]),
        };
    }

    public static async Task<SourceRef> ResolveTargetAsync(AssistantDbContext db, string? url, int? itemId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url) && itemId is not null)
        {
            url = await db.Items.AsNoTracking().Where(i => i.Id == itemId).Select(i => i.SourceUrl).FirstOrDefaultAsync(cancellationToken)
                ?? throw new ToolException($"Item {itemId} does not exist or has no link. Pass url instead.");
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ToolException("Pass the url of the PR, issue, work item, or ticket, or an itemId.");
        }

        return SourceLinks.Parse(url)
            ?? throw new ToolException($"{url} isn't a GitHub, Bitbucket, Azure DevOps, or Jira link this tool can act on.");
    }

    /// <summary>Records what was done on the item: a linked artifact and a note.</summary>
    public static async Task<ActionResult> LinkAsync(
        AssistantDbContext db, ToolCallContext context, TimeProvider time, int? itemId, ActionResult result, CancellationToken cancellationToken)
    {
        if (itemId is null)
        {
            return result;
        }

        var item = await db.Items.Include(i => i.Artifacts).FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken);
        if (item is null)
        {
            return result;
        }

        var now = time.GetUtcNow().UtcDateTime;
        if (!item.Artifacts.Any(a => a.Url == result.Url))
        {
            item.Artifacts.Add(new ItemArtifact { Url = result.Url, Kind = result.Kind, Description = result.Summary, AddedBy = context.Caller.Id, CreatedAt = now });
        }

        db.ItemNotes.Add(new ItemNote { ItemId = item.Id, Text = result.Summary, Author = context.Caller.Id, CreatedAt = now });
        item.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    /// <summary>Host errors become tool errors, so the caller sees the host's explanation.</summary>
    public static async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (ConnectorException ex)
        {
            throw new ToolException(ex.Message);
        }
    }
}
