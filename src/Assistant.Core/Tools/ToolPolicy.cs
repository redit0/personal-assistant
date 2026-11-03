namespace Assistant.Core.Tools;

/// <summary>What happens when a tool is called.</summary>
public enum ToolPolicy
{
    /// <summary>Run without asking.</summary>
    Allow,

    /// <summary>Show an approval prompt in the tray app first.</summary>
    Ask,

    /// <summary>Never run.</summary>
    Deny,
}

/// <summary>Broad grouping that decides a tool's default policy.</summary>
public enum ToolCategory
{
    /// <summary>Reads data. Default: Allow.</summary>
    Read,

    /// <summary>Changes the assistant's own item list. Default: Allow.</summary>
    ItemManagement,

    /// <summary>Changes something outside the assistant (a PR, a ticket, a message). Default: Ask.</summary>
    SourceAction,
}

/// <summary>Your override of a tool's default policy.</summary>
public class ToolPolicySetting
{
    public required string ToolName { get; set; }

    public ToolPolicy Policy { get; set; }
}
