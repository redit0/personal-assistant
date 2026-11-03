using Assistant.Core.Tools;
using Assistant.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.Tools.Catalog;

public interface IToolPolicyStore
{
    /// <summary>Your override if you set one, otherwise the tool's default.</summary>
    Task<ToolPolicy> GetEffectivePolicyAsync(ToolDefinition tool, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, ToolPolicy>> GetOverridesAsync(CancellationToken cancellationToken = default);

    /// <summary>Pass null to go back to the default.</summary>
    Task SetOverrideAsync(string toolName, ToolPolicy? policy, CancellationToken cancellationToken = default);
}

public sealed class DbToolPolicyStore(IServiceScopeFactory scopes) : IToolPolicyStore
{
    public async Task<ToolPolicy> GetEffectivePolicyAsync(ToolDefinition tool, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
        var setting = await db.ToolPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.ToolName == tool.Name, cancellationToken);
        return setting?.Policy ?? tool.DefaultPolicy;
    }

    public async Task<IReadOnlyDictionary<string, ToolPolicy>> GetOverridesAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
        return await db.ToolPolicies.AsNoTracking().ToDictionaryAsync(p => p.ToolName, p => p.Policy, cancellationToken);
    }

    public async Task SetOverrideAsync(string toolName, ToolPolicy? policy, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
        var existing = await db.ToolPolicies.FindAsync([toolName], cancellationToken);

        if (policy is null)
        {
            if (existing is not null)
            {
                db.ToolPolicies.Remove(existing);
            }
        }
        else if (existing is null)
        {
            db.ToolPolicies.Add(new ToolPolicySetting { ToolName = toolName, Policy = policy.Value });
        }
        else
        {
            existing.Policy = policy.Value;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
