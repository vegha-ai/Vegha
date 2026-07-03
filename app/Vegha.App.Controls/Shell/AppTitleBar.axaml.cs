using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Vegha.App.Controls.Icons;
using Vegha.App.Controls.Workspace;
using Vegha.App.ViewModels;

namespace Vegha.App.Controls.Shell;

public partial class AppTitleBar : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<AppTitleBar, string?>(nameof(Title));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Flyout shown by the hamburger button at the title bar's left edge. Set from
    /// MainWindow.axaml so File/Edit/View/Help menu items keep their Click handlers wired
    /// to MainWindow code-behind.</summary>
    public static readonly StyledProperty<FlyoutBase?> HamburgerMenuProperty =
        AvaloniaProperty.Register<AppTitleBar, FlyoutBase?>(nameof(HamburgerMenu));

    public FlyoutBase? HamburgerMenu
    {
        get => GetValue(HamburgerMenuProperty);
        set => SetValue(HamburgerMenuProperty, value);
    }

    /// <summary>The workspace switcher's data source. Set from MainWindow so the title bar
    /// can render the dropdown without taking a direct dependency on MainWindowViewModel.</summary>
    public static readonly StyledProperty<WorkspacesViewModel?> WorkspacesProperty =
        AvaloniaProperty.Register<AppTitleBar, WorkspacesViewModel?>(nameof(Workspaces));

    public WorkspacesViewModel? Workspaces
    {
        get => GetValue(WorkspacesProperty);
        set => SetValue(WorkspacesProperty, value);
    }

    public AppTitleBar()
    {
        InitializeComponent();
        // Build the workspace menu eagerly + on every relevant change so the flyout is
        // always populated regardless of whether MenuFlyout.Opening fires reliably. The
        // initial build runs once Workspaces gets set from MainWindow.axaml's binding.
    }

    /// <summary>Reacts to the Workspaces styled property being assigned (from MainWindow)
    /// and to changes inside it (workspace add/remove, active-workspace change). Triggers
    /// a menu rebuild so the dropdown reflects current state without relying on
    /// MenuFlyout.Opening.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WorkspacesProperty)
        {
            if (change.GetOldValue<WorkspacesViewModel?>() is { } prev)
            {
                prev.PropertyChanged -= OnWorkspacesPropertyChanged;
                prev.Workspaces.CollectionChanged -= OnWorkspacesCollectionChanged;
            }
            if (change.GetNewValue<WorkspacesViewModel?>() is { } next)
            {
                next.PropertyChanged += OnWorkspacesPropertyChanged;
                next.Workspaces.CollectionChanged += OnWorkspacesCollectionChanged;
            }
            RebuildWorkspaceMenuIfReady();
        }
    }

    private void OnWorkspacesPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspacesViewModel.ActiveWorkspace))
            RebuildWorkspaceMenuIfReady();
    }

    private void OnWorkspacesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => RebuildWorkspaceMenuIfReady();

    private void RebuildWorkspaceMenuIfReady()
    {
        if (WorkspaceDropButton?.Flyout is MenuFlyout mf) RebuildWorkspaceMenu(mf);
    }

    /// <summary>Title-bar drag + double-click-to-maximize. ExtendClientAreaToDecorationsHint=True means
    /// the OS no longer renders the title strip, so the app must drive window movement itself.</summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var window = TopLevel.GetTopLevel(this) as Window;
            if (window is null) return;

            if (e.ClickCount == 2)
            {
                window.WindowState = window.WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
                e.Handled = true;
                return;
            }

            window.BeginMoveDrag(e);
            e.Handled = true;
        }
    }

    private async void OnCreateWorkspace_Click(object? sender, RoutedEventArgs e)
    {
        if (Workspaces is null) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        var dlg = new CreateWorkspaceDialog();
        var ok = await dlg.ShowDialog<bool>(owner);
        if (!ok || dlg.Result is null) return;
        Workspaces.CreateWorkspace(dlg.Result.Name, dlg.Result.FolderPath);
    }

    private async void OnOpenWorkspace_Click(object? sender, RoutedEventArgs e)
    {
        if (Workspaces is null) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open existing workspace folder",
            AllowMultiple = false,
        });
        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) Workspaces.AddWorkspace(path);
    }

    private async void OnManageWorkspaces_Click(object? sender, RoutedEventArgs e)
    {
        if (Workspaces is null) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        // The dialog is self-contained now (rename runs inline through the VM); the old
        // Edit → WorkspaceEditRequested routed-event bridge went away with the workspace
        // editor's retirement.
        var dlg = new ManageWorkspacesDialog(Workspaces);
        await dlg.ShowDialog(owner);
    }

    private void OnRemoveWorkspace_Click(object? sender, RoutedEventArgs e)
    {
        if (Workspaces is null) return;
        if (sender is not MenuItem mi || mi.Tag is not WorkspaceItemViewModel item) return;
        e.Handled = true;
        Workspaces.RemoveWorkspaceCommand.Execute(item);
    }

    /// <summary>Rebuilds the workspace menu every time it opens: one row per workspace with
    /// a check on the active one, then a "Workspaces" section header, then the Create / Open /
    /// Manage actions. Dynamic rebuild (vs. data-bound items) keeps mixing static actions
    /// with dynamic workspace rows straightforward.</summary>
    private void RebuildWorkspaceMenu(MenuFlyout flyout)
    {
        flyout.Items.Clear();
        if (Workspaces is null) return;

        foreach (var ws in Workspaces.Workspaces)
        {
            var item = new MenuItem
            {
                Header = ws.Name,
                Tag = ws,
            };
            if (ReferenceEquals(ws, Workspaces.ActiveWorkspace))
            {
                item.Icon = new TextBlock
                {
                    Text = "✓",
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#7C3AED")), // matches the active-row accent in the mock
                };
                item.Foreground = new SolidColorBrush(Color.Parse("#7C3AED"));
                item.FontWeight = FontWeight.SemiBold;
            }
            item.Click += OnSelectWorkspace_Click;
            flyout.Items.Add(item);
        }

        if (Workspaces.Workspaces.Count > 0) flyout.Items.Add(new Separator());

        // Section heading — a non-interactive menu row styled like a small label. We rebuild
        // these every Opening so they show up under whatever workspace rows we just added.
        flyout.Items.Add(BuildSectionHeader("Workspaces"));

        flyout.Items.Add(BuildActionItem("Create workspace…",  IconKind.Plus,       OnCreateWorkspace_Click));
        flyout.Items.Add(BuildActionItem("Open workspace…",    IconKind.FolderOpen, OnOpenWorkspace_Click));
        flyout.Items.Add(BuildActionItem("Manage workspaces…", IconKind.Settings,   OnManageWorkspaces_Click));
    }

    private void OnSelectWorkspace_Click(object? sender, RoutedEventArgs e)
    {
        if (Workspaces is null) return;
        if (sender is not MenuItem mi || mi.Tag is not WorkspaceItemViewModel item) return;
        Workspaces.ActiveWorkspace = item;
        WorkspaceDropButton?.Flyout?.Hide();
    }

    private static MenuItem BuildSectionHeader(string text) => new()
    {
        Header = new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Opacity = 0.6,
        },
        IsEnabled = false,
        Padding = new Thickness(8, 4),
    };

    private static MenuItem BuildActionItem(string text, IconKind iconKind, EventHandler<RoutedEventArgs> click)
    {
        var item = new MenuItem
        {
            Header = text,
            Icon = new Icon { Kind = iconKind, Size = 14 },
        };
        item.Click += click;
        return item;
    }
}
