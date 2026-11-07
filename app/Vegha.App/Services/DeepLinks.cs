using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Vegha.App.ViewModels.Services;

namespace Vegha.App.Services;

/// <summary>A <c>vegha://</c> link that reached this process, parsed or rejected.</summary>
internal sealed record DeepLinkArrival(string Raw, DeepLinkRequest? Request, string? Error);

/// <summary>
/// Everything behind <c>vegha://</c> links (see <see cref="DeepLinkRequest"/> for the grammar):
/// <list type="bullet">
/// <item><b>Registration</b> — tells the OS that this executable handles the scheme. The
/// Microsoft Store (MSIX) and Mac App Store builds declare it in their manifests, so
/// <see cref="RegisterProtocolHandler"/> only writes the per-user registry key on the
/// Velopack Windows build and a <c>.desktop</c> entry on Linux. macOS picks the scheme up
/// from the bundle's Info.plist (both the DMG and Mac App Store builds ship one).</item>
/// <item><b>Single instance</b> — on Windows and Linux the OS starts a <i>new</i> process
/// per link, with the URL in argv. <see cref="TryForwardToRunningInstance"/> hands the link
/// to an already-running Vegha over a named pipe and lets the newcomer exit, so the user
/// never ends up with two windows. macOS routes the URL to the running app itself
/// (Avalonia raises <c>IActivatableLifetime.Activated</c> with
/// <c>ActivationKind.OpenUri</c>), so no pipe is needed there.</item>
/// <item><b>Queueing</b> — links can arrive before the main window exists (cold start) or
/// at any time afterwards. <see cref="Enqueue"/> parks them until
/// <see cref="AttachHandler"/> is called from the UI thread, then delivers everything in
/// order, always on the UI thread.</item>
/// </list>
/// </summary>
internal static class DeepLinks
{
    private static readonly object Gate = new();
    private static readonly Queue<DeepLinkArrival> Pending = new();
    private static Func<DeepLinkArrival, Task>? _handler;

    /// <summary>Per-user pipe so two desktop sessions on one machine don't cross wires.</summary>
    private static string PipeName
    {
        get
        {
            var user = new string(Environment.UserName.Where(char.IsLetterOrDigit).ToArray());
            return $"vegha-deeplink-{user}";
        }
    }

    /// <summary>The first <c>vegha:</c> argument on the command line, if any.</summary>
    public static string? FindInArgs(IEnumerable<string> args)
        => args.FirstOrDefault(DeepLinkRequest.IsDeepLink);

