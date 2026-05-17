using System;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Vegha.App.ViewModels;

namespace Vegha.App.Controls.Workspace;

/// <summary>
/// Code-behind for <see cref="BodyEditor"/>. Subscribes to the ViewModel's file-pick
/// events so the multipart-form table's "📁" button and the File-body's "Pick file…"
/// button can open the platform file dialog through <see cref="IStorageProvider"/>.
/// Picking happens here (in the view) because IStorageProvider needs a TopLevel — the
/// ViewModel deliberately doesn't depend on Avalonia.Controls so it stays headless-testable.
/// </summary>
public partial class BodyEditor : UserControl
{
    private RequestEditorViewModel? _attachedVm;

    public BodyEditor()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => DetachVm();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        DetachVm();
        if (DataContext is RequestEditorViewModel vm)
        {
            _attachedVm = vm;
            vm.PickMultipartFileRequested += OnPickMultipartFile;
            vm.PickBodyFileRequested += OnPickBodyFile;
        }
    }

    private void DetachVm()
    {
        if (_attachedVm is null) return;
        _attachedVm.PickMultipartFileRequested -= OnPickMultipartFile;
        _attachedVm.PickBodyFileRequested -= OnPickBodyFile;
        _attachedVm = null;
    }

    private async void OnPickMultipartFile(MultipartFormRow row)
    {
        var path = await PickSingleFileAsync(title: "Pick file for multipart-form row");
        if (string.IsNullOrEmpty(path)) return;
        row.Value = path;
        row.Kind = "file";
    }

    private async void OnPickBodyFile()
    {
        if (_attachedVm is null) return;
        var path = await PickSingleFileAsync(title: "Pick request body file");
        if (string.IsNullOrEmpty(path)) return;
        _attachedVm.FilePath = path;
    }

    /// <summary>Opens the platform file dialog, returning the absolute path of the chosen
    /// file (or null when the user cancels or the visual tree has no TopLevel — happens in
    /// headless tests, where the test sets the path directly).</summary>
    private async System.Threading.Tasks.Task<string?> PickSingleFileAsync(string title)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        if (files.Count == 0) return null;
        var local = files[0].TryGetLocalPath();
        return string.IsNullOrEmpty(local) ? null : local;
    }
}
