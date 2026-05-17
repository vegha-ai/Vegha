using Avalonia.Controls;
using Avalonia.Interactivity;
using Vegha.App.ViewModels.Settings;

namespace Vegha.App.Controls.Settings.Pages;

public partial class AppearancePage : UserControl
{
    public AppearancePage()
    {
        InitializeComponent();
    }

    private void OnModeClick(object? sender, RoutedEventArgs e)
    {
        // The radio's IsChecked binding is one-way (the VM owns the ThemeMode string and the
        // radios reflect equality). Setting ThemeMode imperatively keeps the model authoritative
        // and avoids the "two radios checked at once" flicker that happens with two-way bindings
        // on a string-valued group. We pull the mode from Tag rather than Content because the
        // Light/Dark radios render rich content (icon + text) instead of a plain string.
        if (sender is RadioButton { Tag: string mode } && DataContext is AppearanceSettingsViewModel vm)
        {
            vm.ThemeMode = mode;
        }
    }

    private void OnVariantClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ThemeVariantEntry entry } &&
            DataContext is AppearanceSettingsViewModel vm)
        {
            if (entry.Mode == "light") vm.ThemeVariantLight = entry.Id;
            else vm.ThemeVariantDark = entry.Id;
        }
    }
}
