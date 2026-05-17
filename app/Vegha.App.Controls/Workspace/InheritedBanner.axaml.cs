using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Vegha.App.Controls.Workspace;

/// <summary>
/// "Inherited from {parent} [Override]" banner used by the Authorization tab and the three
/// script tabs (Pre-request / Post-response / Tests). Replaces four copy-pasted Border
/// instances that had drifted by 1-2px in padding — now all four share one visual definition.
/// Wrap inside an <c>IsVisible</c> binding on the inherited-state flag from the parent VM.
/// </summary>
public partial class InheritedBanner : UserControl
{
    /// <summary>Display name of the parent folder/collection the value is inherited from.
    /// Rendered bold next to "Inherited from".</summary>
    public static readonly StyledProperty<string?> InheritedFromProperty =
        AvaloniaProperty.Register<InheritedBanner, string?>(nameof(InheritedFrom));

    /// <summary>Command invoked when the user clicks Override. Wired by the parent VM to
    /// materialize the inherited value onto the current request.</summary>
    public static readonly StyledProperty<ICommand?> OverrideCommandProperty =
        AvaloniaProperty.Register<InheritedBanner, ICommand?>(nameof(OverrideCommand));

    public string? InheritedFrom
    {
        get => GetValue(InheritedFromProperty);
        set => SetValue(InheritedFromProperty, value);
    }

    public ICommand? OverrideCommand
    {
        get => GetValue(OverrideCommandProperty);
        set => SetValue(OverrideCommandProperty, value);
    }

    public InheritedBanner()
    {
        InitializeComponent();
    }
}
