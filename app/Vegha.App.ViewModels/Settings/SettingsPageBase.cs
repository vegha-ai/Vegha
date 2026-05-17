using CommunityToolkit.Mvvm.ComponentModel;
using Vegha.Core.Persistence;

namespace Vegha.App.ViewModels.Settings;

/// <summary>Common base for the six Settings page VMs. Each subclass binds its UI to
/// observable properties and implements <see cref="WriteTo"/> to produce an updated
/// <see cref="AppSettings"/> record. <see cref="SettingsWindowViewModel.Save"/> chains
/// WriteTo across all pages so one disk write captures every category.</summary>
public abstract partial class SettingsPageBase : ObservableObject
{
    /// <summary>Stable id used by the navigation rail. Lowercase, no spaces.</summary>
    public abstract string Id { get; }

    /// <summary>Human-readable title shown in the nav rail.</summary>
    public abstract string Title { get; }

    /// <summary>Path geometry key for the nav rail icon — looked up in IconLibrary.</summary>
    public abstract string IconKey { get; }

    /// <summary>Read the current values from <paramref name="settings"/> into the VM's
    /// observable properties. Called by <see cref="SettingsWindowViewModel"/> on construct
    /// and on RestoreDefaults.</summary>
    public abstract void ReadFrom(AppSettings settings);

    /// <summary>Apply this page's edits to <paramref name="existing"/> and return the
    /// updated record. Implementations use the record's <c>with</c> expression.</summary>
    public abstract AppSettings WriteTo(AppSettings existing);
}
