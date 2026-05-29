using Avalonia.Controls;

namespace Vegha.App;

/// <summary>Minimal startup splash shown immediately so the user sees the app launching while
/// the DI container and main window are built on the UI thread (several view-models capture
/// <c>SynchronizationContext.Current</c> in their constructors, so that work cannot move to a
/// background thread). Closed by <see cref="App"/> once the main window is shown.</summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
    }
}
