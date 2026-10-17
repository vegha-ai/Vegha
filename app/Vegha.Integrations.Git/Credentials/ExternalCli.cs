using System.Diagnostics;
using System.Text;

namespace Vegha.Integrations.Git.Credentials;

/// <summary>Runs helper CLIs that participate in git auth: <c>gh</c>, <c>az</c>,
/// <c>ssh</c>, <c>ssh-keygen</c>, <c>ssh-keyscan</c>. Resolves executables on PATH plus the
/// usual install locations (GUI apps on macOS don't inherit the shell PATH, so Homebrew paths
/// are probed explicitly).</summary>
public sealed class ExternalCli
{
    public sealed record Result(int ExitCode, string StdOut, string StdErr)
    {
        public bool Success => ExitCode == 0;
    }

    private readonly Dictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Resolves <paramref name="tool"/> to an absolute path, or null when not installed.</summary>
    public string? Find(string tool)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(tool, out var cached)) return cached;
            var found = Probe(tool);
            _cache[tool] = found;
            return found;
        }
    }

    public void Invalidate() { lock (_gate) _cache.Clear(); }

    private static string? Probe(string tool)
    {
        var names = OperatingSystem.IsWindows()
            ? new[] { tool + ".exe", tool + ".cmd", tool + ".bat" }
            : new[] { tool };
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim('"')).ToList();

        if (OperatingSystem.IsWindows())
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            dirs.AddRange(new[]
            {
                Path.Combine(sys, "OpenSSH"),
                Path.Combine(pf, "GitHub CLI"),
                Path.Combine(pf, "Microsoft SDKs", "Azure", "CLI2", "wbin"),
                Path.Combine(pf86, "Microsoft SDKs", "Azure", "CLI2", "wbin"),
                Path.Combine(pf, "Git", "usr", "bin"),
            });
        }
        else
        {
            dirs.AddRange(new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin" });
        }

        foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var n in names)
            {
                string full;
                try { full = Path.Combine(dir, n); }
                catch { continue; }
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }

    public async Task<Result> RunAsync(string tool, IReadOnlyList<string> args, CancellationToken ct = default,
        string? stdin = null, Action<string>? onLine = null, TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? env = null)
    {
        var exe = Find(tool) ?? throw new FileNotFoundException($"'{tool}' is not installed or not on PATH.", tool);
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is null) return; lock (stdout) stdout.AppendLine(e.Data); onLine?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is null) return; lock (stderr) stderr.AppendLine(e.Data); onLine?.Invoke(e.Data); };
        if (!p.Start()) throw new InvalidOperationException($"Failed to start {tool}.");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (stdin is not null) await p.StandardInput.WriteAsync(stdin.AsMemory(), ct).ConfigureAwait(false);
        p.StandardInput.Close();

        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try { await p.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            return new Result(-1, stdout.ToString(), stderr + $"{tool} timed out.");
        }
        string o, e2;
        lock (stdout) o = stdout.ToString();
        lock (stderr) e2 = stderr.ToString();
        return new Result(p.ExitCode, o, e2);
    }
}
