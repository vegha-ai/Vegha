using System.Diagnostics;
using System.Text;

namespace Vegha.Integrations.Git;

/// <summary>Async wrapper around the git executable. All network ops (fetch / pull / push /
/// clone / ls-remote) shell out through here — that path picks up Git Credential Manager
/// (incl. browser SSO / MFA / WAM), the SSH agent and ~/.ssh/config, proxies and enterprise
/// gitconfig for free, which libgit2sharp does not.</summary>
public sealed class GitProcessRunner
{
    /// <summary>Result of a single <c>git</c> invocation.</summary>
    public sealed record Result(int ExitCode, string StdOut, string StdErr)
    {
        public bool Success => ExitCode == 0;
    }

    private readonly GitExecutableLocator _locator;

    public GitProcessRunner() : this(new GitExecutableLocator()) { }

    public GitProcessRunner(GitExecutableLocator locator)
    {
        _locator = locator;
    }

    public GitExecutableLocator Locator => _locator;

    /// <summary>True when a usable git executable was found.</summary>
    public bool IsAvailable => _locator.Current is not null;

    /// <summary>Output of <c>git --version</c> (e.g. "git version 2.43.0.windows.1"), or null when git isn't available.</summary>
    public string? Version => _locator.Current?.VersionString;

    /// <summary>Absolute path of the git executable in use ("git" when unresolved, so callers get
    /// a clear "file not found" rather than a null reference).</summary>
    public string ExecutablePath => _locator.Current?.Path ?? "git";

    public Task<Result> RunAsync(string workingDir, IReadOnlyList<string> args, string? stdin = null, CancellationToken ct = default) =>
        RunAsync(workingDir, args, new GitRunOptions { StdIn = stdin }, ct);

    public async Task<Result> RunAsync(string workingDir, IReadOnlyList<string> args, GitRunOptions options, CancellationToken ct = default)
    {
        if (!IsAvailable) throw new GitOperationException(args.Count > 0 ? args[0] : "git", -1, "Git is not installed or could not be found.", GitErrorKind.GitNotInstalled);

        // ProcessStartInfo.ArgumentList builds a properly-escaped command line per the
        // platform's quoting rules — manual quoting is a footgun on Windows.
        var psi = new ProcessStartInfo(ExecutablePath)
        {
            WorkingDirectory = workingDir,
            // Always redirect stdin so we can close it immediately when there's no input —
            // otherwise git inherits the host process's stdin (none, for a GUI app) and can
            // hang on interactive prompts. GCM's GUI flow still works because it pops a
            // separate window / browser for the user.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        ApplyRuntimeLayout(psi.Environment);
        (options.Environment ?? new GitEnvironment()).ApplyTo(psi.Environment);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var stdoutBuf = new StringBuilder();
        var stderrBuf = new StringBuilder();

        if (!p.Start()) throw new InvalidOperationException("Failed to start git process.");

        // stdout via line events; stderr read raw so "\r"-delimited progress updates
        // ("Receiving objects:  42%") stream live instead of arriving at exit.
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdoutBuf) stdoutBuf.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        var stderrTask = PumpStdErrAsync(p.StandardError, stderrBuf, options.OnProgress);

        if (options.StdIn is not null)
            await p.StandardInput.WriteAsync(options.StdIn.AsMemory(), ct).ConfigureAwait(false);
        // Close stdin regardless so git doesn't block waiting for input we'll never send.
        p.StandardInput.Close();

        using var timeoutCts = options.Timeout is { } t ? new CancellationTokenSource(t) : null;
        using var linked = timeoutCts is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var waitToken = linked?.Token ?? ct;
        try
        {
            await p.WaitForExitAsync(waitToken).ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            if (timeoutCts?.IsCancellationRequested == true && !ct.IsCancellationRequested)
                throw new GitOperationException(args.Count > 0 ? args[0] : "git", -1,
                    $"Timed out after {options.Timeout!.Value.TotalSeconds:0}s.", GitErrorKind.Timeout);
            throw;
        }

        string stdout;
        lock (stdoutBuf) stdout = stdoutBuf.ToString();
        return new Result(p.ExitCode, stdout, stderrBuf.ToString());
    }

    /// <summary>Portable git builds (dugite-native, the Mac App Store embedded copy) need to be
    /// told where their helper programs and templates live.</summary>
    private void ApplyRuntimeLayout(IDictionary<string, string?> env)
    {
        var exe = _locator.Current;
        if (exe is null || OperatingSystem.IsWindows()) return;
        if (exe.Source is not (GitExecutableSource.Managed or GitExecutableSource.Bundled)) return;
        var root = Path.GetDirectoryName(Path.GetDirectoryName(exe.Path));
        if (root is null) return;
        env["GIT_EXEC_PATH"] = Path.Combine(root, "libexec", "git-core");
        env["GIT_TEMPLATE_DIR"] = Path.Combine(root, "share", "git-core", "templates");
        if (exe.Source == GitExecutableSource.Bundled) env["GIT_CONFIG_NOSYSTEM"] = "1";
    }

    private static async Task PumpStdErrAsync(StreamReader reader, StringBuilder sink, Action<string>? onLine)
    {
        var buf = new char[1024];
        var line = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buf.AsMemory()).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buf[i];
                if (c == '\n' || c == '\r')
                {
                    if (line.Length > 0)
                    {
                        var text = line.ToString();
                        // Keep only completed lines in the transcript; progress repaint frames
                        // ("\r") are reported live but only the last one is kept.
                        if (c == '\n') sink.AppendLine(text);
                        onLine?.Invoke(text);
                        line.Clear();
                    }
                }
                else line.Append(c);
            }
        }
        if (line.Length > 0)
        {
            sink.AppendLine(line.ToString());
            onLine?.Invoke(line.ToString());
        }
    }
}

/// <summary>Options for a single git invocation.</summary>
public sealed class GitRunOptions
{
    public string? StdIn { get; init; }

    /// <summary>Extra env vars + injected config (credentials, proxy, ssh command).</summary>
    public GitEnvironment? Environment { get; init; }

    /// <summary>Called for each stderr line / progress frame as it arrives (worker thread).</summary>
    public Action<string>? OnProgress { get; init; }

    /// <summary>Kill the process after this long. Null = no timeout.</summary>
    public TimeSpan? Timeout { get; init; }
}
