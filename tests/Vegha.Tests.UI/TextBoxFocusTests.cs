using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.UI;

/// <summary>
/// Regression test for audit §04 — "the focused field is rendering pure black".
/// Avalonia's Fluent theme otherwise paints a focused TextBox's <c>PART_BorderElement</c>
/// with <c>TextControlBackgroundFocused</c> (near-black on our dark variants) plus a
/// system-accent border — neither is a theme token. The per-variant token dictionaries
/// (Themes/Tokens/*.axaml) override those exact Fluent resource keys so a focused field
/// keeps the theme input background and signals focus through the accent border.
///
/// This asserts the *resolved* focused visual matches BgInput + Accent and is never black,
/// which is the part the XAML compiler can't check (DynamicResource keys resolve at runtime).
/// </summary>
public class TextBoxFocusTests
{
    [AvaloniaFact]
    public void FocusedTextBox_KeepsThemeInputBackground_AndAccentBorder()
    {
        var app = Application.Current!;
        app.TryGetResource("BgInputBrush", app.ActualThemeVariant, out var bgRes);
        app.TryGetResource("AccentBrush", app.ActualThemeVariant, out var accentRes);
        var expectedBg = ((ISolidColorBrush)bgRes!).Color;
        var expectedBorder = ((ISolidColorBrush)accentRes!).Color;

        var tb = new TextBox { Width = 200 };
        var window = new Window { Width = 400, Height = 200, Content = tb };
        window.Show();
        try
        {
            window.UpdateLayout();
            tb.Focus();
            window.UpdateLayout();

            tb.IsFocused.Should().BeTrue("the TextBox must take focus for the :focus visual to apply");

            var border = tb.GetVisualDescendants()
                .OfType<Border>()
                .FirstOrDefault(b => b.Name == "PART_BorderElement");
            border.Should().NotBeNull("Fluent's TextBox template exposes its background/border as PART_BorderElement");

            var bg = border!.Background as ISolidColorBrush;
            var bd = border.BorderBrush as ISolidColorBrush;

            bg.Should().NotBeNull("the focused border element must have a solid background");
            bg!.Color.Should().NotBe(Colors.Black, "the audit bug was a pure #000 focused background");
            bg.Color.Should().Be(expectedBg, "focus must keep the theme input background (BgInput)");

            bd.Should().NotBeNull("the focused border element must have a solid border brush");
            bd!.Color.Should().Be(expectedBorder, "focus must signal through the theme accent border");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SingleLineTextBox_CentersContent_AndUsesThemedSelection()
    {
        var app = Application.Current!;
        app.TryGetResource("SelectionBrush", app.ActualThemeVariant, out var selRes);
        var expectedSelection = ((ISolidColorBrush)selRes!).Color;

        var tb = new TextBox { Width = 200 };
        var window = new Window { Width = 400, Height = 200, Content = tb };
        window.Show();
        try
        {
            window.UpdateLayout();

            tb.VerticalContentAlignment.Should().Be(VerticalAlignment.Center,
                "single-line input text must be vertically centered, not top-aligned");

            var sel = tb.SelectionBrush as ISolidColorBrush;
            sel.Should().NotBeNull();
            sel!.Color.Should().Be(expectedSelection,
                "selection must use the per-theme translucent SelectionBrush, not Fluent's saturated system blue");
            tb.SelectionForegroundBrush.Should().NotBeNull(
                "selected text must have an explicit foreground so it stays legible");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MultiLineTextBox_TopAlignsContent()
    {
        var tb = new TextBox { AcceptsReturn = true, Width = 200, Height = 80 };
        var window = new Window { Width = 400, Height = 200, Content = tb };
        window.Show();
        try
        {
            window.UpdateLayout();
            tb.VerticalContentAlignment.Should().Be(VerticalAlignment.Top,
                "multi-line inputs must keep text top-aligned, not vertically centered");
        }
        finally
        {
            window.Close();
        }
    }
}
