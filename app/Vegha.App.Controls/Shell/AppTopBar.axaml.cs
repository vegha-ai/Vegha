using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Vegha.App.Controls.Icons;
using Vegha.App.Controls.Workspace;
using Vegha.App.ViewModels;

namespace Vegha.App.Controls.Shell;

public partial class AppTopBar : UserControl
{
    /// <summary>Raised when the user picks "Import collection" from the "+" flyout. The host
    /// (MainWindow) opens the unified import wizard (the bar can't reach the dialog factory
    /// itself without a circular reference).</summary>
    public event EventHandler? ImportRequested;

    /// <summary>Raised when the user picks "Open collection" and a folder is chosen. The host
    /// routes this to <c>WorkspacesViewModel.LinkCollection</c> so the path persists in the
    /// active workspace.</summary>
    public event EventHandler<string>? OpenCollectionRequested;

    public AppTopBar()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            DetachCollectionListeners();
            AttachCollectionListeners();
            RebuildCollectionPickerList();
        };
        if (CollectionPickerButton?.Flyout is Flyout flyout)
            flyout.Opened += OnCollectionPickerFlyoutOpened;
    }

    /// <summary>Flyout content is loaded eagerly enough that x:Name fields populate, but
    /// wiring the TextChanged / Click handlers here (lazily, once) is the safe pattern —
    /// it avoids the constructor running before the named controls are attached.</summary>
    private bool _pickerFlyoutWired;
    private void OnCollectionPickerFlyoutOpened(object? sender, EventArgs e)
    {
        if (!_pickerFlyoutWired)
        {
            _pickerFlyoutWired = true;
            if (CollectionPickerFilterBox is not null)
                CollectionPickerFilterBox.TextChanged += (_, _) => RebuildCollectionPickerList();
            if (CollectionPickerClearFilter is not null)
                CollectionPickerClearFilter.Click += (_, _) =>
                {
                    if (CollectionPickerFilterBox is not null)
                        CollectionPickerFilterBox.Text = string.Empty;
                };
        }
        // Reset + focus the search box every time the flyout opens. Without the reset a
        // stale filter from the previous open hides rows on second open; without the focus
        // the user has to click into the box before typing.
        if (CollectionPickerFilterBox is not null)
        {
            CollectionPickerFilterBox.Text = string.Empty;
            CollectionPickerFilterBox.Focus();
        }
        RebuildCollectionPickerList();
        // Always open at the top — the ScrollViewer otherwise keeps the previous session's
        // offset (e.g. scrolled to a collection deep in the list), so the flyout appeared
        // "opened partway down." Post so the reset runs after the rebuilt content lays out.
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (CollectionPickerScroller is not null)
                CollectionPickerScroller.Offset = new global::Avalonia.Vector(0, 0);
        }, global::Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private CollectionsViewModel? _attachedCollections;

    private void AttachCollectionListeners()
    {
        if (DataContext is MainWindowViewModel vm)
        {
            _attachedCollections = vm.Collections;
            _attachedCollections.PropertyChanged += OnCollectionsPropertyChanged;
            _attachedCollections.AvailableCollections.CollectionChanged += OnAvailableCollectionsChanged;
        }
    }

    private void DetachCollectionListeners()
    {
        if (_attachedCollections is not null)
        {
            _attachedCollections.PropertyChanged -= OnCollectionsPropertyChanged;
            _attachedCollections.AvailableCollections.CollectionChanged -= OnAvailableCollectionsChanged;
            _attachedCollections = null;
        }
    }

    private void OnCollectionsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CollectionsViewModel.ActiveCollection))
            RebuildCollectionPickerList();
    }

    private void OnAvailableCollectionsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        RebuildCollectionPickerList();

    /// <summary>Builds one row per workspace collection in the picker flyout, filtered by the
    /// search box. Each row carries a click-to-activate handler, a "..." menu, and a right-click
    /// context flyout that surfaces the same per-row actions. Replaces the old MenuFlyout —
    /// MenuItem doesn't support inline trailing buttons + a stable search box at the top, so we
    /// build a regular Flyout with custom row controls.</summary>
    private void RebuildCollectionPickerList()
    {
        if (CollectionPickerList is null) return;
        CollectionPickerList.Children.Clear();
        if (DataContext is not MainWindowViewModel vm) return;
        var collections = vm.Collections;
        var workspaces = vm.Workspaces;

        var filter = CollectionPickerFilterBox?.Text ?? string.Empty;
        if (CollectionPickerClearFilter is not null)
            CollectionPickerClearFilter.IsVisible = !string.IsNullOrEmpty(filter);

        // Everything here is scoped to the CURRENT workspace — AvailableCollections is already
        // the active workspace's collections. The set is split into the "open" working set
        // (capped, MRU) and the remaining collections in the workspace.
        var openPaths = workspaces.ActiveWorkspace?.OpenCollectionPaths ?? new List<string>();
        int OpenRank(CollectionRootViewModel c)
        {
            var idx = openPaths.FindIndex(p => string.Equals(p, c.SourcePath, StringComparison.OrdinalIgnoreCase));
            return idx < 0 ? int.MaxValue : idx;
        }
        bool IsOpen(CollectionRootViewModel c) =>
            openPaths.Any(p => string.Equals(p, c.SourcePath, StringComparison.OrdinalIgnoreCase));

        var matching = collections.AvailableCollections.Where(c => MatchesFilter(c.Name, filter)).ToList();
        var open = matching.Where(IsOpen).OrderBy(OpenRank).ToList();     // MRU order
        var others = matching.Where(c => !IsOpen(c)).ToList();            // remaining in this workspace

        if (open.Count > 0)
        {
            CollectionPickerList.Children.Add(SectionHeader("OPEN COLLECTIONS"));
            foreach (var c in open)
                CollectionPickerList.Children.Add(BuildCollectionPickerRow(
                    c, isActive: ReferenceEquals(c, collections.ActiveCollection), isOpen: true));
        }

        if (others.Count > 0)
        {
            CollectionPickerList.Children.Add(SectionHeader(open.Count > 0 ? "OTHER COLLECTIONS" : "COLLECTIONS"));
            foreach (var c in others)
                CollectionPickerList.Children.Add(BuildCollectionPickerRow(
                    c, isActive: ReferenceEquals(c, collections.ActiveCollection), isOpen: false));
        }

        if (open.Count == 0 && others.Count == 0)
        {
            CollectionPickerList.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(filter) ? "No collections" : "No matches",
                FontSize = 11,
                FontStyle = FontStyle.Italic,
                Foreground = ResolveBrush("Text3Brush", Brushes.Gray),
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 12),
            });
        }
    }

    private TextBlock SectionHeader(string text) => new()
    {
        Text = text,
        FontSize = 9,
        FontWeight = FontWeight.SemiBold,
        LetterSpacing = 1,
        Foreground = ResolveBrush("Text2Brush", Brushes.Gray),
        Margin = new Thickness(12, 8, 12, 4),
    };

    private static bool MatchesFilter(string? name, string filter) =>
        string.IsNullOrEmpty(filter) ||
        (name is not null && name.Contains(filter, StringComparison.OrdinalIgnoreCase));

    /// <summary>Builds a single picker row: [✓ marker][name][...][✕]. Clicking the row body
    /// switches the active collection (opening it if it wasn't); the trailing "..." button and
    /// right-click open the per-row action menu (rename / reveal / settings / remove). The ✕
    /// (open rows only) CLOSES the collection — removes it from the open set but keeps it linked
    /// to the workspace, so it reappears under "Other collections" and reopens instantly.</summary>
    private Control BuildCollectionPickerRow(CollectionRootViewModel collection, bool isActive, bool isOpen)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
        };

        var check = new TextBlock
        {
            Text = "✓",
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#7C3AED")),
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Width = 12,
            TextAlignment = TextAlignment.Center,
            IsVisible = isActive,
        };
        Grid.SetColumn(check, 0);
        grid.Children.Add(check);

        var name = new TextBlock
        {
            Text = collection.Name,
            FontSize = 12,
            FontWeight = isActive ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = isActive
                ? new SolidColorBrush(Color.Parse("#7C3AED"))
                : ResolveBrush("Text0Brush", Brushes.Black),
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        // Trailing "..." opens the per-row action menu. We stop event propagation so the
        // row's click handler doesn't ALSO fire and switch the active collection.
        var more = new Button
        {
            Content = new TextBlock { Text = "⋯", FontSize = 14 },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = ResolveBrush("Text2Brush", Brushes.Gray),
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand),
        };
        ToolTip.SetTip(more, "Collection actions");
        more.Click += (sender, e) =>
        {
            e.Handled = true;
            ShowCollectionRowActions(collection, more);
        };
        Grid.SetColumn(more, 2);
        grid.Children.Add(more);

        // Trailing "✕" (open rows only) CLOSES the collection: removes it from the workspace's
        // open set but keeps it linked (unlike the "…" menu's Remove, which unlinks). It stays
        // loaded and reappears under "Other collections". Not shown for non-open rows.
        if (isOpen)
        {
            var close = new Button
            {
                Content = new TextBlock { Text = "✕", FontSize = 11 },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = ResolveBrush("Text3Brush", Brushes.Gray),
                Width = 22,
                Height = 24,
                Padding = new Thickness(0),
                Margin = new Thickness(2, 0, 0, 0),
                Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand),
            };
            ToolTip.SetTip(close, "Close collection (keeps it in the workspace)");
            close.Click += (_, e) =>
            {
                e.Handled = true;
                if (DataContext is MainWindowViewModel mvm)
                    mvm.Workspaces.CloseCollection(collection.SourcePath);
                RebuildCollectionPickerList();
            };
            Grid.SetColumn(close, 3);
            grid.Children.Add(close);
        }

        var rowButton = new Button { Content = grid };
        rowButton.Classes.Add("envPickerRow");
        rowButton.Padding = new Thickness(12, 6, 8, 6);
        // Open rows carry a subtle accent-tinted background so the "open" group reads as
        // distinct from the (untinted) "other collections" below it at a glance. The hover
        // style paints over it, so hover feedback still works.
        if (isOpen)
            rowButton.Background = new SolidColorBrush(Color.Parse("#1F7C3AED")); // ~12% accent
        rowButton.Click += (_, _) =>
        {
            if (DataContext is MainWindowViewModel mvm)
                mvm.Collections.ActiveCollection = collection;
            CollectionPickerButton?.Flyout?.Hide();
        };
        // Right-click surfaces the same per-row actions as the "..." button. Wired on the
        // row button (not the inner TextBlock) so any pointer in the row triggers it.
        rowButton.ContextFlyout = BuildCollectionRowMenu(collection);
        return rowButton;
    }

    /// <summary>Builds the rename/reveal/settings/remove menu used by both the row's "..."
    /// button and its right-click context flyout. Built fresh each time so callbacks capture
    /// the current VM reference.</summary>
    private MenuFlyout BuildCollectionRowMenu(CollectionRootViewModel collection)
    {
        var flyout = new MenuFlyout();
        flyout.Items.Add(MenuItemFor("Rename", () => InvokeRowRename(collection)));
        flyout.Items.Add(MenuItemFor("Reveal in File Explorer", () => InvokeRowReveal(collection)));
        flyout.Items.Add(MenuItemFor("Settings", () => InvokeRowSettings(collection)));
        flyout.Items.Add(new Separator());
        // "Remove from workspace" unlinks the collection (files stay on disk) — heavier than
        // the row's ✕, which only closes it.
        flyout.Items.Add(MenuItemFor("Remove from workspace", () => InvokeRowRemove(collection)));
        return flyout;
    }

    private static MenuItem MenuItemFor(string header, Action onClick)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    private void ShowCollectionRowActions(CollectionRootViewModel collection, Control anchor)
    {
        var menu = BuildCollectionRowMenu(collection);
        menu.ShowAt(anchor);
    }

    private async void InvokeRowRename(CollectionRootViewModel collection)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        CollectionPickerButton?.Flyout?.Hide();
        await CollectionDialogActions.PromptAndRenameAsync(owner, vm.Collections, collection);
    }

    private void InvokeRowReveal(CollectionRootViewModel collection)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        CollectionPickerButton?.Flyout?.Hide();
        CollectionDialogActions.Reveal(vm.Collections, collection);
    }

    private void InvokeRowSettings(CollectionRootViewModel collection)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        CollectionPickerButton?.Flyout?.Hide();
        CollectionDialogActions.OpenSettings(vm.Collections, collection);
    }

    private async void InvokeRowRemove(CollectionRootViewModel collection)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        CollectionPickerButton?.Flyout?.Hide();
        await CollectionDialogActions.ConfirmAndRemoveAsync(owner, vm.Collections, collection);
    }

    private async void OnManageCollections_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        CollectionPickerButton?.Flyout?.Hide();
        var dlg = new ManageCollectionsDialog(vm.Collections, vm.Workspaces);
        dlg.ImportRequested += (_, _) => ImportRequested?.Invoke(this, EventArgs.Empty);
        await dlg.ShowDialog<bool>(owner);
    }

    private async void OnCreateCollection_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        // Default to the active workspace's collections/ folder so a fresh collection lands
        // in the conventional place. Falls back to empty if no workspace is active.
        var defaultDir = vm.Workspaces.ActiveWorkspace is { } ws
            ? System.IO.Path.Combine(ws.FolderPath, "collections")
            : string.Empty;

        var dlg = new CreateCollectionDialog(defaultDir);
        var ok = await dlg.ShowDialog<bool>(owner);
        if (!ok || dlg.Result is null) return;

        var folder = dlg.Result.FolderPath;
        var name = dlg.Result.Name;
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            var bru = $"meta {{\n  name: {name}\n  type: collection\n}}\n";
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "collection.bru"), bru);
            // Drop a .gitignore so per-collection secrets aren't committed if the user inits git.
            Vegha.Core.Persistence.WorkspaceBootstrapper.EnsureCollectionGitIgnore(folder);
            // Route through LinkCollection so the new collection is persisted in
            // workspaces.json. Without this, a collection created outside the workspace's
            // collections/ folder would only exist in memory and vanish on next launch.
            vm.Workspaces.LinkCollection(folder);
        }
        catch (Exception ex)
        {
            vm.Collections.StatusMessage = $"Failed to create collection: {ex.Message}";
        }
    }

    private async void OnOpenCollection_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select a collection folder",
            AllowMultiple = false,
        });

        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        // LinkCollection both loads the tree AND persists the path to workspaces.json.
        // Calling it directly avoids relying on a downstream event subscriber being wired.
        vm.Workspaces.LinkCollection(path);
        // Keep raising the event so other listeners (notifications, telemetry) still work.
        OpenCollectionRequested?.Invoke(this, path);
    }

    private void OnImportCollection_Click(object? sender, RoutedEventArgs e) =>
        ImportRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Public entry points so the workspace-editor tab's "Quick Actions" can reuse
    /// the same handlers as the top-bar dropdown. We forward to the existing private click
    /// handlers (sender=null is fine — they only read DataContext and TopLevel).</summary>
    public void RaiseCreateCollection() => OnCreateCollection_Click(null, new RoutedEventArgs());
    public void RaiseOpenCollection()   => OnOpenCollection_Click(null, new RoutedEventArgs());
    public void RaiseImportCollection() => OnImportCollection_Click(null, new RoutedEventArgs());

    /// <summary>Raised when the user clicks Configure inside the env picker popup. Carries
    /// the scope of the picker tab that was active (collection vs global) so the host can
    /// route to the right surface: collection → left rail Environments panel, global →
    /// workspace editor dialog pre-selected to its Environments sub-tab.</summary>
    public event EventHandler<EnvScope>? ConfigureEnvsRequested;

    private bool _envPickerCollectionTabActive = true;

    private void OnCollectionEnvButton_Click(object? sender, RoutedEventArgs e)
    {
        _envPickerCollectionTabActive = true;
        ShowEnvPicker(CollectionEnvButton);
    }

    private void OnGlobalEnvButton_Click(object? sender, RoutedEventArgs e)
    {
        _envPickerCollectionTabActive = false;
        ShowEnvPicker(GlobalEnvButton);
    }

    private void ShowEnvPicker(Control anchor)
    {
        EnvPickerPopup.PlacementTarget = anchor;
        RebuildEnvPickerContent();
        EnvPickerPopup.IsOpen = true;
    }

    private void RebuildEnvPickerContent()
    {
        RebuildScopeHeader();

        EnvList.Children.Clear();
        if (DataContext is not MainWindowViewModel vm) return;
        var collections = vm.Collections;

        var pickFromCollection = _envPickerCollectionTabActive;
        var items = pickFromCollection
            ? (System.Collections.Generic.IEnumerable<Vegha.Core.Domain.Environment>)collections.CollectionEnvironments
            : collections.GlobalEnvironments;
        var active = pickFromCollection ? collections.ActiveEnvironment : collections.ActiveGlobalEnvironment;

        // "No Environment" — distinct clear-selection action; no dot, dimmer + smaller so
        // users don't mistake it for an environment named "No Environment".
        EnvList.Children.Add(BuildClearRow(active is null, click: () => SelectEnv(null)));

        foreach (var env in items)
        {
            var captured = env;
            EnvList.Children.Add(BuildEnvRow(env.Name, env.Color, isActive: IsActiveEnv(env, active),
                click: () => SelectEnv(captured)));
        }
    }

    /// <summary>True when <paramref name="env"/> is the active environment. Matched by stable
    /// Id rather than reference: the active env and the picker-list entry can be distinct
    /// record instances (after a save or a workspace reload) yet denote the same environment,
    /// so a reference check would wrongly show no row as selected.</summary>
    private static bool IsActiveEnv(
        Vegha.Core.Domain.Environment env,
        Vegha.Core.Domain.Environment? active) =>
        active is not null &&
        (ReferenceEquals(env, active) ||
         (!string.IsNullOrEmpty(active.Id) && string.Equals(env.Id, active.Id, StringComparison.Ordinal)));

    /// <summary>Renders the scope label at the top of the popup. Replaces the old
    /// Collection/Global tab strip now that each pill opens its own picker.</summary>
    private void RebuildScopeHeader()
    {
        if (EnvPickerScopeHeader is null) return;
        EnvPickerScopeHeader.Children.Clear();
        var icon = new Icon
        {
            Kind = _envPickerCollectionTabActive ? IconKind.Collection : IconKind.Workspace,
            Size = 13,
            Foreground = ResolveBrush("Text2Brush", Brushes.Gray),
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
        };
        var label = new TextBlock
        {
            Text = _envPickerCollectionTabActive ? "Collection environments" : "Global environments",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = ResolveBrush("Text2Brush", Brushes.Gray),
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
        };
        EnvPickerScopeHeader.Children.Add(icon);
        EnvPickerScopeHeader.Children.Add(label);
    }

    private void SelectEnv(Vegha.Core.Domain.Environment? env)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (_envPickerCollectionTabActive) vm.Collections.ActiveEnvironment = env;
        else                                vm.Collections.ActiveGlobalEnvironment = env;
        EnvPickerPopup.IsOpen = false;
    }

    private Button BuildEnvRow(string label, string? colorHex, bool isActive, Action click)
    {
        // Grid: [dot] [name] [✓]. The trailing check column reserves space on every row so
        // active and inactive rows stay column-aligned.
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
        };

        // Dot reflects the env's own color when set; otherwise fall back to the historical
        // active-vs-inactive contrast. Inactive envs without a color stay muted so the
        // active row remains visually obvious.
        IBrush dotBrush;
        if (!string.IsNullOrEmpty(colorHex) && Color.TryParse(colorHex, out var parsed))
            dotBrush = new SolidColorBrush(parsed);
        else
            dotBrush = isActive
                ? new SolidColorBrush(Color.Parse("#10B981"))
                : new SolidColorBrush(Color.Parse("#9CA3AF"));

        var dot = new TextBlock
        {
            Text = "●",
            FontSize = 10,
            Foreground = dotBrush,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        var name = new TextBlock
        {
            Text = label,
            FontSize = 12,
            FontWeight = isActive ? FontWeight.SemiBold : FontWeight.Normal,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        // Tick on the active row so users can confirm what's currently selected at a glance.
        var check = new TextBlock
        {
            Text = "✓",
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#16A34A")),
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            IsVisible = isActive,
        };
        Grid.SetColumn(check, 2);
        grid.Children.Add(check);

        var btn = new Button { Content = grid };
        btn.Classes.Add("envPickerRow");
        // Indent so the env list aligns under the "No Environment" header and reads as a
        // grouped list of actual environments.
        btn.Padding = new Thickness(22, 6, 14, 6);
        btn.Click += (_, _) => click();
        return btn;
    }

    /// <summary>"No Environment" row — visually distinct from an actual env: smaller,
    /// dimmer, italicized, no colored dot. Reads as "clear selection" rather than as a
    /// real environment named "No Environment".</summary>
    private Button BuildClearRow(bool isActive, Action click)
    {
        var tb = new TextBlock
        {
            Text = "No Environment",
            FontSize = 11,
            FontStyle = FontStyle.Italic,
            FontWeight = isActive ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = isActive
                ? new SolidColorBrush(Color.Parse("#7C3AED"))
                : ResolveBrush("Text2Brush", Brushes.Gray),
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
        };
        var btn = new Button { Content = tb };
        btn.Classes.Add("envPickerRow");
        btn.Padding = new Thickness(14, 6);
        btn.Click += (_, _) => click();
        return btn;
    }

    private void OnConfigureEnvs_Click(object? sender, RoutedEventArgs e)
    {
        EnvPickerPopup.IsOpen = false;
        ConfigureEnvsRequested?.Invoke(this,
            _envPickerCollectionTabActive ? EnvScope.Collection : EnvScope.Global);
    }

    /// <summary>Resolves a brush resource through the visual tree (falls back to the app
    /// dictionary and finally a literal). <c>FindResource</c> on its own returns
    /// <see cref="AvaloniaProperty.UnsetValue"/> for unknown keys, which crashes a direct cast
    /// to <see cref="IBrush"/>; checking the runtime type with <c>is</c> dodges that.</summary>
    private IBrush ResolveBrush(string key, IBrush fallback)
    {
        var value = this.FindResource(key);
        if (value is IBrush b) return b;
        value = Application.Current?.FindResource(key);
        if (value is IBrush b2) return b2;
        return fallback;
    }
}
