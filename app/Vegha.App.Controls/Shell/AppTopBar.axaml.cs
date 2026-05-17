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
            RebuildCollectionPickerMenu();
        };
        if (CollectionPickerButton?.Flyout is MenuFlyout mf)
            mf.Opening += (_, _) => RebuildCollectionPickerMenu();
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
            RebuildCollectionPickerMenu();
    }

    private void OnAvailableCollectionsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        RebuildCollectionPickerMenu();

    /// <summary>Builds one MenuItem per workspace collection. The active row is bolded +
    /// ✓-prefixed. Clicking flips <c>Collections.ActiveCollection</c>.</summary>
    private void RebuildCollectionPickerMenu()
    {
        if (CollectionPickerButton?.Flyout is not MenuFlyout flyout) return;
        flyout.Items.Clear();
        if (DataContext is not MainWindowViewModel vm) return;
        var collections = vm.Collections;
        foreach (var c in collections.AvailableCollections)
        {
            var item = new MenuItem { Header = c.Name, Tag = c };
            if (ReferenceEquals(c, collections.ActiveCollection))
            {
                item.Icon = new TextBlock
                {
                    Text = "✓",
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#7C3AED")),
                };
                item.Foreground = new SolidColorBrush(Color.Parse("#7C3AED"));
                item.FontWeight = FontWeight.SemiBold;
            }
            item.Click += OnPickCollection_Click;
            flyout.Items.Add(item);
        }
    }

    private void OnPickCollection_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not MenuItem mi || mi.Tag is not CollectionRootViewModel item) return;
        vm.Collections.ActiveCollection = item;
        CollectionPickerButton?.Flyout?.Hide();
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
