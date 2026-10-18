using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Vegha.App.ViewModels;

namespace Vegha.App.Controls.Workspace;

/// <summary>Modal Export dialog (File → Export…, collection right-click → Export…). The VM
/// builds the export first, so the save picker can suggest the right name and extension
/// (single file vs. zip). Closes after a clean save; when the format had to drop details,
/// stays open with the warnings listed and Cancel relabelled "Close".</summary>
public partial class ExportDialog : Window
{
    /// <summary>"Exported N collections to …" after a successful save; null when cancelled.
    /// The host surfaces it on the status bar.</summary>
    public string? ResultMessage { get; private set; }

    public ExportDialog()
    {
        InitializeComponent();
    }

    private async void OnExport_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ExportDialogViewModel vm) return;
        var result = await vm.BuildAsync();
        if (result is null) return;

        var ext = result.IsZip ? ".zip" : Path.GetExtension(result.SuggestedFileName);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export",
            SuggestedFileName = result.SuggestedFileName,
            DefaultExtension = ext.TrimStart('.'),
            ShowOverwritePrompt = true,
            FileTypeChoices = new[]
            {
                new FilePickerFileType(result.IsZip ? "Zip archive" : vm.SelectedFormat.Label) { Patterns = new[] { "*" + ext } },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } },
            },
        });
        if (file is null) return;

        bool saved;
        await using (var stream = await file.OpenWriteAsync())
        {
            if (stream.CanSeek) stream.SetLength(0); // don't leave a longer old file's tail behind
            saved = await vm.SaveAsync(result, stream, file.TryGetLocalPath() ?? file.Name);
        }
        if (!saved) return;

        ResultMessage = vm.StatusMessage;
        if (vm.HasWarnings)
        {
            CancelButton.Content = "Close";
            // The list sits below the options — scroll it into view once it has laid out.
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => WarningsPanel.BringIntoView(), Avalonia.Threading.DispatcherPriority.Background);
            return;
        }
        Close(true);
    }

    private void OnCancel_Click(object? sender, RoutedEventArgs e) => Close(ResultMessage is not null);
}
