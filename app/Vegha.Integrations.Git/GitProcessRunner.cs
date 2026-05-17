using System.Diagnostics;
using System.Text;

namespace Vegha.Integrations.Git;

/// <summary>Async wrapper around <c>git.exe</c>. Network ops (fetch / pull / push / clone) prefer
/// shelling out to the system git when available — that path picks up Git Credential Manager,
/// SSH config, HTTP proxies, and signed-push support for free, which libgit2sharp does not.</summary>
public sealed class GitProcessRunner
{
    /// <summary>Result of a single <c>git</c> invocation.</summary>
    public sealed record Result(int ExitCode, string StdOut, string StdErr)
    {
        public bool Success => ExitCode == 0;
    }

    private readonly Lazy<bool> _isAvailable;
    private readonly Lazy<string?> _version;

    public GitProcessRunner()
    {
        _isAvailable = new Lazy<bool>(ProbeAvailable);
        _version = new Lazy<string?>(ProbeVersion);
    }

    /// <summary>True when <c>git --version</c> exits successfully on PATH.</summary>
    public bool IsAvailable => _isAvailable.Value;

    /// <summary>Output of <c>git --version</c> (e.g. "git version 2.43.0.windows.1"), or null when git isn't on PATH.</summary>
    public string? Version => _version.Value;

    public async Task<Result> RunAsync(string workingDir, IReadOnlyList<string> args, string? stdin = null, CancellationToken ct = default)
    {
        // ProcessStartInfo.ArgumentList builds a properly-escaped command line per the
        // platform's quoting rules — manual quoting is a footgun on Windows.
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            // Always redirect stdin so we can close it immediately when there's no input —
            // otherwise git.exe inherits the host process's stdin (none, for a GUI app) and
            // can hang on interactive prompts. GCM's GUI flow still works because it pops a
            // separate window for the user.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // GIT_TERMINAL_PROMPT=0: fail fast instead of blocking on a tty prompt when no
        // credential helper is configured. GCM_INTERACTIVE=auto lets GCM still pop its GUI
        // dialog on Windows; it's the terminal-only fallback that would hang.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var stdoutBuf = new StringBuilder();
        var stderrBuf = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutBuf.AppendLine(e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) stderrBuf.AppendLine(e.Data); };

        if (!p.Start()) throw new InvalidOperationException("Failed to start git process.");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        if (stdin is not null)
            await p.StandardInput.WriteAsync(stdin.AsMemory(), ct).ConfigureAwait(false);
        // Close stdin regardless so git doesn't block waiting for input we'll never send.
        p.StandardInput.Close();

        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        return new Result(p.ExitCode, stdoutBuf.ToString(), stderrBuf.ToString());
    }

    private bool ProbeAvailable() => _version.Value is not null;

    private static string? ProbeVersion()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return p.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
