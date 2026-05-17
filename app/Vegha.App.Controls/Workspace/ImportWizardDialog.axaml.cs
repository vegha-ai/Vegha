using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Vegha.App.ViewModels;

namespace Vegha.App.Controls.Workspace;

/// <summary>Unified Import dialog with four source tabs (File / URL / Git / GitHub).
/// Each tab funnels through <see cref="ImportWizardViewModel"/> which calls
/// <c>ImportPipeline.DetectAndImport</c> on the resulting bytes / folder. Drag-and-drop
/// onto the File tab is wired here in the code-behind because Avalonia's drop events
/// arrive through the visual tree.</summary>
public partial class ImportWizardDialog : Window
{
    public ImportWizardDialog()
    {
        InitializeComponent();
        // Enable drag-and-drop onto the File tab's drop area.
        DragDrop.SetAllowDrop(FileDropArea, true);
        FileDropArea.AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        FileDropArea.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        FileDropArea.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private async void OnBrowseFile_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Pick collection file(s)",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Importable")
                    { Patterns = new[] { "*.json", "*.yaml", "*.yml", "*.wsdl", "*.xml", "*.zip" } },
                new FilePickerFileType("ZIP archive") { Patterns = new[] { "*.zip" } },
                new FilePickerFileType("WSDL / XML") { Patterns = new[] { "*.wsdl", "*.xml" } },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } },
            },
        });
        if (files.Count == 0) return;
        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Cast<string>()
            .ToList();
        if (paths.Count == 0) return;
        if (DataContext is ImportWizardViewModel vm) vm.StageFiles(paths);
    }

    private async void OnBrowseFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Pick a Bruno collection folder",
            AllowMultiple = false,
        });
        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        if (DataContext is ImportWizardViewModel vm) vm.SelectedPath = path;
    }

    /// <summary>Mirrors the ComboBox's selected Tag string into the VM's auth-mode property
    /// + flips the HTTPS-PAT credential panel's visibility.</summary>
    private void OnGitAuthMode_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not ImportWizardViewModel vm) return;
        if (sender is not ComboBox cb || cb.SelectedItem is not ComboBoxItem item) return;
        var mode = item.Tag as string ?? "none";
        vm.GitAuthMode = mode;
        if (this.FindControl<StackPanel>("GitHttpsPanel") is { } panel)
            panel.IsVisible = mode == "https-pat";
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DataFormats.Files))
        {
            e.DragEffects = DragDropEffects.Copy;
            FileDropArea.Classes.Add("dragOver");
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void OnDragLeave(object? sender, RoutedEventArgs e)
    {
        FileDropArea.Classes.Remove("dragOver");
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        FileDropArea.Classes.Remove("dragOver");
        if (DataContext is not ImportWizardViewModel vm) return;
        if (!e.Data.Contains(DataFormats.Files)) return;
        var files = e.Data.GetFiles();
        if (files is null) return;
        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Cast<string>()
            .ToList();
        if (paths.Count == 0) return;
        vm.StageFiles(paths);
        e.Handled = true;
    }

    private void OnImport_Click(object? sender, RoutedEventArgs e)
    {
        // The Import command runs first via binding; we just close after.
        Close(true);
    }

    private void OnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