    /// <summary>If another Vegha is already listening, sends <paramref name="link"/> to it and
    /// returns true — the caller should then exit without showing any UI. Returns false when no
    /// instance answered within a short timeout (this process becomes the main instance).</summary>
    public static bool TryForwardToRunningInstance(string link)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 750);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.WriteLine(link);
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Starts the pipe server that receives links from later launches. Runs for the
    /// life of the process; gives up quietly if another instance already owns the pipe.</summary>
    public static void StartListener()
    {
        if (OperatingSystem.IsMacOS()) return; // LaunchServices delivers links to the running app.
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync().ConfigureAwait(false);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(line)) Enqueue(line.Trim());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Pipe already owned by another Vegha instance (or no permission) — that
                    // instance will handle links. Nothing to do here.
                    Debug.WriteLine($"[deeplink] listener stopped: {ex.Message}");
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[deeplink] listener error: {ex}");
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
        });
    }

    /// <summary>Thread-safe. Parses the link and either hands it to the attached handler (on the
    /// UI thread) or parks it until one is attached.</summary>
    public static void Enqueue(string raw)
    {
        var request = DeepLinkRequest.TryParse(raw, out var error);
        var arrival = new DeepLinkArrival(raw, request, error);
        Func<DeepLinkArrival, Task>? handler;
        lock (Gate)
        {
            handler = _handler;
            if (handler is null)
            {
                Pending.Enqueue(arrival);
                return;
            }
        }
        Dispatch(handler, arrival);
    }

    /// <summary>Call once from the UI thread when the main window is ready to show dialogs.
    /// Delivers anything that arrived earlier, in order, then everything that follows.</summary>
    public static void AttachHandler(Func<DeepLinkArrival, Task> handler)
    {
        List<DeepLinkArrival> backlog;
        lock (Gate)
        {
            _handler = handler;
            backlog = Pending.ToList();
            Pending.Clear();
        }
        foreach (var arrival in backlog) Dispatch(handler, arrival);
    }

    private static Task _tail = Task.CompletedTask;

    /// <summary>Runs handlers one at a time on the UI thread: a second link clicked while the
    /// first one's dialog is still open waits for that dialog to close instead of stacking.</summary>
    private static void Dispatch(Func<DeepLinkArrival, Task> handler, DeepLinkArrival arrival)
    {
        lock (Gate) _tail = RunAfter(_tail, handler, arrival);

        static async Task RunAfter(Task previous, Func<DeepLinkArrival, Task> handler, DeepLinkArrival arrival)
        {
            try { await previous.ConfigureAwait(false); } catch { /* logged by its own run */ }
            try
            {
                await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => handler(arrival));
            }
            catch (Exception ex) { Debug.WriteLine($"[deeplink] handler failed: {ex}"); }
        }
    }

    // ----- OS registration ------------------------------------------------------------

    /// <summary>Makes this executable the per-user handler for <c>vegha://</c>. Idempotent and
    /// cheap: only writes when the stored command differs from the running binary, so moving
    /// or updating the install self-heals on next launch. Never throws.</summary>
    public static void RegisterProtocolHandler()
    {
#if VEGHA_MSIX || VEGHA_MAS
        // Declared in Package.appxmanifest / Info.plist; the Store installs it for us.
        return;
#else
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            if (OperatingSystem.IsWindows()) RegisterWindows(exe);
            else if (OperatingSystem.IsLinux()) RegisterLinux(exe);
            // macOS: CFBundleURLTypes in Info.plist (pack-mac.sh / Resources/Info.plist).
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[deeplink] protocol registration failed: {ex.Message}");
        }
#endif
    }

#if !VEGHA_MSIX && !VEGHA_MAS
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RegisterWindows(string exe)
    {
        var command = $"\"{exe}\" \"%1\"";
        using var root = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + DeepLinkRequest.Scheme);
        using var cmd = root.CreateSubKey(@"shell\open\command");
        if (string.Equals(cmd.GetValue(null) as string, command, StringComparison.OrdinalIgnoreCase)
            && root.GetValue("URL Protocol") is not null)
            return;
        root.SetValue(null, "URL:Vegha link");
        root.SetValue("URL Protocol", string.Empty);
        using var icon = root.CreateSubKey("DefaultIcon");
        icon.SetValue(null, $"\"{exe}\",0");
        cmd.SetValue(null, command);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static void RegisterLinux(string exe)
    {
        // Velopack's AppImage ships a .desktop without a MimeType line, and the AppImage path
        // is user-chosen anyway, so we install a hidden companion entry that points at the
        // running image (or the bare executable for tarball installs).
        var target = Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage ? appImage : exe;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(home, ".local", "share", "applications");
        const string fileName = "vegha-url-handler.desktop";
        var path = Path.Combine(dir, fileName);
        var content = "[Desktop Entry]\n" +
                      "Type=Application\n" +
                      "Name=Vegha\n" +
                      "Comment=Open vegha:// links in Vegha\n" +
                      "NoDisplay=true\n" +
                      $"Exec=\"{target.Replace("\"", "\\\"")}\" %u\n" +
                      "Terminal=false\n" +
                      $"MimeType=x-scheme-handler/{DeepLinkRequest.Scheme};\n";
        if (File.Exists(path) && File.ReadAllText(path) == content) return;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, content);
        RunQuietly("xdg-mime", "default", fileName, $"x-scheme-handler/{DeepLinkRequest.Scheme}");
        RunQuietly("update-desktop-database", dir);
    }

    private static void RunQuietly(string fileName, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch { /* tool missing — the .desktop file alone is enough for most desktops */ }
    }
#endif
}
