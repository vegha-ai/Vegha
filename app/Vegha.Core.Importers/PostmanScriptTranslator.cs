using System.Text.RegularExpressions;

namespace Vegha.Core.Importers;

/// <summary>
/// Translates Postman <c>pm.*</c> / <c>postman.*</c> script calls into Bruno-flavored
/// <c>bru.*</c> + <c>req.*</c> + <c>res.*</c> calls so that scripts pulled from Postman
/// v2.1 collections run cleanly under Vegha's Jint sandbox.
///
/// Ports Bruno's <c>bruno-converters/src/postman/postman-translations.js</c>. The
/// implementation is a deliberately simple ordered table of regex pattern → replacement
/// pairs — same shape as Bruno's source — so the mapping stays easy to audit and extend.
/// Token-level rewrites (<c>pm.response</c> → <c>res</c>, <c>pm.request</c> → <c>req</c>)
/// run AFTER the API-shaped rules so the higher-fidelity rewrites win first.
///
/// Limitations (surfaced by <see cref="Lint"/> as <see cref="TranslationDiagnostic"/>s):
///   - Bracket access like <c>pm["environment"].get("k")</c> is not detected.
///   - <c>pm</c> identifiers shadowed in inner scopes still get rewritten.
///   - <c>pm.sendRequest(req, callback)</c> is partially mapped: the call is rewritten
///     to <c>bru.sendRequest</c> but the callback signature won't line up — Bruno's is
///     synchronous. A TODO comment is injected for the user.
/// </summary>
public static class PostmanScriptTranslator
{
    /// <summary>Translates a Postman script body to its Bruno-equivalent form. Returns the
    /// translated text plus a list of unhandled <c>pm.*</c> / <c>postman.*</c> tokens for the
    /// caller to surface as warnings.</summary>
    public static TranslationOutcome Translate(string? script)
    {
        if (string.IsNullOrEmpty(script)) return new TranslationOutcome(string.Empty, Array.Empty<string>());

        var text = script!;
        foreach (var rule in Rules)
            text = rule.Pattern.Replace(text, rule.Replacement);

        var unhandled = Lint(text);
        return new TranslationOutcome(text, unhandled);
    }

