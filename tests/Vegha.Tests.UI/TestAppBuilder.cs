using Avalonia;
using Avalonia.Headless;
using AppType = Vegha.App.App;

// Avalonia.Headless.XUnit picks up this attribute at assembly load and uses the named
// method to construct the AppBuilder. The real Program.BuildAvaloniaApp uses
// .UsePlatformDetect(), which picks the macOS/Windows native backend — we override here
// to .UseHeadless so tests run without needing a real OS window server.
[assembly: AvaloniaTestApplication(typeof(Vegha.Tests.UI.TestAppBuilder))]

namespace Vegha.Tests.UI;

public static class TestAppBuilder
{
    // `using AppType = ...` is needed because the bare name `App` would otherwise resolve
    // to the ambient `Vegha.App` namespace (the parent of Vegha.App)
    // rather than the App class itself.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<AppType>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = true,
            });
}
