using System.IO.Compression;
using Vegha.Core.FileFormat;
using DomainEnv = Vegha.Core.Domain.Environment;

namespace Vegha.Core.Exporters;

/// <summary>
/// Single funnel for the Export dialog — the counterpart of <c>ImportPipeline</c>. Runs the
/// format exporter for each collection and packages the result:
///
///   - One collection: a single file (<c>.postman_collection.json</c>, <c>.openapi.json</c>,
///     <c>.insomnia.json</c>). Bruno is always a zip; Postman becomes a zip only when
///     environments are included and the collection has any.
///   - Several collections or a whole workspace: one zip —
///     <code>
///     collections/&lt;name&gt;.&lt;ext&gt;          (Bruno: collections/&lt;name&gt;/…)
///     environments/&lt;collection&gt;/&lt;env&gt;…    (Postman collection envs)
///     environments/&lt;env&gt;…                 (workspace envs; Postman + Bruno)
///     scripts/{pre-request,post-response,tests}.js   (workspace scripts)
///     </code>
/// </summary>
public static class ExportService
{
    public static ExportResult Export(
        IReadOnlyList<ExportSource> sources, ExportFormat format, ExportOptions options, ExportWorkspace? workspace = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0) throw new ArgumentException("Nothing to export.", nameof(sources));

        var warnings = new List<string>();
        var globalEnvs = options.IncludeEnvironments && workspace is not null
            ? workspace.Model.Environments.ToList()
            : new List<DomainEnv>();

        return sources.Count == 1 && workspace is null
            ? ExportSingle(sources[0], format, options, warnings)
            : ExportMany(sources, format, options, workspace, globalEnvs, warnings);
    }

    private static ExportResult ExportSingle(ExportSource source, ExportFormat format, ExportOptions options, List<string> warnings)
    {
        var c = source.Collection;
        var stem = ExportNaming.Sanitize(c.Name);
        switch (format)
        {
            case ExportFormat.PostmanV21:
            {
                var main = new ExportedFile(PostmanV2Exporter.FileNameFor(c), PostmanV2Exporter.Export(c, options, warnings));
                var envs = PostmanEnvironments(c.Environments, "environments", options);
                if (envs.Count == 0)
                    return new ExportResult(main.RelativePath, main.Content, IsZip: false, 1, warnings);
                return new ExportResult(stem + ".postman.zip", Zip(envs.Prepend(main)), IsZip: true, 1, warnings);
            }
            case ExportFormat.OpenApi30:
                return new ExportResult(OpenApiExporter.FileNameFor(c, options.OpenApiYaml),
                    OpenApiExporter.Export(c, options, warnings), IsZip: false, 1, warnings);
            case ExportFormat.InsomniaV4:
                return new ExportResult(InsomniaV4Exporter.FileNameFor(c),
                    InsomniaV4Exporter.Export(c, options, warnings), IsZip: false, 1, warnings);
            case ExportFormat.Bruno:
                return new ExportResult(stem + ".bruno.zip", Zip(BrunoExporter.Export(source, stem, options)), IsZip: true, 1, warnings);
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, null);
        }
    }

    private static ExportResult ExportMany(IReadOnlyList<ExportSource> sources, ExportFormat format, ExportOptions options,
        ExportWorkspace? workspace, List<DomainEnv> globalEnvs, List<string> warnings)
    {
        var files = new List<ExportedFile>();
        var usedStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            var c = source.Collection;
            var stem = ExportNaming.Unique(usedStems, ExportNaming.Sanitize(c.Name));
            switch (format)
            {
                case ExportFormat.PostmanV21:
                    files.Add(new ExportedFile($"collections/{stem}.postman_collection.json", PostmanV2Exporter.Export(c, options, warnings)));
                    files.AddRange(PostmanEnvironments(c.Environments, $"environments/{stem}", options));
                    break;
                case ExportFormat.OpenApi30:
                    files.Add(new ExportedFile(
                        $"collections/{stem}{(options.OpenApiYaml ? ".openapi.yaml" : ".openapi.json")}",
                        OpenApiExporter.Export(c, options, warnings)));
                    break;
                case ExportFormat.InsomniaV4:
                    files.Add(new ExportedFile($"collections/{stem}.insomnia.json",
                        InsomniaV4Exporter.Export(c, options, warnings, globalEnvs)));
                    break;
                case ExportFormat.Bruno:
                    files.AddRange(BrunoExporter.Export(source, "collections/" + stem, options));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(format), format, null);
            }
        }

        // Workspace-level environments: Postman + Bruno get standalone files (Insomnia merged
        // them into each collection above; OpenAPI has no environments).
        if (globalEnvs.Count > 0)
        {
            if (format == ExportFormat.PostmanV21)
                files.AddRange(PostmanEnvironments(globalEnvs, "environments", options));
            else if (format == ExportFormat.Bruno)
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var env in globalEnvs)
                    files.Add(new ExportedFile(
                        $"environments/{ExportNaming.Unique(used, ExportNaming.Sanitize(env.Name))}.bru",
                        ExportNaming.ToUtf8(Importers.BruMetaEmitter.EmitEnvironment(env, options.IncludeSecrets))));
            }
        }

        if (workspace is not null)
        {
            void Script(string file, string? body)
            {
                if (!string.IsNullOrWhiteSpace(body))
                    files.Add(new ExportedFile($"{WorkspaceModelLoader.ScriptsFolder}/{file}", ExportNaming.ToUtf8(body!)));
            }
            Script(WorkspaceModelLoader.PreRequestScriptFile, workspace.Model.PreRequestScript);
            Script(WorkspaceModelLoader.PostResponseScriptFile, workspace.Model.PostResponseScript);
            Script(WorkspaceModelLoader.TestsScriptFile, workspace.Model.TestsScript);
        }

        var baseName = ExportNaming.Sanitize(workspace?.Name ?? "collections");
        return new ExportResult($"{baseName}-{ZipSuffix(format)}.zip", Zip(files), IsZip: true, sources.Count, warnings);
    }

    private static List<ExportedFile> PostmanEnvironments(IEnumerable<DomainEnv> envs, string folder, ExportOptions options)
    {
        var list = new List<ExportedFile>();
        if (!options.IncludeEnvironments) return list;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var env in envs)
        {
            var stem = ExportNaming.Unique(used, ExportNaming.Sanitize(env.Name));
            list.Add(new ExportedFile($"{folder}/{stem}.postman_environment.json",
                PostmanV2Exporter.ExportEnvironment(env, options.IncludeSecrets)));
        }
        return list;
    }

    private static string ZipSuffix(ExportFormat format) => format switch
    {
        ExportFormat.PostmanV21 => "postman",
        ExportFormat.OpenApi30 => "openapi",
        ExportFormat.Bruno => "bruno",
        ExportFormat.InsomniaV4 => "insomnia",
        _ => "export",
    };

    private static byte[] Zip(IEnumerable<ExportedFile> files)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                if (!seen.Add(f.RelativePath)) continue;
                var entry = archive.CreateEntry(f.RelativePath, CompressionLevel.Optimal);
                using var s = entry.Open();
                s.Write(f.Content);
            }
        }
        return ms.ToArray();
    }
}
