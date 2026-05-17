using System.Text.Json;
using System.Xml.Linq;
using Vegha.Core.Importers;
using Vegha.Core.Requests;
using Vegha.Core.Scripting;
using Vegha.Integrations.Secrets;

namespace Vegha.Cli;

/// <summary>
/// Vegha CLI. Two modes:
///   vegha run &lt;collection&gt; [--name &lt;request&gt;]
///   vegha --protocol json
///
/// The JSON-RPC mode (--protocol json) reads requests on stdin, writes responses on
/// stdout, one JSON message per line. This is what the VSCode extension talks to —
/// the executor stays a long-lived process so it can keep cookies + tokens warm.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0) { PrintUsage(); return 1; }

        if (args[0] == "--protocol" && args.Length > 1 && args[1] == "json")
            return await JsonProtocolLoopAsync().ConfigureAwait(false);

        if (args[0] == "run" && args.Length >= 2)
            return await RunCollectionAsync(args[1], args).ConfigureAwait(false);

        if (args[0] == "import" && args.Length >= 3)
            return ImportCollection(args[1], args[2]);

        PrintUsage();
        return 1;
    }

    private static async Task<int> JsonProtocolLoopAsync()
    {
        using var http = new HttpClient();
        var executor = new HttpExecutor(http);

        string? line;
        while ((line = await Console.In.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var method = root.GetProperty("method").GetString();
                var id = root.TryGetProperty("id", out var idEl) ? idEl.ToString() : null;
                JsonElement paramsEl = default;
                root.TryGetProperty("params", out paramsEl);

                var responseObj = method switch
                {
                    "executeRequest" => await HandleExecuteRequest(executor, paramsEl).ConfigureAwait(false),
                    "ping" => (object)new { ok = true },
                    _ => new { error = $"unknown method '{method}'" },
                };

                var envelope = new { id, result = responseObj };
                Console.WriteLine(JsonSerializer.Serialize(envelope));
            }
            catch (Exception ex)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        }
        return 0;
    }

    private static async Task<object> HandleExecuteRequest(HttpExecutor executor, JsonElement p)
    {
        var method = p.TryGetProperty("method", out var m) ? m.GetString() : "GET";
        var url = p.GetProperty("url").GetString()!;
        string? body = p.TryGetProperty("body", out var b) ? b.GetString() : null;
        string? contentType = p.TryGetProperty("contentType", out var ct) ? ct.GetString() : null;
        var headers = new List<KeyValuePair<string, string>>();
        if (p.TryGetProperty("headers", out var hs) && hs.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in hs.EnumerateObject())
                headers.Add(new(prop.Name, prop.Value.GetString() ?? string.Empty));
        }

        var request = new HttpExecutionRequest(
            new HttpMethod(method ?? "GET"),
            new Uri(url),
            Headers: headers,
            Body: body,
            ContentType: contentType);

        var result = await executor.ExecuteAsync(request).ConfigureAwait(false);
        return new
        {
            status = result.StatusCode,
            statusText = result.ReasonPhrase,
            headers = result.Headers.ToDictionary(h => h.Key, h => h.Value),
            body = result.Body,
            elapsedMs = result.ElapsedMilliseconds,
            error = result.ErrorMessage,
        };
    }

    /// <summary>A request slower than this many ms is flagged as a warning. Override with --slow.</summary>
    private const int SlowThresholdMs = 1000;

    private static readonly IReadOnlyDictionary<string, string> s_noVars =
        new Dictionary<string, string>();

    /// <summary>One request's result, retained so a JUnit report can be emitted.</summary>
    private sealed record RunResult(
        string Name, string ClassName, long ElapsedMs,
        bool Failed, bool Warned, int TestCount, int FailedTests, string? Detail);

    private static async Task<int> RunCollectionAsync(string path, string[] args)
    {
        // Collections are Bruno-style .bru folders — load them with the same
        // loader the desktop app uses (CollectionLoader). Accept either the
        // collection directory or a .bru file inside it.
        var root = path;
        if (!Directory.Exists(path) && File.Exists(path))
            root = Path.GetDirectoryName(Path.GetFullPath(path)) ?? path;

        var nameFilter = ArgValue(args, "--name");
        var envName = ArgValue(args, "--env");
        var outPath = ArgValue(args, "--out");
        var reporter = ArgValue(args, "--reporter");
        var slowMs = int.TryParse(ArgValue(args, "--slow"), out var parsedSlow) ? parsedSlow : SlowThresholdMs;

        if (reporter is not null && !string.Equals(reporter, "junit", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Unknown --reporter '{reporter}'. Supported: junit.");
            return 1;
        }
        if (outPath is not null) reporter ??= "junit";

        // Glyphs need UTF-8; harmless when the console is already UTF-8 or redirected.
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* non-fatal */ }

        // --- Resolve the collection --------------------------------------
        WriteSeg("Resolving collection ", ConsoleColor.DarkGray);
        Vegha.Core.Domain.Collection collection;
        try
        {
            collection = CollectionLoader.Load(root);
        }
        catch (Exception ex)
        {
            WriteSeg("✗\n", ConsoleColor.Red);
            WriteSeg(ex.Message + "\n", ConsoleColor.Red);
            return 1;
        }
        WriteSeg("………………………… ", ConsoleColor.DarkGray);
        WriteSeg("✓\n", ConsoleColor.Green);

        // --- Resolve the environment -------------------------------------
        var envVars = new Dictionary<string, string>(StringComparer.Ordinal);
        if (envName is not null)
        {
            WriteSeg($"Resolving environment '{envName}' ", ConsoleColor.DarkGray);
            var env = collection.Environments
                .FirstOrDefault(e => string.Equals(e.Name, envName, StringComparison.OrdinalIgnoreCase));
            if (env is null)
            {
                WriteSeg("✗\n", ConsoleColor.Red);
                var names = collection.Environments.Count == 0
                    ? "(none defined)"
                    : string.Join(", ", collection.Environments.Select(e => e.Name));
                WriteSeg($"Environment '{envName}' not found. Available: {names}\n", ConsoleColor.Red);
                return 1;
            }
            foreach (var kv in env.Variables)
                if (kv.Enabled && !string.IsNullOrEmpty(kv.Name))
                    envVars[kv.Name] = kv.Value;
            WriteSeg("……… ", ConsoleColor.DarkGray);
            WriteSeg("✓\n", ConsoleColor.Green);
        }

        // --- Resolve secret:// references carried by environment values ---
        var secretCount = envVars.Values.Count(v =>
            !string.IsNullOrEmpty(v) && v.StartsWith("secret://", StringComparison.OrdinalIgnoreCase));
        if (secretCount > 0)
        {
            WriteSeg($"Resolving {secretCount} secret{(secretCount == 1 ? "" : "s")} ", ConsoleColor.DarkGray);
            try
            {
                var registry = new SecretRegistry();
                LoadSecretProviders(registry);
                envVars = await registry.ResolveSecretsAsync(envVars).ConfigureAwait(false);
                WriteSeg("…… ", ConsoleColor.DarkGray);
                WriteSeg("✓\n", ConsoleColor.Green);
            }
            catch (Exception ex)
            {
                WriteSeg("✗\n", ConsoleColor.Red);
                WriteSeg(ex.Message + "\n", ConsoleColor.Red);
                return 1;
            }
        }
        Console.WriteLine();

        // --- Select requests ---------------------------------------------
        var items = FlattenRequests(collection).ToList();
        if (nameFilter is not null)
            items = items.Where(x => string.Equals(x.Request.Name, nameFilter, StringComparison.Ordinal)).ToList();
        if (items.Count == 0)
        {
            WriteSeg($"No requests matching {nameFilter ?? "*"}\n", ConsoleColor.Red);
            return 1;
        }

        // --- Execute each request through the shared pipeline ------------
        // RequestPipeline applies variable interpolation, auth, and pre/post
        // scripts + tests — the same engine the desktop app's runner uses.
        using var http = new HttpClient();
        var executor = new HttpExecutor(http);
        var scripting = new JintHost();

        int passed = 0, failed = 0, warned = 0;
        var results = new List<RunResult>(items.Count);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        foreach (var (request, chain) in items)
        {
            var inputs = new RequestPipeline.Inputs(
                Collection: collection,
                FolderChain: chain,
                Request: request,
                EnvironmentVariables: envVars,
                IterationVariables: s_noVars);

            var o = await RequestPipeline.ExecuteAsync(inputs, executor, scripting).ConfigureAwait(false);

            var hasTests = o.Tests.Count > 0;
            var errored = o.ErrorMessage is not null;
            var isFailed = errored
                || (hasTests && o.FailedTests > 0)
                || (!hasTests && (o.StatusCode == 0 || o.StatusCode >= 400));
            var isWarn = !isFailed && o.ElapsedMilliseconds >= slowMs;

            var label = Label(request.Method, DisplayPath(o.ResolvedUrl));
            var timing = $"{o.ElapsedMilliseconds} ms";
            var className = chain.Count == 0
                ? collection.Name
                : string.Join(".", chain.Select(f => f.Name));

            string? detail;
            if (isFailed)
            {
                failed++;
                detail = errored ? Oneline(o.ErrorMessage!)
                    : hasTests ? $"{o.FailedTests}/{o.Tests.Count} tests failed"
                    : $"HTTP {o.StatusCode}";
                WriteSeg("  ✗  ", ConsoleColor.Red);
                Console.Write(label);
                WriteSeg("  " + timing, ConsoleColor.DarkYellow);
                WriteSeg("  " + detail + "\n", ConsoleColor.Red);
            }
            else if (isWarn)
            {
                warned++;
                detail = "slow";
                WriteSeg("  ⚠  ", ConsoleColor.DarkYellow);
                Console.Write(label);
                WriteSeg("  " + timing, ConsoleColor.DarkYellow);
                WriteSeg("  slow\n", ConsoleColor.DarkYellow);
            }
            else
            {
                passed++;
                detail = null;
                WriteSeg("  ✓  ", ConsoleColor.Green);
                Console.Write(label);
                WriteSeg("  " + timing, ConsoleColor.DarkYellow);
                if (hasTests)
                    WriteSeg($"  {o.Tests.Count} test{(o.Tests.Count == 1 ? "" : "s")}", ConsoleColor.DarkGray);
                Console.WriteLine();
            }

            results.Add(new RunResult(
                request.Name, className, o.ElapsedMilliseconds,
                isFailed, isWarn, o.Tests.Count, o.FailedTests, detail));
        }
        sw.Stop();

        // --- Summary ------------------------------------------------------
        Console.WriteLine();
        var secs = (sw.ElapsedMilliseconds / 1000.0)
            .ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var summary = $"  {passed} passed, {failed} failed, {warned} warning{(warned == 1 ? "" : "s")} in {secs} s";
        WriteSeg(summary + "\n", failed > 0 ? ConsoleColor.Red : ConsoleColor.Green);

        // --- JUnit report -------------------------------------------------
        if (reporter is not null && outPath is not null)
        {
            try
            {
                WriteJUnitReport(outPath, collection.Name, results, sw.ElapsedMilliseconds / 1000.0);
                WriteSeg($"  Report written to {outPath}\n", ConsoleColor.DarkGray);
            }
            catch (Exception ex)
            {
                WriteSeg($"  Failed to write report: {ex.Message}\n", ConsoleColor.Red);
                return 1;
            }
        }

        return failed > 0 ? 1 : 0;
    }

    /// <summary>Path component of a resolved URL, for the per-request line.</summary>
    private static string DisplayPath(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url;

    /// <summary>Collapses a (possibly multi-line) message to a single trimmed line.</summary>
    private static string Oneline(string s)
    {
        var line = s.ReplaceLineEndings(" ").Trim();
        return line.Length > 70 ? line[..69] + "…" : line;
    }

    /// <summary>Registers the user's configured secret-manager providers — the same
    /// encrypted config the desktop app's Secret Manager settings page writes — so
    /// <c>secret://&lt;name&gt;/path#field</c> URIs resolve. A missing or unreadable
    /// store simply yields an empty registry.</summary>
    private static void LoadSecretProviders(SecretRegistry registry)
    {
        try
        {
            var store = new Vegha.Core.Persistence.SecretProviderConfigStore();
            foreach (var cfg in store.Load())
            {
                try
                {
                    ISecretProvider? provider = cfg.Type switch
                    {
                        "azure" => Vegha.Integrations.Secrets.Azure.AzureKeyVaultProvider.FromConfig(cfg.Settings),
                        "aws" => Vegha.Integrations.Secrets.Aws.AwsSecretsProvider.FromConfig(cfg.Settings),
                        _ => null,
                    };
                    if (provider is not null) registry.Register(cfg.Name, provider);
                }
                catch { /* skip a malformed provider entry */ }
            }
        }
        catch { /* no configured providers — proceed with an empty registry */ }
    }

    /// <summary>Writes a JUnit XML report: one &lt;testcase&gt; per request, with a
    /// &lt;failure&gt; child when the request errored, returned 4xx/5xx, or had a
    /// failing post-response test.</summary>
    private static void WriteJUnitReport(
        string outPath, string collectionName, IReadOnlyList<RunResult> results, double totalSeconds)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var failures = results.Count(r => r.Failed);

        var cases = results.Select(r =>
        {
            var tc = new XElement("testcase",
                new XAttribute("name", r.Name),
                new XAttribute("classname", string.IsNullOrEmpty(r.ClassName) ? collectionName : r.ClassName),
                new XAttribute("time", (r.ElapsedMs / 1000.0).ToString("0.000", ci)));
            if (r.Failed)
                tc.Add(new XElement("failure",
                    new XAttribute("message", r.Detail ?? "failed"),
                    r.Detail ?? "failed"));
            return tc;
        });

        var suite = new XElement("testsuite",
            new XAttribute("name", collectionName),
            new XAttribute("tests", results.Count),
            new XAttribute("failures", failures),
            new XAttribute("time", totalSeconds.ToString("0.000", ci)),
            cases);

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("testsuites",
                new XAttribute("name", collectionName),
                new XAttribute("tests", results.Count),
                new XAttribute("failures", failures),
                new XAttribute("time", totalSeconds.ToString("0.000", ci)),
                suite));

        var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        doc.Save(outPath);
    }

    /// <summary>Method + target padded to a fixed width so the timing column lines up.</summary>
    private static string Label(string method, string target) =>
        $"{method,-6} {target}".PadRight(46);

    /// <summary>Writes a coloured segment, then resets. When the console output is
    /// redirected (piped to a file) the colour changes emit no bytes, so captured
    /// output stays plain text.</summary>
    private static void WriteSeg(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ResetColor();
    }

    private static int ImportCollection(string source, string destination)
    {
        var ext = Path.GetExtension(source).ToLowerInvariant();
        if (ext == ".bru" || Directory.Exists(source))
            throw new NotSupportedException("Bruno folder import via CLI not yet wired (use the app).");

        var text = File.ReadAllText(source);
        var collection = DetectAndImport(text, ext);
        // Write the Bruno-style .bru layout that CollectionLoader (and the
        // desktop app) read back — not the legacy JSON CollectionStore format.
        BruCollectionWriter.Write(destination, collection);
        Console.WriteLine($"Imported {collection.Requests.Count} root request(s) + {collection.Folders.Count} folder(s) → {destination}");
        return 0;
    }

    private static Vegha.Core.Domain.Collection DetectAndImport(string content, string ext = "")
    {
        // YAML extension wins outright (content isn't valid JSON anyway).
        if (ext is ".yaml" or ".yml")
            return OpenApiImporter.ImportFromString(content);

        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(content); }
        catch (JsonException)
        {
            // Fallback: maybe it's YAML OpenAPI without a hint extension.
            if (LooksLikeOpenApiYaml(content))
                return OpenApiImporter.ImportFromString(content);
            throw new NotSupportedException("Source is neither valid JSON nor an OpenAPI YAML spec.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("info", out _) && root.TryGetProperty("item", out _))
                return PostmanV2Importer.ImportFromJson(content);
            if (root.TryGetProperty("openapi", out _) || root.TryGetProperty("swagger", out _))
                return OpenApiImporter.ImportFromString(content);
            if ((root.TryGetProperty("type", out var t) && (t.GetString() ?? string.Empty).StartsWith("collection.insomnia"))
                || root.TryGetProperty("resources", out _))
                return InsomniaImporter.ImportFromString(content);
            throw new NotSupportedException("Unknown source format.");
        }
    }

    private static readonly System.Text.RegularExpressions.Regex s_openApiYamlHead = new(
        @"^(?:openapi|swagger)\s*:\s*[""']?\d",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Multiline);

    private static bool LooksLikeOpenApiYaml(string content)
    {
        var head = content.Length > 4096 ? content[..4096] : content;
        return s_openApiYamlHead.IsMatch(head);
    }

    /// <summary>Flattens the collection into (request, folder-chain) pairs. The chain runs
    /// outer → inner — RequestPipeline composes headers / auth / vars against it.</summary>
    private static IEnumerable<(Vegha.Core.Domain.RequestItem Request, IReadOnlyList<Vegha.Core.Domain.Folder> Chain)>
        FlattenRequests(Vegha.Core.Domain.Collection c)
    {
        var empty = Array.Empty<Vegha.Core.Domain.Folder>();
        foreach (var r in c.Requests) yield return (r, empty);
        foreach (var f in c.Folders)
            foreach (var pair in WalkFolder(f, new List<Vegha.Core.Domain.Folder>()))
                yield return pair;
    }

    private static IEnumerable<(Vegha.Core.Domain.RequestItem, IReadOnlyList<Vegha.Core.Domain.Folder>)>
        WalkFolder(Vegha.Core.Domain.Folder f, List<Vegha.Core.Domain.Folder> parents)
    {
        var chain = new List<Vegha.Core.Domain.Folder>(parents) { f };
        foreach (var r in f.Requests) yield return (r, chain);
        foreach (var sub in f.Folders)
            foreach (var pair in WalkFolder(sub, chain))
                yield return pair;
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
        return null;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            Vegha CLI

              vegha run <collection> [options]
                  --name <request>      run only the named request
                  --env <name>          select an environment for variable resolution
                  --reporter junit      emit a report (with --out)
                  --out <file>          write the report to <file>
                  --slow <ms>           warn on requests slower than <ms> (default 1000)

              vegha import <source> <destination-folder>
              vegha --protocol json
            """);
    }
}
