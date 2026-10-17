using System.Globalization;
using Avalonia.Data.Converters;
using Vegha.Integrations.Git;

namespace Vegha.App.Controls.Git;

/// <summary>Button caption for an error-banner remedy.</summary>
public sealed class GitErrorActionLabelConverter : IValueConverter
{
    public static readonly GitErrorActionLabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        GitErrorAction.AddRemote => "Add remote…",
        GitErrorAction.PublishRepository => "Publish…",
        GitErrorAction.SignIn => "Sign in…",
        GitErrorAction.ConfigureCredentials => "Credentials…",
        GitErrorAction.TrustHost => "Verify & trust host…",
        GitErrorAction.OpenSsoAuthorization => "Authorize SSO ↗",
        GitErrorAction.PullFirst => "Pull now",
        GitErrorAction.OpenDiagnostics => "Diagnose…",
        GitErrorAction.OpenNetworkSettings => "Network settings…",
        GitErrorAction.InstallGit => "Install Git…",
        GitErrorAction.ResolveConflicts => "OK",
        GitErrorAction.StashAndRetry => "Stash changes",
        _ => value?.ToString(),
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
