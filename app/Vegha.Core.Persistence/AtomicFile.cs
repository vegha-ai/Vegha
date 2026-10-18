namespace Vegha.Core.Persistence;

/// <summary>
/// Crash- and reader-safe file replacement for the state files the desktop app, the CLI and
/// the VS Code sidecar share under <c>%LocalAppData%/Vegha</c>. Content goes to a temp file in
/// the same directory and is renamed over the target, so a concurrent reader sees either the
/// old file or the new one — never a truncated, half-written file that it would parse as
/// "empty" and then save back.
/// </summary>
public static class AtomicFile
{
    /// <summary>How long to keep retrying the rename while another process holds the target open.</summary>
    private static readonly TimeSpan ReplaceBudget = TimeSpan.FromSeconds(2);

    public static void WriteAllText(string path, string contents) =>
        Write(path, temp => File.WriteAllText(temp, contents));

    public static void WriteAllBytes(string path, byte[] bytes) =>
        Write(path, temp => File.WriteAllBytes(temp, bytes));

    private static void Write(string path, Action<string> writeTemp)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir ?? ".", "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            writeTemp(temp);
            var deadline = DateTime.UtcNow + ReplaceBudget;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    return;
                }
                // Windows refuses to replace a file another process has open for reading
                // (no FILE_SHARE_DELETE). Readers hold it for milliseconds, so retry with
                // short sleeps — long ones keep landing inside the next read.
                catch (Exception ex) when (DateTime.UtcNow < deadline && ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(Math.Min(attempt, 15));
                }
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best-effort */ }
        }
    }
}
