using Assistant.Agent.Triage;
using Assistant.Connectors.Slack;
using Assistant.Core.Connectors;
using Assistant.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.Agent;

public static class AgentServiceCollectionExtensions
{
    public static IServiceCollection AddAssistantAgent(this IServiceCollection services)
    {
        services.AddSingleton<ChatClientFactory>();
        services.AddSingleton<ITriageClientSource>(sp => sp.GetRequiredService<ChatClientFactory>());

        // One chat conversation per app run. The chat page keeps it while you switch pages.
        services.AddSingleton<ChatSession>();

        services.AddSingleton<TriageService>();
        services.AddSingleton<ThreadTriageEngine>();
        services.AddSingleton<Tools.Sync.IDuplicateDetector, DuplicateFinder>();
        services.AddToolType<Calendar.CalendarTools>();
        services.AddToolType<Documents.DocumentTools>();
        services.AddSingleton<Digest.DigestBuilder>();
        services.AddToolType<Digest.DigestTools>();
        services.AddSingleton<Digest.DigestService>();
        services.AddHostedService(sp => sp.GetRequiredService<Digest.DigestService>());
        services.AddSingleton<OutlookMailConnector>();
        services.AddSingleton<OutlookFlaggedConnector>();
        services.AddSingleton<OutlookCalendarConnector>();
        services.AddSingleton<IConnector>(sp => sp.GetRequiredService<OutlookMailConnector>());
        services.AddSingleton<IConnector>(sp => sp.GetRequiredService<OutlookFlaggedConnector>());
        services.AddSingleton<IConnector>(sp => sp.GetRequiredService<OutlookCalendarConnector>());
        services.AddSingleton<TeamsConnector>();
        services.AddSingleton<IConnector>(sp => sp.GetRequiredService<TeamsConnector>());
        services.AddSlackClient();
        services.AddSingleton<SlackConnector>();
        services.AddSingleton<IConnector>(sp => sp.GetRequiredService<SlackConnector>());
        return services;
    }
}
