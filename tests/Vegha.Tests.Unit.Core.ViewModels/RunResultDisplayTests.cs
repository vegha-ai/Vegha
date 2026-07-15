using Vegha.App.ViewModels.Tabs;
using Vegha.Core.Domain;
using Vegha.Core.Flow;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

/// <summary>Covers the pure display projections behind the Postman-style results view — the
/// duration/size formatters and the per-row view-model (tests summary, size text, detail-pane
/// header/body formatting). No HTTP or scripting needed.</summary>
public class RunResultDisplayTests
{
    [Theory]
    [InlineData(865, "865 ms")]
    [InlineData(5865, "5s 865ms")]
    [InlineData(65000, "1m 5s")]
    public void FormatDuration_matches_postman_style(long ms, string expected) =>
        CollectionRunTabViewModel.FormatDuration(ms).Should().Be(expected);

    [Theory]
    [InlineData(918, "918 B")]
    [InlineData(1545, "1.509 KB")]      // 1545 / 1024 = 1.509…
    [InlineData(2_621_440, "2.5 MB")]
    public void FormatSize_matches_postman_style(long bytes, string expected) =>
        CollectionRunTabViewModel.FormatSize(bytes).Should().Be(expected);

    [Fact]
    public void Row_without_tests_reads_No_tests_found()
    {
        var r = new RequestRunResult("R", "GET", "http://x", 200, 10, true, null,
            RequestRunStatus.Passed, PassedTests: 0, FailedTests: 0);
        var vm = new RunResultRowVm(r, iterationIndex: 0);
        vm.HasTests.Should().BeFalse();
        vm.TestsSummary.Should().Be("No tests found");
    }

    [Fact]
    public void Row_with_tests_summarizes_pass_fail()
    {
        var r = new RequestRunResult("R", "GET", "http://x", 200, 10, true, null,
            RequestRunStatus.Passed, PassedTests: 3, FailedTests: 1);
        var vm = new RunResultRowVm(r, 0);
        vm.HasTests.Should().BeTrue();
        vm.TestsSummary.Should().Be("3 passed, 1 failed");
    }

    [Fact]
    public void Row_projects_size_and_status()
    {
        var r = new RequestRunResult("R", "POST", "http://x", 201, 42, true, null,
            RequestRunStatus.Passed, ResponseSizeBytes: 918);
        var vm = new RunResultRowVm(r, 0);
        vm.SizeText.Should().Be("918 B");
        vm.StatusText.Should().Be("201");
        vm.StatusTimeSize.Should().Contain("201").And.Contain("42 ms").And.Contain("918 B");
    }

    [Fact]
    public void Detail_pretty_prints_json_and_formats_headers()
    {
        var detail = new RunResultDetail(
            ResponseBody: "{\"a\":1}",
            ResponseHeaders: new[] { new KeyValuePair<string, string>("Content-Type", "application/json") },
            RequestBody: null,
            RequestHeaders: new[] { new KeyValuePair<string, string>("Accept", "*/*") },
            Tests: System.Array.Empty<RunTestOutcome>(),
            Console: System.Array.Empty<RunConsoleMessage>());
        var r = new RequestRunResult("R", "GET", "http://x", 200, 10, true, null, Detail: detail);
        var vm = new RunResultRowVm(r, 0);

        vm.ResponseBodyText.Should().Contain("\"a\": 1");                 // pretty-printed
        vm.ResponseHeadersText.Should().Be("Content-Type: application/json");
        vm.RequestHeadersText.Should().Be("Accept: */*");
        vm.RequestLine.Should().Be("GET http://x");
    }

    [Fact]
    public void Detail_leaves_non_json_body_untouched()
    {
        var detail = new RunResultDetail("<a>1</a>",
            System.Array.Empty<KeyValuePair<string, string>>(), null,
            System.Array.Empty<KeyValuePair<string, string>>(),
            System.Array.Empty<RunTestOutcome>(), System.Array.Empty<RunConsoleMessage>());
        var vm = new RunResultRowVm(new RequestRunResult("R", "GET", "http://x", 200, 1, true, null, Detail: detail), 0);
        vm.ResponseBodyText.Should().Be("<a>1</a>");
    }

