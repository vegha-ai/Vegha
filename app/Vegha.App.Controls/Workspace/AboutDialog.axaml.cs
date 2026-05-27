using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Vegha.App.Controls.Workspace;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            this.RemoveMinimizeMaximize();
            PopulateInfo();
        };
    }

    private void PopulateInfo()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        VersionText.Text = ResolveVersion(asm);
        DistributionText.Text = ResolveDistribution();
        RuntimeText.Text = $".NET {Environment.Version} · {RuntimeInformation.OSArchitecture} · {RuntimeInformation.OSDescription.Trim()}";
        CopyrightText.Text = asm.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
            ?? "Copyright (c) VAMC Consulting LLC";
    }

    private static string ResolveVersion(Assembly asm)
    {
        // InformationalVersion picks up the <Version> in Directory.Build.props (e.g. "1.0.0").
        // Strip a "+sha" suffix if SourceLink ever adds one so the displayed string stays clean.
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString() ?? "unknown";
    }

    private static string ResolveDistribution()
    {
#if VEGHA_MSIX
        return "Microsoft Store (MSIX)";
#elif VEGHA_MAS
        return "Mac App Store";
#else
        return "Direct download";
#endif
    }

    private async void OnCopyVersion_Click(object? sender, RoutedEventArgs e)
    {
        var summary =
            $"Vegha {VersionText.Text}\n" +
            $"Distribution: {DistributionText.Text}\n" +
            $"Runtime: {RuntimeText.Text}";
        var clipboard = Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(summary);
    }

    private void OnClose_Click(object? sender, RoutedEventArgs e) => Close();
}
