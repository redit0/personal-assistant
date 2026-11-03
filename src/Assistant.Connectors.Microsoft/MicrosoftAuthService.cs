using Assistant.Core;
using Assistant.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Assistant.Connectors.Microsoft;

/// <summary>
/// Signs you in to Microsoft 365 with MSAL and the Windows account broker (WAM), so the app never
/// sees your password. Later connectors (Graph mail, calendar, Teams, Azure DevOps) get tokens from here.
/// </summary>
/// <summary>Access tokens for Microsoft APIs. Lets connectors be tested without a real sign-in.</summary>
public interface IMicrosoftTokenProvider
{
    bool IsConfigured { get; }

    Task<string> GetAccessTokenAsync(IEnumerable<string> scopes, CancellationToken cancellationToken = default);
}

public sealed class MicrosoftAuthService(UserSettingsStore settings, ILogger<MicrosoftAuthService> logger) : IMicrosoftTokenProvider
{
    /// <summary>Scopes requested at sign-in. Connectors ask for more incrementally as they are added.</summary>
    public static readonly string[] SignInScopes = ["User.Read"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPublicClientApplication? _app;
    private string? _appKey;

    /// <summary>Supplies the window handle the broker parents its sign-in dialog to.</summary>
    public Func<IntPtr>? WindowHandleProvider { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.Current.Microsoft.ClientId);

    public async Task<IAccount?> GetAccountAsync()
    {
        var app = await GetAppAsync();
        return app is null ? null : (await app.GetAccountsAsync()).FirstOrDefault();
    }

    public async Task<AuthenticationResult> SignInAsync(CancellationToken cancellationToken = default)
    {
        var app = await GetAppAsync() ?? throw new InvalidOperationException("Set the Entra application (client) ID in Settings first.");
        var account = (await app.GetAccountsAsync()).FirstOrDefault();

        try
        {
            return await app.AcquireTokenSilent(SignInScopes, account ?? PublicClientApplication.OperatingSystemAccount)
                .ExecuteAsync(cancellationToken);
        }
        catch (MsalUiRequiredException)
        {
            return await app.AcquireTokenInteractive(SignInScopes)
                .WithAccount(account)
                .WithPrompt(Prompt.SelectAccount)
                .ExecuteAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Asks you to sign in (or consent) for extra scopes, such as Azure DevOps. Needed once per scope,
    /// after which <see cref="GetAccessTokenAsync"/> works silently.
    /// </summary>
    public async Task ConsentAsync(IEnumerable<string> scopes, CancellationToken cancellationToken = default)
    {
        var app = await GetAppAsync() ?? throw new InvalidOperationException("Set the Entra application (client) ID in Settings first.");
        var account = (await app.GetAccountsAsync()).FirstOrDefault();
        var scopeList = scopes.ToArray();

        try
        {
            if (account is not null)
            {
                await app.AcquireTokenSilent(scopeList, account).ExecuteAsync(cancellationToken);
                return;
            }
        }
        catch (MsalUiRequiredException)
        {
        }

        await app.AcquireTokenInteractive(scopeList).WithAccount(account).ExecuteAsync(cancellationToken);
    }

    /// <summary>Gets a token without prompting. Throws <see cref="MsalUiRequiredException"/> if you need to sign in again.</summary>
    public async Task<string> GetAccessTokenAsync(IEnumerable<string> scopes, CancellationToken cancellationToken = default)
    {
        var app = await GetAppAsync() ?? throw new InvalidOperationException("Microsoft sign-in is not configured.");
        var account = (await app.GetAccountsAsync()).FirstOrDefault()
            ?? throw new MsalUiRequiredException(MsalError.UserNullError, "Not signed in.");

        var result = await app.AcquireTokenSilent(scopes, account).ExecuteAsync(cancellationToken);
        return result.AccessToken;
    }

    public async Task SignOutAsync()
    {
        var app = await GetAppAsync();
        if (app is null)
        {
            return;
        }

        foreach (var account in await app.GetAccountsAsync())
        {
            await app.RemoveAsync(account);
        }
    }

    private async Task<IPublicClientApplication?> GetAppAsync()
    {
        var ms = settings.Current.Microsoft;
        if (string.IsNullOrWhiteSpace(ms.ClientId))
        {
            return null;
        }

        var key = $"{ms.ClientId}|{ms.TenantId}";
        await _gate.WaitAsync();
        try
        {
            if (_app is not null && _appKey == key)
            {
                return _app;
            }

            var app = PublicClientApplicationBuilder.Create(ms.ClientId)
                .WithAuthority(AzureCloudInstance.AzurePublic, string.IsNullOrWhiteSpace(ms.TenantId) ? "organizations" : ms.TenantId)
                .WithDefaultRedirectUri()
                .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows) { Title = "Personal Assistant" })
                .WithParentActivityOrWindow(() => WindowHandleProvider?.Invoke() ?? IntPtr.Zero)
                .Build();

            var storage = new StorageCreationPropertiesBuilder("msal.cache", AppPaths.DataDirectory).Build();
            var cache = await MsalCacheHelper.CreateAsync(storage);
            cache.RegisterCache(app.UserTokenCache);

            _app = app;
            _appKey = key;
            logger.LogInformation("Microsoft sign-in configured for tenant {Tenant}", ms.TenantId);
            return app;
        }
        finally
        {
            _gate.Release();
        }
    }
}
