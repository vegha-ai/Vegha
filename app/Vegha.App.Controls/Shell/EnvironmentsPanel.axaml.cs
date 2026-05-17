using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Vegha.App.Controls.Workspace;
using Vegha.App.ViewModels;
using DomainEnv = Vegha.Core.Domain.Environment;

namespace Vegha.App.Controls.Shell;

public partial class EnvironmentsPanel : UserControl
{
    private EnvironmentsViewModel? _attachedVm;

    public EnvironmentsPanel()
    {
        InitializeComponent();
        BuildSwatches();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_attachedVm is not null)
            _attachedVm.RenameRequested -= OnVmRenameRequested;
        _attachedVm = DataContext as EnvironmentsViewModel;
        if (_attachedVm is not null)
            _attachedVm.RenameRequested += OnVmRenameRequested;
    }

    private async void OnVmRenameRequested(object? sender, DomainEnv env)
    {
        if (_attachedVm is null) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        var dlg = new RenameDialog("Rename environment", "Environment name", env.Name);
        var ok = await dlg.ShowDialog<bool>(owner);
        if (!ok || string.IsNullOrWhiteSpace(dlg.ResultName)) return;
        await _attachedVm.RenameEnvironmentAsync(env, dlg.ResultName.Trim());
    }

    /// <summary>Populates the color picker popup with one button per <see cref="EnvironmentColorPalette"/>
    /// swatch. Done once in the constructor — the palette is static so no rebuilds are needed.</summary>
    private void BuildSwatches()
    {
        if (SwatchPanel is null) return;
        foreach (var swatch in EnvironmentColorPalette.Swatches)
        {
            var btn = new Button
            {
                Tag = swatch.Hex,
                Background = new SolidColorBrush(Color.Parse(swatch.Hex)),
            };
            btn.Classes.Add("swatch");
            ToolTip.SetTip(btn, swatch.Name);
            btn.Click += OnSwatch_Click;
            SwatchPanel.Children.Add(btn);
        }
    }

    // ---- Header (detail pane) actions for the selected env ----
    private void OnRename_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is EnvironmentsViewModel vm && vm.SelectedEnvironment is { } env)
            vm.RequestRenameCommand.Execute(env);
    }

    private void OnCopy_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is EnvironmentsViewModel vm && vm.SelectedEnvironment is { } env)
            vm.CopyEnvironmentCommand.Execute(env);
    }

    private async void OnDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EnvironmentsViewModel vm || vm.SelectedEnvironment is not { } env) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) { vm.DeleteEnvironmentCommand.Execute(env); return; }
        var confirmed = await ConfirmDeleteAsync(owner, env.Name);
        if (confirmed) vm.DeleteEnvironmentCommand.Execute(env);
    }

    private void OnSetColor_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        ColorPickerPopup.PlacementTarget = b;
        ColorPickerPopup.IsOpen = true;
    }

    private void OnRemoveVariable_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EnvironmentsViewModel vm) return;
        if (sender is not Button b || b.Tag is not EnvVarRow row) return;
        vm.RemoveVariableCommand.Execute(row);
    }

    // ---- Header (master) actions ----
    private async void OnImport_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EnvironmentsViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import environment(s)",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Environment files") { Patterns = new[] { "*.env.json", "*.postman_environment.json", "*.json" } },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } },
            },
        });

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList();
        if (paths.Count == 0) return;

        vm.ImportEnvironments(paths);
    }

    private async void OnExport_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EnvironmentsViewModel vm) return;
        // Export uses the currently-selected env. Active is no longer the right primitive
        // because activation and selection are separate concerns post-redesign.
        var env = vm.SelectedEnvironment ?? vm.Active;
        if (env is null)
        {
            vm.StatusMessage = "Select an environment to export.";
            return;
        }
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Export “{env.Name}”",
            SuggestedFileName = env.Name + ".env.json",
            DefaultExtension = "env.json",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Environment file") { Patterns = new[] { "*.env.json" } },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } },
            },
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        vm.ExportEnvironment(env, path);
    }

    private void OnToggleSearch_Click(object? sender, RoutedEventArgs e)
    {
        if (SearchRow is null) return;
        SearchRow.IsVisible = !SearchRow.IsVisible;
        if (SearchRow.IsVisible)
        {
            SearchBox?.Focus();
        }
        else if (DataContext is EnvironmentsViewModel vm)
        {
            vm.SearchText = string.Empty;
        }
    }

    // ---- Color picker handlers ----
    private async void OnSwatch_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EnvironmentsViewModel vm || vm.SelectedEnvironment is not { } env) return;
        if (sender is not Button b || b.Tag is not string hex) return;
        ColorPickerPopup.IsOpen = false;
        await vm.SetColorAsync(env, hex);
    }

    private async void OnClearColor_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EnvironmentsViewModel vm || vm.SelectedEnvironment is not { } env) return;
        ColorPickerPopup.IsOpen = false;
        await vm.SetColorAsync(env, null);
    }

    /// <summary>Minimal Yes/No confirmation dialog used by Delete.</summary>
    private static async System.Threading.Tasks.Task<bool> ConfirmDeleteAsync(Window owner, string envName)
    {
        var dlg = new Window
        {
            Title = "Delete environment",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        var ok = new Button { Content = "Delete", IsDefault = true, Margin = new global::Avalonia.Thickness(4) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new global::Avalonia.Thickness(4) };
        var result = false;
        ok.Click += (_, _) => { result = true; dlg.Close(); };
        cancel.Click += (_, _) => { result = false; dlg.Close(); };

        dlg.Content = new StackPanel
        {
            Margin = new global::Avalonia.Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = $"Delete environment “{envName}”? This removes the env file from disk.",
                    TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = global::Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { cancel, ok },
                },
            },
        };

        await dlg.ShowDialog(owner);
        return result;
    }
}
