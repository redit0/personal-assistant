using Assistant.Core.Connectors;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.Connectors.Atlassian;

public static class AtlassianServiceCollectionExtensions
{
    public static IServiceCollection AddAtlassianConnectors(this IServiceCollection services)
    {
        services.AddHttpClient(JiraConnector.HttpClientName);
        services.AddHttpClient(BitbucketConnector.HttpClientName);
        services.AddSingleton<JiraConnector>();
        services.AddSingleton<BitbucketConnector>();
        services.AddSingleton<IConnector>(sp => sp.GetRequiredService<JiraConnector>());
        services.AddSingleton<IConnector>(sp => sp.GetRequiredService<BitbucketConnector>());
        return services;
    }
}
