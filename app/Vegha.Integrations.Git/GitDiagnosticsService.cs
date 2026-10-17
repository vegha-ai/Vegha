using System.Text;
using Vegha.Integrations.Git.Credentials;

namespace Vegha.Integrations.Git;

/// <summary>"Test connection" for a remote: collects git / GCM versions, helper chain, the
/// credential profile that applies, the effective proxy, and runs <c>git ls-remote</c> with
/// tracing so the user (or their IT team) can see exactly why sync fails. The report is
/// redacted before it's shown or copied.</summary>
public sealed class GitDiagnosticsService
{
    private readonly GitProcessRunner _runner;
    private readonly GitCredentialBroker _broker;
    private readonly GitCredentialsService _credentials;
    private readonly IGitEnvironmentProvider _environment;

    public GitDiagnosticsService(GitProcessRunner runner, GitCredentialBroker broker, GitCredentialsService credentials, IGitEnvironmentProvider environment)
    {
        _runner = runner;
        _broker = broker;
        _credentials = credentials;
        _environment = environment;
    }

    /// <summary>Optional extra redaction (the app plugs in its SecretRedactor).</summary>
    public Func<string, string>? Redactor { get; set; }

    public async Task<GitDiagnosticsReport> RunAsync(string remoteUrl, string? repoRoot, IProgress<string>? progress, CancellationToken ct)
    {
        var sb = new StringBuilder();
        void Line(string s) { sb.AppendLine(s); progress?.Report(s); }

        Line($"Vegha git diagnostics — {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        Line($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})");

        var exe = _runner.Locator.Current;
        if (exe is null)
        {
            Line("Git: NOT FOUND");
            return Finish(sb, GitErrorClassifier.NotInstalled(), success: false);
        }
        Line($"Git: {exe.VersionString} ({exe.Source}) at {exe.Path}{(exe.MeetsMinimum ? "" : $"  ⚠ older than {GitExecutableLocator.MinimumVersion}")}");

        var gcm = await _runner.RunAsync(Path.GetTempPath(), new[] { "credential-manager", "--version" }, ct: ct).ConfigureAwait(false);
        Line($"Git Credential Manager: {(gcm.Success ? gcm.StdOut.Trim() : "not available")}");
        Line($"Credential helpers:\n  {(await _credentials.DescribeHelpersAsync(ct).ConfigureAwait(false)).Replace("\n", "\n  ", StringComparison.Ordinal)}");
        foreach (var tool in new[] { "gh", "az", "ssh" })
            Line($"{tool}: {_broker.Cli.Find(tool) ?? "not found"}");

        var remote = GitRemoteUrl.TryParse(remoteUrl);
        Line($"Remote: {remoteUrl}");
        if (remote is null)
        {
            Line("Remote URL could not be parsed.");
            return Finish(sb, new GitErrorInfo(GitErrorKind.Unknown, "Invalid remote URL", "The URL couldn't be parsed.", Array.Empty<GitErrorAction>()), false);
        }
        Line($"Host: {remote.Host} ({(remote.IsSsh ? "SSH" : remote.Scheme.ToUpperInvariant())}){ProviderLabel(remote.Host)}");
        Line($"Credentials: {_broker.DescribeSource(remoteUrl, repoRoot)}");

        var env = await _environment.BuildAsync(new GitEnvironmentRequest("ls-remote", remoteUrl, repoRoot ?? Path.GetTempPath()), ct).ConfigureAwait(false);
        foreach (var kv in env.Config)
            Line($"  config {kv.Key} = {(kv.Key.Contains("extraHeader", StringComparison.OrdinalIgnoreCase) ? "<redacted>" : kv.Value)}");

        if (remote.IsSsh)
        {
            Line("");
            Line("SSH handshake (ssh -T):");
            try
            {
                var args = new List<string> { "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10" };
                if (remote.Port is { } port) { args.Add("-p"); args.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
                args.Add($"{remote.User ?? "git"}@{remote.Host}");
                var ssh = await _broker.Cli.RunAsync("ssh", args, ct, timeout: TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                Line("  " + (ssh.StdErr + ssh.StdOut).Trim().Replace("\n", "\n  ", StringComparison.Ordinal));
            }
            catch (Exception ex) { Line("  " + ex.Message); }
        }

        Line("");
        Line("git ls-remote (traced):");
        env.Set("GIT_TRACE", "1").Set("GIT_CURL_VERBOSE", "1").Set("GIT_TRACE_CURL_NO_DATA", "1").Set("GCM_TRACE", "1");
        var started = DateTime.UtcNow;
        GitProcessRunner.Result result;
        try
        {
            result = await _runner.RunAsync(repoRoot ?? Path.GetTempPath(), new[] { "ls-remote", "--heads", "--", remoteUrl },
                new GitRunOptions { Environment = env, Timeout = TimeSpan.FromMinutes(5) }, ct).ConfigureAwait(false);
        }
        catch (GitOperationException ex)
        {
            Line("  " + ex.Message);
            return Finish(sb, ex.Error, false);
        }
        Line($"  exit {result.ExitCode} in {(DateTime.UtcNow - started).TotalSeconds:0.0}s");
        foreach (var l in result.StdErr.Split('\n').Where(IsInteresting).Take(200))
            Line("  " + l.TrimEnd('\r'));

        if (result.Success)
        {
            var heads = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            Line($"  ✓ Connected — {heads} branch(es) visible.");
            return Finish(sb, null, true);
        }
        return Finish(sb, GitErrorClassifier.Classify(result.StdErr, "ls-remote"), false);
    }

    private GitDiagnosticsReport Finish(StringBuilder sb, GitErrorInfo? error, bool success)
    {
        if (error is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"Result: {error.Title}");
            sb.AppendLine(error.Explanation);
        }
        var text = Redact(sb.ToString());
        return new GitDiagnosticsReport(success, error, text);
    }

    private string Redact(string text)
    {
        var lines = text.Split('\n').Select(l =>
        {
            var idx = l.IndexOf("Authorization:", StringComparison.OrdinalIgnoreCase);
            return idx >= 0 ? l[..(idx + "Authorization:".Length)] + " <redacted>" : l;
        });
        var joined = string.Join('\n', lines);
        return Redactor is null ? joined : Redactor(joined);
    }

    /// <summary>Drops pack-protocol noise from GIT_TRACE output; keeps HTTP status, auth,
    /// proxy, TLS and GCM lines.</summary>
    private static bool IsInteresting(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        if (line.Contains("<= Recv data", StringComparison.Ordinal) || line.Contains("=> Send data", StringComparison.Ordinal)) return false;
        if (line.Contains("pkt-line", StringComparison.Ordinal)) return false;
        return true;
    }

    private static string ProviderLabel(string host) =>
        GitHostKinds.IsGitHub(host) ? " — GitHub"
        : GitHostKinds.IsAzureDevOps(host) ? " — Azure DevOps"
        : GitHostKinds.IsGitLab(host) ? " — GitLab"
        : GitHostKinds.IsBitbucket(host) ? " — Bitbucket"
        : "";
}

public sealed record GitDiagnosticsReport(bool Success, GitErrorInfo? Error, string Text);
