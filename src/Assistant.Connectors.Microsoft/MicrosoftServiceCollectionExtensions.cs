using Assistant.Core.Connectors;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.Connectors.Microsoft;

public static class MicrosoftServiceCollectionExtensions
{
    public static IServiceCollection AddMicrosoftConnectors(this IServiceCollection services)
    {
        services.AddSingleton<MicrosoftAuthService>();
        services.AddSingleton<IMicrosoftTokenProvider>(sp => sp.GetRequiredService<MicrosoftAuthService>());
        services.AddHttpClient(AzureDevOpsConnector.HttpClientName);
        services.AddHttpClient(GraphClient.HttpClientName);
        services.AddSingleton<GraphClient>();
        services.AddHttpClient(WorkIqClient.HttpClientName);
        services.AddSingleton<WorkIqClient>();
        services.AddSingleton<IAzureCliToken, AzureCliToken>();
        services.AddHttpClient(AzureAppServices.HttpClientName);
        services.AddSingleton<AzureAppServices>();
        services.AddSingleton<AzureDevOpsConnector>();
        services.AddSingleton<IConnector>(sp => sp.GetRequiredService<AzureDevOpsConnector>());
        return services;
    }
}
