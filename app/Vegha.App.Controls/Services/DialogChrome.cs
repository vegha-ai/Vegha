using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Vegha.App.Controls.Services;

/// <summary>Gives every dialog the same themed title bar as the Settings window: app logo,
/// title, and a ✕ that turns red on hover, drawn in <c>ChromeTitleBarBrush</c> instead of the
/// OS caption (which picks up the Windows accent / Mica tint and never matched the app
/// palette). Registered once for all windows (<see cref="Register"/>), so dialogs don't need
/// per-file XAML changes and new dialogs get it automatically.
///
/// Skipped for windows that already draw their own chrome (main window, Settings — they set
/// <c>ExtendClientAreaToDecorationsHint</c>) and for borderless overlays (command palette,
/// quick switcher, splash — <c>SystemDecorations</c> other than Full).</summary>
public static class DialogChrome
{
    public const double TitleBarHeight = 36;

    private static readonly AttachedProperty<bool> AppliedProperty =
        AvaloniaProperty.RegisterAttached<Window, bool>("DialogChromeApplied", typeof(DialogChrome));

    /// <summary>Hooks <see cref="Window.WindowOpenedEvent"/> for every window in the app.</summary>
    public static void Register() =>
        Window.WindowOpenedEvent.AddClassHandler<Window>((w, _) => Apply(w));

    public static void Apply(Window window)
    {
        if (window.GetValue(AppliedProperty)) return;
        if (window.ExtendClientAreaToDecorationsHint) return;            // draws its own chrome
        if (window.SystemDecorations != SystemDecorations.Full) return;  // borderless overlay
        if (window.Content is not Control content) return;
        window.SetValue(AppliedProperty, true);

        window.ExtendClientAreaToDecorationsHint = true;
        window.ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
        window.ExtendClientAreaTitleBarHeightHint = TitleBarHeight;

        var header = BuildHeader(window);
        GrowForHeader(window);

        // Keep the interface-zoom wrapper (ZoomHost) outermost if it's already there, so the
        // title bar scales with the rest of the dialog.
        if (content is LayoutTransformControl zoom && zoom.Child is Control inner)
        {
            zoom.Child = null;
            zoom.Child = Wrap(header, inner);
        }
        else
        {
            window.Content = null;
            window.Content = Wrap(header, content);
        }
    }

    /// <summary>The header is drawn inside the client area, so a dialog with a fixed
    /// <see cref="Layoutable.Height"/> (sized for its content back when the OS caption sat
    /// outside it) loses <see cref="TitleBarHeight"/> of room — its body then overflows onto
    /// its own footer (About dialog: the info card covered the Close button). Grow the window
    /// by the header so the body keeps exactly the space it was designed for, and shift it up
    /// by half so a centered dialog stays centered. Size-to-content heights already include
    /// the header and are left alone.</summary>
    private static void GrowForHeader(Window window)
    {
        if (window.SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight) return;
        if (double.IsNaN(window.Height)) return;

        // The header lives inside the ZoomHost transform, so it occupies its zoomed height.
        var extra = TitleBarHeight * ZoomHost.CurrentZoom;
        if (window.MinHeight > 0) window.MinHeight += extra;
        if (!double.IsInfinity(window.MaxHeight)) window.MaxHeight += extra;
        window.Height += extra;

        var shift = (int)Math.Round(extra / 2 * window.DesktopScaling);
        window.Position = window.Position.WithY(Math.Max(0, window.Position.Y - shift));
    }

    private static DockPanel Wrap(Control header, Control body)
    {
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(body);
        return dock;
    }

    private static Border BuildHeader(Window window)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var logo = new Image
        {
            Height = 16,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        try { logo.Source = new Bitmap(AssetLoader.Open(new Uri("avares://Vegha.App/Assets/logo.png"))); }
        catch { logo.IsVisible = false; }
        grid.Children.Add(logo);

        var title = new TextBlock
        {
            Margin = new Thickness(8, 0, 12, 0),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false,
            [!TextBlock.TextProperty] = window[!Window.TitleProperty],
        };
        title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable("Text0Brush"));
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        var close = new Button { Content = new TextBlock { Text = "✕", FontSize = 11 } };
        close.Classes.Add("winCtrl");
        close.Classes.Add("close");
        ToolTip.SetTip(close, "Close");
        close.Click += (_, e) =>
        {
            e.Handled = true;
            window.Close();
        };
        Grid.SetColumn(close, 2);
        grid.Children.Add(close);

        var header = new Border
        {
            Height = TitleBarHeight,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid,
        };
        header.Bind(Border.BackgroundProperty, header.GetResourceObservable("ChromeTitleBarBrush"));
        header.Bind(Border.BorderBrushProperty, header.GetResourceObservable("Border0Brush"));

        // The whole bar (except the ✕) drags the window, like the OS caption did.
        header.PointerPressed += (_, e) =>
        {
            if (e.Source is Visual v && IsInside<Button>(v)) return;
            if (e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
                window.BeginMoveDrag(e);
        };
        return header;
    }

    private static bool IsInside<T>(Visual v) where T : Visual
    {
        for (Visual? cur = v; cur is not null; cur = cur.GetVisualParent())
            if (cur is T) return true;
        return false;
    }
}
