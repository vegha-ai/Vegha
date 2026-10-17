namespace Vegha.Integrations.Git;

/// <summary>Thin wrapper over <c>git credential fill / approve / reject</c>. These delegate to
/// whatever helper chain the user has configured (Git Credential Manager on Windows / macOS,
/// osxkeychain, libsecret, …), so a PAT saved here is also what the terminal uses and vice
/// versa.</summary>
public sealed class GitCredentialsService
{
    private readonly GitProcessRunner _runner;

    public GitCredentialsService(GitProcessRunner runner)
    {
        _runner = runner;
    }

    /// <summary>Asks the helper chain for a credential without any UI (<c>GCM_INTERACTIVE=never</c>).
    /// Returns null when nothing is stored.</summary>
    public async Task<(string Username, string Secret)?> FillAsync(string url, string? usernameHint, CancellationToken ct = default)
    {
        if (!_runner.IsAvailable) return null;
        try
        {
            var input = Describe(url, usernameHint, null);
            var env = HelperEnvironment(url);
            var result = await _runner.RunAsync(Path.GetTempPath(), new[] { "credential", "fill" },
                new GitRunOptions { StdIn = input, Environment = env, Timeout = TimeSpan.FromSeconds(20) }, ct).ConfigureAwait(false);
            if (!result.Success) return null;
            string? user = null, secret = null;
            foreach (var raw in result.StdOut.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) break;
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                var key = line[..eq];
                var value = line[(eq + 1)..];
                if (key == "username") user = value;
                else if (key == "password") secret = value;
            }
            return !string.IsNullOrEmpty(secret) ? (user ?? string.Empty, secret!) : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>Stores a credential in the helper chain (GCM / keychain).</summary>
    public Task<bool> ApproveAsync(string url, string username, string secret, CancellationToken ct = default) =>
        RunAsync("approve", Describe(url, username, secret), ct);

    /// <summary>Removes a stored credential from the helper chain.</summary>
    public Task<bool> RejectAsync(string url, string? username, CancellationToken ct = default) =>
        RunAsync("reject", Describe(url, username, null), ct);

    /// <summary>The configured helper chain (<c>git config --show-origin --get-all credential.helper</c>).</summary>
    public async Task<string> DescribeHelpersAsync(CancellationToken ct = default)
    {
        if (!_runner.IsAvailable) return "(git not available)";
        var r = await _runner.RunAsync(Path.GetTempPath(), new[] { "config", "--show-origin", "--get-all", "credential.helper" }, ct: ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(r.StdOut) ? "(none configured)" : r.StdOut.Trim();
    }

    private async Task<bool> RunAsync(string action, string input, CancellationToken ct)
    {
        if (!_runner.IsAvailable) return false;
        try
        {
            var env = HelperEnvironment(input);
            var r = await _runner.RunAsync(Path.GetTempPath(), new[] { "credential", action },
                new GitRunOptions { StdIn = input, Environment = env, Timeout = TimeSpan.FromSeconds(20) }, ct).ConfigureAwait(false);
            return r.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    /// <summary>No GCM UI; for Azure DevOps key credentials by path (one host, many orgs) — the
    /// same setting the broker applies to network operations so lookups match.</summary>
    private static GitEnvironment HelperEnvironment(string urlOrInput)
    {
        var env = new GitEnvironment().Set("GCM_INTERACTIVE", "never");
        foreach (var host in new[] { "dev.azure.com" })
            if (urlOrInput.Contains(host, StringComparison.OrdinalIgnoreCase))
                env.AddConfig($"credential.https://{host}.useHttpPath", "true");
        return env;
    }

    private static string Describe(string url, string? username, string? secret)
    {
        var sb = new System.Text.StringBuilder();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            sb.Append("protocol=").Append(uri.Scheme).Append('\n');
            sb.Append("host=").Append(uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}").Append('\n');
            var path = uri.AbsolutePath.Trim('/');
            if (path.Length > 0) sb.Append("path=").Append(path).Append('\n');
        }
        else
        {
            sb.Append("url=").Append(url).Append('\n');
        }
        if (!string.IsNullOrEmpty(username)) sb.Append("username=").Append(username).Append('\n');
        if (!string.IsNullOrEmpty(secret)) sb.Append("password=").Append(secret).Append('\n');
        sb.Append('\n');
        return sb.ToString();
    }
}
