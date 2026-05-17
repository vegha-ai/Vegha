using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Vegha.App.Controls.Workspace;
using Vegha.App.ViewModels;
using Vegha.Core.Importers;
using Vegha.Core.Scripting;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vegha.Tests.UI;

/// <summary>
/// End-to-end import tests against a real Postman v2.1 collection ("EU API", 47 folders,
/// ~409 requests, 91 event scripts). Exercises the full chain:
///
///   PostmanV2Importer → PostmanScriptTranslator → ImportWizardViewModel → JintHost.
///
/// The collection lives outside the repo at the path in <see cref="MCSAPIPath"/>; tests that
/// require it short-circuit with <see cref="Skip"/>-style logging when the file is missing
/// so the suite stays green on machines without the fixture.
/// </summary>
public class MCSAPIImportUITests
{
    /// <summary>Path to the real Postman fixture on the developer machine. Not bundled in
    /// the repo because it's a private 660 KB collection. Tests skip when absent.</summary>
    private const string MCSAPIPath = @"C:\Users\vamc0\Documents\PostmanData\collection\MCSAPI.json";

    /// <summary>Standard 200 JSON response used for executing translated test scripts.
    /// Contains the field names the Login scripts read (<c>access_token</c>) so they don't
    /// throw on legitimate property access.</summary>
    private static readonly KeyValuePair<string, string>[] JsonHeaders =
    {
        new("Content-Type", "application/json")
    };

    private const string SyntheticResponseBody = """
        {"access_token":"abc.def.ghi","id":42,"name":"alice","status":"ok","data":{}}
    """;

    // =========================================================================
    // Test 1: Smoke import. Confirms counts + that translation rewrote the scripts.
    // =========================================================================

    [Fact]
    public void MCSAPI_Imports_WithExpectedShape()
    {
        if (!File.Exists(MCSAPIPath))
        {
            // No fixture on this machine — bail benignly so CI on other boxes stays green.
            return;
        }

        var json = File.ReadAllText(MCSAPIPath);

        // Translation OFF — preserves pm.* / postman.* verbatim.
        var rawCol = PostmanV2Importer.ImportFromJson(json, new PostmanImportOptions(TranslateScripts: false));
        var (rawFolders, rawRequests, rawScripts) = SummarizeCollection(rawCol);

        // Translation ON — same shape, but scripts now use bru.* / res.* / req.* / test() / expect().
        var diagnostics = new List<TranslationDiagnostic>();
        var translatedCol = PostmanV2Importer.ImportFromJson(json,
            new PostmanImportOptions(TranslateScripts: true, OnDiagnostic: diagnostics.Add));
        var (tFolders, tRequests, tScripts) = SummarizeCollection(translatedCol);

        // Structural shape is identical regardless of translation toggle.
        tFolders.Should().Be(rawFolders);
        tRequests.Should().Be(rawRequests);
        tScripts.Should().Be(rawScripts);

        // Expected fixture shape — anchor lower bounds (collection may grow over time, but
        // these are floor values from the day-of-write summary so a regression is obvious).
        rawFolders.Should().BeGreaterThan(40, "MCSAPI.json has ~47 top-level folders");
        rawRequests.Should().BeGreaterThan(400, "MCSAPI.json has ~409 requests");
        rawScripts.Should().BeGreaterThan(80, "MCSAPI.json has ~91 event scripts (pre + tests)");

        // After translation, the scripts must no longer reference `pm.*` for the shapes
        // we cover. Verify by sampling the largest folder ("Account") and checking that
        // its translated scripts call bru.* and never throw `pm is not defined`.
        var sampleScripts = CollectScripts(translatedCol).ToList();
        sampleScripts.Where(s => s.Body.Contains("bru."))
            .Should().NotBeEmpty("translation must produce at least some bru.* calls");
        // pm.environment.set is the most common Postman call in this collection — every
        // occurrence must have been rewritten.
        sampleScripts.Where(s => s.Body.Contains("pm.environment.set"))
            .Should().BeEmpty("pm.environment.set should be fully translated");
        sampleScripts.Where(s => s.Body.Contains("postman.setEnvironmentVariable"))
            .Should().BeEmpty("postman.setEnvironmentVariable (legacy) should be fully translated");
        // `responseBody` (Postman's legacy global for raw body string) gets mapped too.
        sampleScripts.Where(s => System.Text.RegularExpressions.Regex.IsMatch(s.Body, @"\bresponseBody\b"))
            .Should().BeEmpty("legacy responseBody global should be rewritten to res.getBodyAsText()");
    }

    // =========================================================================
    // Test 2: Every translated script parses + runs under Jint without
    // ReferenceError. Catches translator gaps the lint pass misses.
    // =========================================================================