    /// <summary>Scans <paramref name="translatedScript"/> for any remaining <c>pm.</c>
    /// or <c>postman.</c> tokens — these signal patterns the regex table didn't catch and
    /// will explode at runtime as <c>ReferenceError: pm is not defined</c>. The caller can
    /// surface these to the user via the import wizard.</summary>
    public static IReadOnlyList<string> Lint(string translatedScript)
    {
        if (string.IsNullOrEmpty(translatedScript)) return Array.Empty<string>();
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in PmLeftovers.Matches(translatedScript))
        {
            if (seen.Add(m.Value)) found.Add(m.Value);
        }
        return found;
    }

    private static readonly Regex PmLeftovers = new(
        @"\b(?:pm|postman)\.[A-Za-z_$][A-Za-z0-9_$.]*",
        RegexOptions.Compiled);

    // -------- Translation table (order matters) --------
    // Patterns are written to match Postman's most common call shapes. We avoid Bruno's
    // AST-based fallback (heavier, brings in a parser dependency) since the regex table
    // covers the vast majority of real-world Postman scripts.

    private sealed record Rule(Regex Pattern, string Replacement);

    private static readonly Rule[] Rules = Build();

    private static Rule[] Build()
    {
        // Helper to compile a literal-ish pattern with anchors that allow following
        // method/property access without breaking the rewrite chain.
        Rule R(string pattern, string replacement) =>
            new(new Regex(pattern, RegexOptions.Compiled), replacement);

        return new[]
        {
            // ----- environment -----
            R(@"pm\.environment\.get\(",   "bru.getEnvVar("),
            R(@"pm\.environment\.set\(",   "bru.setEnvVar("),
            R(@"pm\.environment\.unset\(", "bru.deleteEnvVar("),
            R(@"pm\.environment\.has\(",   "bru.hasEnvVar("),
            R(@"pm\.environment\.name",    "bru.getEnvName()"),
            R(@"pm\.environment\.clear\(\)", "/* pm.environment.clear() — no-op in bru */"),

            // ----- variables (runtime) -----
            R(@"pm\.variables\.get\(",        "bru.getVar("),
            R(@"pm\.variables\.set\(",        "bru.setVar("),
            R(@"pm\.variables\.has\(",        "bru.hasVar("),
            R(@"pm\.variables\.replaceIn\(",  "bru.interpolate("),

            // ----- collection variables -----
            R(@"pm\.collectionVariables\.get\(",   "bru.getCollectionVar("),
            R(@"pm\.collectionVariables\.set\(",   "bru.setCollectionVar("),
            R(@"pm\.collectionVariables\.has\(",   "bru.hasCollectionVar("),
            R(@"pm\.collectionVariables\.unset\(", "bru.deleteCollectionVar("),

            // ----- globals (mapped to env in Vegha's flatter model) -----
            R(@"pm\.globals\.get\(",      "bru.getGlobalEnvVar("),
            R(@"pm\.globals\.set\(",      "bru.setGlobalEnvVar("),
            R(@"pm\.globals\.has\(",      "bru.hasGlobalEnvVar("),
            R(@"pm\.globals\.unset\(",    "bru.deleteGlobalEnvVar("),
            R(@"pm\.globals\.toObject\(", "bru.getAllGlobalEnvVars("),

            // ----- request flow control -----
            R(@"pm\.setNextRequest\(",            "bru.setNextRequest("),
            R(@"postman\.setNextRequest\(",       "bru.setNextRequest("),
            R(@"pm\.execution\.skipRequest\(",    "bru.runner.skipRequest("),
            // pm.execution.setNextRequest(null) → bru.runner.stopExecution()
            R(@"pm\.execution\.setNextRequest\(\s*null\s*\)", "bru.runner.stopExecution()"),
            R(@"pm\.execution\.setNextRequest\(", "bru.setNextRequest("),

            // ----- Postman legacy globals (pre-`pm` API, still very common in older collections) -----
            R(@"postman\.setEnvironmentVariable\(",   "bru.setEnvVar("),
            R(@"postman\.getEnvironmentVariable\(",   "bru.getEnvVar("),
            R(@"postman\.clearEnvironmentVariable\(", "bru.deleteEnvVar("),
            R(@"postman\.clearEnvironmentVariables\(\)", "/* postman.clearEnvironmentVariables() — no bru equivalent */"),
            R(@"postman\.setGlobalVariable\(",        "bru.setGlobalEnvVar("),
            R(@"postman\.getGlobalVariable\(",        "bru.getGlobalEnvVar("),
            R(@"postman\.clearGlobalVariable\(",      "bru.deleteGlobalEnvVar("),
            R(@"postman\.clearGlobalVariables\(\)",   "/* postman.clearGlobalVariables() — no bru equivalent */"),
            // Pre-`pm.response` shorthand for the raw body string — older collections use it
            // directly as if it were a global. Postman injected it into the script scope.
            R(@"\bresponseBody\b",                    "res.getBodyAsText()"),

            // ----- response (specific shapes first so they win over generic pm.response → res) -----
            // pm.response.to.have.status(N) → expect(res.getStatus()).to.equal(N)
            R(@"pm\.response\.to\.have\.status\(([^)]*)\)", "expect(res.getStatus()).to.equal($1)"),
            R(@"pm\.response\.to\.be\.ok",                  "expect(res.getStatus()).to.be.below(300)"),
            R(@"pm\.response\.json\(\)",     "res.getBody()"),
            R(@"pm\.response\.text\(\)",     "res.getBodyAsText()"),
            R(@"pm\.response\.code",         "res.getStatus()"),
            R(@"pm\.response\.status\b",     "res.statusText"),
            R(@"pm\.response\.responseTime", "res.getResponseTime()"),
            R(@"pm\.response\.responseSize", "res.getSize().total"),
            R(@"pm\.response\.size\(\)",     "res.getSize()"),
            R(@"pm\.response\.headers\.get\(",  "res.getHeader("),
            R(@"pm\.response\.headers\.has\(",  "res.hasHeader("),
            R(@"pm\.response\.headers",      "res.getHeaders()"),

            // ----- request (specific shapes first) -----
            R(@"pm\.request\.url(?:\.toString\(\))?", "req.getUrl()"),
            R(@"pm\.request\.method",   "req.getMethod()"),
            R(@"pm\.request\.body(?:\.raw)?", "req.getBody()"),
            R(@"pm\.request\.headers\.add\(",   "req.headerList.add("),
            R(@"pm\.request\.headers\.remove\(","req.headerList.remove("),
            R(@"pm\.request\.headers\.upsert\(","req.headerList.upsert("),
            R(@"pm\.request\.headers\.each\(",  "req.headerList.each("),
            R(@"pm\.request\.headers\.filter\(","req.headerList.filter("),
            R(@"pm\.request\.headers\.map\(",   "req.headerList.map("),
            R(@"pm\.request\.headers\.get\(",   "req.getHeader("),
            R(@"pm\.request\.headers\.has\(",   "req.headerList.has("),
            R(@"pm\.request\.headers",          "req.getHeaders()"),

            // ----- cookies -----
            R(@"pm\.cookies\.jar\(\)\.get\(",   "bru.cookies.jar().getCookie("),
            R(@"pm\.cookies\.jar\(\)\.set\(",   "bru.cookies.jar().setCookie("),
            R(@"pm\.cookies\.jar\(\)\.unset\(", "bru.cookies.jar().deleteCookie("),

            // ----- tests / expect -----
            // Chai's `expect.fail(msg)` raises an AssertionError unconditionally. Vegha
            // doesn't expose a `.fail` property on the C# `expect` method, so we rewrite to
            // an immediately-invoked throw — the surrounding test() catches it and records
            // a failed outcome.
            R(@"pm\.expect\.fail\(",  "(function(__m){ throw new Error(__m); })("),
            R(@"pm\.test\(",    "test("),
            R(@"pm\.expect\(",  "expect("),
            R(@"pm\.assert\(",  "console.assert("),

            // ----- sendRequest -----
            // The semantics differ (Bruno is synchronous, no callback) — emit a hint comment.
            R(@"pm\.sendRequest\(",
              "/* TODO: pm.sendRequest had a callback — bru.sendRequest is synchronous; flatten the callback body here. */ bru.sendRequest("),

            // ----- bare tokens (do these LAST so the specific shapes above already matched) -----
            // After the rewrites above, lingering `pm.response` / `pm.request` becomes `res` / `req`.
            R(@"\bpm\.response\b", "res"),
            R(@"\bpm\.request\b",  "req"),
            R(@"\bpostman\.",      "/* postman.* */ "),
        };
    }

    // ==================== Reverse direction: bru.* → pm.* (Export) ====================

    /// <summary>Translates a Vegha / Bruno script into Postman's <c>pm.*</c> API — the inverse
    /// of <see cref="Translate"/>, used when exporting a collection to Postman v2.1. Returns
    /// the translated text plus any <c>bru.</c> / <c>req.</c> / <c>res.</c> tokens the table
    /// didn't cover (they'll throw in Postman, so the exporter surfaces them as warnings).</summary>
    public static TranslationOutcome TranslateToPostman(string? script)
    {
        if (string.IsNullOrEmpty(script)) return new TranslationOutcome(string.Empty, Array.Empty<string>());

        var text = script!;
        foreach (var rule in ReverseRules)
            text = rule.Pattern.Replace(text, rule.Replacement);

        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in BruLeftovers.Matches(text))
        {
            if (seen.Add(m.Value)) found.Add(m.Value);
        }
        return new TranslationOutcome(text, found);
    }

    // `(?<![\w.$])` keeps `myres.getBody()` / `foo.test(` from matching.
    private static readonly Regex BruLeftovers = new(
        @"(?<![\w.$])(?:bru|req|res)\.[A-Za-z_$][A-Za-z0-9_$.]*",
        RegexOptions.Compiled);

    private static readonly Rule[] ReverseRules = BuildReverse();

    private static Rule[] BuildReverse()
    {
        Rule R(string pattern, string replacement) =>
            new(new Regex(pattern, RegexOptions.Compiled), replacement);
        const string B = @"(?<![\w.$])"; // token boundary: not part of a longer member chain

        return new[]
        {
            // ----- environment -----
            R(B + @"bru\.getEnvVar\(",    "pm.environment.get("),
            R(B + @"bru\.setEnvVar\(",    "pm.environment.set("),
            R(B + @"bru\.deleteEnvVar\(", "pm.environment.unset("),
            R(B + @"bru\.hasEnvVar\(",    "pm.environment.has("),
            R(B + @"bru\.getEnvName\(\)", "pm.environment.name"),

            // ----- runtime variables -----
            R(B + @"bru\.getVar\(",       "pm.variables.get("),
            R(B + @"bru\.setVar\(",       "pm.variables.set("),
            R(B + @"bru\.hasVar\(",       "pm.variables.has("),
            R(B + @"bru\.deleteVar\(",    "pm.variables.unset("),
            R(B + @"bru\.interpolate\(",  "pm.variables.replaceIn("),

            // ----- collection variables -----
            R(B + @"bru\.getCollectionVar\(",    "pm.collectionVariables.get("),
            R(B + @"bru\.setCollectionVar\(",    "pm.collectionVariables.set("),
            R(B + @"bru\.hasCollectionVar\(",    "pm.collectionVariables.has("),
            R(B + @"bru\.deleteCollectionVar\(", "pm.collectionVariables.unset("),

            // ----- globals -----
            R(B + @"bru\.getGlobalEnvVar\(",     "pm.globals.get("),
            R(B + @"bru\.setGlobalEnvVar\(",     "pm.globals.set("),
            R(B + @"bru\.hasGlobalEnvVar\(",     "pm.globals.has("),
            R(B + @"bru\.deleteGlobalEnvVar\(",  "pm.globals.unset("),
            R(B + @"bru\.getAllGlobalEnvVars\(", "pm.globals.toObject("),

            // ----- request flow control -----
            R(B + @"bru\.runner\.stopExecution\(\)", "pm.execution.setNextRequest(null)"),
            R(B + @"bru\.runner\.skipRequest\(",     "pm.execution.skipRequest("),
            R(B + @"bru\.runner\.setNextRequest\(",  "pm.execution.setNextRequest("),
            R(B + @"bru\.setNextRequest\(",          "pm.execution.setNextRequest("),

            // ----- response (specific shapes first) -----
            R(B + @"expect\(\s*res\.getStatus\(\)\s*\)\.to\.equal\(([^)]*)\)", "pm.response.to.have.status($1)"),
            R(B + @"expect\(\s*res\.getStatus\(\)\s*\)\.to\.be\.below\(\s*300\s*\)", "pm.response.to.be.ok"),
            R(B + @"res\.getBodyAsText\(\)",       "pm.response.text()"),
            R(B + @"res\.getBody\(\)",             "pm.response.json()"),
            R(B + @"res\.getStatus\(\)",           "pm.response.code"),
            R(B + @"res\.statusText\b",            "pm.response.status"),
            R(B + @"res\.getResponseTime\(\)",     "pm.response.responseTime"),
            R(B + @"res\.getSize\(\)\.total",      "pm.response.responseSize"),
            R(B + @"res\.getSize\(\)",             "pm.response.size()"),
            R(B + @"res\.getHeader\(",             "pm.response.headers.get("),
            R(B + @"res\.hasHeader\(",             "pm.response.headers.has("),
            R(B + @"res\.getHeaders\(\)",          "pm.response.headers.toObject()"),
            R(B + @"res\.getStatusText\(\)",       "pm.response.status"),
            R(B + @"res\.headerList\.",            "pm.response.headers."),
            // Property shorthands on Vegha's ResponseApi: `body` is the RAW string (scripts
            // JSON.parse it), so it maps to text(), not json().
            R(B + @"res\.body\b",                  "pm.response.text()"),
            R(B + @"res\.status\b",                "pm.response.code"),
            R(B + @"res\.responseTime\b",          "pm.response.responseTime"),

            // ----- request -----
            R(B + @"req\.getUrl\(\)",              "pm.request.url.toString()"),
            R(B + @"req\.getMethod\(\)",           "pm.request.method"),
            R(B + @"req\.getBody\(\)",             "pm.request.body.raw"),
            R(B + @"req\.headerList\.",            "pm.request.headers."),
            R(B + @"req\.getHeader\(",             "pm.request.headers.get("),
            R(B + @"req\.getHeaders\(\)",          "pm.request.headers"),

            // ----- cookies -----
            R(B + @"bru\.cookies\.jar\(\)\.getCookie\(",    "pm.cookies.jar().get("),
            R(B + @"bru\.cookies\.jar\(\)\.setCookie\(",    "pm.cookies.jar().set("),
            R(B + @"bru\.cookies\.jar\(\)\.deleteCookie\(", "pm.cookies.jar().unset("),

            // ----- sendRequest (Bruno's is synchronous; Postman's takes a callback) -----
            R(B + @"bru\.sendRequest\(",
              "/* TODO: bru.sendRequest is synchronous — pm.sendRequest takes a callback (err, res). */ pm.sendRequest("),

            // ----- tests / expect (bare globals in Vegha, namespaced in Postman) -----
            // `(?<!function\s+)` leaves a user-defined `function test(…)` alone.
            R(B + @"(?<!function\s+)test\(",   "pm.test("),
            R(B + @"(?<!function\s+)expect\(", "pm.expect("),
        };
    }
}

/// <summary>Result of translating one script — the translated text plus any unhandled
/// <c>pm.*</c> / <c>postman.*</c> tokens the lint pass found.</summary>
public sealed record TranslationOutcome(string TranslatedScript, IReadOnlyList<string> UnhandledTokens);

/// <summary>Per-script diagnostic raised by <see cref="PostmanV2Importer"/> when
/// <see cref="PostmanImportOptions.TranslateScripts"/> is true and the lint pass finds
/// tokens it couldn't rewrite. Bubbled up to the import wizard's preview panel.</summary>
/// <param name="RequestName">Name of the originating Postman request.</param>
/// <param name="Phase"><c>"pre-request"</c> or <c>"tests"</c>.</param>
/// <param name="UnhandledTokens">Raw <c>pm.*</c> / <c>postman.*</c> tokens still in the
/// translated script — every one will throw <c>ReferenceError</c> at run time.</param>
public sealed record TranslationDiagnostic(
    string RequestName,
    string Phase,
    IReadOnlyList<string> UnhandledTokens);
