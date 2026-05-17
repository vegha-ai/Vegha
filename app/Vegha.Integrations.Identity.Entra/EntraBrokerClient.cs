using Microsoft.Identity.Client;

#if WINDOWS
using Microsoft.Identity.Client.Broker;
#endif

namespace Vegha.Integrations.Identity.Entra;

/// <summary>
/// Acquires tokens from Entra ID (Azure AD) on behalf of the signed-in OS user. On
/// Windows this uses the WAM broker — the user picks "Use Windows broker (SSO)" and
/// we silently get a token through the OS account picker. Non-Windows falls back to
/// the public-client interactive flow against the system browser.
///
/// Surface area is intentionally tiny: build a client per (clientId, tenantId), call
/// AcquireTokenAsync with the scope set the API requires, get the access token back.
/// Caching is handled by MSAL's in-memory token cache.
/// </summary>
public sealed class EntraBrokerClient
{
    private readonly IPublicClientApplication _app;

    public EntraBrokerClient(string clientId, string tenantId, string redirectUri = "http://localhost")
    {
        var builder = PublicClientApplicationBuilder.Create(clientId)
            .WithTenantId(tenantId)
            .WithRedirectUri(redirectUri);

#if WINDOWS
        // Wire up the WAM broker on Windows 10+. On Mac/Linux MSAL falls back to the
        // browser flow automatically.
        builder = builder.WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows)
        {
            Title = "Vegha"
        });
#endif

        _app = builder.Build();
    }

    /// <summary>Acquires a token, preferring silent (broker / cache) over interactive.
    /// Throws if the user cancels the interactive prompt.</summary>
    public async Task<EntraTokenResult> AcquireTokenAsync(
        IEnumerable<string> scopes,
        CancellationToken cancellationToken = default)
    {
        var accounts = await _app.GetAccountsAsync().ConfigureAwait(false);
        var account = accounts.FirstOrDefault() ?? PublicClientApplication.OperatingSystemAccount;

        try
        {
            var silent = await _app
                .AcquireTokenSilent(scopes, account)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
            return Map(silent);
        }
        catch (MsalUiRequiredException)
        {
            // Fall through to interactive — the user must sign in again or first time.
        }

        var interactive = await _app
            .AcquireTokenInteractive(scopes)
            .WithAccount(account)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
        return Map(interactive);
    }

    private static EntraTokenResult Map(AuthenticationResult r) =>
        new(r.AccessToken, r.ExpiresOn.UtcDateTime, r.Account?.Username, r.Scopes.ToList());
}

public sealed record EntraTokenResult(
    string AccessToken,
    DateTime ExpiresAtUtc,
    string? Username,
    IReadOnlyList<string> Scopes);
