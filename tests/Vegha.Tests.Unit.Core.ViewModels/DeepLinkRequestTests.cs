using FluentAssertions;
using Vegha.App.ViewModels.Services;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

public class DeepLinkRequestTests
{
    [Fact]
    public void Parses_git_import_link_from_a_browser()
    {
        var link = "vegha://import/git?url=https%3A%2F%2Fgithub.com%2Fusebruno%2Fgithub-rest-api-collection.git";

        var req = DeepLinkRequest.TryParse(link, out var error);

        error.Should().BeNull();
        req.Should().NotBeNull();
        req!.Kind.Should().Be(DeepLinkKind.ImportGit);
        req.Url.Should().Be("https://github.com/usebruno/github-rest-api-collection.git");
        req.Branch.Should().BeNull();
        req.Folder.Should().BeNull();
    }

    [Fact]
    public void Carries_branch_and_folder()
    {
        var req = DeepLinkRequest.TryParse("vegha://import/git?url=https://github.com/acme/apis&branch=dev&folder=postman%2Fexports", out _);

        req!.Branch.Should().Be("dev");
        req.Folder.Should().Be("postman/exports");
    }

    [Fact]
    public void Accepts_browser_style_repo_links_and_ssh()
    {
        DeepLinkRequest.TryParse("vegha://import/git?url=https://github.com/acme/apis/tree/main/collections", out _)
            .Should().NotBeNull();
        DeepLinkRequest.TryParse("vegha://import/git?url=git@github.com:acme/apis.git", out _)
            .Should().NotBeNull();
    }

    [Theory]
    [InlineData("vegha://import/git?url=file:///C:/secrets")]
    [InlineData("vegha://import/git?url=C:%5Crepos%5Cx")]
    [InlineData("vegha://import/git?url=/etc")]
    [InlineData("vegha://import/git")]
    [InlineData("vegha://import/git?url=")]
    public void Rejects_local_or_missing_repository_urls(string link)
    {
        DeepLinkRequest.TryParse(link, out var error).Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("vegha://open/file?path=x")]
    [InlineData("https://github.com/acme/apis")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_unknown_routes_and_foreign_schemes(string? link)
    {
        DeepLinkRequest.TryParse(link, out var error).Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Drops_unsafe_folder_and_branch_values()
    {
        var req = DeepLinkRequest.TryParse("vegha://import/git?url=https://github.com/acme/apis&branch=has%20space&folder=..%2F..%2Fetc", out _);

        req!.Branch.Should().BeNull();
        req.Folder.Should().BeNull();
    }

    [Fact]
    public void IsDeepLink_matches_scheme_case_insensitively()
    {
        DeepLinkRequest.IsDeepLink("VEGHA://import/git?url=x").Should().BeTrue();
        DeepLinkRequest.IsDeepLink(@"C:\repos").Should().BeFalse();
        DeepLinkRequest.IsDeepLink(null).Should().BeFalse();
    }
}
