using System.ComponentModel;
using System.Text;
using Assistant.Core.Repos;
using Assistant.Core.Tools;
using Assistant.Data;
using Assistant.Tools.Catalog;
using Assistant.Tools.Items;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Tools.Repos;

public sealed record RepoDto(string Name, CodeHost Host, string RemoteUrl, string LocalPath)
{
    public static RepoDto From(RepoMapping r) => new(r.Name, r.Host, r.RemoteUrl, r.LocalPath);
}

public sealed record WorkContext(
    ItemDetail Item,
    IReadOnlyList<RepoDto> CandidateRepos,
    string SuggestedBranch,
    string Guidance);

/// <summary>Tells a coding agent where and how to work on an item.</summary>
public sealed class RepoTools(AssistantDbContext db)
{
    [AssistantTool("list_repos", ToolCategory.Read, Title = "List repos")]
    [Description("Lists git clones on this machine with their remotes and code hosts.")]
    public async Task<IReadOnlyList<RepoDto>> ListReposAsync(
        [Description("Only repos whose name or remote URL contains this text.")] string? filter = null,
        CancellationToken cancellationToken = default)
    {
        var repos = await db.RepoMappings.AsNoTracking().OrderBy(r => r.Name).ToListAsync(cancellationToken);
        return [.. repos.Where(r => Matches(r, filter)).Select(RepoDto.From)];
    }

    [AssistantTool("get_work_context", ToolCategory.Read, Title = "Get work context")]
    [Description("Everything a coding agent needs to act on an item: the request and its source text, who asked, " +
        "the due date, which local clone to work in, and a suggested branch name. Do the code work with your own tools, " +
        "then record results here with link_artifact, add_note, and mark_done. " + UntrustedContent.Notice)]
    public async Task<WorkContext> GetWorkContextAsync(
        [Description("The item's ID.")] int itemId,
        CancellationToken cancellationToken = default)
    {
        var item = await db.Items.AsNoTracking().Include(i => i.Notes).Include(i => i.Artifacts).Include(i => i.Duplicates)
            .FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken)
            ?? throw new ToolException($"Item {itemId} does not exist.");

        var repos = string.IsNullOrWhiteSpace(item.RepoHint)
            ? []
            : (await db.RepoMappings.AsNoTracking().ToListAsync(cancellationToken))
                .Where(r => Matches(r, item.RepoHint))
                .OrderBy(r => string.Equals(r.Name, item.RepoHint, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .Select(RepoDto.From)
                .ToList();

        var guidance = repos.Count switch
        {
            0 when string.IsNullOrWhiteSpace(item.RepoHint) =>
                "This item has no repo hint. Ask the user which repo to use, or call list_repos.",
            0 => $"No local clone matches '{item.RepoHint}'. Ask the user where it is, or call list_repos.",
            1 => $"Work in {repos[0].LocalPath}.",
            _ => "Several clones match. Confirm with the user which one to use.",
        };

        return new WorkContext(ItemDetail.From(item), repos, SuggestBranch(item.Id, item.Title), guidance);
    }

    public static string SuggestBranch(int itemId, string title)
    {
        var slug = new StringBuilder();
        foreach (var c in title.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                slug.Append(c);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }

            if (slug.Length >= 40)
            {
                break;
            }
        }

        return $"assistant/{itemId}-{slug.ToString().Trim('-')}".TrimEnd('-');
    }

    private static bool Matches(RepoMapping repo, string? filter) =>
        string.IsNullOrWhiteSpace(filter) ||
        repo.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        repo.RemoteUrl.Contains(filter, StringComparison.OrdinalIgnoreCase);
}
