using CommunityToolkit.Mvvm.ComponentModel;
using Vegha.Core.Persistence;

namespace Vegha.App.ViewModels.Settings;

/// <summary>Editor page VM — font family/size, tab size, word wrap, line numbers. These
/// values are pushed into <see cref="global::Avalonia.Application.Resources"/> by the host
/// on Save so all open editors update live.</summary>
public partial class EditorSettingsViewModel : SettingsPageBase
{
    public override string Id => "editor";
    public override string Title => "Editor";
    public override string IconKey => "Code";

    [ObservableProperty] private string _fontFamily = "JetBrains Mono";
    [ObservableProperty] private int _fontSize = 12;
    [ObservableProperty] private int _tabSize = 2;
    [ObservableProperty] private bool _wordWrap = true;
    [ObservableProperty] private bool _showLineNumbers = true;

    public override void ReadFrom(AppSettings s)
    {
        FontFamily = s.FontFamily;
        FontSize = s.FontSize;
        TabSize = s.EditorTabSize;
        WordWrap = s.EditorWordWrap;
        ShowLineNumbers = s.EditorShowLineNumbers;
    }

    public override AppSettings WriteTo(AppSettings e) => e with
    {
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? "JetBrains Mono" : FontFamily,
        FontSize = Math.Clamp(FontSize, 8, 24),
        EditorTabSize = Math.Clamp(TabSize, 1, 8),
        EditorWordWrap = WordWrap,
        EditorShowLineNumbers = ShowLineNumbers,
    };
}
