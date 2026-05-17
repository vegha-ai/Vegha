using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Vegha.App.ViewModels.Tabs;

namespace Vegha.App.Controls.Workspace;

public partial class CollectionRunTab : UserControl
{
    public CollectionRunTab()
    {
        InitializeComponent();
    }

    private async void OnPickDataFile_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CollectionRunTabViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select iteration data file",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("CSV / JSON")
                {
                    Patterns = new[] { "*.csv", "*.json" },
                },
                new FilePickerFileType("All files") { Patterns = new[] { "*.*" } },
            },
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        await vm.PickDataFileAsync(path);
    }
}
