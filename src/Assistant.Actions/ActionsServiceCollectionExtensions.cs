using Assistant.Connectors.Atlassian;
using Assistant.Connectors.GitHub;
using Assistant.Connectors.Microsoft;
using Assistant.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.Actions;

public static class ActionsServiceCollectionExtensions
{
    /// <summary>Adds the source-action tools. Needs the GitHub, Atlassian, and Microsoft connectors registered.</summary>
    public static IServiceCollection AddAssistantActions(this IServiceCollection services)
    {
        services.AddSingleton<GitHubActions>();
        services.AddSingleton<AtlassianActions>();
        services.AddSingleton<AzureDevOpsActions>();
        services.AddSingleton<Shipping.DeploymentFinder>();
        services.AddHttpClient(Shipping.ShippingTools.ProbeClientName);
        services.AddToolType<ActionTools>();
        services.AddToolType<Shipping.ShippingTools>();
        return services;
    }
}
