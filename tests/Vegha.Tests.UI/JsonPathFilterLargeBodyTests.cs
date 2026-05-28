using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Vegha.App.ViewModels;
using Vegha.Core.Requests;
using Vegha.Core.Scripting;
using Xunit;

namespace Vegha.Tests.UI;

/// <summary>
/// Regression tests for JSONPath filtering on large response bodies. Previously the
/// filter was hard-skipped above the 1 MB threshold so typing <c>$..sequenceNumber</c>
/// against the 16 MB notification-tree response left the full payload on screen.
/// </summary>
public class JsonPathFilterLargeBodyTests
{
    private const string LargeFixturePath = @"C:\Temp\getNotificationTree.json";

    [AvaloniaFact]
    public void Filter_LargeJsonBody_RecursiveDescent_ReducesToMatchedValues()
    {
        if (!File.Exists(LargeFixturePath)) return;
        var body = File.ReadAllText(LargeFixturePath);

        var vm = BuildVm();
        vm.ResponseContentType = "application/json";
        vm.ResponseBody = body;

        // Pump the dispatcher until the initial background prettify lands. The DisplayedBody
        // starts as the raw body; after prettify it's the formatted JSON.
        Pump(2_000, until: () => vm.DisplayedBody.Length > body.Length);

        vm.JsonPathFilter = "$..sequenceNumber";

        // Debounce is 400ms; filter eval + serialize takes <1s for 16MB. Wait up to 5s.
        Pump(5_000, until: () =>
            !vm.DisplayedBody.Contains("childTree") &&
            vm.DisplayedBody.Length < body.Length &&
            !vm.IsPrettifying);

        // The result should be a JSON array of numbers (sequenceNumber values). Crucially
        // it MUST NOT contain the unrelated keys from the original tree.
        vm.DisplayedBody.Should().NotBeNullOrEmpty();
        vm.DisplayedBody.Length.Should().BeLessThan(body.Length / 100,
            $"filtered body ({vm.DisplayedBody.Length:N0} chars) should be tiny vs the source ({body.Length:N0} chars) — " +
            "$..sequenceNumber matches a flat list of integers");
        vm.DisplayedBody.Should().StartWith("[");
        vm.DisplayedBody.Should().Contain("\n");
        vm.DisplayedBody.Should().NotContain("childTree", "filter result should only contain matched values, not unrelated keys");
    }

    [AvaloniaFact]
    public void Filter_LargeJsonBody_InvalidPath_SurfacesErrorMessage()
    {
        if (!File.Exists(LargeFixturePath)) return;
        var body = File.ReadAllText(LargeFixturePath);

        var vm = BuildVm();
        vm.ResponseContentType = "application/json";
        vm.ResponseBody = body;
        Pump(2_000, until: () => vm.DisplayedBody.Length > body.Length);

        vm.JsonPathFilter = "$.this.is..nonsense[*";

        Pump(5_000, until: () => vm.DisplayedBody.StartsWith("// JSONPath error:"));

        vm.DisplayedBody.Should().StartWith("// JSONPath error:",
            "invalid path expressions must produce a visible error rather than silently keeping the old view");
    }

    [AvaloniaFact]
    public void Filter_LargeJsonBody_ClearingFilter_RestoresFormattedBody()
    {
        if (!File.Exists(LargeFixturePath)) return;
        var body = File.ReadAllText(LargeFixturePath);

        var vm = BuildVm();
        vm.ResponseContentType = "application/json";
        vm.ResponseBody = body;
        Pump(2_000, until: () => vm.DisplayedBody.Length > body.Length);
        var prettyLen = vm.DisplayedBody.Length;

        vm.JsonPathFilter = "$..sequenceNumber";
        Pump(5_000, until: () => vm.DisplayedBody.Length < body.Length / 100);
        vm.DisplayedBody.Length.Should().BeLessThan(body.Length);

        vm.JsonPathFilter = string.Empty;
        // Wait until prettify completes — DisplayedBody is set to raw (= body.Length)
        // immediately on RecomputeDisplayedBody, then upgraded to the pretty form in the
        // background. Watch for the pretty length AND IsPrettifying=false so we don't
        // race the in-flight pass.
        Pump(10_000, until: () => vm.DisplayedBody.Length >= prettyLen && !vm.IsPrettifying);

        vm.DisplayedBody.Length.Should().BeGreaterThan(body.Length,
            "clearing the filter must restore the formatted full body");
    }

    private static RequestEditorViewModel BuildVm()
    {
        var http = new System.Net.Http.HttpClient();
        return new RequestEditorViewModel(
            new HttpExecutor(http),
            new OAuth2TokenAcquirer(http),
            new JintHost(),
            NullLogger<RequestEditorViewModel>.Instance);
    }

    /// <summary>Pumps the Avalonia dispatcher (which drains queued continuations from
    /// Task.Run scheduler) until <paramref name="until"/> returns true or
    /// <paramref name="budgetMs"/> elapses.</summary>
    private static void Pump(int budgetMs, Func<bool> until)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            Dispatcher.UIThread.RunJobs();
            if (until()) return;
            Thread.Sleep(50);
        }
    }
}
