using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace Vegha.Core.Engine.Reports;

/// <summary>One check (scriptless assertion or JS <c>test()</c>) inside a request result.</summary>
public sealed record RunReportTest(string Name, bool Passed, string? FailureMessage);

/// <summary>One executed request in one iteration.</summary>
public sealed record RunReportEntry(
    string Name,
    /// <summary>Folder path ("Auth.Login") or the collection name for root requests.</summary>
    string Group,
    int Iteration,
    string Method,
    string Url,
    int StatusCode,
    long ElapsedMs,
    bool Failed,
    bool Slow,
    string? Detail,
    IReadOnlyList<RunReportTest> Tests);

/// <summary>A finished collection run, as the CLI and the VS Code runner report it.</summary>
public sealed record RunReport(
    string CollectionName,
    DateTimeOffset StartedAt,
    long DurationMs,
    int Iterations,
    IReadOnlyList<RunReportEntry> Entries)
{
    public int Total => Entries.Count;
    public int FailedCount => Entries.Count(e => e.Failed);
    public int PassedCount => Entries.Count(e => !e.Failed);
}

/// <summary>Serializes a <see cref="RunReport"/> as JUnit XML (CI dashboards), JSON (tooling)
/// or a self-contained HTML page (humans). Formats are chosen by name so CLI flags and
/// sidecar requests share one switch.</summary>
public static class RunReportWriter
{
    public static readonly IReadOnlyList<string> Formats = new[] { "junit", "json", "html" };

    /// <summary>File extension for a format, without the dot.</summary>
    public static string ExtensionFor(string format) => format switch
    {
        "junit" => "xml",
        "json" => "json",
        "html" => "html",
        _ => throw new ArgumentException($"Unknown report format '{format}'. Supported: {string.Join(", ", Formats)}.", nameof(format)),
    };

    public static string Render(RunReport report, string format) => format switch
    {
        "junit" => JUnit(report),
        "json" => Json(report),
        "html" => Html(report),
        _ => throw new ArgumentException($"Unknown report format '{format}'. Supported: {string.Join(", ", Formats)}.", nameof(format)),
    };

    public static void Write(RunReport report, string format, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, Render(report, format), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>JUnit XML: one &lt;testcase&gt; per request per iteration, with a
    /// &lt;failure&gt; child when the request errored, returned 4xx/5xx, or had a failing check.</summary>
    public static string JUnit(RunReport report)
    {
        var ci = CultureInfo.InvariantCulture;
        var seconds = (report.DurationMs / 1000.0).ToString("0.000", ci);
        var multi = report.Iterations > 1;

        var cases = report.Entries.Select(e =>
        {
            var tc = new XElement("testcase",
                new XAttribute("name", multi ? $"{e.Name} [iteration {e.Iteration + 1}]" : e.Name),
                new XAttribute("classname", string.IsNullOrEmpty(e.Group) ? report.CollectionName : e.Group),
                new XAttribute("time", (e.ElapsedMs / 1000.0).ToString("0.000", ci)));
            if (e.Failed)
            {
                var failedChecks = e.Tests.Where(t => !t.Passed).Select(t => $"{t.Name}: {t.FailureMessage}");
                tc.Add(new XElement("failure",
                    new XAttribute("message", e.Detail ?? "failed"),
                    string.Join("\n", new[] { e.Detail ?? "failed" }.Concat(failedChecks))));
            }
            return tc;
        });

        var suite = new XElement("testsuite",
            new XAttribute("name", report.CollectionName),
            new XAttribute("tests", report.Total),
            new XAttribute("failures", report.FailedCount),
            new XAttribute("time", seconds),
            new XAttribute("timestamp", report.StartedAt.UtcDateTime.ToString("s", ci)),
            cases);

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("testsuites",
                new XAttribute("name", report.CollectionName),
                new XAttribute("tests", report.Total),
                new XAttribute("failures", report.FailedCount),
                new XAttribute("time", seconds),
                suite));
        using var writer = new Utf8StringWriter();
        doc.Save(writer);
        return writer.ToString();
    }

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Json(RunReport report) => JsonSerializer.Serialize(new
    {
        collection = report.CollectionName,
        startedAt = report.StartedAt,
        durationMs = report.DurationMs,
        iterations = report.Iterations,
        summary = new { total = report.Total, passed = report.PassedCount, failed = report.FailedCount },
        results = report.Entries,
    }, s_json);

