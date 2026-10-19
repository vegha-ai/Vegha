using System.Text.Json;
using System.Xml.Linq;
using Vegha.Core.Importers;
using Vegha.Core.Requests;
using Vegha.Core.Scripting;
using Vegha.Core.Engine;
using Vegha.Core.Engine.Reports;
using Vegha.Core.Flow;

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
        var dataPath = ArgValue(args, "--data");
        var slowMs = int.TryParse(ArgValue(args, "--slow"), out var parsedSlow) ? parsedSlow : SlowThresholdMs;
        var delayMs = int.TryParse(ArgValue(args, "--delay"), out var parsedDelay) ? Math.Max(0, parsedDelay) : 0;
        var bail = args.Contains("--bail");

        var iterationsArg = ArgValue(args, "--iterations");
        var iterations = 1;
        if (iterationsArg is not null && (!int.TryParse(iterationsArg, out iterations) || iterations < 1))
        {
            Console.Error.WriteLine($"--iterations must be a positive integer (got '{iterationsArg}').");
            return 1;
        }

        var reporters = (ArgValue(args, "--reporter") ?? (outPath is null ? string.Empty : "junit"))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(r => r.ToLowerInvariant())
            .Distinct()
            .ToList();
        var unknown = reporters.Where(r => !RunReportWriter.Formats.Contains(r)).ToList();
        if (unknown.Count > 0)
        {
            Console.Error.WriteLine($"Unknown --reporter '{string.Join(", ", unknown)}'. Supported: {string.Join(", ", RunReportWriter.Formats)}.");
            return 1;
        }
        if (reporters.Count > 0 && outPath is null)
        {
            Console.Error.WriteLine("--reporter needs --out <file> (or a base path when several reporters are given).");
            return 1;
        }

        var cliVars = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in ArgValues(args, "--env-var"))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                Console.Error.WriteLine($"--env-var expects name=value (got '{pair}').");
                return 1;
            }
            cliVars[pair[..eq]] = pair[(eq + 1)..];
        }

        // Glyphs need UTF-8; harmless when the console is already UTF-8 or redirected.
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* non-fatal */ }

        // --- Resolve the collection --------------------------------------
        WriteSeg("Resolving collection ", ConsoleColor.DarkGray);
        Vegha.Core.Domain.Collection collection;
        var loadIssues = new System.Collections.Concurrent.ConcurrentBag<(string File, string Error)>();
        try
        {
            collection = CollectionLoader.Load(root, (file, err) => loadIssues.Add((file, err)));
        }
        catch (Exception ex)
        {
            WriteSeg("✗\n", ConsoleColor.Red);
            WriteSeg(ex.Message + "\n", ConsoleColor.Red);
            return 1;
        }
        WriteSeg("………………………… ", ConsoleColor.DarkGray);
        WriteSeg("✓\n", ConsoleColor.Green);
        foreach (var (file, err) in loadIssues.OrderBy(i => i.File, StringComparer.Ordinal))
            WriteSeg($"  warning: skipped '{Path.GetFileName(file)}': {err}\n", ConsoleColor.Yellow);

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
        // --env-var overrides win over the selected environment (CI injects secrets this way).
        foreach (var (k, v) in cliVars) envVars[k] = v;

        // --- Iteration data ----------------------------------------------
        IterationDataSource? data = null;
        if (dataPath is not null)
        {
            WriteSeg($"Loading data '{Path.GetFileName(dataPath)}' ", ConsoleColor.DarkGray);
            try { data = await IterationDataSource.LoadAsync(dataPath).ConfigureAwait(false); }
            catch (Exception ex)
            {
                WriteSeg("✗\n", ConsoleColor.Red);
                WriteSeg(ex.Message + "\n", ConsoleColor.Red);
                return 1;
            }
            WriteSeg("……… ", ConsoleColor.DarkGray);
            WriteSeg($"✓ {data.RowCount} row{(data.RowCount == 1 ? "" : "s")}\n", ConsoleColor.Green);
            // One iteration per data row unless --iterations narrows it.
            if (iterationsArg is null && data.RowCount > 0) iterations = data.RowCount;
        }

        // A CI run is isolated: in-memory cookie jar, no history rows — but the same
        // secret-manager config and settings.json the desktop app uses.
        using var services = new EngineServices(new EngineOptions
        {
            ClientName = "cli",
            PersistCookies = false,
            UseHistory = false,
        });

        // --- Resolve secret:// references carried by environment values ---
        var secretCount = envVars.Values.Count(v =>
            !string.IsNullOrEmpty(v) && v.StartsWith("secret://", StringComparison.OrdinalIgnoreCase));
        if (secretCount > 0)
        {
            WriteSeg($"Resolving {secretCount} secret{(secretCount == 1 ? "" : "s")} ", ConsoleColor.DarkGray);
            try
            {
                envVars = await services.Secrets.ResolveSecretsAsync(envVars).ConfigureAwait(false);
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
        // RequestPipeline applies variable interpolation, every auth type, and pre/post
        // scripts + tests — the same engine the desktop app's runner and VS Code use.
        int passed = 0, failed = 0, warned = 0;
        var entries = new List<RunReportEntry>(items.Count * iterations);
        var startedAt = DateTimeOffset.Now;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var pipelineOptions = services.PipelineOptions with { BasePath = root };

        // Linked requests (chain:pre-request) resolve against the collection on disk and share one
        // response cache, so a "when-stale" login only runs once per CLI invocation.
        var chainContext = new ChainContext(collection, root, services.ResponseCache);
        var bailed = false;

        for (var iteration = 0; iteration < iterations && !bailed; iteration++)
        {
            if (iterations > 1)
                WriteSeg($"{(iteration == 0 ? "" : "\n")}Iteration {iteration + 1}/{iterations}\n", ConsoleColor.Cyan);

            // Variables captured by earlier requests (runtime captures / bru.setVar, plus env and
            // global captures) flow into later ones within an iteration — Bruno CLI behavior.
            // Each iteration starts from its data row.
            var row = data?.GetRow(iteration) ?? s_noVars;
            var carried = new Dictionary<string, string>(StringComparer.Ordinal);

            for (var i = 0; i < items.Count; i++)
            {
                var (request, chain) = items[i];
                if (delayMs > 0 && (i > 0 || iteration > 0))
                    await Task.Delay(delayMs).ConfigureAwait(false);

                var inputs = new RequestPipeline.Inputs(
                    Collection: collection,
                    FolderChain: chain,
                    Request: request,
                    EnvironmentVariables: envVars,
                    IterationVariables: Overlay(row, carried),
                    Chain: chainContext);

                var o = await RequestPipeline.ExecuteAsync(
                    inputs, services.Http, services.Scripting, default, pipelineOptions).ConfigureAwait(false);
                foreach (var (k, v) in o.RuntimeVariableMutations) carried[k] = v;
                foreach (var (k, v) in o.EnvVarMutations) carried[k] = v;
                foreach (var (k, v) in o.GlobalVarMutations) carried[k] = v;

                var hasTests = o.Tests.Count > 0;
                var errored = o.ErrorMessage is not null;
                var isFailed = errored
                    || (hasTests && o.FailedTests > 0)
                    || (!hasTests && (o.StatusCode == 0 || o.StatusCode >= 400));
                var isWarn = !isFailed && o.ElapsedMilliseconds >= slowMs;

                var label = Label(request.Method, DisplayPath(o.ResolvedUrl));
                var timing = $"{o.ElapsedMilliseconds} ms";
                var group = chain.Count == 0
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

                // Linked requests (chain:pre-request) that ran or were skipped for this request.
                foreach (var step in o.ChainSteps)
                {
                    var what = step.Failed ? step.Error
                        : step.Ran ? $"{step.StatusCode} · {step.ElapsedMilliseconds} ms"
                          + (step.Captured.Count > 0 ? " → " + string.Join(" ", step.Captured.Keys.Select(k => "{{" + k + "}}")) : "")
                        : "skipped — " + step.SkipReason;
                    WriteSeg($"       ↳ ran first: {step.Name}  {what}\n",
                        step.Failed ? ConsoleColor.Red : ConsoleColor.DarkGray);
                }
                if (o.UnresolvedVariables.Count > 0)
                    WriteSeg($"       ⚠ unresolved: {string.Join(", ", o.UnresolvedVariables.Select(n => "{{" + n + "}}"))}\n",
                        ConsoleColor.DarkYellow);
                // Failed assertions / tests: name the check and why, so CI logs are actionable.
                if (isFailed && hasTests)
                {
                    foreach (var t in o.Tests.Where(t => !t.Passed))
                        WriteSeg($"       ✗ {t.Name}: {t.FailureMessage}\n", ConsoleColor.Red);
                }

                entries.Add(new RunReportEntry(
                    request.Name, group, iteration, request.Method, o.ResolvedUrl, o.StatusCode,
                    o.ElapsedMilliseconds, isFailed, isWarn, detail,
                    o.Tests.Select(t => new RunReportTest(t.Name, t.Passed, t.FailureMessage)).ToList()));

                if (isFailed && bail)
                {
                    bailed = true;
                    WriteSeg("\n  --bail: stopping after the first failure\n", ConsoleColor.Red);
                    break;
                }
            }
        }
        sw.Stop();

        // --- Summary ------------------------------------------------------
        Console.WriteLine();
        var secs = (sw.ElapsedMilliseconds / 1000.0)
            .ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var summary = $"  {passed} passed, {failed} failed, {warned} warning{(warned == 1 ? "" : "s")} in {secs} s";
        WriteSeg(summary + "\n", failed > 0 ? ConsoleColor.Red : ConsoleColor.Green);

        // --- Reports ------------------------------------------------------
        if (reporters.Count > 0 && outPath is not null)
        {
            var report = new RunReport(collection.Name, startedAt, sw.ElapsedMilliseconds, iterations, entries);
            foreach (var format in reporters)
            {
                // One reporter writes exactly --out; several share --out as a base name.
                var target = reporters.Count == 1
                    ? outPath
                    : Path.ChangeExtension(outPath, RunReportWriter.ExtensionFor(format));
                try
                {
                    RunReportWriter.Write(report, format, target);
                    WriteSeg($"  Report written to {target}\n", ConsoleColor.DarkGray);
                }
                catch (Exception ex)
                {
                    WriteSeg($"  Failed to write {format} report: {ex.Message}\n", ConsoleColor.Red);
                    return 1;
                }
            }
        }

        return failed > 0 ? 1 : 0;
    }

    private static IReadOnlyDictionary<string, string> Overlay(
        IReadOnlyDictionary<string, string> row, Dictionary<string, string> carried)
    {
        if (carried.Count == 0) return row;
        var merged = new Dictionary<string, string>(row, StringComparer.Ordinal);
        foreach (var (k, v) in carried) merged[k] = v;
        return merged;
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

        // XML formats — SoapUI projects are checked before WSDL because they embed cached WSDL.
        if (ext is ".xml" or ".wsdl" || LooksLikeXml(content))
        {
            if (LooksLikeSoapUiProject(content))
                return SoapUiImporter.ImportFromString(content);
            if (LooksLikeWsdl(content))
                return WsdlImporter.ImportFromString(content);
            if (ext is ".xml" or ".wsdl")
                throw new NotSupportedException("XML source is neither a SoapUI project nor a WSDL.");
        }

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
                return PostmanV2Importer.ImportFromJson(content, new PostmanImportOptions(
                    TranslateScripts: true,
                    OnDiagnostic: d => Console.Error.WriteLine(
                        $"  warning: {d.RequestName} ({d.Phase}): untranslated Postman tokens: {string.Join(", ", d.UnhandledTokens)}")));
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

    private static bool LooksLikeXml(string content)
    {
        foreach (var ch in content)
        {
            if (ch is ' ' or '\t' or '\r' or '\n' or '﻿') continue;
            return ch == '<';
        }
        return false;
    }

    private static bool LooksLikeSoapUiProject(string content)
    {
        var head = content.Length > 4096 ? content[..4096] : content;
        return head.Contains("eviware.com/soapui/config", StringComparison.Ordinal)
            || head.Contains("soapui-project", StringComparison.Ordinal);
    }

    private static bool LooksLikeWsdl(string content)
    {
        var head = content.Length > 4096 ? content[..4096] : content;
        return head.Contains("http://schemas.xmlsoap.org/wsdl/", StringComparison.Ordinal)
            && head.Contains("definitions", StringComparison.Ordinal);
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

    /// <summary>Every value of a repeatable option (<c>--env-var a=1 --env-var b=2</c>).</summary>
    private static IEnumerable<string> ArgValues(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++) if (args[i] == name) yield return args[i + 1];
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            Vegha CLI

              vegha run <collection> [options]
                  --name <request>      run only the named request
                  --env <name>          select an environment for variable resolution
                  --env-var <k=v>       set/override a variable (repeatable)
                  --iterations <n>      run the collection n times (default 1)
                  --data <file>         CSV/JSON rows; one iteration per row
                  --delay <ms>          pause between requests
                  --bail                stop at the first failing request
                  --reporter <list>     junit, json, html (comma-separated; with --out)
                  --out <file>          report path (a base name when several reporters)
                  --slow <ms>           warn on requests slower than <ms> (default 1000)

              vegha import <source> <destination-folder>
              vegha --protocol json
            """);
    }
}
