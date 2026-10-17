using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Vegha.Integrations.Git;

/// <summary>Downloads a private copy of git when none is installed: MinGit (Git for Windows'
/// portable build) on Windows, dugite-native (the portable git GitHub Desktop ships) on macOS
/// and Linux. Extracts into <see cref="GitExecutableLocator.ManagedInstallRoot"/>.
///
/// Integrity: the release asset's SHA-256 published by GitHub (the asset <c>digest</c> field,
/// or the companion <c>.sha256</c> asset dugite-native publishes) must match the downloaded
/// bytes, otherwise nothing is extracted.</summary>
public sealed class GitRuntimeInstaller
{
    private readonly Func<HttpClient> _httpFactory;

    public GitRuntimeInstaller(Func<HttpClient>? httpFactory = null)
    {
        _httpFactory = httpFactory ?? (() => new HttpClient());
    }

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    /// <summary>Downloads, verifies and installs; reports human-readable progress.</summary>
    public async Task InstallAsync(IProgress<string>? progress, CancellationToken ct)
    {
        using var http = _httpFactory();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Vegha");
        http.Timeout = TimeSpan.FromMinutes(10);

        progress?.Report("Looking up the latest portable Git release…");
        var asset = await FindAssetAsync(http, ct).ConfigureAwait(false);

        var tmp = Path.Combine(Path.GetTempPath(), "vegha-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var archive = Path.Combine(tmp, asset.Name);
            progress?.Report($"Downloading {asset.Name} ({asset.Size / 1024 / 1024} MB)…");
            await DownloadAsync(http, asset.Url, archive, asset.Size, progress, ct).ConfigureAwait(false);

            progress?.Report("Verifying checksum…");
            var expected = asset.Sha256 ?? (asset.Sha256Url is null ? null : await ReadShaAssetAsync(http, asset.Sha256Url, ct).ConfigureAwait(false));
            if (string.IsNullOrEmpty(expected))
                throw new InvalidOperationException("The release has no published SHA-256 checksum; refusing to install an unverified download.");
            await using (var fs = File.OpenRead(archive))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Checksum mismatch — the download was corrupted or tampered with.");
            }

            progress?.Report("Extracting…");
            var root = GitExecutableLocator.ManagedInstallRoot;
            var staging = root + ".new";
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
            if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ZipFile.ExtractToDirectory(archive, staging);
            else
            {
                await using var gz = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gz, staging, overwriteFiles: true, ct).ConfigureAwait(false);
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            Directory.Move(staging, root);
            progress?.Report("Git installed.");
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    private sealed record Asset(string Name, string Url, long Size, string? Sha256, string? Sha256Url);

    private static async Task<Asset> FindAssetAsync(HttpClient http, CancellationToken ct)
    {
        var arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        string api;
        Func<string, bool> match;
        if (OperatingSystem.IsWindows())
        {
            api = "https://api.github.com/repos/git-for-windows/git/releases/latest";
            var suffix = arm ? "-arm64.zip" : "-64-bit.zip";
            match = n => n.StartsWith("MinGit-", StringComparison.Ordinal) && n.EndsWith(suffix, StringComparison.Ordinal) && !n.Contains("busybox", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            api = "https://api.github.com/repos/desktop/dugite-native/releases/latest";
            var os = OperatingSystem.IsMacOS() ? "macOS" : "ubuntu";
            var cpu = arm ? "arm64" : "x64";
            match = n => n.Contains($"-{os}-{cpu}", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".tar.gz", StringComparison.Ordinal);
        }

        using var resp = await http.GetAsync(api, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        var assets = doc.RootElement.GetProperty("assets").EnumerateArray().ToList();
        var pick = assets.FirstOrDefault(a => match(a.GetProperty("name").GetString() ?? ""));
        if (pick.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("No portable Git build is published for this platform.");

        var name = pick.GetProperty("name").GetString()!;
        string? sha = null;
        if (pick.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:", StringComparison.Ordinal))
            sha = digest["sha256:".Length..];
        var shaAsset = assets.FirstOrDefault(a => a.GetProperty("name").GetString() == name + ".sha256");
        return new Asset(name, pick.GetProperty("browser_download_url").GetString()!, pick.GetProperty("size").GetInt64(), sha,
            shaAsset.ValueKind == JsonValueKind.Undefined ? null : shaAsset.GetProperty("browser_download_url").GetString());
    }

    private static async Task<string?> ReadShaAssetAsync(HttpClient http, string url, CancellationToken ct)
    {
        var text = await http.GetStringAsync(url, ct).ConfigureAwait(false);
        return text.Trim().Split(' ', '\t', '\n')[0];
    }

    private static async Task DownloadAsync(HttpClient http, string url, string target, long size, IProgress<string>? progress, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(target);
        var buf = new byte[81920];
        long total = 0;
        var lastPct = -1;
        int read;
        while ((read = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
            total += read;
            if (size > 0)
            {
                var pct = (int)(total * 100 / size);
                if (pct != lastPct && pct % 5 == 0) { lastPct = pct; progress?.Report($"Downloading… {pct}%"); }
            }
        }
    }
}
