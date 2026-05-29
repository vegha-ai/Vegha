using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Vegha.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Vegha.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        Program.Trace("App.Initialize:start (loading App.axaml styles/themes)");
        AvaloniaXamlLoader.Load(this);
        Program.Trace("App.Initialize:end");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Program.Trace("FII:start");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Show a lightweight splash immediately. Building the DI graph + resolving
            // MainWindowViewModel (which reads workspaces.json, constructs the seed editor /
            // JintHost, etc.) and inflating MainWindow's XAML all take a noticeable beat — and
            // they must run on the UI thread because several view-models capture
            // SynchronizationContext.Current in their constructors. The splash gives the user
            // immediate feedback instead of a blank screen while that work happens.
            var splash = new SplashWindow();
            splash.Opened += (_, _) => Program.Trace("splash:opened (first pixels)");
            splash.Show();
            Program.Trace("FII:splash-show-queued");

            // Defer the heavy build to Background priority so the splash actually paints first,
            // then build the real window and swap. MainWindow.OnLoaded already defers the
            // workspace-tree load past first paint (see WorkspacesViewModel.ApplyActiveAsync),
            // so the main window appears as a responsive shell and fills in afterward.
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    Program.Trace("build:start (DI + MainWindowViewModel + MainWindow)");
                    Services = new ServiceCollection()
                        .AddVeghaServices()
                        .BuildServiceProvider();
                    Program.Trace("build:di-ready");

                    var main = new MainWindow
                    {
                        DataContext = Services.GetRequiredService<MainWindowViewModel>()
                    };
                    Program.Trace("build:mainwindow-constructed");
                    desktop.MainWindow = main;
                    main.Show();
                    splash.Close();
                    Program.Trace("build:main-shown splash-closed");
                }
                catch (Exception ex)
                {
                    // Preserve the crash-log behavior the synchronous path had, then surface
                    // the fatal startup error rather than leaving a stranded splash.
                    Program.LogCrash(ex);
                    splash.Close();
                    throw;
                }
            }, global::Avalonia.Threading.DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
