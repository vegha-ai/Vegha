using Vegha.Core.Domain;

namespace Vegha.Core.Exporters;

/// <summary>Target formats the Export dialog offers.</summary>
public enum ExportFormat
{
    PostmanV21,
    OpenApi30,
    Bruno,
    InsomniaV4,
}

/// <summary>Knobs for <see cref="ExportService.Export"/>.</summary>
/// <param name="IncludeEnvironments">Emit collection (and, for workspace scope, workspace)
/// environments alongside the collection. Ignored by OpenAPI, which has no env concept.</param>
/// <param name="IncludeSecrets">When false (the default), variables listed in
/// <see cref="Domain.Environment.SecretVariables"/> are exported with an empty value.</param>
/// <param name="OpenApiYaml">OpenAPI only: YAML instead of JSON.</param>
/// <param name="TranslateScripts">Postman only: rewrite <c>bru.*</c> / <c>req</c> / <c>res</c>
/// calls into <c>pm.*</c> so scripts run in Postman.</param>
public sealed record ExportOptions(
    bool IncludeEnvironments = true,
    bool IncludeSecrets = false,
    bool OpenApiYaml = false,
    bool TranslateScripts = true);

/// <summary>One collection to export. <see cref="SourcePath"/> is the on-disk Bruno root
/// (used by the Bruno exporter to copy JS files and other side files verbatim); null for
/// in-memory collections.</summary>
public sealed record ExportSource(Collection Collection, string? SourcePath = null);

/// <summary>Workspace-level payload included in a whole-workspace export.</summary>
/// <param name="Name">Workspace display name — used for the zip file name.</param>
/// <param name="Model">Workspace environments + scripts from <c>WorkspaceModelLoader</c>.</param>
public sealed record ExportWorkspace(string Name, Vegha.Core.FileFormat.WorkspaceModel Model);

/// <summary>One file produced by a format exporter. <see cref="RelativePath"/> uses forward
/// slashes; it becomes the zip entry name when the export is packaged.</summary>
public sealed record ExportedFile(string RelativePath, byte[] Content);

/// <summary>The packaged outcome of an export: a single file (or zip) ready to write to the
/// path the user picks, plus lossy-mapping notes to surface in the dialog.</summary>
public sealed record ExportResult(
    string SuggestedFileName,
    byte[] Content,
    bool IsZip,
    int CollectionCount,
    IReadOnlyList<string> Warnings);

/// <summary>Display metadata for each format (dialog picker + file naming).</summary>
public sealed record ExportFormatInfo(ExportFormat Format, string Label, string FidelityNote)
{
    public static IReadOnlyList<ExportFormatInfo> All { get; } = new[]
    {
        new ExportFormatInfo(ExportFormat.PostmanV21, "Postman v2.1",
            "Requests, folders, auth, variables and scripts. Folder headers are copied into each request."),
        new ExportFormatInfo(ExportFormat.OpenApi30, "OpenAPI 3.0",
            "Paths, parameters, bodies and security schemes. Scripts, tests and environments are not included."),
        new ExportFormatInfo(ExportFormat.Bruno, "Bruno (.bru zip)",
            "Lossless — the collection folder exactly as Vegha stores it, ready to open in Bruno."),
        new ExportFormatInfo(ExportFormat.InsomniaV4, "Insomnia v4",
            "Requests, folders, auth and environments. Scripts are not included."),
    };

    public static ExportFormatInfo For(ExportFormat format) => All.First(f => f.Format == format);
}
