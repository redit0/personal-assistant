using Assistant.Core.Secrets;
using Assistant.Data;
using Assistant.Tools;
using Assistant.Tools.Catalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace Assistant.Mcp.Tests;

/// <summary>Runs the real MCP server in memory against an in-memory SQLite database.</summary>
public sealed class McpTestHost : IAsyncDisposable
{
    public const string Token = "test-token";

    private readonly SqliteConnection _connection;
    private readonly WebApplication _app;

    private McpTestHost(SqliteConnection connection, WebApplication app)
    {
        _connection = connection;
        _app = app;
    }

    public required FakeApprovalBroker Approvals { get; init; }

    public IServiceProvider Services => _app.Services;

    public HttpClient CreateHttpClient() => _app.GetTestClient();

    public static async Task<McpTestHost> StartAsync(Action<IServiceCollection>? configure = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var approvals = new FakeApprovalBroker();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        var secrets = new InMemorySecretStore();
        secrets.Set(SecretNames.McpToken, Token);

        builder.Services.AddSingleton<ISecretStore>(secrets);
        builder.Services.AddSingleton<IApprovalBroker>(approvals);
        builder.Services.AddAssistantData(connection);
        builder.Services.AddAssistantTools();
        builder.Services.AddAssistantMcp();
        configure?.Invoke(builder.Services);

        var app = builder.Build();
        app.MapAssistantMcp();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AssistantDbContext>().Database.MigrateAsync();
        }

        await app.StartAsync();
        return new McpTestHost(connection, app) { Approvals = approvals };
    }

    public async Task<McpClient> ConnectAsync(string clientName = "test-client", IDictionary<string, string>? headers = null)
    {
        var http = CreateHttpClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(http.BaseAddress!, "mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>(headers ?? new Dictionary<string, string>()) { ["Authorization"] = $"Bearer {Token}" },
            },
            http,
            ownsHttpClient: true);

        return await McpClient.CreateAsync(
            transport,
            new McpClientOptions { ClientInfo = new() { Name = clientName, Version = "1.0" } });
    }

    public async Task<T> WithDbAsync<T>(Func<AssistantDbContext, Task<T>> action)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AssistantDbContext>());
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

public sealed class FakeApprovalBroker : IApprovalBroker
{
    public bool Answer { get; set; }

    public List<ApprovalRequest> Requests { get; } = [];

    public Task<bool> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(Answer);
    }
}
