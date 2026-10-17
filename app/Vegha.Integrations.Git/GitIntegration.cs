using Vegha.Integrations.Git.Credentials;

namespace Vegha.Integrations.Git;

/// <summary>One object that wires the git stack together: executable locator → process
/// runner → credential broker (profiles + vault + gh/az) → <see cref="GitService"/>, plus SSH,
/// diagnostics and the on-demand installer. Registered as a singleton by the app; view models
/// take this instead of a dozen individual services.</summary>
public sealed class GitIntegration
{
    public GitExecutableLocator Locator { get; }
    public GitProcessRunner Runner { get; }
    public ExternalCli Cli { get; }
    public GitCredentialProfileStore Profiles { get; }
    public IGitSecretVault Vault { get; }
    public GitCredentialBroker Broker { get; }
    public GitCredentialsService Credentials { get; }
    public GitService Service { get; }
    public SshKeyService SshKeys { get; }
    public GitDiagnosticsService Diagnostics { get; }
    public GitRuntimeInstaller Installer { get; }

    public GitIntegration(string? dataFolder = null, Func<HttpClient>? httpFactory = null)
    {
        Locator = new GitExecutableLocator();
        Runner = new GitProcessRunner(Locator);
        Cli = new ExternalCli();
        Profiles = new GitCredentialProfileStore(dataFolder is null
            ? GitCredentialProfileStore.DefaultPath
            : Path.Combine(dataFolder, "git-credential-profiles.json"));
        Vault = new GitSecretVault(dataFolder);
        Broker = new GitCredentialBroker(Profiles, Vault, Cli);
        Credentials = new GitCredentialsService(Runner);
        Service = new GitService(Runner, Broker);
        SshKeys = new SshKeyService(Cli);
        Diagnostics = new GitDiagnosticsService(Runner, Broker, Credentials, Broker);
        Installer = new GitRuntimeInstaller(httpFactory);
    }

    /// <summary>Saves a PAT for <paramref name="profile"/> according to its store mode:
    /// Git Credential Manager (shared with the terminal) or the Vegha vault.</summary>
    public async Task SavePatAsync(GitCredentialProfile profile, string pat, CancellationToken ct = default)
    {
        if (profile.StoreMode == GitSecretStoreMode.VeghaIsolated)
        {
            Vault.Set(profile.Id, pat);
            Profiles.Upsert(profile with { HasStoredSecret = true });
            return;
        }
        var host = profile.HostPattern.Trim().TrimEnd('/');
        if (host.Contains("://", StringComparison.Ordinal)) host = host[(host.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var url = "https://" + host;
        var user = string.IsNullOrEmpty(profile.Username)
            ? GitCredentialBroker.DefaultTokenUser(GitRemoteUrl.TryParse(url) ?? new GitRemoteUrl("https", host, null, "", null))
            : profile.Username!;
        if (!await Credentials.ApproveAsync(url, user, pat, ct).ConfigureAwait(false))
            throw new InvalidOperationException("Git Credential Manager didn't accept the credential. Is a credential helper configured? Choose \"Store in Vegha\" instead.");
        Vault.Delete(profile.Id);
        Profiles.Upsert(profile with { Username = user, HasStoredSecret = false });
    }

    /// <summary>Removes a profile and any secret it stored (in the vault and, best-effort, GCM).</summary>
    public async Task DeleteProfileAsync(GitCredentialProfile profile, CancellationToken ct = default)
    {
        Vault.Delete(profile.Id);
        if (profile.Method == GitAuthMethod.Pat && profile.StoreMode == GitSecretStoreMode.GitCredentialManager)
        {
            var host = profile.HostPattern.Trim().TrimEnd('/');
            await Credentials.RejectAsync("https://" + host, profile.Username, ct).ConfigureAwait(false);
        }
        Profiles.Remove(profile.Id);
    }
}
