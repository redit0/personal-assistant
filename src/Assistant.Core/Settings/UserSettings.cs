using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assistant.Core.Settings;

/// <summary>Settings you change from the Settings page. Secrets are not stored here.</summary>
public sealed class UserSettings
{
    public AiSettings AI { get; set; } = new();

    public MicrosoftSettings Microsoft { get; set; } = new();

    public McpSettings Mcp { get; set; } = new();

    public RepoSettings Repos { get; set; } = new();

    public ReminderSettings Reminders { get; set; } = new();

    public GitHubSettings GitHub { get; set; } = new();

    public AtlassianSettings Atlassian { get; set; } = new();

    public AzureDevOpsSettings AzureDevOps { get; set; } = new();

    public OutlookSettings Outlook { get; set; } = new();

    public TriageSettings Triage { get; set; } = new();

    public UiSettings Ui { get; set; } = new();

    public DigestSettings Digest { get; set; } = new();

    public TeamsSettings Teams { get; set; } = new();

    public SlackSettings Slack { get; set; } = new();

    public SharePointSettings SharePoint { get; set; } = new();

    public WorkIqSettings WorkIq { get; set; } = new();

    public UpdateSettings Updates { get; set; } = new();

    public DeploymentSettings Deployments { get; set; } = new();
}

/// <summary>Where each repo's branches deploy, once confirmed. The shipping tools use these before looking anything up.</summary>
public sealed class DeploymentSettings
{
    public List<DeploymentTargetSetting> Targets { get; set; } = [];

    /// <summary>Rules for repos whose branches don't speak for themselves, such as which branch is production.</summary>
    public List<RepoDeploymentRule> Repos { get; set; } = [];
}

/// <summary>How one repo ships, when it differs from "merge into the QA branch".</summary>
public sealed class RepoDeploymentRule
{
    /// <summary>The repo's name from list_repos.</summary>
    public string Repo { get; set; } = "";

    /// <summary>Branches that deploy production. Added to what the assistant finds; never merged into.</summary>
    public List<string> ProductionBranches { get; set; } = [];

    /// <summary>
    /// When set, each feature branch deploys to its own slot of this App Service, named after the branch
    /// (feature/SUP-1580 to sup-1580). Work is tested there and isn't merged; the pull request is
    /// for review, and people merge it into production later.
    /// </summary>
    public string? FeatureSlotApp { get; set; }
}

/// <summary>One branch of one repo and the App Service it deploys to.</summary>
public sealed class DeploymentTargetSetting
{
    /// <summary>The repo's name from list_repos.</summary>
    public string Repo { get; set; } = "";

    public string Branch { get; set; } = "";

    /// <summary>The App Service app, or "app/slot".</summary>
    public string App { get; set; } = "";

    public string Url { get; set; } = "";

    /// <summary>dev, qa, uat, or prod. The tools never merge into a branch whose target is prod.</summary>
    public string Environment { get; set; } = "";

    public string? Notes { get; set; }
}

/// <summary>Updates for the installed app, published as GitHub releases with Velopack.</summary>
public sealed class UpdateSettings
{
    /// <summary>The GitHub repo that hosts the releases, such as https://github.com/you/personal-assistant. Empty turns updates off.</summary>
    public string GitHubRepo { get; set; } = "";

    /// <summary>Check once a day and download in the background. You still choose when to restart.</summary>
    public bool AutoCheck { get; set; } = true;

    public bool IncludePrerelease { get; set; }
}

public sealed class TeamsSettings
{
    /// <summary>One-on-one, group, and meeting chats.</summary>
    public bool ChatsEnabled { get; set; }

    /// <summary>Channel threads that mention you or that you posted in. Needs admin consent.</summary>
    public bool ChannelsEnabled { get; set; }

    /// <summary>How far back the first sync reads.</summary>
    public int BackfillDays { get; set; } = 7;

    /// <summary>Chats with new activity are read most recent first, up to this many per sync.</summary>
    public int MaxChatsPerSync { get; set; } = 40;

    /// <summary>Caps AI calls per sync.</summary>
    public int MaxThreadsPerSync { get; set; } = 30;

    public int IntervalMinutes { get; set; } = 5;
}

public sealed class SlackSettings
{
    public bool Enabled { get; set; }

    /// <summary>Direct messages and group DMs.</summary>
    public bool IncludeDirectMessages { get; set; } = true;

