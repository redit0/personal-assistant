using Assistant.Core.Actions;

namespace Assistant.Mcp.Tests;

public sealed class SourceLinksTests
{
    [Fact]
    public void Parses_web_links_for_each_host()
    {
        Assert.Equal(new GitHubIssueOrPull("acme", "web", 12, true), SourceLinks.Parse("https://github.com/acme/web/pull/12"));
        Assert.Equal(new GitHubIssueOrPull("acme", "web", 7, false), SourceLinks.Parse("https://github.com/acme/web/issues/7#issuecomment-1"));
        Assert.Equal(new BitbucketPull("acme", "reporting", 9), SourceLinks.Parse("https://bitbucket.org/acme/reporting/pull-requests/9/overview"));
        Assert.Equal(new AzureDevOpsPull("contoso", "Platform", "api", 41), SourceLinks.Parse("https://dev.azure.com/contoso/Platform/_git/api/pullrequest/41"));
        Assert.Equal(new AzureDevOpsPull("contoso", "My Project", "api", 5), SourceLinks.Parse("https://contoso.visualstudio.com/DefaultCollection/My%20Project/_git/api/pullrequest/5"));
        Assert.Equal(new AzureDevOpsWorkItem("acme-dev", "Ops", 100), SourceLinks.Parse("https://dev.azure.com/acme-dev/Ops/_workitems/edit/100"));

        var jira = Assert.IsType<JiraIssue>(SourceLinks.Parse("https://example.atlassian.net/browse/OPS-12"));
        Assert.Equal("OPS-12", jira.Key);
        Assert.Equal("https://example.atlassian.net/", jira.Site.ToString());
    }

    [Theory]
    [InlineData("https://example.com/acme/web/pull/12")]
    [InlineData("https://github.com/acme/web")]
    [InlineData("not a url")]
    [InlineData(null)]
    public void Returns_null_for_links_it_cant_act_on(string? url) => Assert.Null(SourceLinks.Parse(url));

    [Theory]
    [InlineData("https://github.com/acme/web.git")]
    [InlineData("git@github.com:acme/web.git")]
    [InlineData("ssh://git@github.com/acme/web")]
    [InlineData("git@github.com-work:acme/web.git")]
    public void Parses_github_remotes(string remote) => Assert.Equal(new GitHubRepo("acme", "web"), SourceLinks.ParseRemote(remote));

    [Theory]
    [InlineData("https://sam@bitbucket.org/acme/reporting.git")]
    [InlineData("git@bitbucket.org:acme/reporting.git")]
    [InlineData("git@bitbucket.org-work:acme/reporting.git")]
    public void Parses_bitbucket_remotes(string remote) => Assert.Equal(new BitbucketRepo("acme", "reporting"), SourceLinks.ParseRemote(remote));

    [Theory]
    [InlineData("https://contoso@dev.azure.com/contoso/Platform/_git/api")]
    [InlineData("git@ssh.dev.azure.com:v3/contoso/Platform/api")]
    [InlineData("https://contoso.visualstudio.com/Platform/_git/api")]
    [InlineData("https://contoso.visualstudio.com/DefaultCollection/Platform/_git/api")]
    public void Parses_azure_devops_remotes(string remote) =>
        Assert.Equal(new AzureDevOpsRepo("contoso", "Platform", "api"), SourceLinks.ParseRemote(remote));

    [Theory]
    [InlineData("jira", "https://example.atlassian.net/browse/PROJ-1037", "PROJ")]
    [InlineData("jira", "https://example.atlassian.net/browse/OPS2_X-9", "OPS2_X")]
    [InlineData("jira", null, null)]
    [InlineData("outlook", "https://example.atlassian.net/browse/PROJ-1037", null)]
    public void Finds_the_jira_project_of_an_item(string source, string? url, string? project) =>
        Assert.Equal(project, Assistant.Web.Services.SourceGroups.JiraProjectOf(source, url));
}
