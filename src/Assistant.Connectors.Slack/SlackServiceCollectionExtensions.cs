using Microsoft.Extensions.DependencyInjection;

namespace Assistant.Connectors.Slack;

public static class SlackServiceCollectionExtensions
{
    /// <summary>The Slack API client. The connector itself lives with triage, since it shares the conversation engine.</summary>
    public static IServiceCollection AddSlackClient(this IServiceCollection services)
    {
        services.AddHttpClient(SlackClient.HttpClientName);
        services.AddSingleton<SlackClient>();
        return services;
    }
}
