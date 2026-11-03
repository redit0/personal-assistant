using Assistant.Core.Repos;
using Assistant.Tools.Repos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.Mcp.Tests;

public sealed class RepoScannerTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pa-scan-" + Guid.NewGuid().ToString("N"));
    private McpTestHost _host = null!;

    public async Task InitializeAsync() => _host = await McpTestHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Finds_clones_and_reads_their_origin()
    {
        MakeRepo("work/reporting-service", "git@bitbucket.org:acme/reporting-service.git");
        MakeRepo("work/web", "https://github.com/acme/web.git");
        MakeRepo("work/web/node_modules/dep", "https://github.com/someone/dep.git");
        MakeRepo("ado/Platform", "https://acme@dev.azure.com/acme/Platform/_git/Platform");

        var result = await ScanAsync();

        Assert.Equal(3, result.Found);
        var repos = await _host.WithDbAsync(db => db.RepoMappings.OrderBy(r => r.Name).ToListAsync());
        Assert.Collection(repos,
            r => { Assert.Equal("Platform", r.Name); Assert.Equal(CodeHost.AzureDevOps, r.Host); },
            r => { Assert.Equal("reporting-service", r.Name); Assert.Equal(CodeHost.Bitbucket, r.Host); },
            r => { Assert.Equal("web", r.Name); Assert.Equal(CodeHost.GitHub, r.Host); });
    }

    [Fact]
    public async Task Forgets_clones_that_were_deleted()
    {
        MakeRepo("a", "https://github.com/acme/a.git");
        MakeRepo("b", "https://github.com/acme/b.git");
        await ScanAsync();

        Directory.Delete(Path.Combine(_root, "b"), recursive: true);
        var result = await ScanAsync();

        Assert.Equal(1, result.Removed);
        Assert.Equal(["a"], await _host.WithDbAsync(db => db.RepoMappings.Select(r => r.Name).ToListAsync()));
    }

    private async Task<RepoScanner.ScanResult> ScanAsync()
    {
        await using var scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RepoScanner>().ScanAsync([_root]);
    }

    private void MakeRepo(string relativePath, string remote)
    {
        var git = Path.Combine(_root, relativePath, ".git");
        Directory.CreateDirectory(git);
        File.WriteAllText(Path.Combine(git, "config"), $"""
            [core]
            	bare = false
            [remote "origin"]
            	url = {remote}
            	fetch = +refs/heads/*:refs/remotes/origin/*
            [branch "main"]
            	remote = origin
            """);
    }
}