    [Fact]
    public void RampSchedule_initial_hold_then_linear_ramp_to_target_at_ramp_end()
    {
        // 20 VUs, initial 5, ramp window [120s, 360s] within a 600s test.
        const int vus = 20, initial = 5;
        const long rampStart = 120_000, rampEnd = 360_000;

        // The first `initial` VUs start immediately (initial-hold phase).
        for (var i = 0; i < initial; i++)
            CollectionRunTabViewModel.VuStartOffsetMs(i, initial, vus, rampStart, rampEnd).Should().Be(0);

        // Ramp VUs are spread across the window; the last reaches the target at the ramp end.
        CollectionRunTabViewModel.VuStartOffsetMs(initial, initial, vus, rampStart, rampEnd)
            .Should().BeGreaterThan(rampStart).And.BeLessThanOrEqualTo(rampEnd);
        CollectionRunTabViewModel.VuStartOffsetMs(vus - 1, initial, vus, rampStart, rampEnd)
            .Should().Be(rampEnd);

        // Offsets are non-decreasing and never exceed the ramp end.
        long prev = -1;
        for (var i = 0; i < vus; i++)
        {
            var off = CollectionRunTabViewModel.VuStartOffsetMs(i, initial, vus, rampStart, rampEnd);
            off.Should().BeGreaterThanOrEqualTo(prev);
            off.Should().BeLessThanOrEqualTo(rampEnd);
            prev = off;
        }
    }

    [Fact]
    public void Fixed_profile_starts_all_vus_immediately()
    {
        // Fixed = initialLoad equals the target → every VU starts at t0.
        for (var i = 0; i < 10; i++)
            CollectionRunTabViewModel.VuStartOffsetMs(i, initialLoad: 10, totalVus: 10, rampStartMs: 0, rampEndMs: 0)
                .Should().Be(0);
    }

    [Fact]
    public void PhaseDescription_reflects_three_phases_and_ramp_is_a_subsegment()
    {
        var coll = new Collection { Name = "c", Requests = new List<RequestItem>() };
        var vm = new CollectionRunTabViewModel(coll, "run:test", http: null!, scripting: null!)
        {
            VirtualUsers = 20,
            InitialLoad = 5,
            TestDurationMinutes = 9,
            RampStartFraction = 1.0 / 3,
            RampEndFraction = 2.0 / 3,
        };

        // 9 min split into thirds → 3:00 each.
        vm.PhaseDescription.Should().Contain("5 virtual users run for 3:00 minutes");
        vm.PhaseDescription.Should().Contain("ramp up to 20 for 3:00 minutes");
        vm.PhaseDescription.Should().Contain("maintain 20 for 3:00 minutes");
    }

    [Fact]
    public void LoadProfileIndex_toggles_ramp_flag()
    {
        var coll = new Collection { Name = "c", Requests = new List<RequestItem>() };
        var vm = new CollectionRunTabViewModel(coll, "run:test", http: null!, scripting: null!);

        vm.IsRampProfile.Should().BeFalse();
        vm.LoadProfile.Should().Be(PerfLoadProfile.Fixed);

        vm.LoadProfileIndex = 1;
        vm.IsRampProfile.Should().BeTrue();
        vm.LoadProfile.Should().Be(PerfLoadProfile.Ramp);
    }

    [Fact]
    public void Reorder_commands_move_rows_and_reset_restores_order()
    {
        var coll = new Collection
        {
            Name = "c",
            Requests = new List<RequestItem>
            {
                new() { Name = "A", Method = "GET", Url = "http://a" },
                new() { Name = "B", Method = "GET", Url = "http://b" },
                new() { Name = "C", Method = "GET", Url = "http://c" },
            },
        };
        var vm = new CollectionRunTabViewModel(coll, "run:test", http: null!, scripting: null!);

        vm.RequestRows.Select(r => r.Name).Should().Equal("A", "B", "C");

        vm.MoveRowDownCommand.Execute(vm.RequestRows[0]);   // A moves after B
        vm.RequestRows.Select(r => r.Name).Should().Equal("B", "A", "C");

        vm.DeselectAllCommand.Execute(null);
        vm.RequestRows.All(r => !r.IsSelected).Should().BeTrue();

        vm.ResetOrderCommand.Execute(null);
        vm.RequestRows.Select(r => r.Name).Should().Equal("A", "B", "C");
        vm.RequestRows.All(r => r.IsSelected).Should().BeTrue();
    }
}