    /// <summary>Messages anywhere that mention you.</summary>
    public bool IncludeMentions { get; set; } = true;

    /// <summary>Channels read in full, beyond mentions.</summary>
    public List<SlackChannelChoice> Channels { get; set; } = [];

    public int BackfillDays { get; set; } = 7;

    public int MaxThreadsPerSync { get; set; } = 30;

    public int IntervalMinutes { get; set; } = 5;
}

public sealed class SlackChannelChoice
{
    public required string Id { get; set; }

    public required string Name { get; set; }
}

public sealed class SharePointSettings
{
    /// <summary>Lets the chat and agents search and read SharePoint and OneDrive files. Needs admin consent.</summary>
    public bool DocumentsEnabled { get; set; }
}

public sealed class WorkIqSettings
{
    /// <summary>Lets the chat and agents ask Microsoft Work IQ. Needs admin setup and a Copilot Credits billing policy.</summary>
    public bool Enabled { get; set; }

    /// <summary>Let Work IQ use web results. Off keeps answers to your work data.</summary>
    public bool WebGrounding { get; set; }
}

public sealed class DigestSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Local time of day for the morning digest, as HH:mm.</summary>
    public string Time { get; set; } = "08:00";

    public bool WeekdaysOnly { get; set; } = true;

    /// <summary>Add a short AI-written overview and top priorities. Uses the triage model.</summary>
    public bool AiSummary { get; set; } = true;

    /// <summary>Waiting-on items older than this many days are called out as worth a nudge.</summary>
    public int StaleWaitingDays { get; set; } = 3;
}

public sealed class UiSettings
{
    /// <summary>Source groups hidden on the Items page, such as "jira". Remembered between runs.</summary>
    public List<string> HiddenItemSources { get; set; } = [];

    /// <summary>How the Items page sorts, by name, such as "Due" or "Newest". Remembered between runs.</summary>
    public string ItemSort { get; set; } = "Due";
}

public sealed class OutlookSettings
{
    /// <summary>Read mail and turn asks, commitments, and things you're waiting on into items.</summary>
    public bool MailEnabled { get; set; }

    public bool IncludeInbox { get; set; } = true;

    /// <summary>Your sent mail, for promises you made.</summary>
    public bool IncludeSent { get; set; } = true;

    /// <summary>Folders beyond the inbox, such as ones your rules file mail into.</summary>
    public List<MailFolderChoice> ExtraFolders { get; set; } = [];

    /// <summary>Messages you flag in Outlook become items, whatever triage thinks.</summary>
    public bool FlaggedEnabled { get; set; }

    public bool CalendarEnabled { get; set; }

    public int CalendarDaysAhead { get; set; } = 14;

    /// <summary>How far back the first mail sync reads.</summary>
    public int BackfillDays { get; set; } = 14;

    /// <summary>Caps AI calls per sync. A large backlog is worked through over several syncs.</summary>
    public int MaxThreadsPerSync { get; set; } = 40;

    public int IntervalMinutes { get; set; } = 5;

    /// <summary>How drafted replies end. Empty means "Thanks," and your first name.</summary>
    public string EmailSignOff { get; set; } = "";
}

public sealed class MailFolderChoice
{
    public required string Id { get; set; }

    public required string Name { get; set; }
}

public sealed class TriageSettings
{
    /// <summary>
    /// Model for sorting mail and calendar events. Only used with the Anthropic provider; other providers
    /// use the main model.
    /// </summary>
    public string Model { get; set; } = "claude-sonnet-5-5";

    /// <summary>Who you are and what matters to you, in your own words. Sent with every triage request.</summary>
    public string Profile { get; set; } = "";

    /// <summary>Items the model is less sure about than this go to Needs a look.</summary>
    public double ReviewThreshold { get; set; } = 0.6;

    /// <summary>Senders (addresses or @domains) never sent to triage.</summary>
    public List<string> MutedSenders { get; set; } = [];
}

public sealed class GitHubSettings
{
    public bool Enabled { get; set; }

    /// <summary>Use the GitHub CLI's login (gh auth token) when no token is saved.</summary>
    public bool UseGhCli { get; set; } = true;

    public int IntervalMinutes { get; set; } = 5;
}

public sealed class AtlassianSettings
{
    /// <summary>Your Atlassian account email, used with API tokens for Jira and Bitbucket.</summary>
    public string? Email { get; set; }

