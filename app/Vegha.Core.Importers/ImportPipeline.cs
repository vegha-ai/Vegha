using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vegha.Core.Domain;
using Vegha.Integrations.Wsdl;

namespace Vegha.Core.Importers;

/// <summary>
/// Single funnel for the unified Import dialog (File / URL / Git / GitHub tabs).
/// Each tab acquires raw bytes (file read, HTTP fetch, git clone tree) and hands them
/// here; the pipeline sniffs format, optionally unpacks ZIPs / scans cloned trees, and
/// returns a staged <see cref="ImportResult"/>.
///
/// Detection ladder: ZIP (PK magic) → WSDL (XML namespace sniff) → JSON (Postman env /
/// Postman v2.1 / Insomnia v5 / Insomnia v4 / OpenAPI / Swagger). Bruno folders are
/// detected only when given a directory path via <see cref="DetectAndImportPath"/>.
/// </summary>
public static class ImportPipeline
{
    /// <summary>Entry point for raw bytes (URL fetch, file read). <paramref name="hintFilename"/>
    /// is used only as a fallback for the WSDL path-extension heuristic and to suggest a
    /// collection name when the format itself doesn't carry one. <paramref name="postmanOptions"/>
    /// only matters when the payload is a Postman v2.1 collection — other formats ignore it.</summary>
    public static ImportResult DetectAndImport(byte[] payload, string? hintFilename = null, PostmanImportOptions? postmanOptions = null)
    {
        if (payload is null || payload.Length == 0)
            return ImportResult.Failure("Empty payload.");

        // ZIP — peek the magic bytes. We unpack to a temp dir then recurse-scan.
        if (LooksLikeZip(payload))
        {
            try { return ImportZipPayload(payload, hintFilename, postmanOptions); }
            catch (Exception ex) { return ImportResult.Failure($"ZIP extraction failed: {ex.Message}"); }
        }

        // Try as text — every remaining format is text-based.
        string text;
        try { text = Encoding.UTF8.GetString(payload); }
        catch (Exception ex) { return ImportResult.Failure($"Could not decode payload as UTF-8: {ex.Message}"); }

        return DetectAndImportText(text, hintFilename, postmanOptions);
    }

    /// <summary>Entry point for a directory or file path. Folders take the Bruno-folder
    /// path; files are read and routed through <see cref="DetectAndImport(byte[],string?,PostmanImportOptions?)"/>.</summary>
    public static ImportResult DetectAndImportPath(string path, PostmanImportOptions? postmanOptions = null)
    {
        if (Directory.Exists(path)) return ImportFolder(path);
        if (!File.Exists(path)) return ImportResult.Failure("Path does not exist.");
        return DetectAndImport(File.ReadAllBytes(path), Path.GetFileName(path), postmanOptions);
    }

    private static ImportResult ImportFolder(string path)
    {
        var hasBru = Directory.EnumerateFiles(path, "*.bru", SearchOption.AllDirectories).Any();
        if (!hasBru)
            return ImportResult.Failure("Folder selected, but no .bru files found.");

        var bruCount = Directory.EnumerateFiles(path, "*.bru", SearchOption.AllDirectories).Count();
        return new ImportResult(
            Collection: new Collection { Name = Path.GetFileName(path) },
            Environment: null,
            FormatLabel: "Bruno collection (folder)",
            Summary: $"{bruCount} .bru file(s) under {path}",
            FolderPath: path);
    }