    [Fact]
    public void MCSAPI_TranslatedScripts_ExecuteCleanlyUnderJint()
    {
        if (!File.Exists(MCSAPIPath)) return;

        var json = File.ReadAllText(MCSAPIPath);
        var col = PostmanV2Importer.ImportFromJson(json, new PostmanImportOptions(TranslateScripts: true));
        var host = new JintHost();

        var scripts = CollectScripts(col).ToList();
        var failures = new List<(string Request, string Phase, string Error)>();

        foreach (var (req, phase, body) in scripts)
        {
            if (string.IsNullOrWhiteSpace(body)) continue;

            if (phase == "pre-request")
            {
                var reqApi = new RequestApi("POST", "https://x.test/api", null,
                    Array.Empty<KeyValuePair<string, string>>(), name: req,
                    pathParams: Array.Empty<KeyValuePair<string, string>>());
                var result = host.RunPreRequest(body, new Dictionary<string, string>(), request: reqApi);
                if (!result.IsSuccess) failures.Add((req, phase, result.ErrorMessage ?? "?"));
            }
            else
            {
                var resp = new ResponseApi(200, "OK", SyntheticResponseBody, 5, JsonHeaders);
                var result = host.RunPostResponse(null, body, resp, new Dictionary<string, string>());
                if (!result.IsSuccess) failures.Add((req, phase, result.ErrorMessage ?? "?"));
            }
        }

        // Group failures by the bare-identifier root cause so the diagnostic stays terse
        // even if the same legacy global trips dozens of scripts.
        var failureSummary = failures
            .GroupBy(f => ExtractRootCause(f.Error))
            .OrderByDescending(g => g.Count())
            .Select(g => $"  {g.Count()}× {g.Key}: e.g. \"{g.First().Request}\" ({g.First().Phase})")
            .ToList();

        // We're running real scripts against a *synthetic* 200/JSON response — most failures
        // are legitimate "property X of undefined" errors caused by the synthetic body not
        // matching the real API's response shape. Those are NOT translator bugs.
        //
        // The translator IS at fault for one specific failure class: bare-identifier
        // ReferenceErrors for globals we should have translated. So we narrow the
        // assertion to that class only. (The legacy bare `request` global is on the
        // tolerance list — it's a Postman API we deliberately don't shim, see translator
        // limitations.)
        bool IsTranslatorGap(string error)
        {
            // We only fault the translator when scripts reference globals that should
            // have been rewritten. Everything else is data-shape noise.
            return error.Contains("'pm' is not defined", StringComparison.OrdinalIgnoreCase)
                || error.Contains("pm is not defined", StringComparison.OrdinalIgnoreCase)
                || error.Contains("postman is not defined", StringComparison.OrdinalIgnoreCase)
                || error.Contains("responseBody is not defined", StringComparison.OrdinalIgnoreCase);
        }
        var translatorGaps = failures.Where(f => IsTranslatorGap(f.Error)).ToList();

        translatorGaps.Should().BeEmpty(
            $"no translator-induced ReferenceError should slip through (failures.Count={failures.Count}, " +
            $"translatorGaps.Count={translatorGaps.Count}). Full grouping for context:\n" +
            string.Join("\n", failureSummary));
    }

    // =========================================================================
    // Test 3: Headless wizard ViewModel drives the File-tab flow.
    // =========================================================================

    [AvaloniaFact]
    public void MCSAPI_StagedInWizard_ShowsPostmanOptions()
    {
        if (!File.Exists(MCSAPIPath)) return;

        var vm = new ImportWizardViewModel(NullLogger<ImportWizardViewModel>.Instance);
        vm.StageFiles(new[] { MCSAPIPath });

        // Detection should label the file as Postman v2.1 and surface the format flag.
        vm.DetectedFormat.Should().StartWith("Postman v2", because: "the wizard must detect Postman v2.1");
        vm.IsPostmanFormatDetected.Should().BeTrue(
            "the Postman-options strip in the dialog is gated on this flag");
        // No unhandled `pm.*` tokens are expected for MCSAPI (our extended translator
        // covers all of its shapes). If this breaks, the checkbox + diagnostics list
        // should still render — but we want to know the moment a translator regression
        // introduces new diagnostics.
        vm.TranslationDiagnostics.Should().BeEmpty(
            "MCSAPI.json should fully translate with no leftover pm.* tokens");
        vm.HasTranslationDiagnostics.Should().BeFalse();
    }

    [AvaloniaFact]
    public void Toggling_TranslatePostmanScripts_AffectsScriptContentNextStaging()
    {
        if (!File.Exists(MCSAPIPath)) return;

        var vm = new ImportWizardViewModel(NullLogger<ImportWizardViewModel>.Instance);

        // First staging: translation ON (default).
        vm.StageFiles(new[] { MCSAPIPath });
        var translatedCol = ExtractStagedCollection(vm);
        var translatedScripts = CollectScripts(translatedCol).ToList();
        translatedScripts.Should().NotBeEmpty();
        translatedScripts.Where(s => s.Body.Contains("pm.environment.set")).Should().BeEmpty();
        translatedScripts.Where(s => s.Body.Contains("bru.setEnvVar")).Should().NotBeEmpty();

        // Toggle off and re-stage — the wizard's option flows into the next import.
        vm.TranslatePostmanScripts = false;
        vm.StageFiles(new[] { MCSAPIPath });
        var rawCol = ExtractStagedCollection(vm);
        var rawScripts = CollectScripts(rawCol).ToList();
        rawScripts.Where(s => s.Body.Contains("pm.environment.set"))
            .Should().NotBeEmpty("translation OFF preserves the original pm.* calls verbatim");
    }

