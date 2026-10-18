using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Vegha.App.Controls.Shell;
using Vegha.App.ViewModels;

namespace Vegha.App.Controls.Workspace;

/// <summary>Edits an environment's name and color in one place. The color comes from a
/// saturation/value spectrum + hue slider (any shade), with the palette swatches as quick
/// picks and a hex box for exact values. Returns <see cref="ResultName"/> /
/// <see cref="ResultColor"/> when the dialog closes with true.</summary>
public partial class EnvironmentEditDialog : Window
{
    public string? ResultName { get; private set; }
    public string? ResultColor { get; private set; }

    // Set while code pushes a color into the controls so the change handlers
    // don't echo it back (spectrum → hex → spectrum loops).
    private bool _syncing;

    public EnvironmentEditDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.RemoveMinimizeMaximize();
    }

    public EnvironmentEditDialog(string currentName, string? currentColor) : this()
    {
        NameBox.Text = currentName;
        NameBox.SelectAll();
        Opened += (_, _) => NameBox.Focus();

        BuildSwatches();

        Spectrum.ColorChanged += (_, e) => OnPickerColorChanged(e.NewColor);
        HueSlider.ColorChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            Spectrum.HsvColor = HueSlider.HsvColor;
            _syncing = false;
            UpdatePreviewAndHex(Spectrum.Color);
        };
        HexBox.LostFocus += (_, _) => ApplyHex();
        HexBox.KeyDown += OnHexKeyDown;
        // Live-apply as soon as a complete hex is typed; partial input waits for Enter/blur.
        HexBox.TextChanged += (_, _) =>
        {
            if (_syncing || !HexBox.IsFocused) return;
            var t = HexBox.Text?.Trim() ?? string.Empty;
            if (t.Length > 0 && t[0] != '#') t = "#" + t;
            if (t.Length == 7 && Color.TryParse(t, out var typed)) SetColor(typed);
        };
        NameBox.TextChanged += (_, _) => PreviewText.Text = DisplayName;

        var initial = Color.TryParse(currentColor, out var c)
            ? c
            : Color.Parse(EnvironmentsViewModel.RandomColorHex());
        SetColor(initial);
        PreviewText.Text = DisplayName;
    }

    private string DisplayName =>
        string.IsNullOrWhiteSpace(NameBox.Text) ? "Environment" : NameBox.Text.Trim();

    private void BuildSwatches()
    {
        foreach (var swatch in EnvironmentColorPalette.Swatches)
        {
            var btn = new Button
            {
                Tag = swatch.Hex,
                Background = new SolidColorBrush(Color.Parse(swatch.Hex)),
            };
            btn.Classes.Add("swatch");
            ToolTip.SetTip(btn, swatch.Name);
            btn.Click += (_, _) => SetColor(Color.Parse(swatch.Hex));
            // Keep the shuffle button (declared in XAML) as the last chip.
            SwatchPanel.Children.Insert(SwatchPanel.Children.Count - 1, btn);
        }
    }

    /// <summary>Pushes a color into spectrum + hue slider + hex box + preview together.</summary>
    private void SetColor(Color color)
    {
        _syncing = true;
        Spectrum.Color = color;
        HueSlider.HsvColor = Spectrum.HsvColor;
        _syncing = false;
        UpdatePreviewAndHex(color);
    }

    private void OnPickerColorChanged(Color color)
    {
        if (_syncing) return;
        _syncing = true;
        HueSlider.HsvColor = Spectrum.HsvColor;
        _syncing = false;
        UpdatePreviewAndHex(color);
    }

    private void UpdatePreviewAndHex(Color color)
    {
        PreviewChip.Background = new SolidColorBrush(color);
        // Keep the chip label legible on very light picks.
        var luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;
        PreviewText.Foreground = luminance > 0.65 ? Brushes.Black : Brushes.White;
        if (!HexBox.IsFocused) HexBox.Text = ToHex(color);
    }

    private void OnHexKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyHex();
        e.Handled = true;
    }

    private void ApplyHex()
    {
        var text = HexBox.Text?.Trim() ?? string.Empty;
        if (text.Length > 0 && text[0] != '#') text = "#" + text;
        if (text.Length == 7 && Color.TryParse(text, out var c)) SetColor(c);
        else HexBox.Text = ToHex(Spectrum.Color);
    }

    private void OnShuffle_Click(object? sender, RoutedEventArgs e) =>
        SetColor(Color.Parse(EnvironmentsViewModel.RandomColorHex()));

    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private void OnOk_Click(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (HexBox.IsFocused) ApplyHex();
        ResultName = name;
        ResultColor = ToHex(Spectrum.Color);
        Close(true);
    }

    private void OnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