    /// <summary>A single-file HTML report — inline CSS, no scripts, no external assets, so it
    /// can be attached to a CI run or emailed as-is.</summary>
    public static string Html(RunReport report)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">")
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
          .Append("<title>").Append(E(report.CollectionName)).Append(" — Vegha run</title><style>")
          .Append(":root{--bg:#fff;--fg:#1d1d1f;--muted:#6e6e73;--line:#e5e5ea;--pass:#1a7f37;--fail:#cf222e;--chip:#f2f2f7}")
          .Append("@media (prefers-color-scheme:dark){:root{--bg:#1c1c1e;--fg:#f2f2f7;--muted:#a1a1a6;--line:#3a3a3c;--pass:#3fb950;--fail:#ff7b72;--chip:#2c2c2e}}")
          .Append("body{margin:0;padding:24px 16px;background:var(--bg);color:var(--fg);font:14px/1.5 system-ui,-apple-system,Segoe UI,sans-serif}")
          .Append("main{max-width:1100px;margin:0 auto}h1{font-size:20px;margin:0 0 4px}.meta{color:var(--muted);margin-bottom:16px}")
          .Append(".sum span{display:inline-block;background:var(--chip);border-radius:999px;padding:2px 10px;margin-right:6px}")
          .Append(".wrap{overflow-x:auto}table{width:100%;border-collapse:collapse;margin-top:16px}th,td{text-align:left;padding:8px;border-bottom:1px solid var(--line);vertical-align:top}")
          .Append("th{color:var(--muted);font-weight:600}.ok{color:var(--pass)}.bad{color:var(--fail)}code{font-family:ui-monospace,Consolas,monospace;font-size:12px}")
          .Append("ul{margin:4px 0 0;padding-left:18px}</style></head><body><main>");
        sb.Append("<h1>").Append(E(report.CollectionName)).Append("</h1>");
        sb.Append("<div class=\"meta\">").Append(E(report.StartedAt.ToString("u", ci)))
          .Append(" · ").Append((report.DurationMs / 1000.0).ToString("0.00", ci)).Append(" s")
          .Append(report.Iterations > 1 ? $" · {report.Iterations} iterations" : string.Empty).Append("</div>");
        sb.Append("<div class=\"sum\"><span>").Append(report.Total).Append(" requests</span><span class=\"ok\">")
          .Append(report.PassedCount).Append(" passed</span><span class=\"bad\">").Append(report.FailedCount).Append(" failed</span></div>");
        sb.Append("<div class=\"wrap\"><table><thead><tr><th></th><th>Request</th>")
          .Append(report.Iterations > 1 ? "<th>Iter</th>" : string.Empty)
          .Append("<th>Status</th><th>Time</th><th>Checks</th></tr></thead><tbody>");
        foreach (var e in report.Entries)
        {
            sb.Append("<tr><td class=\"").Append(e.Failed ? "bad\">✗" : "ok\">✓").Append("</td><td><strong>")
              .Append(E(e.Name)).Append("</strong><br><code>").Append(E(e.Method)).Append(' ').Append(E(e.Url)).Append("</code>");
            if (!string.IsNullOrEmpty(e.Detail)) sb.Append("<br><span class=\"bad\">").Append(E(e.Detail)).Append("</span>");
            sb.Append("</td>");
            if (report.Iterations > 1) sb.Append("<td>").Append(e.Iteration + 1).Append("</td>");
            sb.Append("<td>").Append(e.StatusCode == 0 ? "—" : e.StatusCode.ToString(ci)).Append("</td><td>")
              .Append(e.ElapsedMs).Append(" ms</td><td>");
            if (e.Tests.Count > 0)
            {
                sb.Append(e.Tests.Count(t => t.Passed)).Append('/').Append(e.Tests.Count).Append("<ul>");
                foreach (var t in e.Tests)
                {
                    sb.Append("<li class=\"").Append(t.Passed ? "ok" : "bad").Append("\">").Append(E(t.Name));
                    if (!t.Passed && !string.IsNullOrEmpty(t.FailureMessage)) sb.Append(" — ").Append(E(t.FailureMessage));
                    sb.Append("</li>");
                }
                sb.Append("</ul>");
            }
            sb.Append("</td></tr>");
        }
        sb.Append("</tbody></table></div></main></body></html>");
        return sb.ToString();
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
