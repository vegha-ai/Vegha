using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Vegha.App.ViewModels.Runner;

/// <summary>Backs the runner sidebar panel. Lists collections the user can run and exposes a
/// "New Run" action per collection. Past runs (in-memory only for v1) are also listed so the
/// user can jump back to an open tab.</summary>
public partial class RunnerSidebarViewModel : ObservableObject
{
    private readonly CollectionsViewModel _collections;

    public ObservableCollection<CollectionRootViewModel> AvailableCollections => _collections.AvailableCollections;

    [ObservableProperty]
    private CollectionRootViewModel? _activeCollection;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Host hook: invoked when the user clicks "Run" for a collection. The host
    /// (MainWindow) opens (or activates) a CollectionRunTab for that collection.</summary>
    public Action<CollectionRootViewModel>? OpenRunRequested { get; set; }

    public RunnerSidebarViewModel(CollectionsViewModel collections)
    {
        _collections = collections;
        ActiveCollection = collections.ActiveCollection;
        collections.PropertyChanged += OnCollectionsPropertyChanged;
    }

    private void OnCollectionsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CollectionsViewModel.ActiveCollection))
            ActiveCollection = _collections.ActiveCollection;
    }

    [RelayCommand]
    private void NewRun(CollectionRootViewModel? root)
    {
        var target = root ?? ActiveCollection;
        if (target?.Collection is null)
        {
            StatusMessage = "Open a collection first.";
            return;
        }
        OpenRunRequested?.Invoke(target);
    }

    [RelayCommand]
    private void NewRunForActive() => NewRun(ActiveCollection);
}
