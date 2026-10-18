using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Vegha.App.ViewModels;
using Vegha.App.ViewModels.Scriptless;
using Vegha.Core.Scripting.Testing;

namespace Vegha.App.Controls.Workspace;

/// <summary>
/// Point-and-click scriptless testing from the response pane: right-click a JSON value, a
/// header, or the status / time pills to capture it into a variable or add an assertion —
/// the path is worked out from where the user clicked, so there's no syntax to type.
/// </summary>
public partial class ResponseDisplay
{
    /// <summary>What the body context menu points at: the source (JSON body or XPath), the
    /// path, and a suggested variable name.</summary>
    private sealed record MenuTarget(ResponseSource Source, string Path, string SuggestedName);

    private MenuTarget? _menuTarget;
    private Action? _undoLastAdd;
    private DispatcherTimer? _noticeTimer;

    private RequestEditorViewModel? Vm => DataContext as RequestEditorViewModel;

    // ------------------------------------------------------------------ body

    private void OnBodyMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _menuTarget = null;
        var vm = Vm;
        var view = this.FindControl<ReadOnlyCodeView>("BodyCodeView");
        // Paths are computed from the displayed text, so they only line up with the real body
        // when no JSONPath filter is reshaping the view.
        if (vm is not null && view is not null)
        {
            if (vm.ResponseIsJson && string.IsNullOrWhiteSpace(vm.JsonPathFilter)
                && JsonPathLocator.Locate(view.DocumentText, view.ContextOffset) is { } json)
                _menuTarget = new MenuTarget(ResponseSource.Body, json.Path, json.SuggestedName);
            else if (vm.ResponseIsXml && XmlPathLocator.Locate(view.DocumentText, view.ContextOffset) is { } xml)
                _menuTarget = new MenuTarget(ResponseSource.XPath, xml.XPath, xml.SuggestedName);
        }

