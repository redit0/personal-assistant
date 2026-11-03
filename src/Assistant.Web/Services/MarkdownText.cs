using Markdig;
using Microsoft.AspNetCore.Components;

namespace Assistant.Web.Services;

/// <summary>
/// Renders model-written Markdown (chat replies, the digest summary). Raw HTML is turned off, so text that
/// came from an email can't inject markup into the app.
/// </summary>
public static class MarkdownText
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseAutoLinks()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseTaskLists()
        .Build();

    public static MarkupString ToHtml(string? markdown) =>
        new(string.IsNullOrWhiteSpace(markdown) ? "" : Markdown.ToHtml(markdown, Pipeline));
}