    public bool JiraEnabled { get; set; }

    /// <summary>Such as https://yourcompany.atlassian.net.</summary>
    public string? JiraSiteUrl { get; set; }

    /// <summary>Only track Jira issues in a sprint that's currently open. Issues in Kanban projects have no sprint and are left out.</summary>
    public bool JiraActiveSprintOnly { get; set; } = true;

    public bool BitbucketEnabled { get; set; }

    /// <summary>Only repos updated within this many days are checked for review requests.</summary>
    public int BitbucketActiveRepoDays { get; set; } = 30;

    public int IntervalMinutes { get; set; } = 10;
}

public sealed class AzureDevOpsSettings
{
    public bool Enabled { get; set; }

    /// <summary>Whether organizations discovered later are watched without asking.</summary>
    public bool WatchNewOrganizations { get; set; } = true;

    /// <summary>Every organization seen so far, and whether it's watched.</summary>
    public Dictionary<string, bool> Organizations { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Where each organization's sign-in comes from: <see cref="HomeSource"/>, a client tenant
    /// (<see cref="TenantSource"/>), or <see cref="TokenSource"/>. Missing means home.
    /// </summary>
    public Dictionary<string, string> OrganizationSources { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Client tenants where you're a guest, reached through the Azure CLI login.</summary>
    public List<AzureDevOpsTenant> Tenants { get; set; } = [];

    public int IntervalMinutes { get; set; } = 5;

    public const string HomeSource = "home";
    public const string TokenSource = "token";

    public static string TenantSource(string tenantId) => $"tenant:{tenantId}";
}

public sealed class AzureDevOpsTenant
{
    /// <summary>Tenant GUID or domain, such as contoso.onmicrosoft.com.</summary>
    public required string TenantId { get; set; }

    /// <summary>A name you recognize, such as the client's name.</summary>
    public string? Label { get; set; }
}

public enum AiProvider
{
    None,
    Anthropic,
    OpenAI,
    AzureOpenAI,
    Ollama,
}

public sealed class AiSettings
{
    public AiProvider Provider { get; set; } = AiProvider.None;

    /// <summary>Model ID for Anthropic, OpenAI, or Ollama; deployment name for Azure OpenAI.</summary>
    public string? Model { get; set; }

    /// <summary>Endpoint for Azure OpenAI or Ollama.</summary>
    public string? Endpoint { get; set; }

    public int MaxOutputTokens { get; set; } = 16000;
}

public sealed class MicrosoftSettings
{
    /// <summary>Application (client) ID of your Entra app registration.</summary>
    public string? ClientId { get; set; }

    /// <summary>Directory (tenant) ID. "organizations" works for any work account.</summary>
    public string TenantId { get; set; } = "organizations";
}

public sealed class McpSettings
{
    public bool Enabled { get; set; } = true;

    public int Port { get; set; } = 5199;
}

public sealed class RepoSettings
{
    /// <summary>Folders scanned for git clones, such as D:\src.</summary>
    public List<string> ScanRoots { get; set; } = [];
}

public sealed class ReminderSettings
{
    /// <summary>How far ahead of an item's due time to show a reminder.</summary>
    public int LeadTimeMinutes { get; set; } = 120;
}

/// <summary>Loads and saves <see cref="UserSettings"/> as JSON.</summary>
public sealed class UserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Lock _lock = new();
    private UserSettings _current;

    public UserSettingsStore(string path)
    {
        _path = path;
        _current = Load(path);
    }

    /// <summary>Raised after settings are saved.</summary>
    public event Action<UserSettings>? Changed;

    public UserSettings Current
    {
        get
        {
            lock (_lock)
            {
                return Clone(_current);
            }
        }
    }

    public void Save(UserSettings settings)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
            _current = Clone(settings);
        }

        Changed?.Invoke(Current);
    }

    public void Update(Action<UserSettings> change)
    {
        var settings = Current;
        change(settings);
        Save(settings);
    }

    private static UserSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new UserSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path), JsonOptions) ?? new UserSettings();
        }
        catch (JsonException)
        {
            // A hand-edited file with a typo should not stop the app from starting.
            File.Copy(path, path + ".bad", overwrite: true);
            return new UserSettings();
        }
    }

    private static UserSettings Clone(UserSettings settings) =>
        JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions)!;
}
