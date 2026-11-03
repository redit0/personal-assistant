// Stdio bridge for MCP clients that can only launch local servers, such as Claude Desktop extensions.
// It forwards every request to the tray app's HTTP endpoint, so policies, approvals, and the audit log
// all still apply. stdout carries the MCP protocol; logs go to stderr.

using Assistant.Core.Secrets;
using Assistant.Mcp.Stdio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// The packaging script asks for the prompts in manifest form: Claude Desktop only allows declared prompts.
if (args.Contains("--manifest-prompts"))
{
    var prompts = Assistant.Core.Agents.PromptTemplates.All.Select(p => new
    {
        name = p.Name,
        description = p.Description,
        arguments = p.Arguments.Select(a => a.Name).ToArray(),
        text = p.ManifestText(),
    });
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(prompts));
    return;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// The upstream client warns when a tools/call arrives before it has cached the tool list. The bridge
// forwards raw requests, so that cache is never filled and the warning means nothing here.
builder.Logging.AddFilter("ModelContextProtocol.Client.McpClient", LogLevel.Error);

builder.Services.AddSingleton<ISecretStore, WindowsCredentialSecretStore>();
builder.Services.AddSingleton(sp => new UpstreamConnection(
    UpstreamOptions.FromEnvironment(),
    () => new HttpClient(),
    sp.GetRequiredService<ISecretStore>(),
    sp.GetRequiredService<ILoggerFactory>()));

builder.Services.AddBridgeServer().WithStdioServerTransport();

await builder.Build().RunAsync();
