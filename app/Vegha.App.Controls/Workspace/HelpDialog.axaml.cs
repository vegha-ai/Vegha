using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Vegha.App.Controls.Workspace;

public partial class HelpDialog : Window
{
    public const string DocsUrl = "https://vegha.ai/docs";

    /// <summary>Caller supplies a host-OS-aware gesture formatter (Ctrl on win/linux, ⌘ on mac).
    /// Defaults to identity so the dialog still works if instantiated bare for designer preview.</summary>
    public System.Func<string, string> FormatGesture { get; set; } = g => g;

    public HelpDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            this.RemoveMinimizeMaximize();
            BuildSections();
        };
    }

    private void BuildSections()
    {
        BodyHost.Children.Clear();
        string K(string g) => FormatGesture(g);

        BodyHost.Children.Add(BuildShortcutCard("Requests", new (string, string)[]
        {
            (K("Ctrl+T"),         "New request"),
            (K("Ctrl+S"),         "Save the active request"),
            (K("Ctrl+Enter"),     "Send the active request"),
            (K("Ctrl+Tab"),       $"Next tab    ({K("Ctrl+Shift+Tab")} previous)"),
            (K("Ctrl+W"),         "Close the active tab"),
        }));

        BodyHost.Children.Add(BuildShortcutCard("Workspace", new (string, string)[]
        {
            (K("Ctrl+O"),                  "Open a collection folder"),
            (K("Ctrl+I"),                  "Import a collection"),
            (K("Ctrl+K"),                  "Find a request"),
            (K("Ctrl+,"),                  "Settings"),
            ($"{K("Ctrl+=")} / {K("Ctrl+-")}", $"Zoom in / out   ({K("Ctrl+0")} reset)"),
        }));

        BodyHost.Children.Add(BuildBulletCard("Activity rail (left edge)",
            "Collections · Environments · History · Source Control · OpenAPI · Runner · Settings · Help"));

        BodyHost.Children.Add(BuildBulletCard("Workspace tabs (above the editor)",
            "REST · GraphQL · WebSocket · gRPC · SOAP"));

        BodyHost.Children.Add(BuildBulletCard("Import button (top bar)",
            "Postman v2.1 collection / environment\nInsomnia v4 / v5 export\nOpenAPI 3.x / Swagger 2.0 spec\nWSDL\nSoapUI project\nBruno collection folder"));

        BodyHost.Children.Add(BuildBulletCard("Privacy",
            "Zero telemetry. The only outgoing traffic is the requests you fire and the auth flows you start."));
    }

    private static Border BuildShortcutCard(string title, (string Key, string Description)[] rows)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,16,*") };
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            if (i > 0) grid.RowDefinitions[i - 1] = new RowDefinition(GridLength.Auto) { };
        }
        for (int i = 0; i < rows.Length; i++)
        {
            var key = new TextBlock { Text = rows[i].Key, Margin = new Thickness(0, 3, 0, 3) };
            key.Classes.Add("key");
            Grid.SetRow(key, i); Grid.SetColumn(key, 0);

            var desc = new TextBlock { Text = rows[i].Description, Margin = new Thickness(0, 3, 0, 3) };
            desc.Classes.Add("desc");
            Grid.SetRow(desc, i); Grid.SetColumn(desc, 2);

            grid.Children.Add(key);
            grid.Children.Add(desc);
        }
        return WrapCard(title, grid);
    }

    private static Border BuildBulletCard(string title, string body)
    {
        var text = new TextBlock { Text = body };
        text.Classes.Add("desc");
        return WrapCard(title, text);
    }

    private static Border WrapCard(string title, Control body)
    {
        var header = new TextBlock { Text = title };
        header.Classes.Add("section");
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(header);
        stack.Children.Add(body);
        return new Border
        {
            Background = (IBrush?)Application.Current?.FindResource("Bg2Brush"),
            BorderBrush = (IBrush?)Application.Current?.FindResource("Border0Brush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14, 12),
            Child = stack,
        };
    }

    private void OnDocs_Click(object? sender, RoutedEventArgs e) => OpenUrl(DocsUrl);

    private void OnClose_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Cross-platform browser launch. UseShellExecute = true lets the OS pick the
    /// registered handler for http(s); platform-specific fallbacks cover the (rare) case
    /// where the shell-execute path doesn't resolve.</summary>
    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return;
        }
        catch { /* fall through */ }
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start("cmd", new[] { "/c", "start", "", url });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", url);
            else if (OperatingSystem.IsLinux())
                Process.Start("xdg-open", url);
        }
        catch { /* best-effort — failure to open browser shouldn't crash the app */ }
    }
}
