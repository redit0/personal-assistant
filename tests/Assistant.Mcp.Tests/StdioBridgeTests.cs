using System.IO.Pipelines;
using Assistant.Core.Secrets;
using Assistant.Mcp.Stdio;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Assistant.Mcp.Tests;

/// <summary>Runs the bridge in memory between an MCP client and the real (in-memory) tray app server.</summary>
public sealed class StdioBridgeTests : IAsyncLifetime
{
    private McpTestHost _upstream = null!;

    public async Task InitializeAsync() => _upstream = await McpTestHost.StartAsync();

    public async Task DisposeAsync() => await _upstream.DisposeAsync();

    [Fact]
    public async Task Forwards_the_tool_list()
    {
        await using var bridge = await BridgeHarness.StartAsync(
            new UpstreamOptions(new Uri(_upstream.CreateHttpClient().BaseAddress!, "mcp"), McpTestHost.Token),
            _upstream.CreateHttpClient);

        var bridged = (await bridge.Client.ListToolsAsync()).Select(t => t.Name).Order().ToList();

        await using var direct = await _upstream.ConnectAsync();
        var expected = (await direct.ListToolsAsync()).Select(t => t.Name).Order().ToList();

        Assert.Equal(expected, bridged);
        Assert.Contains("untrusted_content", bridge.Client.ServerInstructions);
    }

    [Fact]
    public async Task Forwards_calls_under_the_downstream_client_name()
    {
        await using var bridge = await BridgeHarness.StartAsync(
            new UpstreamOptions(new Uri(_upstream.CreateHttpClient().BaseAddress!, "mcp"), McpTestHost.Token),
            _upstream.CreateHttpClient,
            clientName: "claude-ai");

        var result = await bridge.Client.CallToolAsync("create_item", new Dictionary<string, object?> { ["title"] = "Via the bridge" });

        Assert.False(result.IsError);
        var item = await _upstream.WithDbAsync(db => db.Items.SingleAsync());
        Assert.Equal("Via the bridge", item.Title);
        Assert.Equal("mcp:claude-ai", item.Source);
    }

    [Fact]
    public async Task Passes_tool_errors_through()
    {
        await using var bridge = await BridgeHarness.StartAsync(
            new UpstreamOptions(new Uri(_upstream.CreateHttpClient().BaseAddress!, "mcp"), McpTestHost.Token),
            _upstream.CreateHttpClient);

        var result = await bridge.Client.CallToolAsync("get_item", new Dictionary<string, object?> { ["itemId"] = 404 });

        Assert.True(result.IsError);
        Assert.Contains("does not exist", string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text)));
    }

    [Fact]
    public async Task Explains_when_the_tray_app_is_not_running()
    {
        // Nothing listens on port 1, so the connection is refused.
        await using var bridge = await BridgeHarness.StartAsync(
            new UpstreamOptions(new Uri("http://127.0.0.1:1/mcp"), McpTestHost.Token),
            () => new HttpClient());

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => bridge.Client.ListToolsAsync().AsTask());
        Assert.Contains("Make sure the tray app is running", ex.Message);
    }

    private sealed class BridgeHarness : IAsyncDisposable
    {
        private readonly IHost _host;

        private BridgeHarness(IHost host, McpClient client)
        {
            _host = host;
            Client = client;
        }

        public McpClient Client { get; }

        public static async Task<BridgeHarness> StartAsync(UpstreamOptions options, Func<HttpClient> httpClientFactory, string clientName = "test-downstream")
        {
            var clientToBridge = new Pipe();
            var bridgeToClient = new Pipe();

            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddSingleton(new UpstreamConnection(options, httpClientFactory, new InMemorySecretStore(), NullLoggerFactory.Instance));
            builder.Services.AddBridgeServer().WithStreamServerTransport(clientToBridge.Reader.AsStream(), bridgeToClient.Writer.AsStream());
            var host = builder.Build();
            await host.StartAsync();

            var client = await McpClient.CreateAsync(
                new StreamClientTransport(clientToBridge.Writer.AsStream(), bridgeToClient.Reader.AsStream(), NullLoggerFactory.Instance),
                new McpClientOptions { ClientInfo = new() { Name = clientName, Version = "1.0" } });

            return new BridgeHarness(host, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