    private static ImportResult DetectAndImportText(string content, string? hintFilename, PostmanImportOptions? postmanOptions = null)
    {
        // WSDL detection beats JSON parsing (the file is XML, not JSON).
        if (LooksLikeWsdl(content)
            || (hintFilename?.EndsWith(".wsdl", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            try
            {
                var col = WsdlImporter.ImportFromString(content);
                return new ImportResult(col, null, "SOAP WSDL 1.1", SummarizeCollection(col));
            }
            catch (Exception ex) { return ImportResult.Failure($"WSDL parse failed: {ex.Message}"); }
        }

        // YAML OpenAPI takes priority when filename hints at YAML — the content is not
        // valid JSON, so we'd otherwise fall off the end. We still re-check via the YAML
        // content sniff after the JSON ladder for files passed without a hint.
        var isYamlHint = hintFilename is not null &&
            (hintFilename.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
             || hintFilename.EndsWith(".yml", StringComparison.OrdinalIgnoreCase));
        if (isYamlHint && LooksLikeOpenApiYaml(content))
        {
            try
            {
                var col = OpenApiImporter.ImportFromString(content);
                return new ImportResult(col, null, "OpenAPI / Swagger (YAML)", SummarizeCollection(col));
            }
            catch (Exception ex) { return ImportResult.Failure($"OpenAPI YAML parse failed: {ex.Message}"); }
        }

        if (TryParseJson(content, out var root))
        {
            // Postman environment.
            if (root!.Value.ValueKind == JsonValueKind.Object &&
                root.Value.TryGetProperty("values", out var values) &&
                values.ValueKind == JsonValueKind.Array &&
                !root.Value.TryGetProperty("info", out _))
            {
                var env = PostmanEnvironmentImporter.ImportFromString(content);
                return new ImportResult(null, env, "Postman environment",
                    $"\"{env.Name}\" · {env.Variables.Count} variable(s)");
            }

            // Postman v2/v2.1 collection.
            if (root.Value.ValueKind == JsonValueKind.Object &&
                root.Value.TryGetProperty("info", out _) &&
                root.Value.TryGetProperty("item", out _))
            {
                var col = PostmanV2Importer.ImportFromJson(content, postmanOptions ?? new PostmanImportOptions());
                return new ImportResult(col, null, "Postman v2.1 collection", SummarizeCollection(col));
            }

            // Insomnia v5.
            if (root.Value.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String &&
                (type.GetString() ?? "").StartsWith("collection.insomnia.rest/5", StringComparison.OrdinalIgnoreCase))
            {
                var col = InsomniaImporter.ImportFromString(content);
                return new ImportResult(col, null, "Insomnia v5 collection", SummarizeCollection(col));
            }

            // Insomnia v4.
            if (root.Value.TryGetProperty("resources", out var resources) &&
                resources.ValueKind == JsonValueKind.Array)
            {
                var col = InsomniaImporter.ImportFromString(content);
                return new ImportResult(col, null, "Insomnia v4 export", SummarizeCollection(col));
            }

            // OpenAPI 3.x or Swagger 2.0.
            if (root.Value.ValueKind == JsonValueKind.Object &&
                (root.Value.TryGetProperty("openapi", out _) || root.Value.TryGetProperty("swagger", out _)))
            {
                var col = OpenApiImporter.ImportFromString(content);
                return new ImportResult(col, null, "OpenAPI / Swagger", SummarizeCollection(col));
            }
        }

        // Content-sniff fallback for YAML OpenAPI specs that arrive without a filename hint.
        if (LooksLikeOpenApiYaml(content))
        {
            try
            {
                var col = OpenApiImporter.ImportFromString(content);
                return new ImportResult(col, null, "OpenAPI / Swagger (YAML)", SummarizeCollection(col));
            }
            catch (Exception ex) { return ImportResult.Failure($"OpenAPI YAML parse failed: {ex.Message}"); }
        }

        return ImportResult.Failure(
            "Content doesn't match any supported format (Bruno folder, Postman v2.1, Postman env, Insomnia v4/v5, OpenAPI, WSDL, ZIP).");
    }

    private static ImportResult ImportZipPayload(byte[] payload, string? hintFilename, PostmanImportOptions? postmanOptions = null)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "Vegha-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        using (var ms = new MemoryStream(payload))
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
        {
            archive.ExtractToDirectory(tempRoot, overwriteFiles: true);
        }

        // Look for a workspace.yml or any *.bru in the extracted tree → treat as a Bruno
        // collection folder (the wizard host will copy the tree to the destination).
        var bruFiles = Directory.EnumerateFiles(tempRoot, "*.bru", SearchOption.AllDirectories).Any();
        if (bruFiles)
        {
            // Use the deepest unique folder containing the .bru files (the archive often
            // wraps everything in a single top-level folder named after the repo / zip).
            var root = ResolveSingleChildIfWrapped(tempRoot);
            var col = new Collection { Name = Path.GetFileName(root) };
            return new ImportResult(col, null, "Bruno collection (zip)",
                $"{Directory.EnumerateFiles(root, "*.bru", SearchOption.AllDirectories).Count()} .bru file(s)",
                FolderPath: root);
        }

        // Otherwise scan extracted files for a single recognizable importable file.
        foreach (var candidate in EnumerateLikelyImports(tempRoot))
        {
            try
            {
                var bytes = File.ReadAllBytes(candidate);
                var inner = DetectAndImport(bytes, Path.GetFileName(candidate), postmanOptions);
                if (inner.Success) return inner;
            }
            catch { /* keep trying */ }
        }

        return ImportResult.Failure("ZIP did not contain a recognizable collection (Bruno tree, Postman/Insomnia JSON, OpenAPI, or WSDL).");
    }

    private static IEnumerable<string> EnumerateLikelyImports(string root)
    {
        // Order by extension bias — JSON first (most common), then YAML/WSDL/XML.
        foreach (var pat in new[] { "*.json", "*.yaml", "*.yml", "*.wsdl", "*.xml" })
            foreach (var f in Directory.EnumerateFiles(root, pat, SearchOption.AllDirectories))
                yield return f;
    }

    private static string ResolveSingleChildIfWrapped(string root)
    {
        // If root has exactly one subdirectory and no files, descend (zip archives commonly
        // wrap everything under <repo-name>/...). Stops as soon as files appear or branching.
        var current = root;
        while (true)
        {
            var dirs = Directory.GetDirectories(current);
            var files = Directory.GetFiles(current);
            if (files.Length > 0 || dirs.Length != 1) return current;
            current = dirs[0];
        }
    }

    /// <summary>Recursively scans a directory (e.g., a freshly cloned git tree) for the
    /// first importable artifact. Used by the Git/GitHub tabs.</summary>
    public static ImportResult ScanDirectory(string root, PostmanImportOptions? postmanOptions = null)
    {
        if (!Directory.Exists(root)) return ImportResult.Failure("Directory does not exist.");

        // Bruno tree wins.
        if (Directory.EnumerateFiles(root, "*.bru", SearchOption.AllDirectories).Any())
        {
            var resolved = ResolveSingleChildIfWrapped(root);
            var col = new Collection { Name = Path.GetFileName(resolved) };
            return new ImportResult(col, null, "Bruno collection (folder)",
                $"{Directory.EnumerateFiles(resolved, "*.bru", SearchOption.AllDirectories).Count()} .bru file(s)",
                FolderPath: resolved);
        }

        foreach (var candidate in EnumerateLikelyImports(root))
        {
            try
            {
                var bytes = File.ReadAllBytes(candidate);
                var inner = DetectAndImport(bytes, Path.GetFileName(candidate), postmanOptions);
                if (inner.Success) return inner;
            }
            catch { /* keep trying */ }
        }
        return ImportResult.Failure("Directory did not contain a recognizable collection.");
    }

    // ---------------- helpers ----------------

    private static bool LooksLikeZip(byte[] b) =>
        b.Length >= 4 && b[0] == 0x50 && b[1] == 0x4B && b[2] == 0x03 && b[3] == 0x04;

    private static bool LooksLikeWsdl(string content)
    {
        var head = content.Length > 4096 ? content[..4096] : content;
        return head.Contains("http://schemas.xmlsoap.org/wsdl/", StringComparison.Ordinal)
            && head.Contains("definitions", StringComparison.Ordinal);
    }

    // Top-level YAML key match: `openapi: 3.x` or `swagger: "2.0"` at column 0
    // (with optional surrounding quotes on the version). We scan the first 4 KiB so
    // we don't pay for the whole file on a negative.
    private static readonly Regex s_openApiYamlHead = new(
        @"^(?:openapi|swagger)\s*:\s*[""']?\d",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static bool LooksLikeOpenApiYaml(string content)
    {
        var head = content.Length > 4096 ? content[..4096] : content;
        return s_openApiYamlHead.IsMatch(head);
    }

    private static bool TryParseJson(string content, out JsonElement? root)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            root = doc.RootElement.Clone();
            return true;
        }
        catch
        {
            root = null;
            return false;
        }
    }

    private static string SummarizeCollection(Collection c)
    {
        int CountAll(Collection col)
        {
            var n = col.Requests.Count;
            void walk(IList<Folder> folders) { foreach (var f in folders) { n += f.Requests.Count; walk(f.Folders); } }
            walk(col.Folders);
            return n;
        }
        return $"\"{c.Name}\" · {CountAll(c)} request(s) · {c.Folders.Count} top-level folder(s)";
    }
}

/// <summary>Outcome of running an import. <see cref="Success"/> is true when either
/// <see cref="Collection"/> or <see cref="Environment"/> is non-null. <see cref="FolderPath"/>
/// is set when the import is best handled by loading a folder directly (Bruno tree from
/// disk or extracted ZIP) rather than re-serializing a parsed Collection.</summary>
public sealed record ImportResult(
    Collection? Collection,
    Vegha.Core.Domain.Environment? Environment,
    string FormatLabel,
    string Summary,
    string? FolderPath = null,
    string? Error = null)
{
    public bool Success => Collection is not null || Environment is not null;

    public static ImportResult Failure(string error) =>
        new(null, null, "Unrecognized format", string.Empty, null, error);
}
