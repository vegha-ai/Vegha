using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using Vegha.App.Controls.Workspace;
using Vegha.App.ViewModels;
using Vegha.Core.Domain;
using Vegha.Core.Exporters;
using Xunit;

namespace Vegha.Tests.UI;

/// <summary>The Export dialog's option rows follow the selected format, and the Export
/// button follows the scope + checklist.</summary>
public class ExportDialogTests
{
    private static (ExportDialog Dialog, ExportDialogViewModel Vm) Open(string? preselect = null)
    {
        var vm = new ExportDialogViewModel();
        vm.Initialize(new (string, string, Collection?)[]
        {
            ("Alpha", @"Z:\a", new Collection { Name = "Alpha" }),
            ("Beta", @"Z:\b", new Collection { Name = "Beta" }),
        }, "Team", @"Z:\ws", preselect);
        var dlg = new ExportDialog { DataContext = vm };
        dlg.Show();
        dlg.UpdateLayout();
        return (dlg, vm);
    }

    private static CheckBox Option(ExportDialog dlg, string content) =>
        dlg.GetVisualDescendants().OfType<CheckBox>().First(c => (c.Content as string) == content);

    [AvaloniaFact]
    public void Lists_every_collection_and_lists_all_formats()
    {
        var (dlg, _) = Open();
        dlg.GetVisualDescendants().OfType<CheckBox>().Select(c => c.Content as string)
            .Should().Contain(new[] { "Alpha", "Beta" });
        dlg.FindControl<ComboBox>("FormatBox")!.ItemCount.Should().Be(ExportFormatInfo.All.Count);
    }

    [AvaloniaFact]
    public void Environment_options_hide_for_OpenAPI_and_yaml_option_shows()
    {
        var (dlg, vm) = Open();
        Option(dlg, "Include environments").IsEffectivelyVisible.Should().BeTrue();
        Option(dlg, "Write YAML instead of JSON").IsEffectivelyVisible.Should().BeFalse();

        vm.SelectedFormat = ExportFormatInfo.For(ExportFormat.OpenApi30);
        dlg.UpdateLayout();
        Option(dlg, "Include environments").IsEffectivelyVisible.Should().BeFalse();
        Option(dlg, "Write YAML instead of JSON").IsEffectivelyVisible.Should().BeTrue();
    }

    [AvaloniaFact]
    public void Export_button_disables_when_nothing_is_selected()
    {
        var (dlg, vm) = Open(preselect: @"Z:\a");
        var export = dlg.FindControl<Button>("ExportButton")!;
        export.IsEnabled.Should().BeTrue();

        vm.Collections[0].IsSelected = false;
        export.IsEnabled.Should().BeFalse();
    }
}
