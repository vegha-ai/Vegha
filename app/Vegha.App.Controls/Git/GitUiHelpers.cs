using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Vegha.App.Controls.Workspace;

namespace Vegha.App.Controls.Git;

/// <summary>Small helpers shared by the git dialogs.</summary>
public static class GitUiHelpers
{
    public static void OpenUrl(string url) => HelpDialog.OpenUrl(url);

    public static async Task CopyAsync(Visual visual, string text)
    {
        var clipboard = TopLevel.GetTopLevel(visual)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(text);
    }
}
