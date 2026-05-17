using Vegha.Core.Bru.Parser;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Vegha.Tests.Integration;

/// <summary>
/// Regression: walk every <c>.bru</c> fixture in <c>C:\src\bruno\packages\bruno-tests\collection</c>
/// through <see cref="BruParser.TryParse"/>. The test prints a parse summary and asserts the
/// success rate stays above a configured floor — bumping the floor is how we ratchet up
/// parser coverage without flipping the whole suite green at once.
///
/// The test is skipped when the fixture directory isn't present (e.g., in CI without the
/// Bruno checkout) so it can ship without breaking the build.
/// </summary>
public class BrunoFixtureRegressionTests
{
    private const string FixtureRoot = @"C:\src\bruno\packages\bruno-tests\collection";

    /// <summary>Floor for the % of fixtures that must parse successfully. Now 100%
    /// (226/226) after the parser learned the <c>vars:secret [ ... ]</c> list-block
    /// syntax. Stays at 1.00 until the next regression.</summary>
    private const double SuccessRateFloor = 1.0;

    private readonly ITestOutputHelper _output;

    public BrunoFixtureRegressionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void EveryBruFixture_ParsesOrFailsCleanly_AboveFloor()
    {
        if (!Directory.Exists(FixtureRoot))
        {
            _output.WriteLine($"Skipped — fixture root not present at {FixtureRoot}");
            return;
        }

        var files = Directory.EnumerateFiles(FixtureRoot, "*.bru", SearchOption.AllDirectories).ToList();
        files.Should().NotBeEmpty("the fixture directory exists but contains no .bru files");

        var parsed = 0;
        var failed = new List<(string Path, string Error)>();

        foreach (var file in files)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch (Exception ex)
            {
                failed.Add((file, "read: " + ex.Message));
                continue;
            }

            try
            {
                if (BruParser.TryParse(text, out _, out var error))
                {
                    parsed++;
                }
                else
                {
                    failed.Add((file, "parse: " + (error ?? "unknown")));
                }
            }
            catch (Exception ex)
            {
                // The contract is TryParse never throws — flag any escaping exception.
                failed.Add((file, "throw: " + ex.GetType().Name + ": " + ex.Message));
            }
        }

        var rate = (double)parsed / files.Count;
        _output.WriteLine($"Bruno fixture parse rate: {parsed}/{files.Count} ({rate:P0})");
        if (failed.Count > 0)
        {
            _output.WriteLine($"\n{Math.Min(failed.Count, 20)} of {failed.Count} failures (truncated):");
            foreach (var (path, error) in failed.Take(20))
            {
                var rel = Path.GetRelativePath(FixtureRoot, path);
                _output.WriteLine($"  {rel}: {error}");
            }
        }

        rate.Should().BeGreaterThanOrEqualTo(SuccessRateFloor,
            $"parser regressed below the {SuccessRateFloor:P0} floor — see test output for failing files");
    }
}
