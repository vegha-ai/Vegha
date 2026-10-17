using System.Text;
using System.Text.RegularExpressions;

namespace Vegha.Integrations.Git.Credentials;

/// <summary>Global git options from Settings → Git that shape every network operation.</summary>
public sealed record GitGlobalOptions
{
    /// <summary>Windows: let GCM sign in with the account already signed in to Windows (WAM
    /// broker) — SSO with Entra ID, compliant with MFA / Conditional Access.</summary>
    public bool UseWindowsBroker { get; init; } = false;

    /// <summary>Azure Repos: have GCM use OAuth tokens instead of minting PATs.</summary>
    public bool AzureReposOAuth { get; init; } = false;

    /// <summary>Windows: validate TLS against the Windows certificate store (schannel) — needed
    /// behind TLS-inspecting corporate proxies whose root CA is pushed via GPO.</summary>
    public bool UseOsCertificateStore { get; init; } = true;

    /// <summary>Windows: run the built-in OpenSSH (talks to the Windows ssh-agent service)
    /// instead of Git's bundled ssh.</summary>
    public bool PreferWindowsOpenSsh { get; init; } = true;
}

/// <summary>Proxy for git, derived from Settings → Network.</summary>
public sealed record GitProxy(string Url, string? NoProxy);

/// <summary>Builds the per-operation <see cref="GitEnvironment"/>: proxy, GCM tuning, and the
/// credential injection for the profile that matches the remote URL.
///
/// Token delivery for Vegha-managed credentials (isolated PAT, gh / az / device-flow tokens) is
/// an <c>http.&lt;origin&gt;/.extraHeader</c> injected through <c>GIT_CONFIG_*</c> environment
/// variables — scoped to that one host, never written to disk or to the command line, and
/// accompanied by an empty <c>credential.&lt;origin&gt;.helper</c> so a stale token surfaces
/// as an auth error instead of an unexpected GCM prompt.</summary>
public sealed partial class GitCredentialBroker : IGitEnvironmentProvider
{
    /// <summary>Azure DevOps resource id for Entra ID tokens.</summary>
    public const string AzureDevOpsResource = "499b84ac-1321-427f-aa17-267ca6975798";

    private readonly GitCredentialProfileStore _profiles;
    private readonly IGitSecretVault _vault;
    private readonly ExternalCli _cli;

    public GitCredentialBroker(GitCredentialProfileStore profiles, IGitSecretVault vault, ExternalCli cli)
    {
        _profiles = profiles;
        _vault = vault;
        _cli = cli;
    }

    public GitCredentialProfileStore Profiles => _profiles;
    public IGitSecretVault Vault => _vault;
    public ExternalCli Cli => _cli;

    public GitGlobalOptions Options { get; set; } = new();

    /// <summary>Returns the proxy for a remote URL (null = leave git's own config alone).</summary>
    public Func<string, GitProxy?>? ProxyResolver { get; set; }

    public async Task<GitEnvironment> BuildAsync(GitEnvironmentRequest request, CancellationToken ct)
    {
        var env = new GitEnvironment();
        var remote = GitRemoteUrl.TryParse(request.RemoteUrl);
        var repoRoot = GitRepoLocator.Locate(request.WorkingDirectory)?.RepoRoot;

        ApplyGlobal(env, remote, repoRoot);

        if (remote is not null && remote.IsHttp && ProxyResolver?.Invoke(request.RemoteUrl) is { } proxy)
        {
            env.AddConfig("http.proxy", proxy.Url);
            if (!string.IsNullOrWhiteSpace(proxy.NoProxy)) env.Set("NO_PROXY", proxy.NoProxy!).Set("no_proxy", proxy.NoProxy!);
        }

        if (remote is null || remote.Scheme == "file") return env;

        var profile = _profiles.Resolve(request.RemoteUrl, repoRoot);
        if (profile is null) return env;

        await ApplyProfileAsync(env, profile, remote, ct).ConfigureAwait(false);
        return env;
    }

    /// <summary>Describes which credential source will answer for <paramref name="url"/> — shown
    /// in the clone dialog and diagnostics ("Signed in via GitHub CLI", …).</summary>
    public string DescribeSource(string url, string? repoRoot)
    {
        var profile = _profiles.Resolve(url, repoRoot);
        return profile is null
            ? "System Git credentials (Git Credential Manager / SSH agent)"
            : $"{(string.IsNullOrEmpty(profile.DisplayName) ? profile.HostPattern : profile.DisplayName)} — {DescribeMethod(profile)}";
    }

