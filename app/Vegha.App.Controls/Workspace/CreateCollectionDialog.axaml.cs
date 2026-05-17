using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Vegha.App.Controls.Workspace;

/// <summary>Modal "Create Collection" dialog. Defaults the location to the active
/// workspace's <c>collections/</c> folder. Returns name + absolute folder path; the
/// caller writes a minimal <c>collection.bru</c> on disk and asks the
/// CollectionsViewModel to load it.</summary>
public partial class CreateCollectionDialog : Window
{
    public CreateCollectionResult? Result { get; private set; }

    public CreateCollectionDialog() : this(string.Empty) { }

    public CreateCollectionDialog(string defaultLocation)
    {
        InitializeComponent();
        LocationBox.Text = defaultLocation;
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
        ResolvedPath.Text = $"Will create: {Path.Combine(loc, Sanitize(name))}";
    }

    private static string Sanitize(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(clean) ? "collection" : clean;
    }

    private async void OnBrowse_Click(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a parent folder for the new collection",
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
        Result = new CreateCollectionResult(name, Path.Combine(loc, Sanitize(name)));
        Close(true);
    }

    private void OnCancel_Click(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close(false);
    }
}

public sealed record CreateCollectionResult(string Name, string FolderPath);
