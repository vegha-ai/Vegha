using System.ComponentModel;
using System.IO;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Vegha.App.ViewModels;
using UglyToad.PdfPig;

namespace Vegha.App.Controls.Workspace;

public partial class ResponseDisplay : UserControl
{
    public ResponseDisplay()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // OS-aware Send shortcut on the empty-state chip (⌘↩ on macOS, Ctrl+Enter elsewhere).
        if (this.FindControl<TextBlock>("SendShortcutChip") is { } chip)
            chip.Text = System.OperatingSystem.IsMacOS() ? "⌘ ↩" : "Ctrl + Enter";
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is RequestEditorViewModel vm)
        {
            vm.PropertyChanged -= OnVmPropertyChanged;
            vm.PropertyChanged += OnVmPropertyChanged;
            RefreshPdfIfNeeded(vm);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not RequestEditorViewModel vm) return;
        if (e.PropertyName is nameof(RequestEditorViewModel.ResponseBodyBytes)
            or nameof(RequestEditorViewModel.ResponseContentType))
            RefreshPdfIfNeeded(vm);
    }

    /// <summary>Renders up to the first 5 pages of an application/pdf response into the
    /// PdfPreviewHost ContentControl. PdfPig extracts text only — for visual rasterization
    /// we'd need pdfium/native, which we deliberately avoid here. Showing text-per-page is
    /// enough for an API testing tool's "is this the response I expected" use case.</summary>
    private void RefreshPdfIfNeeded(RequestEditorViewModel vm)
    {
        var host = this.FindControl<ContentControl>("PdfPreviewHost");
        if (host is null) return;
        if (!vm.ResponseIsPdf || vm.ResponseBodyBytes.Length == 0)
        {
            host.Content = null;
            return;
        }

        var pages = ExtractPdfPages(vm.ResponseBodyBytes, maxPages: 5);
        var stack = new StackPanel { Spacing = 12 };
        if (pages.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "(could not parse PDF)",
                FontStyle = FontStyle.Italic,
                Foreground = TryFindBrush("Text3Brush"),
                FontSize = 11,
            });
        }
        else
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"PDF · showing first {pages.Count} page(s)",
                FontSize = 10, FontWeight = FontWeight.SemiBold,
                Foreground = TryFindBrush("Text2Brush"),
            });
            for (var i = 0; i < pages.Count; i++)
            {
                stack.Children.Add(new Border
                {
                    Background = TryFindBrush("Bg3Brush"),
                    Padding = new global::Avalonia.Thickness(10),
                    CornerRadius = new global::Avalonia.CornerRadius(3),
                    Child = new SelectableTextBlock
                    {
                        Text = $"-- Page {i + 1} --\n\n{pages[i]}",
                        FontFamily = TryFindFont("MonoFont"),
                        FontSize = 11,
                        Foreground = TryFindBrush("Text0Brush"),
                        TextWrapping = TextWrapping.Wrap,
                    },
                });
            }
        }
        host.Content = stack;
    }

    private static IReadOnlyList<string> ExtractPdfPages(byte[] bytes, int maxPages)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var doc = PdfDocument.Open(ms);
            var pages = new List<string>();
            var count = Math.Min(doc.NumberOfPages, maxPages);
            for (var i = 1; i <= count; i++)
            {
                var page = doc.GetPage(i);
                pages.Add(page.Text ?? string.Empty);
            }
            return pages;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private IBrush TryFindBrush(string key) =>
        this.TryFindResource(key, out var b) && b is IBrush brush ? brush : new SolidColorBrush(Colors.Gray);
    private FontFamily TryFindFont(string key) =>
        this.TryFindResource(key, out var f) && f is FontFamily font ? font : FontFamily.Default;

    /// <summary>Save the raw response bytes to a user-picked file. Suggested name is derived
    /// from the request URL leaf + a content-type extension (.json / .xml / .png / etc.).</summary>
    private async void OnSaveResponse_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RequestEditorViewModel vm) return;
        if (vm.ResponseBodyBytes.Length == 0) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save response",
            SuggestedFileName = vm.SuggestedSaveFileName(),
        });
        if (file is null) return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(vm.ResponseBodyBytes);
        }
        catch
        {
            /* best-effort — host can surface via status bar in a follow-up */
        }
    }
}
