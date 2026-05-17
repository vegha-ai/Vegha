using Avalonia.Controls;

namespace Vegha.App.Controls.Shell;

/// <summary>Lightweight static-content sidebar panel — shows a header, title, body,
/// and a feature bullet list. Used for Git / Secrets / OpenAPI / Flow sections that
/// have backend libraries but their full UI surface is shipped progressively.</summary>
public partial class InfoPanel : UserControl
{
    public static readonly global::Avalonia.StyledProperty<string> HeaderProperty =
        global::Avalonia.AvaloniaProperty.Register<InfoPanel, string>(nameof(Header), "SECTION");
    public static readonly global::Avalonia.StyledProperty<string> TitleProperty =
        global::Avalonia.AvaloniaProperty.Register<InfoPanel, string>(nameof(Title), string.Empty);
    public static readonly global::Avalonia.StyledProperty<string> Description1Property =
        global::Avalonia.AvaloniaProperty.Register<InfoPanel, string>(nameof(Description), string.Empty);
    public static readonly global::Avalonia.StyledProperty<string> StatusProperty =
        global::Avalonia.AvaloniaProperty.Register<InfoPanel, string>(nameof(Status), string.Empty);
    /// <summary>Newline-separated list of feature lines. Easier to author in XAML than
    /// an x:Array, and the InfoPanel splits on \n internally to drive the ItemsControl.</summary>
    public static readonly global::Avalonia.StyledProperty<string> FeaturesProperty =
        global::Avalonia.AvaloniaProperty.Register<InfoPanel, string>(nameof(Features), string.Empty);

    public string Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }
    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }
    public string Description
    {
        get => GetValue(Description1Property);
        set => SetValue(Description1Property, value);
    }
    public string Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }
    public string Features
    {
        get => GetValue(FeaturesProperty);
        set => SetValue(FeaturesProperty, value);
    }

    public InfoPanel()
    {
        InitializeComponent();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == HeaderProperty) HeaderText.Text = Header;
            else if (e.Property == TitleProperty) TitleText.Text = Title;
            else if (e.Property == Description1Property) DescriptionText.Text = Description;
            else if (e.Property == StatusProperty) StatusText.Text = Status;
            else if (e.Property == FeaturesProperty)
                FeaturesList.ItemsSource = (Features ?? string.Empty)
                    .Split('\n', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
        };
    }
}
