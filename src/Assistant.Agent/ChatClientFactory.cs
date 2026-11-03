using System.ClientModel;
using Anthropic;
using Assistant.Core.Secrets;
using Assistant.Core.Settings;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OllamaSharp;
using OpenAI;

namespace Assistant.Agent;

/// <summary>
/// Builds the <see cref="IChatClient"/> for whichever provider is selected in Settings, and rebuilds it
/// when settings change. Everything else in the app only sees <see cref="IChatClient"/>.
/// </summary>
/// <summary>Where triage gets its model. Lets tests supply a scripted one.</summary>
public interface ITriageClientSource
{
    IChatClient? GetTriageClient();

    string? UnavailableReason(AiSettings? ai = null);
}

public sealed class ChatClientFactory : ITriageClientSource, IDisposable
{
    public const string DefaultAnthropicModel = "claude-opus-5-5";
    public const string DefaultOpenAIModel = "gpt-5";
    public const string DefaultOllamaModel = "llama3.3";
    public const string DefaultOllamaEndpoint = "http://localhost:11434";

    private readonly UserSettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, IChatClient> _clients = [];

    public ChatClientFactory(UserSettingsStore settings, ISecretStore secrets, ILoggerFactory loggerFactory)
    {
        _settings = settings;
        _secrets = secrets;
        _loggerFactory = loggerFactory;
        _settings.Changed += _ => Reset();
    }

    /// <summary>Why no client is available, or null when one is.</summary>
    public string? UnavailableReason(AiSettings? ai = null)
    {
        ai ??= _settings.Current.AI;
        return ai.Provider switch
        {
            AiProvider.None => "No AI provider is selected. Choose one in Settings.",
            AiProvider.AzureOpenAI when string.IsNullOrWhiteSpace(ai.Endpoint) => "Azure OpenAI needs an endpoint URL.",
            AiProvider.AzureOpenAI when string.IsNullOrWhiteSpace(ai.Model) => "Azure OpenAI needs a deployment name.",
            AiProvider.Anthropic or AiProvider.OpenAI or AiProvider.AzureOpenAI
                when string.IsNullOrEmpty(_secrets.Get(SecretNames.AiApiKey(ai.Provider.ToString()))) =>
                $"{ai.Provider} needs an API key.",
            _ => null,
        };
    }

    /// <summary>The raw provider client, without function invocation. Null if the provider isn't configured.</summary>
    public IChatClient? GetClient() => GetClient(_settings.Current.AI);

    /// <summary>
    /// The client for mail and calendar triage. With Anthropic it uses the triage model from Settings
    /// (Sonnet by default); other providers use their main model.
    /// </summary>
    public IChatClient? GetTriageClient()
    {
        var current = _settings.Current;
        var ai = current.AI;
        if (ai.Provider == AiProvider.Anthropic && !string.IsNullOrWhiteSpace(current.Triage.Model))
        {
            ai = new AiSettings { Provider = ai.Provider, Model = current.Triage.Model, Endpoint = ai.Endpoint, MaxOutputTokens = ai.MaxOutputTokens };
        }

        return GetClient(ai);
    }

    private IChatClient? GetClient(AiSettings ai)
    {
        if (UnavailableReason(ai) is not null)
        {
            return null;
        }

        var key = $"{ai.Provider}|{ai.Model}|{ai.Endpoint}|{ai.MaxOutputTokens}";
        lock (_lock)
        {
            if (!_clients.TryGetValue(key, out var client))
            {
                client = _clients[key] = Create(ai);
            }

            return client;
        }
    }

    public void Dispose() => Reset();

    private void Reset()
    {
        lock (_lock)
        {
            foreach (var client in _clients.Values)
            {
                client.Dispose();
            }

            _clients.Clear();
        }
    }

    private IChatClient Create(AiSettings ai)
    {
        var apiKey = _secrets.Get(SecretNames.AiApiKey(ai.Provider.ToString()));

        IChatClient inner = ai.Provider switch
        {
            AiProvider.Anthropic => new AnthropicClient { ApiKey = apiKey! }
                .AsIChatClient(Model(ai, DefaultAnthropicModel), ai.MaxOutputTokens),

            AiProvider.OpenAI => new OpenAIClient(apiKey!)
                .GetChatClient(Model(ai, DefaultOpenAIModel))
                .AsIChatClient(),

            AiProvider.AzureOpenAI => new AzureOpenAIClient(new Uri(ai.Endpoint!), new ApiKeyCredential(apiKey!))
                .GetChatClient(ai.Model!)
                .AsIChatClient(),

            AiProvider.Ollama => new OllamaApiClient(
                new Uri(string.IsNullOrWhiteSpace(ai.Endpoint) ? DefaultOllamaEndpoint : ai.Endpoint),
                Model(ai, DefaultOllamaModel)),

            _ => throw new InvalidOperationException($"Unsupported provider {ai.Provider}."),
        };

        return inner.AsBuilder().UseLogging(_loggerFactory).Build();
    }

    private static string Model(AiSettings ai, string fallback) =>
        string.IsNullOrWhiteSpace(ai.Model) ? fallback : ai.Model;
}