        var enabled = _menuTarget is not null;
        SetMenuItem("CaptureValueMenuItem", enabled,
            enabled ? $"Capture {Describe(_menuTarget!)} as variable…" : "Capture as variable… (JSON / XML responses)");
        SetMenuItem("AssertValueMenuItem", enabled,
            enabled ? $"Assert {Describe(_menuTarget!)}" : "Assert this value (JSON / XML responses)");
        SetMenuItem("CopyPathMenuItem", enabled,
            enabled ? $"Copy path  {Truncate(DisplayPath(_menuTarget!.Path), 60)}" : "Copy path");
    }

    private void SetMenuItem(string name, bool enabled, string header)
    {
        if (this.FindControl<MenuItem>(name) is not { } item) return;
        item.IsEnabled = enabled;
        item.Header = header;
    }

    private static string DisplayPath(string path) => path.Length == 0 ? "(whole body)" : path;

    /// <summary>Short label for the menu: JSON paths as-is, XPaths by their element names.</summary>
    private static string Describe(MenuTarget t)
    {
        if (t.Path.Length == 0) return "the body";
        var label = t.Source == ResponseSource.XPath
            ? string.Join("/", System.Text.RegularExpressions.Regex.Matches(t.Path, @"local-name\(\)='([^']+)'\](\[\d+\])?|/([A-Za-z_][\w.-]*(\[\d+\])?)")
                .Select(m => m.Groups[1].Success ? m.Groups[1].Value + m.Groups[2].Value : m.Groups[3].Value))
            : t.Path;
        return "“" + Truncate(label, 40) + "”";
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : "…" + s[^(max - 1)..];

    private void OnCaptureValue_Click(object? sender, RoutedEventArgs e)
    {
        if (_menuTarget is not { } t) return;
        ShowCapturePopover(t.SuggestedName, t.Source, t.Path, this.FindControl<Control>("BodyCodeView"));
    }

    private void OnAssertValue_Click(object? sender, RoutedEventArgs e)
    {
        if (_menuTarget is not { } t || Vm is not { } vm) return;
        var snap = vm.CurrentResponseSnapshot();
        if (snap is null) return;
        var value = ResponseValueResolver.Resolve(ResponseExpression.Create(t.Source, t.Path), snap).Value;
        var (op, expected) = AssertionFor(value);
        AddAssertion(vm, t.Source, t.Path, op, expected);
    }

    private async void OnCopyPath_Click(object? sender, RoutedEventArgs e)
    {
        if (_menuTarget is not { } t) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb) await cb.SetTextAsync(t.Path);
    }

    private async void OnCopySelection_Click(object? sender, RoutedEventArgs e)
    {
        var view = this.FindControl<ReadOnlyCodeView>("BodyCodeView");
        var text = view?.SelectedText;
        if (string.IsNullOrEmpty(text)) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb) await cb.SetTextAsync(text);
    }

    /// <summary>The natural assertion for a value: equality for scalars, a type / length check
    /// for containers.</summary>
    internal static (string Operator, string Expected) AssertionFor(ResolvedValue value) => value.TypeName switch
    {
        "undefined" => ("isDefined", string.Empty),
        "null" => ("isNull", string.Empty),
        "object" => ("isObject", string.Empty),
        "array" => ("length", ValueFiltersLength(value)),
        "string" => ("eq", QuoteIfAmbiguous(value.ToVariableString())),
        _ => ("eq", value.ToVariableString()),
    };

    private static string ValueFiltersLength(ResolvedValue v) =>
        (v.Node as System.Text.Json.Nodes.JsonArray)?.Count.ToString(CultureInfo.InvariantCulture) ?? "0";

    /// <summary>Strings that would read as another type ("200", "true", "") get quoted so the
    /// assertion compares them as text.</summary>
    private static string QuoteIfAmbiguous(string s) =>
        s.Length == 0 || s.Trim() != s || s is "true" or "false" or "null" or "undefined"
        || double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            ? ResponseExpression.Quote(s, '"')
            : s;

    // ------------------------------------------------------------------ headers / status / time

    private void OnCaptureHeader_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not HeaderRow h) return;
        ShowCapturePopover(JsonPathLocator.ToVariableName(h.Name), ResponseSource.Header, h.Name, this);
    }

    private void OnAssertHeader_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not HeaderRow h || Vm is not { } vm) return;
        // Content-Type style headers often carry parameters (charset); assert on the stable part.
        var main = h.Value.Split(';')[0].Trim();
        if (main.Length < h.Value.Length) AddAssertion(vm, ResponseSource.Header, h.Name, "contains", main);
        else AddAssertion(vm, ResponseSource.Header, h.Name, "eq", QuoteIfAmbiguous(h.Value));
    }

    private void OnAssertStatus_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        AddAssertion(vm, ResponseSource.Status, string.Empty, "eq",
            vm.ResponseStatusCode.ToString(CultureInfo.InvariantCulture));
    }

    private void OnCaptureStatus_Click(object? sender, RoutedEventArgs e) =>
        ShowCapturePopover("status", ResponseSource.Status, string.Empty, this);

    private void OnAssertTime_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var budget = Math.Max(200, (int)Math.Ceiling(vm.ResponseElapsedMilliseconds * 2 / 100.0) * 100);
        AddAssertion(vm, ResponseSource.ResponseTime, string.Empty, "lt", budget.ToString(CultureInfo.InvariantCulture));
    }

    private void AddAssertion(RequestEditorViewModel vm, ResponseSource source, string path, string op, string expected)
    {
        var row = vm.AddAssertionFromResponse(source, path, op, expected);
        var label = $"{ResponseExpression.FromEditor(source, path).ToStorage()} {new AssertionSpec(op, expected).ToStorage()}";
        ShowNotice($"Assertion added: {Truncate(label, 60)}", () => vm.Assertions.Remove(row));
    }

    // ------------------------------------------------------------------ capture popover

    /// <summary>Small flyout: variable name (pre-filled from the key), scope, live preview,
    /// Add. Enter adds, Escape cancels.</summary>
    private void ShowCapturePopover(string suggestedName, ResponseSource source, string path, Control? anchor)
    {
        if (Vm is not { } vm || anchor is null) return;
        var snap = vm.CurrentResponseSnapshot();
        var preview = snap is null
            ? ResolvedValue.Missing
            : ResponseValueResolver.Resolve(ResponseExpression.FromEditor(source, path), snap).Value;

        var nameBox = new TextBox
        {
            Text = suggestedName,
            FontFamily = TryFindFont("MonoFont"),
            FontSize = 12,
            MinWidth = 220,
        };
        var scopeBox = new ComboBox
        {
            ItemsSource = ScriptlessOptions.Scopes,
            SelectedIndex = 0,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var addButton = new Button { Content = "Add", Classes = { "primary" }, Padding = new Thickness(14, 4), IsDefault = true };
        var cancelButton = new Button { Content = "Cancel", Classes = { "tertiary" }, Padding = new Thickness(10, 4) };

        var flyout = new Flyout { Placement = PlacementMode.Pointer, ShowMode = FlyoutShowMode.Standard };
        var panel = new StackPanel
        {
            Spacing = 8,
            Width = 320,
            Margin = new Thickness(4),
            Children =
            {
                new TextBlock { Text = "Capture as variable", FontWeight = FontWeight.SemiBold, FontSize = 13 },
                new TextBlock
                {
                    Text = $"{ScriptlessOptions.SourceFor(source).Label}{(path.Length > 0 ? " · " + path : string.Empty)}",
                    FontFamily = TryFindFont("MonoFont"), FontSize = 11,
                    Foreground = TryFindBrush("Text2Brush"), TextTrimming = TextTrimming.CharacterEllipsis,
                },
                Labeled("Name", nameBox),
                Labeled("Save to", scopeBox),
                new TextBlock
                {
                    Text = "→ " + preview.ToDisplayString(80),
                    FontFamily = TryFindFont("MonoFont"), FontSize = 11,
                    Foreground = TryFindBrush(preview.Exists ? "StatusOkBrush" : "StatusWarnBrush"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                new TextBlock
                {
                    Text = "Use it in any field as {{name}}. Edit or remove it in the Tests tab.",
                    FontSize = 11, Foreground = TryFindBrush("Text3Brush"), TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancelButton, addButton },
                },
            },
        };
        flyout.Content = panel;

        void Commit()
        {
            var name = (nameBox.Text ?? string.Empty).Trim();
            if (name.Length == 0) { nameBox.Focus(); return; }
            var scope = (scopeBox.SelectedItem as ScopeOption)?.Scope ?? CaptureScope.Runtime;
            var row = vm.AddCaptureFromResponse(name, source, path, scope);
            flyout.Hide();
            ShowNotice($"Captured → {{{{{row.Name}}}}}", () => vm.Captures.Remove(row));
        }

        addButton.Click += (_, _) => Commit();
        cancelButton.Click += (_, _) => flyout.Hide();
        nameBox.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter) { Commit(); ke.Handled = true; }
            else if (ke.Key == Key.Escape) { flyout.Hide(); ke.Handled = true; }
        };
        flyout.Opened += (_, _) => Dispatcher.UIThread.Post(() => { nameBox.Focus(); nameBox.SelectAll(); });
        flyout.ShowAt(anchor, showAtPointer: true);
    }

    private Control Labeled(string label, Control field)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("64,*") };
        grid.Children.Add(new TextBlock
        {
            Text = label, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Foreground = TryFindBrush("Text2Brush"),
        });
        Grid.SetColumn(field, 1);
        grid.Children.Add(field);
        return grid;
    }

    // ------------------------------------------------------------------ inline notice

    private void ShowNotice(string text, Action? undo)
    {
        if (this.FindControl<Border>("InlineNotice") is not { } notice) return;
        if (this.FindControl<TextBlock>("InlineNoticeText") is { } tb) tb.Text = text;
        if (this.FindControl<Button>("InlineNoticeUndo") is { } undoButton) undoButton.IsVisible = undo is not null;
        _undoLastAdd = undo;
        notice.IsVisible = true;

        _noticeTimer?.Stop();
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer?.Stop();
            notice.IsVisible = false;
            _undoLastAdd = null;
        };
        _noticeTimer.Start();
    }

    private void OnInlineNoticeUndo_Click(object? sender, RoutedEventArgs e)
    {
        _undoLastAdd?.Invoke();
        _undoLastAdd = null;
        _noticeTimer?.Stop();
        if (this.FindControl<Border>("InlineNotice") is { } notice) notice.IsVisible = false;
    }
}

/// <summary>Chain-strip helper: captured variable names → "→ {{token}} {{userId}}".</summary>
public sealed class CapturedKeysConverter : IValueConverter
{
    public static readonly CapturedKeysConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IEnumerable<string> keys && keys.Any()
            ? "→ " + string.Join(" ", keys.Select(k => "{{" + k + "}}"))
            : string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