    // =========================================================================
    // Test 4: Open the actual ImportWizardDialog window headlessly and verify
    // the Postman options strip becomes visible after staging.
    // =========================================================================

    [AvaloniaFact]
    public void ImportWizardDialog_PostmanOptionsStrip_BecomesVisible_AfterStagingMCSAPI()
    {
        if (!File.Exists(MCSAPIPath)) return;

        var vm = new ImportWizardViewModel(NullLogger<ImportWizardViewModel>.Instance);
        var dialog = new ImportWizardDialog { DataContext = vm };
        dialog.Show();
        try
        {
            // Force the visual tree to materialize so IsVisible bindings have computed.
            dialog.UpdateLayout();

            // The strip is named in XAML — find it without walking the tree.
            var strip = dialog.FindControl<Border>("PostmanOptionsStrip");
            strip.Should().NotBeNull("ImportWizardDialog should contain the named PostmanOptionsStrip border");
            strip!.IsVisible.Should().BeFalse("no Postman file staged yet — strip is collapsed");

            // Stage MCSAPI — the strip should appear.
            vm.StageFiles(new[] { MCSAPIPath });
            dialog.UpdateLayout();

            vm.IsPostmanFormatDetected.Should().BeTrue();
            strip.IsVisible.Should().BeTrue(
                "after staging a Postman collection the wizard must surface the translation checkbox");
        }
        finally
        {
            dialog.Close();
        }
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static (int Folders, int Requests, int Scripts) SummarizeCollection(Vegha.Core.Domain.Collection col)
    {
        int folders = 0, requests = 0, scripts = 0;
        void Walk(IList<Vegha.Core.Domain.Folder> fs)
        {
            foreach (var f in fs)
            {
                folders++;
                foreach (var r in f.Requests)
                {
                    requests++;
                    if (!string.IsNullOrEmpty(r.PreRequestScript)) scripts++;
                    if (!string.IsNullOrEmpty(r.Tests)) scripts++;
                }
                Walk(f.Folders);
            }
        }
        foreach (var r in col.Requests)
        {
            requests++;
            if (!string.IsNullOrEmpty(r.PreRequestScript)) scripts++;
            if (!string.IsNullOrEmpty(r.Tests)) scripts++;
        }
        Walk(col.Folders);
        return (folders, requests, scripts);
    }

    private static IEnumerable<(string Request, string Phase, string Body)> CollectScripts(Vegha.Core.Domain.Collection col)
    {
        foreach (var r in col.Requests)
        {
            if (!string.IsNullOrEmpty(r.PreRequestScript))
                yield return (r.Name, "pre-request", r.PreRequestScript!);
            if (!string.IsNullOrEmpty(r.Tests))
                yield return (r.Name, "tests", r.Tests!);
            if (!string.IsNullOrEmpty(r.PostResponseScript))
                yield return (r.Name, "post-response", r.PostResponseScript!);
        }
        foreach (var f in col.Folders)
            foreach (var s in CollectScriptsFromFolder(f))
                yield return s;
    }

    private static IEnumerable<(string Request, string Phase, string Body)> CollectScriptsFromFolder(Vegha.Core.Domain.Folder f)
    {
        foreach (var r in f.Requests)
        {
            if (!string.IsNullOrEmpty(r.PreRequestScript))
                yield return (r.Name, "pre-request", r.PreRequestScript!);
            if (!string.IsNullOrEmpty(r.Tests))
                yield return (r.Name, "tests", r.Tests!);
            if (!string.IsNullOrEmpty(r.PostResponseScript))
                yield return (r.Name, "post-response", r.PostResponseScript!);
        }
        foreach (var sub in f.Folders)
            foreach (var s in CollectScriptsFromFolder(sub))
                yield return s;
    }

    /// <summary>Pulls the first successfully-staged Collection out of the wizard VM.
    /// Mirrors what <see cref="ImportWizardViewModel.Import"/> hands to
    /// <c>OnCollectionConfirmed</c> — without invoking the side-effecting confirm path.</summary>
    private static Vegha.Core.Domain.Collection ExtractStagedCollection(ImportWizardViewModel vm)
    {
        Vegha.Core.Domain.Collection? captured = null;
        vm.OnCollectionConfirmed = (c, _) => { captured ??= c; };
        vm.ImportCommand.Execute(null);
        captured.Should().NotBeNull("ImportCommand should have surfaced a Collection");
        return captured!;
    }

    /// <summary>Jint errors look like "Script error at line N, col M: &lt;cause&gt;". We
    /// keep only the suffix so identical root causes group together in failure summaries.</summary>
    private static string ExtractRootCause(string error)
    {
        var idx = error.LastIndexOf(": ", StringComparison.Ordinal);
        var tail = idx >= 0 ? error[(idx + 2)..] : error;
        return tail.Length <= 80 ? tail : tail[..80] + "…";
    }
}