    public static string DescribeMethod(GitCredentialProfile p) => p.Method switch
    {
        GitAuthMethod.Pat => p.StoreMode == GitSecretStoreMode.VeghaIsolated ? "Personal access token (stored in Vegha)" : "Personal access token (Git Credential Manager)",
        GitAuthMethod.Ssh => $"SSH key {Path.GetFileName(p.SshKeyPath ?? "")}",
        GitAuthMethod.GhCli => "GitHub CLI (gh)",
        GitAuthMethod.AzCli => "Azure CLI (az)",
        GitAuthMethod.DeviceFlow => "Browser sign-in (device code)",
        _ => "System Git credentials",
    };

    /// <summary>Reads the user's effective git config for a key (repo → global → system), so
    /// Vegha's defaults never override something the user configured deliberately.
    /// Overridable for tests.</summary>
    public Func<string?, string, bool> UserHasConfig { get; set; } = DefaultUserHasConfig;

    private static bool DefaultUserHasConfig(string? repoRoot, string key)
    {
        try
        {
            if (!string.IsNullOrEmpty(repoRoot) && LibGit2Sharp.Repository.IsValid(repoRoot))
            {
                using var repo = new LibGit2Sharp.Repository(repoRoot);
                return repo.Config.Get<string>(key) is not null;
            }
            using var cfg = LibGit2Sharp.Configuration.BuildFrom(null);
            return cfg.Get<string>(key) is not null;
        }
        catch { return false; }
    }

