using Meziantou.Framework.Win32;

namespace Assistant.Core.Secrets;

/// <summary>Stores API keys and tokens outside the settings file.</summary>
public interface ISecretStore
{
    string? Get(string name);

    void Set(string name, string value);

    void Delete(string name);
}

/// <summary>Well-known secret names.</summary>
public static class SecretNames
{
    public const string McpToken = "Mcp:Token";

    public const string GitHubToken = "GitHub:Token";

    public const string JiraApiToken = "Jira:ApiToken";

    public const string BitbucketApiToken = "Bitbucket:ApiToken";

    public const string SlackToken = "Slack:UserToken";

    /// <summary>Optional token for a private releases repo. Falls back to the GitHub connector token.</summary>
    public const string UpdateToken = "Updates:GitHubToken";

    public static string AiApiKey(string provider) => $"AI:{provider}:ApiKey";

    public static string AzureDevOpsToken(string organization) => $"AzureDevOps:Pat:{organization.ToLowerInvariant()}";
}

/// <summary>Keeps secrets in Windows Credential Manager, scoped to the current Windows user.</summary>
public sealed class WindowsCredentialSecretStore : ISecretStore
{
    private const string Prefix = "PersonalAssistant:";

    public string? Get(string name) =>
        CredentialManager.ReadCredential(Prefix + name)?.Password;

    public void Set(string name, string value) =>
        CredentialManager.WriteCredential(
            Prefix + name,
            userName: Environment.UserName,
            secret: value,
            persistence: CredentialPersistence.LocalMachine);

    public void Delete(string name)
    {
        if (CredentialManager.ReadCredential(Prefix + name) is not null)
        {
            CredentialManager.DeleteCredential(Prefix + name);
        }
    }
}

/// <summary>In-memory store for tests.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = [];

    public string? Get(string name) => _values.GetValueOrDefault(name);

    public void Set(string name, string value) => _values[name] = value;

    public void Delete(string name) => _values.Remove(name);
}
