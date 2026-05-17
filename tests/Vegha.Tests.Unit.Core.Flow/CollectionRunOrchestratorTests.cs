using Vegha.Core.Domain;
using Vegha.Core.Flow;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Flow;

public class CollectionRunOrchestratorTests
{
    private static Collection BuildCollection(params string[] requestNames)
    {
        return new Collection
        {
            Name = "c",
            Requests = requestNames.Select(n => new RequestItem
            {
                Name = n, Method = "GET", Url = "http://localhost/" + n
            }).ToList(),
        };
    }

    private static RequestRunResult Ok(RequestItem r, int status = 200) =>
        new(r.Name, r.Method, r.Url, status, 10, true, null, RequestRunStatus.Passed, 1, 0);

    [Fact]
    public async Task SingleIteration_runs_each_request_in_tree_order()
    {
        var coll = BuildCollection("A", "B", "C");
        var opts = RunnerOptions.Default(coll);
        var order = new List<string>();

        var summary = await CollectionRunOrchestrator.RunAsync(
            opts,
            executor: (iter, req, chain, vars, ct) =>
            {
                order.Add(req.Name);
                return Task.FromResult(Ok(req));
            });

        order.Should().Equal("A", "B", "C");
        summary.Passed.Should().Be(3);
        summary.Results.Should().HaveCount(3);
    }

    [Fact]
    public async Task MultipleIterations_run_count_matches()
    {
        var coll = BuildCollection("A", "B");
        var opts = RunnerOptions.Default(coll) with { Iterations = 4 };

        var summary = await CollectionRunOrchestrator.RunAsync(
            opts,
            executor: (iter, req, chain, vars, ct) => Task.FromResult(Ok(req)));

        summary.Results.Should().HaveCount(8); // 2 requests × 4 iterations
        summary.Passed.Should().Be(8);
    }

    [Fact]
    public async Task Workers_greater_than_1_runs_iterations_concurrently()
    {
        var coll = BuildCollection("A");
        var opts = RunnerOptions.Default(coll) with { Iterations = 8, Workers = 4 };
        var inFlight = 0;
        var maxInFlight = 0;
        var gate = new object();

        await CollectionRunOrchestrator.RunAsync(opts,
            executor: async (iter, req, chain, vars, ct) =>
            {
                lock (gate)
                {
                    inFlight++;
                    if (inFlight > maxInFlight) maxInFlight = inFlight;
                }
                await Task.Delay(50, ct);
                lock (gate) inFlight--;
                return Ok(req);
            });

        maxInFlight.Should().BeGreaterThan(1).And.BeLessThanOrEqualTo(4);
    }

    [Fact]
    public async Task SelectedRequestNames_skips_unchecked_requests()
    {
        var coll = BuildCollection("Keep", "Drop", "Also");
        var opts = RunnerOptions.Default(coll) with
        {
            SelectedRequestNames = new HashSet<string> { "Keep" },
        };
        var executed = new List<string>();

        var summary = await CollectionRunOrchestrator.RunAsync(opts,
            executor: (iter, req, chain, vars, ct) =>
            {
                executed.Add(req.Name);
                return Task.FromResult(Ok(req));
            });

        executed.Should().Equal("Keep");
        summary.Skipped.Should().Be(2);
        summary.Results.Should().HaveCount(3);
    }

    [Fact]
    public async Task Cancellation_aborts_after_in_flight_request_completes()
    {
        var coll = BuildCollection("A", "B", "C", "D", "E");
        var opts = RunnerOptions.Default(coll);
        using var cts = new CancellationTokenSource();
        var processed = 0;

        var summary = await CollectionRunOrchestrator.RunAsync(opts,
            executor: async (iter, req, chain, vars, ct) =>
            {
                Interlocked.Increment(ref processed);
                if (processed == 2) cts.Cancel();
                await Task.Delay(10, ct);
                return Ok(req);
            },
            cancellationToken: cts.Token);

        summary.WasCanceled.Should().BeTrue();
        processed.Should().BeLessThan(5);
    }

    [Fact]
    public async Task DataSource_drives_iteration_count_over_manual()
    {
        var coll = BuildCollection("A");
        var dataFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(dataFile, "name\nrow1\nrow2\nrow3\n");
        try
        {
            var ds = await IterationDataSource.LoadCsvAsync(dataFile);
            var opts = RunnerOptions.Default(coll) with
            {
                DataSource = ds,
                Iterations = 99,  // ignored when data source is set
            };
            var seenNames = new List<string>();
            await CollectionRunOrchestrator.RunAsync(opts,
                executor: (iter, req, chain, vars, ct) =>
                {
                    seenNames.Add(vars["name"]);
                    return Task.FromResult(Ok(req));
                });

            seenNames.Should().Equal("row1", "row2", "row3");
        }
        finally { File.Delete(dataFile); }
    }

    [Fact]
    public async Task Executor_exceptions_become_Errored_results()
    {
        var coll = BuildCollection("A", "B");
        var opts = RunnerOptions.Default(coll);

        var summary = await CollectionRunOrchestrator.RunAsync(opts,
            executor: (iter, req, chain, vars, ct) =>
            {
                if (req.Name == "B") throw new InvalidOperationException("boom");
                return Task.FromResult(Ok(req));
            });

        summary.Errored.Should().Be(1);
        summary.Passed.Should().Be(1);
        summary.Results.Should().ContainSingle(r => r.ErrorMessage == "boom");
    }

    [Fact]
    public async Task Delay_between_requests_is_honored()
    {
        var coll = BuildCollection("A", "B", "C");
        var opts = RunnerOptions.Default(coll) with { DelayBetweenRequestsMs = 80 };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await CollectionRunOrchestrator.RunAsync(opts,
            executor: (iter, req, chain, vars, ct) => Task.FromResult(Ok(req)));

        // 3 requests × 80ms delay each = 240ms minimum (one delay fires after each).
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(200);
    }

    [Fact]
    public async Task Progress_events_fire_in_expected_sequence()
    {
        var coll = BuildCollection("A", "B");
        var opts = RunnerOptions.Default(coll) with { Iterations = 1 };
        var events = new List<RunnerEvent>();
        var progress = new Progress<RunnerEvent>(events.Add);

        await CollectionRunOrchestrator.RunAsync(opts,
            executor: (iter, req, chain, vars, ct) => Task.FromResult(Ok(req)),
            progress: progress);

        // Progress fires async on the Progress<T> capture thread; flush by yielding.
        await Task.Delay(50);

        events.OfType<RunStarted>().Should().HaveCount(1);
        events.OfType<IterationStarted>().Should().HaveCount(1);
        events.OfType<RequestCompleted>().Should().HaveCount(2);
        events.OfType<IterationCompleted>().Should().HaveCount(1);
        events.OfType<RunCompleted>().Should().HaveCount(1);
    }
}
