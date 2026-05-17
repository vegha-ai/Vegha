using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Vegha.App.Controls.Workspace;

/// <summary>Modal "Create Workspace" dialog. On Create, exposes <see cref="Result"/>;
/// the caller is responsible for invoking <c>WorkspacesViewModel.CreateWorkspace</c>
/// (which writes <c>workspace.yml</c> + subfolder layout and activates).</summary>
public partial class CreateWorkspaceDialog : Window
{
    public CreateWorkspaceResult? Result { get; private set; }

    public CreateWorkspaceDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.RemoveMinimizeMaximize();
        // Default to the parent of the user's Roaming AppData Vegha folder so the
        // user has a sensible starting location distinct from the bootstrapped default.
        var defaultParent = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents");
        LocationBox.Text = defaultParent;
        NameBox.Text = "";

        NameBox.TextChanged += (_, _) => UpdateResolved();
        LocationBox.TextChanged += (_, _) => UpdateResolved();
        UpdateResolved();
    }

    private void UpdateResolved()
    {
        var name = NameBox.Text?.Trim() ?? string.Empty;
        var loc = LocationBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(loc))
        {
            ResolvedPath.Text = "Will create: (enter a name + location)";
            return;
        }
        var sanitized = Sanitize(name);
        ResolvedPath.Text = $"Will create: {Path.Combine(loc, sanitized)}";
    }

    private static string Sanitize(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(clean) ? "workspace" : clean;
    }

    private async void OnBrowse_Click(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a parent folder for the new workspace",
            AllowMultiple = false,
        });
        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) LocationBox.Text = path;
    }

    private void OnCreate_Click(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? string.Empty;
        var loc = LocationBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(loc)) return;
        Result = new CreateWorkspaceResult(name, Path.Combine(loc, Sanitize(name)));
        Close(true);
    }

    private void OnCancel_Click(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close(false);
    }
}

public sealed record CreateWorkspaceResult(string Name, string FolderPath);
