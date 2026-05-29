using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Vegha.App.ViewModels;
using Vegha.App.ViewModels.Tabs;

namespace Vegha.App.Controls.Workspace;

public partial class WorkspaceEditor : UserControl
{
    public WorkspaceEditor()
    {
        InitializeComponent();
    }

    private WorkspaceTabViewModel? Vm => DataContext as WorkspaceTabViewModel;

    // ---- Sub-tab strip ----
    private void OnOverviewTab_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null) Vm.ActiveSection = "overview";
    }

    private void OnEnvironmentsTab_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null) Vm.ActiveSection = "environments";
    }

    // ---- Overview post-import banner ----
    private void OnDismissOverviewMessage_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null) Vm.OverviewMessage = null;
    }

    // ---- Overview quick actions ----
    private void OnCreateCollection_Click(object? sender, RoutedEventArgs e)
        => Vm?.RequestCreateCollection?.Invoke();

    private void OnOpenCollection_Click(object? sender, RoutedEventArgs e)
        => Vm?.RequestOpenCollection?.Invoke();

    private void OnImportCollection_Click(object? sender, RoutedEventArgs e)
        => Vm?.RequestImportCollection?.Invoke();

    /// <summary>Collection-row "…" menu. The bound CollectionRootViewModel travels via Tag so
    /// the host knows which row was clicked.</summary>
    private void OnCollectionMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not CollectionRootViewModel col || Vm is null) return;

        var flyout = new MenuFlyout();
        flyout.Items.Add(MenuItemFor("Activate",  () => Vm.ActivateCollection?.Invoke(col)));
        flyout.Items.Add(MenuItemFor("Rename",    () => Vm.RequestRenameCollection?.Invoke(col)));
        flyout.Items.Add(MenuItemFor("Reveal in File Explorer", () => RevealInExplorer(col.SourcePath)));
        flyout.Items.Add(new Separator());
        flyout.Items.Add(MenuItemFor("Remove",    () => Vm.RequestRemoveCollection?.Invoke(col)));
        var delete = MenuItemFor("Delete",        () => Vm.RequestDeleteCollection?.Invoke(col));
        delete.Foreground = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#DC2626")); // red-600, matches the mock
        flyout.Items.Add(delete);
        flyout.ShowAt(btn);
    }

    private static void RevealInExplorer(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", $"\"{path}\"");
            else
                System.Diagnostics.Process.Start("xdg-open", path);
        }
        catch { /* best-effort */ }
    }

    // ---- Environments ----
    private void OnAddEnvironment_Click(object? sender, RoutedEventArgs e)
        // Persisting the new env (so it survives a dialog close/reopen) needs the workspace
        // folder + file-writing infra the host owns, so the host handles creation. Without
        // this the env lived only in the transient in-memory list and vanished on reopen.
        => Vm?.RequestAddEnvironment?.Invoke();

    private void OnRemoveVariable_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if (sender is not Button btn || btn.Tag is not EnvVarRow row) return;
        Vm.RemoveVariableCommand.Execute(row);
    }

    // ---- Env header actions (rename / copy / delete) ----
    private void OnRenameEnv_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedEnvironment is { } env) Vm.RequestRenameEnvironment?.Invoke(env);
    }

    private void OnCopyEnv_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedEnvironment is { } env) Vm.RequestCopyEnvironment?.Invoke(env);
    }

    private void OnDeleteEnv_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedEnvironment is { } env) Vm.RequestDeleteEnvironment?.Invoke(env);
    }

    private void OnSetEnvColor_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedEnvironment is { } env) Vm.RequestSetEnvColor?.Invoke(env);
    }

    private void OnImportEnvironment_Click(object? sender, RoutedEventArgs e)
        => Vm?.RequestImportEnvironment?.Invoke();

    // ---- Workspace "…" menu ----
    private void OnWorkspaceMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || sender is not Button btn) return;

        var flyout = new MenuFlyout();
        flyout.Items.Add(MenuItemFor("Rename",                () => Vm.RequestRenameWorkspace?.Invoke()));
        flyout.Items.Add(MenuItemFor("Reveal in File Explorer", () => Vm.RequestRevealWorkspaceInExplorer?.Invoke()));
        flyout.Items.Add(MenuItemFor("Export…",               () => Vm.RequestExportWorkspace?.Invoke()));
        flyout.Items.Add(new Separator());
        flyout.Items.Add(MenuItemFor("Close",                 () => Vm.RequestCloseWorkspace?.Invoke()));
        flyout.ShowAt(btn);
    }

    private static MenuItem MenuItemFor(string text, Action click)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => click();
        return item;
    }
}
