using Assistant.Tools.Audit;
using Assistant.Tools.Catalog;
using Assistant.Tools.Items;
using Assistant.Tools.Repos;
using Assistant.Tools.Reminders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Assistant.Tools;

public static class ToolsServiceCollectionExtensions
{
    /// <summary>Types whose <see cref="AssistantToolAttribute"/> methods make up the catalog.</summary>
    public static readonly Type[] ToolTypes = [typeof(ItemTools), typeof(RepoTools), typeof(AuditTools), typeof(Workflows.WorkflowTools)];

    /// <summary>
    /// Registers the tool catalog and everything around it. Register an <see cref="IApprovalBroker"/>
    /// to show approval prompts; without one, calls that need approval are denied.
    /// </summary>
    public static IServiceCollection AddAssistantTools(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IApprovalBroker, DenyAllApprovalBroker>();
        services.TryAddSingleton<Sync.IDuplicateDetector, Sync.NoDuplicateDetector>();
        services.AddSingleton(sp => new ToolCatalog(ToolCatalog.FromTypes(
            [.. ToolTypes, .. sp.GetServices<ToolTypeRegistration>().Select(r => r.Type)])));
        services.AddSingleton<IToolPolicyStore, DbToolPolicyStore>();
        services.AddSingleton<IAuditLog, DbAuditLog>();
        services.AddSingleton<ToolInvoker>();
        services.AddSingleton<HandoffRegistry>();
        services.AddScoped<ToolCallContext>();
        services.AddScoped<RepoScanner>();
        return services;
    }

    /// <summary>Adds another class's <see cref="AssistantToolAttribute"/> methods to the catalog, from any project.</summary>
    public static IServiceCollection AddToolType<T>(this IServiceCollection services) where T : class =>
        services.AddSingleton(new ToolTypeRegistration(typeof(T)));

    public static IServiceCollection AddAssistantReminders(this IServiceCollection services) =>
        services.AddHostedService<ReminderService>();

    /// <summary>Runs every registered <see cref="Core.Connectors.IConnector"/> in the background.</summary>
    public static IServiceCollection AddAssistantSync(this IServiceCollection services)
    {
        services.AddSingleton<Sync.ConnectorSyncService>();
        services.AddHostedService(sp => sp.GetRequiredService<Sync.ConnectorSyncService>());
        return services;
    }
}

/// <summary>A class contributing tools to the catalog. Registered with <see cref="ToolsServiceCollectionExtensions.AddToolType{T}"/>.</summary>
public sealed record ToolTypeRegistration(Type Type);
