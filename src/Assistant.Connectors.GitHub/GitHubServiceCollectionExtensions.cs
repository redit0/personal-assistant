using Assistant.Core.Connectors;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.Connectors.GitHub;

public static class GitHubServiceCollectionExtensions
{
    public static IServiceCollection AddGitHubConnector(this IServiceCollection services)
    {
        services.AddHttpClient(GitHubConnector.HttpClientName, c => c.BaseAddress = new Uri("https://api.github.com/"));
        services.AddSingleton<IGitHubCliToken, GitHubCliToken>();
        services.AddSingleton<GitHubConnector>();
        services.AddSingleton<IConnector>(sp => sp.GetRequiredService<GitHubConnector>());
        return services;
    }
}
