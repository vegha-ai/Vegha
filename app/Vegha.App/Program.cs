using Avalonia;
using System;
using System.Diagnostics;
using Velopack;

namespace Vegha.App;

class Program
{
    /// <summary>Wall-clock since process entry. Used by startup instrumentation in
    /// <see cref="MainWindow"/> to surface paint/load timings via Debug.WriteLine.</summary>
    public static readonly Stopwatch StartupClock = Stopwatch.StartNew();

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        TraceStartupReset();
        Trace("Main:enter");
#if !VEGHA_MSIX && !VEGHA_MAS
        // Velopack must run before Avalonia: if the EXE was launched as part of
        // an install/update step (--squirrel-install, --veloapp-uninstall, etc.)
        // the runtime exits here without showing UI.
        // The Microsoft Store (VEGHA_MSIX) and Mac App Store (VEGHA_MAS)
        // flavors route updates through their respective Store channels — Velopack
        // is disabled at compile time on both. These symbols are defined by
        // Directory.Build.props from $(VeghaFlavor); the casing MUST match it
        // (an earlier mixed-case "Vegha_MSIX" guard never matched, so Velopack
        // wrongly ran in Store builds).
        VelopackApp.Build().Run();
#endif
        Trace("Main:post-velopack");

        // Capture any unhandled exception to %LocalAppData%/Vegha/crash.log so the user
        // can share the stack trace when the app dies mid-action.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash(e.Exception);
            e.SetObserved();
        };

        Trace("Main:pre-avalonia-start");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Startup phase tracing for diagnosing time-to-first-paint. Off unless the
    /// <c>VEGHA_TRACE_STARTUP=1</c> environment variable is set; writes phase timestamps to
    /// <c>%TEMP%/vegha-startup.log</c>. Cheap no-op in normal runs.</summary>
    internal static void Trace(string label)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("VEGHA_TRACE_STARTUP") != "1") return;
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vegha-startup.log");
            System.IO.File.AppendAllText(path, $"{StartupClock.ElapsedMilliseconds,6} ms  {label}{Environment.NewLine}");
        }
        catch { /* tracing must never affect startup */ }
    }

    private static void TraceStartupReset()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("VEGHA_TRACE_STARTUP") != "1") return;
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vegha-startup.log");
            System.IO.File.WriteAllText(path, $"=== Vegha startup trace {DateTime.Now:O} ==={Environment.NewLine}");
        }
        catch { /* best-effort */ }
    }

    internal static void LogCrash(Exception? ex)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Vegha");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "crash.log");
            System.IO.File.AppendAllText(path,
                $"[{DateTime.Now:O}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* best-effort */ }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Force popups (ComboBox dropdowns, MenuFlyouts) to render inside the parent
            // window's overlay layer instead of as separate top-level OS windows. The
            // overlay layer lives inside the LayoutTransformControl that ZoomHost wraps
            // around each window's Content, so popups now scale with the interface-zoom
            // setting. Default Avalonia uses real OS child windows for popups, which
            // escape the transform and look stuck-at-100% when zoomed.
            .With(new global::Avalonia.Win32PlatformOptions { OverlayPopups = true })
            .With(new global::Avalonia.AvaloniaNativePlatformOptions { OverlayPopups = true })
            .WithInterFont()
            .LogToTrace();
}
