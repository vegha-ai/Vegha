using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vegha.Integrations.Git.Credentials;

/// <summary>How git authenticates to a host.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GitAuthMethod
{
    /// <summary>Whatever already works in the terminal: Git Credential Manager (browser SSO,
    /// MFA, Windows WAM), the OS keychain helper, ssh-agent / Pageant / 1Password agent.</summary>
    System,

    /// <summary>Personal access token (GitHub classic/fine-grained, Azure DevOps PAT, GitLab,
    /// Bitbucket app password / API token).</summary>
    Pat,

    /// <summary>A specific SSH private key for this host.</summary>
    Ssh,

    /// <summary>Token from the GitHub CLI (<c>gh auth token</c>) — reuses an existing
    /// <c>gh auth login</c> (incl. SSO-authorized OAuth tokens).</summary>
    GhCli,

    /// <summary>Entra ID access token from the Azure CLI (<c>az account get-access-token</c>)
    /// for Azure DevOps — honors MFA / Conditional Access done at <c>az login</c>.</summary>
    AzCli,

    /// <summary>OAuth device-flow token obtained natively by Vegha (Mac App Store build, where
    /// GCM / gh / az can't run).</summary>
    DeviceFlow,
}

/// <summary>Where a PAT entered in Vegha is stored.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GitSecretStoreMode
{
    /// <summary>Hand it to git's credential helper chain (<c>git credential approve</c> → GCM /
    /// Windows Credential Manager / macOS Keychain) so the terminal uses it too.</summary>
    GitCredentialManager,

    /// <summary>Keep it inside Vegha's own encrypted vault; injected per operation only.</summary>
    VeghaIsolated,
}

/// <summary>Non-secret description of how to authenticate to a host (or host + path prefix,
/// e.g. <c>dev.azure.com/contoso</c> so two ADO orgs — or a work and a personal GitHub
/// account — can use different credentials). Secrets live in <see cref="IGitSecretVault"/>
/// under <see cref="Id"/>.</summary>
public sealed record GitCredentialProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Host with optional path prefix: <c>github.com</c>, <c>github.com/acme</c>,
    /// <c>dev.azure.com/contoso</c>, <c>ghe.acme.com</c>. Case-insensitive.</summary>
    public string HostPattern { get; init; } = string.Empty;

    public GitAuthMethod Method { get; init; } = GitAuthMethod.System;
    public string? Username { get; init; }
    public string? SshKeyPath { get; init; }
    public GitSecretStoreMode StoreMode { get; init; } = GitSecretStoreMode.GitCredentialManager;

    /// <summary>True when a secret for this profile has been saved in the Vegha vault.</summary>
    public bool HasStoredSecret { get; init; }
}

/// <summary>Persisted profile list + per-repo overrides.</summary>
public sealed record GitCredentialProfileState
{
    public List<GitCredentialProfile> Profiles { get; init; } = new();

    /// <summary>Repo root (normalized, case-insensitive) → profile id.</summary>
    public Dictionary<string, string> RepoOverrides { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>JSON-backed store for <see cref="GitCredentialProfileState"/>. Contains no secrets.</summary>
public sealed class GitCredentialProfileStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();

    public GitCredentialProfileStore(string path) { _path = path; }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vegha", "git-credential-profiles.json");

    public event EventHandler? Changed;

    public GitCredentialProfileState Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return new GitCredentialProfileState();
                var state = JsonSerializer.Deserialize<GitCredentialProfileState>(File.ReadAllText(_path), Json);
                if (state is null) return new GitCredentialProfileState();
                // Re-wrap the dictionary so lookups stay case-insensitive after deserialization.
                return state with { RepoOverrides = new Dictionary<string, string>(state.RepoOverrides, StringComparer.OrdinalIgnoreCase) };
            }
            catch { return new GitCredentialProfileState(); }
        }
    }

    public void Save(GitCredentialProfileState state)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Upsert(GitCredentialProfile profile)
    {
        var state = Load();
        var list = state.Profiles.Where(p => p.Id != profile.Id).ToList();
        list.Add(profile);
        Save(state with { Profiles = list });
    }

    public void Remove(string profileId)
    {
        var state = Load();
        var overrides = state.RepoOverrides.Where(kv => kv.Value != profileId)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        Save(state with { Profiles = state.Profiles.Where(p => p.Id != profileId).ToList(), RepoOverrides = overrides });
    }

    public void SetRepoOverride(string repoRoot, string? profileId)
    {
        var state = Load();
        var overrides = new Dictionary<string, string>(state.RepoOverrides, StringComparer.OrdinalIgnoreCase);
        var key = NormalizeRoot(repoRoot);
        if (string.IsNullOrEmpty(profileId)) overrides.Remove(key);
        else overrides[key] = profileId;
        Save(state with { RepoOverrides = overrides });
    }

    /// <summary>Profile for an operation: repo override → longest matching host pattern → null
    /// (meaning <see cref="GitAuthMethod.System"/>).</summary>
    public GitCredentialProfile? Resolve(string remoteUrl, string? repoRoot)
    {
        var state = Load();
        return ResolveFrom(state, remoteUrl, repoRoot);
    }

    public static GitCredentialProfile? ResolveFrom(GitCredentialProfileState state, string remoteUrl, string? repoRoot)
    {
        if (!string.IsNullOrEmpty(repoRoot)
            && state.RepoOverrides.TryGetValue(NormalizeRoot(repoRoot), out var id)
            && state.Profiles.FirstOrDefault(p => p.Id == id) is { } overridden)
            return overridden;

        var target = GitRemoteUrl.TryParse(remoteUrl);
        if (target is null) return null;
        var hostPath = (target.Host + "/" + target.Path).TrimEnd('/');

        return state.Profiles
            .Where(p => !string.IsNullOrWhiteSpace(p.HostPattern) && Matches(p.HostPattern, hostPath))
            .OrderByDescending(p => p.HostPattern.Trim().TrimEnd('/').Length)
            .FirstOrDefault();
    }

    internal static bool Matches(string pattern, string hostPath)
    {
        var p = pattern.Trim().TrimEnd('/');
        if (p.Contains("://", StringComparison.Ordinal)) p = p[(p.IndexOf("://", StringComparison.Ordinal) + 3)..];
        return hostPath.Equals(p, StringComparison.OrdinalIgnoreCase)
            || hostPath.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRoot(string root)
    {
        try { return Path.GetFullPath(root).TrimEnd('\\', '/'); }
        catch { return root.TrimEnd('\\', '/'); }
    }
}
