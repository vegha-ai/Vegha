namespace Vegha.Integrations.Git.Credentials;

/// <summary>SSH key + known_hosts helpers for Settings → Git → SSH and the "Unknown SSH host"
/// error action. Uses the system <c>ssh-keygen</c> / <c>ssh-keyscan</c> (Windows OpenSSH,
/// macOS, or Git for Windows' bundled copies).</summary>
public sealed class SshKeyService
{
    private readonly ExternalCli _cli;

    public SshKeyService(ExternalCli cli) { _cli = cli; }

    public static string SshDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    public static string KnownHostsPath => Path.Combine(SshDirectory, "known_hosts");

    /// <summary>Private keys in ~/.ssh that have a matching .pub file.</summary>
    public IReadOnlyList<SshKeyInfo> ListKeys()
    {
        if (!Directory.Exists(SshDirectory)) return Array.Empty<SshKeyInfo>();
        var list = new List<SshKeyInfo>();
        foreach (var pub in Directory.EnumerateFiles(SshDirectory, "*.pub"))
        {
            var priv = pub[..^4];
            if (!File.Exists(priv)) continue;
            string text;
            try { text = File.ReadAllText(pub).Trim(); }
            catch { continue; }
            var parts = text.Split(' ', 3);
            list.Add(new SshKeyInfo(priv, pub, parts.Length > 0 ? parts[0] : "?", parts.Length > 2 ? parts[2] : string.Empty, text));
        }
        return list.OrderBy(k => k.PrivateKeyPath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Generates an ed25519 key pair (<c>ssh-keygen -t ed25519</c>). An empty passphrase
    /// produces an unencrypted key; a passphrase-protected key must be loaded into ssh-agent.</summary>
    public async Task<SshKeyInfo> GenerateAsync(string privateKeyPath, string comment, string passphrase, CancellationToken ct = default)
    {
        if (File.Exists(privateKeyPath)) throw new IOException($"{privateKeyPath} already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(privateKeyPath)!);
        var r = await _cli.RunAsync("ssh-keygen", new[] { "-t", "ed25519", "-C", comment, "-f", privateKeyPath, "-N", passphrase, "-q" }, ct).ConfigureAwait(false);
        if (!r.Success) throw new InvalidOperationException("ssh-keygen failed: " + (r.StdErr.Trim().Length > 0 ? r.StdErr.Trim() : r.StdOut.Trim()));
        var pub = privateKeyPath + ".pub";
        var text = (await File.ReadAllTextAsync(pub, ct).ConfigureAwait(false)).Trim();
        return new SshKeyInfo(privateKeyPath, pub, "ssh-ed25519", comment, text);
    }

    /// <summary>Fetches a host's keys (<c>ssh-keyscan</c>) and their SHA256 fingerprints so the
    /// user can verify before trusting.</summary>
    public async Task<IReadOnlyList<SshHostKey>> ScanHostAsync(string host, int? port, CancellationToken ct = default)
    {
        var args = new List<string> { "-T", "10" };
        if (port is { } p) { args.Add("-p"); args.Add(p.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        args.Add(host);
        var r = await _cli.RunAsync("ssh-keyscan", args, ct, timeout: TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        var lines = r.StdOut.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        var result = new List<SshHostKey>();
        foreach (var line in lines)
        {
            var fp = await _cli.RunAsync("ssh-keygen", new[] { "-lf", "-" }, ct, stdin: line + "\n").ConfigureAwait(false);
            var parts = fp.StdOut.Trim().Split(' ');
            result.Add(new SshHostKey(line, parts.Length > 1 ? parts[1] : "?", parts.Length > 0 ? parts[^1].Trim('(', ')') : "?"));
        }
        return result;
    }

    /// <summary>Appends verified host key lines to ~/.ssh/known_hosts.</summary>
    public static void Trust(IEnumerable<SshHostKey> keys)
    {
        Directory.CreateDirectory(SshDirectory);
        var existing = File.Exists(KnownHostsPath) ? File.ReadAllText(KnownHostsPath) : string.Empty;
        var toAdd = keys.Select(k => k.KnownHostsLine).Where(l => !existing.Contains(l, StringComparison.Ordinal)).ToList();
        if (toAdd.Count == 0) return;
        var prefix = existing.Length == 0 || existing.EndsWith('\n') ? "" : "\n";
        File.AppendAllText(KnownHostsPath, prefix + string.Join('\n', toAdd) + "\n");
    }

    /// <summary>Where to add a public key for the provider hosting <paramref name="host"/>.</summary>
    public static string? AddKeyUrl(string host) =>
        GitHostKinds.IsGitHub(host) ? (host.EndsWith(".ghe.com", StringComparison.OrdinalIgnoreCase) ? $"https://{host}/settings/ssh/new" : "https://github.com/settings/ssh/new")
        : GitHostKinds.IsAzureDevOps(host) ? "https://dev.azure.com/_usersSettings/keys"
        : GitHostKinds.IsGitLab(host) ? "https://gitlab.com/-/user_settings/ssh_keys"
        : GitHostKinds.IsBitbucket(host) ? "https://bitbucket.org/account/settings/ssh-keys/"
        : null;
}

public sealed record SshKeyInfo(string PrivateKeyPath, string PublicKeyPath, string Type, string Comment, string PublicKeyText);

public sealed record SshHostKey(string KnownHostsLine, string Fingerprint, string KeyType);
