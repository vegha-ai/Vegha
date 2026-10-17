using System.Diagnostics;

namespace Vegha.Integrations.Git;

/// <summary>Finds the git executable Vegha should use. Order: explicit setting → bundled copy
/// (Mac App Store build embeds git inside the app) → PATH → well-known install locations →
/// the copy Vegha downloaded on demand (<see cref="GitRuntimeInstaller"/>).
///
/// On macOS <c>/usr/bin/git</c> is an Xcode shim that pops the Command Line Tools installer
/// when the tools are absent, so it's only accepted when <c>xcode-select -p</c> succeeds.</summary>
public sealed class GitExecutableLocator
{
    /// <summary>Minimum git version for the features Vegha relies on (GIT_CONFIG_COUNT env
    /// config injection landed in 2.31).</summary>
    public static readonly Version MinimumVersion = new(2, 31);

    private readonly object _gate = new();
    private string? _overridePath;
    private GitExecutable? _cached;
    private bool _probed;

    /// <summary>Folder where <see cref="GitRuntimeInstaller"/> extracts the on-demand copy.</summary>
    public static string ManagedInstallRoot =>
        OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Vegha", "git")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vegha", "git");

    /// <summary>User-configured path (Settings → Git → Git executable). Null/empty = auto.</summary>
    public string? OverridePath
    {
        get { lock (_gate) return _overridePath; }
        set { lock (_gate) { _overridePath = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); _probed = false; _cached = null; } }
    }

    /// <summary>The resolved executable, or null when no usable git exists.</summary>
    public GitExecutable? Current
    {
        get
        {
            lock (_gate)
            {
                if (!_probed) { _cached = Probe(); _probed = true; }
                return _cached;
            }
        }
    }

    /// <summary>Forgets the cached probe — call after installing git or changing settings.</summary>
    public void Invalidate() { lock (_gate) { _probed = false; _cached = null; } }

    private GitExecutable? Probe()
    {
        foreach (var (path, source) in Candidates())
        {
            var version = ReadVersion(path);
            if (version is not null) return new GitExecutable(path, version, source, ParseVersion(version));
        }
        return null;
    }

    private IEnumerable<(string Path, GitExecutableSource Source)> Candidates()
    {
        if (_overridePath is { } o) yield return (o, GitExecutableSource.Custom);

        var bundled = BundledPath();
        if (bundled is not null && File.Exists(bundled)) yield return (bundled, GitExecutableSource.Bundled);

        foreach (var p in PathCandidates()) yield return (p, GitExecutableSource.System);

        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                     })
            {
                if (string.IsNullOrEmpty(root)) continue;
                var p = Path.Combine(root, "Git", "cmd", "git.exe");
                if (File.Exists(p)) yield return (p, GitExecutableSource.System);
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            foreach (var p in new[] { "/opt/homebrew/bin/git", "/usr/local/bin/git" })
                if (File.Exists(p)) yield return (p, GitExecutableSource.System);
            if (File.Exists("/usr/bin/git") && XcodeToolsInstalled()) yield return ("/usr/bin/git", GitExecutableSource.System);
        }
        else
        {
            if (File.Exists("/usr/bin/git")) yield return ("/usr/bin/git", GitExecutableSource.System);
        }

        var managed = ManagedExecutablePath();
        if (File.Exists(managed)) yield return (managed, GitExecutableSource.Managed);
    }

    /// <summary>Path of git.exe inside the on-demand install.</summary>
    public static string ManagedExecutablePath() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(ManagedInstallRoot, "cmd", "git.exe")
            : Path.Combine(ManagedInstallRoot, "bin", "git");

    /// <summary>Git embedded in the app bundle (Mac App Store build): Contents/Helpers/git/bin/git.</summary>
    public static string? BundledPath()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var baseDir = AppContext.BaseDirectory.TrimEnd('/');
        // BaseDirectory = …/Vegha.app/Contents/MacOS
        var contents = Path.GetDirectoryName(baseDir);
        return contents is null ? null : Path.Combine(contents, "Helpers", "git", "bin", "git");
    }

    private static IEnumerable<string> PathCandidates()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var exe = OperatingSystem.IsWindows() ? "git.exe" : "git";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string full;
            try { full = Path.Combine(dir.Trim('"'), exe); }
            catch { continue; }
            if (!File.Exists(full)) continue;
            // Skip the Xcode shim on macOS unless the tools are present (it would pop an installer).
            if (OperatingSystem.IsMacOS() && full == "/usr/bin/git" && !XcodeToolsInstalled()) continue;
            yield return full;
        }
    }

    private static bool XcodeToolsInstalled()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("/usr/bin/xcode-select", "-p")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return false;
            p.WaitForExit(3000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch { return false; }
    }

    public static string? ReadVersion(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return p.HasExited && p.ExitCode == 0 && output.StartsWith("git version", StringComparison.Ordinal)
                ? output.Trim()
                : null;
        }
        catch { return null; }
    }

    /// <summary>Parses "git version 2.43.0.windows.1" → 2.43.0.</summary>
    public static Version? ParseVersion(string? versionString)
    {
        if (string.IsNullOrEmpty(versionString)) return null;
        var token = versionString.Replace("git version", "", StringComparison.Ordinal).Trim().Split(' ')[0];
        var parts = token.Split('.').Take(3).Select(s => int.TryParse(new string(s.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0).ToArray();
        return parts.Length switch
        {
            0 => null,
            1 => new Version(parts[0], 0),
            2 => new Version(parts[0], parts[1]),
            _ => new Version(parts[0], parts[1], parts[2]),
        };
    }
}

public enum GitExecutableSource { Custom, Bundled, System, Managed }

/// <summary>A probed git executable.</summary>
public sealed record GitExecutable(string Path, string VersionString, GitExecutableSource Source, Version? Version)
{
    public bool MeetsMinimum => Version is null || Version >= GitExecutableLocator.MinimumVersion;
}
