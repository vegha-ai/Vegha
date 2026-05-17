using Vegha.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

public class SearchPaletteScoreTests
{
    private static SearchResult Row(string title, string subtitle = "") =>
        new(SearchResultKind.Request, title, subtitle, title);

    [Fact]
    public void EmptyQuery_ScoresOne_AsCatchAll()
    {
        SearchPaletteViewModel.Score(Row("anything"), "").Should().Be(1);
    }

    [Fact]
    public void StartOfTitle_BeatsMidWord()
    {
        var startScore = SearchPaletteViewModel.Score(Row("users"), "us");
        var midScore = SearchPaletteViewModel.Score(Row("axiusfg"), "us");
        startScore.Should().BeGreaterThan(midScore);
    }

    [Fact]
    public void NonMatch_ScoresZero()
    {
        SearchPaletteViewModel.Score(Row("users"), "abc").Should().Be(0);
    }

    [Fact]
    public void AllTokensMustMatch()
    {
        SearchPaletteViewModel.Score(Row("users", "GET /api/users"), "users get").Should().BeGreaterThan(0);
        SearchPaletteViewModel.Score(Row("users", "GET /api/users"), "users foo").Should().Be(0);
    }

    [Fact]
    public void WordBoundaryMatch_BeatsArbitraryMidWord()
    {
        var boundary = SearchPaletteViewModel.Score(Row("api/users"), "users");
        var middle = SearchPaletteViewModel.Score(Row("xxusers"), "users");
        boundary.Should().BeGreaterThan(middle);
    }
}
