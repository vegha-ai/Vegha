using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Vegha.App.Controls.Workspace;
using Vegha.Integrations.Git;

namespace Vegha.App.Controls.Git;

/// <summary>"Test connection": runs <see cref="GitDiagnosticsService"/> against a remote and
/// shows the redacted report, with copy-to-clipboard for sharing with IT.</summary>
public partial class GitDiagnosticsDialog : Window
{
    private readonly GitDiagnosticsService? _diagnostics;
    private readonly string? _repoRoot;

    public GitDiagnosticsDialog()
    {
        InitializeComponent();
    }

    public GitDiagnosticsDialog(GitDiagnosticsService diagnostics, string? remoteUrl, string? repoRoot) : this()
    {
        _diagnostics = diagnostics;
        _repoRoot = repoRoot;
        UrlBox.Text = remoteUrl;
        Opened += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(remoteUrl)) OnRun_Click(null, new RoutedEventArgs());
        };
    }

    private async void OnRun_Click(object? sender, RoutedEventArgs e)
    {
        if (_diagnostics is null || string.IsNullOrWhiteSpace(UrlBox.Text)) return;
        RunButton.IsEnabled = false;
        ResultText.Text = "Running…";
        ResultText.Foreground = (Avalonia.Media.IBrush?)this.FindResource("Text2Brush");
        ReportText.Text = string.Empty;
        try
        {
            var progress = new Progress<string>(line => Dispatcher.UIThread.Post(() =>
            {
                ReportText.Text += line + "\n";
                ReportScroll.ScrollToEnd();
            }));
            var report = await _diagnostics.RunAsync(UrlBox.Text.Trim(), _repoRoot, progress, CancellationToken.None);
            ReportText.Text = report.Text;
            ResultText.Text = report.Success ? "✓ Connection works" : "✗ " + (report.Error?.Title ?? "Failed");
            ResultText.Foreground = (Avalonia.Media.IBrush?)this.FindResource(report.Success ? "StatusOkBrush" : "StatusErrBrush");
        }
        catch (Exception ex)
        {
            ResultText.Text = "✗ " + ex.Message;
        }
        finally { RunButton.IsEnabled = true; }
    }

    private async void OnCopy_Click(object? sender, RoutedEventArgs e) =>
        await GitUiHelpers.CopyAsync(this, ReportText.Text ?? string.Empty);

    private void OnClose_Click(object? sender, RoutedEventArgs e) => Close();
}
