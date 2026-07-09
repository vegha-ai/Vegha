using Avalonia.Controls;
using Avalonia.Interactivity;
using Vegha.App.ViewModels;

namespace Vegha.App.Controls.Workspace;

/// <summary>
/// Manage Global Environments — a modal host around the shared <c>EnvironmentsPanel</c>
/// bound to a GLOBAL-scoped <see cref="EnvironmentsViewModel"/> (the workspace's
/// <c>environments/</c> folder). Replaces the retired workspace editor's Environments
/// sub-tab; opened from the top bar's Global env pill → Configure.
/// </summary>
public partial class ManageGlobalEnvironmentsDialog : Window
{
    private readonly EnvironmentsViewModel? _vm;

    public ManageGlobalEnvironmentsDialog()
    {
        InitializeComponent();
    }

    public ManageGlobalEnvironmentsDialog(EnvironmentsViewModel globalScopedVm) : this()
    {
        _vm = globalScopedVm;
        DataContext = globalScopedVm;
        // The VM is transient but subscribes to the long-lived CollectionsViewModel —
        // detach on close or the shared VM keeps this dialog's VM alive forever.
        Closed += (_, _) => _vm?.Detach();
    }

    private void OnClose_Click(object? sender, RoutedEventArgs e) => Close();
}
