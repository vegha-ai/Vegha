using Vegha.Core.Domain;
using Vegha.Core.Flow;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Flow;

public class CollectionRunnerTests
{
    [Fact]
    public async Task RunAsync_WalksRootRequestsThenFolders_DepthFirst()
    {
        var collection = new Collection
        {
            Name = "C",
            Requests = new List<RequestItem>
            {
                new() { Name = "ping", Method = "GET", Url = "https://x.test/ping" }
            },
            Folders = new List<Folder>
            {
                new()
                {
                    Name = "a",
                    Requests = new List<RequestItem>
                    {
                        new() { Name = "a1", Method = "GET", Url = "https://x.test/a/1" },
                        new() { Name = "a2", Method = "GET", Url = "https://x.test/a/2" }
                    },
                    Folders = new List<Folder>
                    {
                        new()
                        {
                            Name = "a-nested",
                            Requests = new List<RequestItem>
                            {
                                new() { Name = "an1", Method = "GET", Url = "https://x.test/a/nested/1" }
                            }
                        }
                    }
                }
            }
        };

        var visited = new List<string>();
        Task<RequestRunResult> Exec(RequestItem req, IReadOnlyList<Folder> chain, CancellationToken ct)
        {
            visited.Add(req.Name);
            return Task.FromResult(new RequestRunResult(req.Name, req.Method, req.Url, 200, 5, true, null));
        }

        var results = await CollectionRunner.RunAsync(collection, Exec);
        visited.Should().Equal("ping", "a1", "a2", "an1");
        results.Should().HaveCount(4);
    }

    [Fact]
    public async Task RunFolderAsync_OnlyVisitsThatFolderSubtree()
    {
        var folder = new Folder
        {
            Name = "F",
            Requests = new List<RequestItem>
            {
                new() { Name = "f1", Method = "GET", Url = "https://x.test/f/1" }
            }
        };

        var visited = new List<string>();
        Task<RequestRunResult> Exec(RequestItem req, IReadOnlyList<Folder> chain, CancellationToken ct)
        {
            visited.Add(req.Name);
            return Task.FromResult(new RequestRunResult(req.Name, req.Method, req.Url, 200, 5, true, null));
        }

        var results = await CollectionRunner.RunFolderAsync(folder, Array.Empty<Folder>(), Exec);
        visited.Should().Equal("f1");
        results.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_ReceivesFolderChain_OuterToInner()
    {
        var collection = new Collection
        {
            Name = "C",
            Folders = new List<Folder>
            {
                new()
                {
                    Name = "outer",
                    Folders = new List<Folder>
                    {
                        new()
                        {
                            Name = "inner",
                            Requests = new List<RequestItem>
                            {
                                new() { Name = "deep", Method = "GET", Url = "https://x.test" }
                            }
                        }
                    }
                }
            }
        };

        var seenChain = new List<string>();
        Task<RequestRunResult> Exec(RequestItem req, IReadOnlyList<Folder> chain, CancellationToken ct)
        {
            foreach (var f in chain) seenChain.Add(f.Name);
            return Task.FromResult(new RequestRunResult(req.Name, req.Method, req.Url, 200, 5, true, null));
        }

        await CollectionRunner.RunAsync(collection, Exec);
        seenChain.Should().Equal("outer", "inner");
    }

    [Fact]
    public async Task Cancellation_StopsAfterInflightRequest()
    {
        var collection = new Collection
        {
            Name = "C",
            Requests = new List<RequestItem>
            {
                new() { Name = "a", Method = "GET", Url = "https://x.test/a" },
                new() { Name = "b", Method = "GET", Url = "https://x.test/b" },
                new() { Name = "c", Method = "GET", Url = "https://x.test/c" }
            }
        };

        var cts = new CancellationTokenSource();
        var visited = new List<string>();

        Task<RequestRunResult> Exec(RequestItem req, IReadOnlyList<Folder> chain, CancellationToken ct)
        {
            visited.Add(req.Name);
            if (req.Name == "a") cts.Cancel();
            return Task.FromResult(new RequestRunResult(req.Name, req.Method, req.Url, 200, 5, true, null));
        }

        await CollectionRunner.RunAsync(collection, Exec, cts.Token);
        visited.Should().Equal("a"); // canceled before running b or c
    }
}
