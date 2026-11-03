using Assistant.Core.Text;

namespace Assistant.Mcp.Tests;

public sealed class SafeLinksTests
{
    private const string Wrapped =
        "https://nam12.safelinks.protection.outlook.com/?url=https%3A%2F%2Fexample.atlassian.net%2Fbrowse%2FOPS-12%3Ffocus%3Dcomment%26id%3D7" +
        "&data=05%7C02%7Csam%40example.com%7Cabc%7C0%7C0%7C638000000000000000&sdata=xyz%3D&reserved=0";

    [Fact]
    public void Unwraps_a_safe_link_to_the_real_address()
    {
        Assert.Equal("https://example.atlassian.net/browse/OPS-12?focus=comment&id=7", SafeLinks.TryUnwrap(Wrapped));
    }

    [Fact]
    public void Unwraps_every_link_in_plain_text_bodies()
    {
        var body = $"See the ticket <{Wrapped}> and the doc ({Wrapped.Replace("OPS-12", "OPS-13")}).\nThanks";

        var result = SafeLinks.Unwrap(body);

        Assert.Equal(
            "See the ticket <https://example.atlassian.net/browse/OPS-12?focus=comment&id=7> and the doc " +
            "(https://example.atlassian.net/browse/OPS-13?focus=comment&id=7).\nThanks",
            result);
    }

    [Fact]
    public void Handles_html_encoded_separators_and_double_wrapping()
    {
        var inner = "https://eur01.safelinks.protection.outlook.com/?url=https%3A%2F%2Fexample.com%2Fa&data=1&reserved=0";
        var outer = "https://nam12.safelinks.protection.outlook.com/?url=" + Uri.EscapeDataString(inner) + "&amp;data=2&amp;reserved=0";

        Assert.Equal("https://example.com/a", SafeLinks.TryUnwrap(outer));
    }

    [Theory]
    [InlineData("https://example.com/?url=https%3A%2F%2Fevil.com")] // Not a Safe Links host.
    [InlineData("https://nam12.safelinks.protection.outlook.com/?data=1")] // No url parameter.
    [InlineData("https://nam12.safelinks.protection.outlook.com/?url=javascript%3Aalert(1)")] // Not a normal link.
    public void Leaves_anything_else_alone(string url)
    {
        Assert.Null(SafeLinks.TryUnwrap(url));
        Assert.Equal(url, SafeLinks.Unwrap(url));
    }

    [Fact]
    public void Link_text_splits_out_urls_without_trailing_punctuation()
    {
        var segments = Web.Services.LinkText.Split("Ticket: https://example.com/a?b=1. Also (https://example.com/c), done").ToList();

        Assert.Equal(
            ["Ticket: ", "https://example.com/a?b=1", ". Also (", "https://example.com/c", "), done"],
            segments.Select(s => s.Text));
        Assert.Equal(["https://example.com/a?b=1", "https://example.com/c"], segments.Where(s => s.Url is not null).Select(s => s.Url));
    }

    [Fact]
    public void Agent_label_is_removed_for_display()
    {
        var wrapped = Tools.Catalog.UntrustedContent.Wrap("outlook", "Hello\nWorld");

        Assert.Equal("Hello\nWorld", Web.Services.LinkText.WithoutAgentLabel(wrapped));
    }

    [Fact]
    public void Text_without_safe_links_is_unchanged()
    {
        const string text = "Plain text with https://github.com/acme/web/pull/12 in it.";
        Assert.Same(text, SafeLinks.Unwrap(text));
    }
}
