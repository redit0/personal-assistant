using System.Diagnostics;
using System.Text.Json;
using Assistant.Core.Audit;
using Assistant.Core.Items;
using Assistant.Core.Tools;
using Assistant.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Assistant.Tools.Catalog;

/// <summary>The outcome of a tool call.</summary>
public sealed record ToolResult(bool Success, object? Value, string? Error)
{
    public static ToolResult Ok(object? value) => new(true, value, null);

    public static ToolResult Fail(string error) => new(false, null, error);

    public string ToJson() => Success
        ? JsonSerializer.Serialize(Value, ToolJson.Options)
        : JsonSerializer.Serialize(new { error = Error }, ToolJson.Options);
}

/// <summary>
/// Every tool call goes through here, whichever surface it came from. In order: policy check,
/// trust escalation, approval prompt, the call itself in a fresh DI scope, then an audit entry.
/// </summary>
public sealed class ToolInvoker(
    ToolCatalog catalog,
    IToolPolicyStore policies,
    IApprovalBroker approvals,
    IAuditLog audit,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<ToolInvoker> logger)
{
    /// <summary>Raised after a tool that changes data succeeds, so open views can refresh.</summary>
    public event Action<ToolDefinition, ToolCaller>? DataChanged;

    /// <summary>Raised after every call is audited, whatever the outcome.</summary>
    public event Action<AuditEntry>? Audited;

    public async Task<ToolResult> InvokeAsync(
        string toolName,
        IDictionary<string, object?>? arguments,
        ToolCaller caller,
        CancellationToken cancellationToken = default)
    {
        var tool = catalog.Find(toolName);
        if (tool is null)
        {
            return ToolResult.Fail($"Unknown tool '{toolName}'.");
        }

        arguments ??= new Dictionary<string, object?>();
        var started = Stopwatch.GetTimestamp();
        var argumentsJson = SerializeArguments(arguments);
        var entry = new AuditEntry
        {
            Timestamp = time.GetUtcNow().UtcDateTime,
            Caller = caller.Id,
            Tool = tool.Name,
            ArgumentsJson = argumentsJson,
        };

        var policy = await policies.GetEffectivePolicyAsync(tool, cancellationToken);
        string? reason = null;

        if (policy == ToolPolicy.Allow && tool.Category == ToolCategory.SourceAction)
        {
            reason = await GetTrustEscalationReasonAsync(arguments, cancellationToken);
            if (reason is not null)
            {
                policy = ToolPolicy.Ask;
            }
        }

        if (policy == ToolPolicy.Allow && tool.AlwaysAsk)
        {
            policy = ToolPolicy.Ask;
            reason ??= "This tool always asks first, because it speaks for you.";
        }

        // You handed this session the item and are watching it work, so calls about that item don't ask again.
        // Deny still blocks, and calls about any other item still ask.
        if (policy == ToolPolicy.Ask && IsHandedOff(caller, arguments))
        {
            policy = ToolPolicy.Allow;
        }

        switch (policy)
        {
            case ToolPolicy.Deny:
                return await FinishAsync(entry, started, AuditDecision.Blocked,
                    ToolResult.Fail($"Blocked: the {tool.Name} tool is set to Deny in the assistant's settings."));

            case ToolPolicy.Ask:
                var request = new ApprovalRequest(tool.Name, tool.Title, tool.Category, caller, argumentsJson, reason);
                if (!await approvals.RequestApprovalAsync(request, cancellationToken))
                {
                    return await FinishAsync(entry, started, AuditDecision.Denied,
                        ToolResult.Fail("Denied: the user did not approve this call."));
                }

                entry.Decision = AuditDecision.Approved;
                break;

            default:
                entry.Decision = AuditDecision.Allowed;
                break;
        }

        ToolResult result;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ToolCallContext>().Caller = caller;

            var functionArguments = new AIFunctionArguments(arguments) { Services = scope.ServiceProvider };
            result = ToolResult.Ok(await tool.Function.InvokeAsync(functionArguments, cancellationToken));
        }
        catch (ToolException ex)
        {
            result = ToolResult.Fail(ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException)
        {
            result = ToolResult.Fail($"Invalid arguments: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Tool {Tool} failed for {Caller}", tool.Name, caller.Id);
            result = ToolResult.Fail($"The {tool.Name} tool failed: {ex.Message}");
        }

        result = await FinishAsync(entry, started, entry.Decision, result);
        if (result.Success && !tool.ReadOnly)
        {
            DataChanged?.Invoke(tool, caller);
        }

        return result;
    }

    private async Task<ToolResult> FinishAsync(AuditEntry entry, long started, AuditDecision decision, ToolResult result)
    {
        entry.Decision = decision;
        entry.Succeeded = result.Success;
        entry.Error = result.Error;
        entry.ResultSummary = result.Success ? result.ToJson() : null;
        entry.DurationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        try
        {
            await audit.RecordAsync(entry);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not write audit entry for {Tool}", entry.Tool);
        }

        Audited?.Invoke(entry);
        return result;
    }

    /// <summary>
    /// Actions tied to an item from an external or unknown sender always need your approval,
    /// because that sender may have written the request to manipulate an agent.
    /// </summary>
    private async Task<string?> GetTrustEscalationReasonAsync(IDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetValue("itemId", out var raw) || !TryGetInt(raw, out var itemId))
        {
            return null;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
        var item = await db.Items.AsNoTracking()
            .Where(i => i.Id == itemId)
            .Select(i => new { i.Trust, i.Requester })
            .FirstOrDefaultAsync(cancellationToken);

        return item?.Trust is SenderTrust.External or SenderTrust.Unknown
            ? $"Item {itemId} came from {(item.Requester ?? "an unknown sender")}, who is outside your organization."
            : null;
    }

    /// <summary>A call from a handed-off session about its own item, or about no item at all, such as opening its pull request.</summary>
    private static bool IsHandedOff(ToolCaller caller, IDictionary<string, object?> arguments)
    {
        if (caller.HandoffItemId is not { } handedOff)
        {
            return false;
        }

        if (!arguments.TryGetValue("itemId", out var raw) || raw is null || raw is JsonElement { ValueKind: JsonValueKind.Null })
        {
            return true;
        }

        return TryGetInt(raw, out var itemId) && itemId == handedOff;
    }

    private static bool TryGetInt(object? value, out int result)
    {
        switch (value)
        {
            case int i:
                result = i;
                return true;
            case long l when l is >= int.MinValue and <= int.MaxValue:
                result = (int)l;
                return true;
            case JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetInt32(out var n):
                result = n;
                return true;
            case JsonElement { ValueKind: JsonValueKind.String } e when int.TryParse(e.GetString(), out var s):
                result = s;
                return true;
            case string str when int.TryParse(str, out var p):
                result = p;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static string SerializeArguments(IDictionary<string, object?> arguments)
    {
        try
        {
            return JsonSerializer.Serialize(arguments, ToolJson.Options);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            return "{\"error\":\"arguments could not be serialized\"}";
        }
    }
}

/// <summary>
/// Wraps a catalog tool for an in-process <see cref="IChatClient"/> so the chat agent gets the
/// same policy checks, approvals, and auditing as MCP clients.
/// </summary>
public sealed class GuardedAIFunction(ToolDefinition tool, ToolInvoker invoker, ToolCaller caller) : DelegatingAIFunction(tool.Function)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var result = await invoker.InvokeAsync(tool.Name, new Dictionary<string, object?>(arguments), caller, cancellationToken);
        return result.Success ? result.Value : new { error = result.Error };
    }
}
