using Vegha.Core.Domain;

namespace Vegha.App.ViewModels.Tabs;

/// <summary>SOAP request tab — wraps a <see cref="SoapWorkspaceViewModel"/>. The tab strip
/// shows method "SOAP" (treated as a kind label, not an HTTP verb) and the request name.</summary>
public sealed class SoapRequestTabViewModel : RequestTabViewModel
{
    public SoapWorkspaceViewModel Workspace_ { get; }

    public override object Workspace => Workspace_;

    public SoapRequestTabViewModel(SoapWorkspaceViewModel workspace, RequestItem? request, string? sourcePath, string id)
    {
        Workspace_ = workspace;
        Id = id;
        SourcePath = sourcePath;
        Method = "SOAP";
        Kind = RequestKind.Soap;
        Name = request?.Name ?? "Untitled SOAP";
    }
}