    private void ApplyGlobal(GitEnvironment env, GitRemoteUrl? remote, string? repoRoot)
    {
        // Defaults only: each is skipped when the user already set the key (or an equivalent
        // env var), so existing working setups — plink/1Password sshCommand, an OpenSSL CA
        // bundle, explicit GCM settings — are left exactly as they are.
        void Default(string key, string value)
        {
            if (!UserHasConfig(repoRoot, key)) env.AddConfig(key, value);
        }

        if (OperatingSystem.IsWindows())
        {
            if (Options.UseOsCertificateStore && !UserHasConfig(repoRoot, "http.sslCAInfo")) Default("http.sslBackend", "schannel");
            if (Options.UseWindowsBroker) Default("credential.msauthUseBroker", "true");
            if (Options.PreferWindowsOpenSsh && WindowsOpenSshPath() is { } ssh
                && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GIT_SSH"))
                && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GIT_SSH_COMMAND")))
                Default("core.sshCommand", QuoteForSh(ssh));
        }
        if (Options.AzureReposOAuth) Default("credential.azreposCredentialType", "oauth");

        // Azure DevOps hosts many orgs on one host: key credentials by path so org A's token
        // isn't offered to org B.
        if (remote is not null && GitHostKinds.IsAzureDevOps(remote.Host))
            env.AddConfig($"credential.{remote.HttpsOrigin}.useHttpPath", "true");
    }

    private async Task ApplyProfileAsync(GitEnvironment env, GitCredentialProfile profile, GitRemoteUrl remote, CancellationToken ct)
    {
        switch (profile.Method)
        {
            case GitAuthMethod.Pat:
                if (profile.StoreMode == GitSecretStoreMode.VeghaIsolated)
                {
                    var pat = _vault.Get(profile.Id);
                    if (!string.IsNullOrEmpty(pat))
                        InjectBasic(env, remote, string.IsNullOrEmpty(profile.Username) ? DefaultTokenUser(remote) : profile.Username!, pat);
                }
                else if (!string.IsNullOrEmpty(profile.Username))
                {
                    // GCM holds the secret; pin the username so the right account is picked
                    // when several are stored for the same host.
                    env.AddConfig($"credential.{remote.HttpsOrigin}.username", profile.Username!);
                }
                break;

            case GitAuthMethod.GhCli:
            {
                var token = await GetGhTokenAsync(remote.Host, ct, profile.Username).ConfigureAwait(false);
                if (token is not null) InjectBasic(env, remote, "x-access-token", token);
                break;
            }

            case GitAuthMethod.AzCli:
            {
                var token = await GetAzTokenAsync(ct).ConfigureAwait(false);
                if (token is not null) InjectBearer(env, remote, token);
                break;
            }

            case GitAuthMethod.DeviceFlow:
            {
                var token = _vault.Get(profile.Id);
                if (!string.IsNullOrEmpty(token))
                {
                    if (GitHostKinds.IsAzureDevOps(remote.Host)) InjectBearer(env, remote, token);
                    else InjectBasic(env, remote, "x-access-token", token);
                }
                break;
            }

            case GitAuthMethod.Ssh when !string.IsNullOrEmpty(profile.SshKeyPath):
            {
                var ssh = OperatingSystem.IsWindows() && Options.PreferWindowsOpenSsh && WindowsOpenSshPath() is { } w ? w : "ssh";
                env.AddConfig("core.sshCommand",
                    $"{QuoteForSh(ssh)} -i {QuoteForSh(profile.SshKeyPath!)} -o IdentitiesOnly=yes");
                break;
            }
        }
    }

    /// <summary>Token for API calls (repo picker, create repo) for <paramref name="url"/> using
    /// the same resolution as git: profile token / gh / az, else <c>git credential fill</c>
    /// (GCM's stored OAuth token or PAT). Returns (scheme, token) or null.</summary>
    public async Task<(string Scheme, string Token)?> GetApiTokenAsync(string url, string? repoRoot, GitCredentialsService gitCredentials, CancellationToken ct)
    {
        var remote = GitRemoteUrl.TryParse(url);
        if (remote is null) return null;
        var profile = _profiles.Resolve(url, repoRoot);
        var ado = GitHostKinds.IsAzureDevOps(remote.Host);
        switch (profile?.Method)
        {
            case GitAuthMethod.Pat when profile.StoreMode == GitSecretStoreMode.VeghaIsolated && _vault.Get(profile.Id) is { Length: > 0 } pat:
                return ado ? ("Basic", Basic("", pat)) : ("Bearer", pat);
            case GitAuthMethod.GhCli when await GetGhTokenAsync(remote.Host, ct, profile.Username).ConfigureAwait(false) is { } gh:
                return ("Bearer", gh);
            case GitAuthMethod.AzCli when await GetAzTokenAsync(ct).ConfigureAwait(false) is { } az:
                return ("Bearer", az);
            case GitAuthMethod.DeviceFlow when _vault.Get(profile.Id) is { Length: > 0 } df:
                return ("Bearer", df);
        }

        // No explicit profile: try gh for GitHub hosts (cheap, no UI), then the git helper chain.
        if (!ado && _cli.Find("gh") is not null && await GetGhTokenAsync(remote.Host, ct).ConfigureAwait(false) is { } ghToken)
            return ("Bearer", ghToken);

        var origin = remote.IsHttp ? $"{remote.HttpsOrigin}/{remote.Path}" : $"https://{remote.Host}/{remote.Path}";
        var fill = await gitCredentials.FillAsync(origin, profile?.Username, ct).ConfigureAwait(false);
        if (fill is null) return null;
        // ADO accepts PATs and GCM OAuth tokens as Basic; GitHub accepts either as Bearer.
        return ado ? ("Basic", Basic(fill.Value.Username, fill.Value.Secret)) : ("Bearer", fill.Value.Secret);
    }

    /// <summary>Token from the GitHub CLI. <paramref name="user"/> picks one of several
    /// logged-in accounts on the same host (<c>gh auth token --user</c>) — e.g. a work account
    /// next to a personal one; null uses gh's active account.</summary>
    public async Task<string?> GetGhTokenAsync(string host, CancellationToken ct, string? user = null)
    {
        if (_cli.Find("gh") is null) return null;
        try
        {
            var args = new List<string> { "auth", "token", "--hostname", host == "ssh.github.com" ? "github.com" : host };
            if (!string.IsNullOrWhiteSpace(user)) { args.Add("--user"); args.Add(user!.Trim()); }
            var r = await _cli.RunAsync("gh", args, ct).ConfigureAwait(false);
            var token = r.StdOut.Trim();
            return r.Success && token.Length > 0 ? token : null;
        }
        catch { return null; }
    }

    public async Task<string?> GetAzTokenAsync(CancellationToken ct)
    {
        if (_cli.Find("az") is null) return null;
        try
        {
            var r = await _cli.RunAsync("az", new[] { "account", "get-access-token", "--resource", AzureDevOpsResource, "--query", "accessToken", "-o", "tsv" }, ct,
                timeout: TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            var token = r.StdOut.Trim();
            return r.Success && token.Length > 0 ? token : null;
        }
        catch { return null; }
    }

    /// <summary>Runs <c>gh auth login --web</c> for <paramref name="host"/>, reporting the
    /// one-time device code through <paramref name="onCode"/> so the UI can show it.</summary>
    public async Task<bool> GhLoginAsync(string host, Action<string> onCode, Action<string>? onOutput, CancellationToken ct)
    {
        var r = await _cli.RunAsync("gh", new[] { "auth", "login", "--hostname", host, "--web", "--git-protocol", "https" }, ct,
            stdin: "\n",
            onLine: line =>
            {
                onOutput?.Invoke(line);
                var m = DeviceCodeRx().Match(line);
                if (m.Success) onCode(m.Groups[1].Value);
            },
            timeout: TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        return r.Success;
    }

    /// <summary>Runs <c>az login</c> (opens the browser; MFA / Conditional Access handled by Entra).</summary>
    public async Task<bool> AzLoginAsync(Action<string>? onOutput, CancellationToken ct, string? tenant = null)
    {
        var args = new List<string> { "login", "--allow-no-subscriptions" };
        if (!string.IsNullOrWhiteSpace(tenant)) { args.Add("--tenant"); args.Add(tenant!); }
        var r = await _cli.RunAsync("az", args, ct, onLine: onOutput, timeout: TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        return r.Success;
    }

    /// <summary>One-off environment for an explicitly entered PAT (not saved anywhere).</summary>
    public static GitEnvironment ExplicitPat(string url, string? username, string pat)
    {
        var env = new GitEnvironment();
        var remote = GitRemoteUrl.TryParse(url);
        if (remote is not null && remote.IsHttp)
            InjectBasic(env, remote, string.IsNullOrEmpty(username) ? DefaultTokenUser(remote) : username!, pat);
        return env;
    }

    /// <summary>One-off environment that forces a specific SSH private key.</summary>
    public static GitEnvironment ExplicitSshKey(string keyPath, bool preferWindowsOpenSsh = true)
    {
        var ssh = OperatingSystem.IsWindows() && preferWindowsOpenSsh && WindowsOpenSshPath() is { } w ? w : "ssh";
        return new GitEnvironment().AddConfig("core.sshCommand", $"{QuoteForSh(ssh)} -i {QuoteForSh(keyPath)} -o IdentitiesOnly=yes");
    }

    private static void InjectBasic(GitEnvironment env, GitRemoteUrl remote, string user, string secret) =>
        InjectHeader(env, remote, "Basic " + Basic(user, secret));

    private static void InjectBearer(GitEnvironment env, GitRemoteUrl remote, string token) =>
        InjectHeader(env, remote, "Bearer " + token);

    private static void InjectHeader(GitEnvironment env, GitRemoteUrl remote, string authorization)
    {
        if (!remote.IsHttp) return;
        var origin = remote.HttpsOrigin + "/";
        env.AddConfig($"credential.{remote.HttpsOrigin}.helper", "");
        env.AddConfig($"http.{origin}.extraHeader", "Authorization: " + authorization);
    }

    private static string Basic(string user, string secret) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{secret}"));

    /// <summary>Username to pair with a bare token: GitHub ignores it but requires one;
    /// Azure DevOps accepts any non-empty value.</summary>
    public static string DefaultTokenUser(GitRemoteUrl remote) =>
        GitHostKinds.IsAzureDevOps(remote.Host) ? "pat" : GitHostKinds.IsGitLab(remote.Host) ? "oauth2" : "x-access-token";

    public static string? WindowsOpenSshPath()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
        return File.Exists(p) ? p : null;
    }

    /// <summary>core.sshCommand is run by git through a POSIX shell (sh.exe on Windows):
    /// forward slashes + single quotes survive it.</summary>
    internal static string QuoteForSh(string path) => "'" + path.Replace('\\', '/').Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    [GeneratedRegex(@"\b([A-Z0-9]{4}-[A-Z0-9]{4})\b")]
    private static partial Regex DeviceCodeRx();
}

/// <summary>Host classification shared by the broker, providers and diagnostics.</summary>
public static class GitHostKinds
{
    public static bool IsGitHub(string host) =>
        host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("ssh.github.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".ghe.com", StringComparison.OrdinalIgnoreCase);

    public static bool IsAzureDevOps(string host) =>
        host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("ssh.dev.azure.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("vs-ssh.visualstudio.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase);

    public static bool IsGitLab(string host) => host.Equals("gitlab.com", StringComparison.OrdinalIgnoreCase);

    public static bool IsBitbucket(string host) => host.Equals("bitbucket.org", StringComparison.OrdinalIgnoreCase);
}
