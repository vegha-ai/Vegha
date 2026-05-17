using Vegha.Core.Domain;
using Vegha.Core.Importers;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Importers;

public class OpenApiDriftDetectorTests
{
    [Fact]
    public void Compare_FlagsAddedRemovedAndUnchanged()
    {
        var user = new Collection
        {
            Requests = new List<RequestItem>
            {
                new() { Method = "GET", Url = "{{baseUrl}}/users", Name = "listUsers" },
                new() { Method = "GET", Url = "{{baseUrl}}/orders", Name = "listOrders-old" },
            }
        };
        var live = new Collection
        {
            Requests = new List<RequestItem>
            {
                new() { Method = "GET", Url = "{{baseUrl}}/users", Name = "listUsers" },
                new() { Method = "POST", Url = "{{baseUrl}}/users", Name = "createUser" },  // new
                // /orders dropped from spec
            }
        };

        var drift = OpenApiDriftDetector.Compare(user, live);

        drift.Should().Contain(d => d.Kind == OpenApiDriftDetector.DriftKind.AddedInSpec
                                 && d.Method == "POST" && d.Path == "/users");
        drift.Should().Contain(d => d.Kind == OpenApiDriftDetector.DriftKind.RemovedFromSpec
                                 && d.Method == "GET" && d.Path == "/orders");
        drift.Should().Contain(d => d.Kind == OpenApiDriftDetector.DriftKind.Unchanged
                                 && d.Method == "GET" && d.Path == "/users");
    }

    [Fact]
    public void NormalizesAbsoluteUrls_AgainstBaseUrlPrefixed()
    {
        var user = new Collection
        {
            Requests = new List<RequestItem>
            {
                new() { Method = "GET", Url = "https://api.test/v1/users", Name = "listUsers" },
            }
        };
        var live = new Collection
        {
            Requests = new List<RequestItem>
            {
                new() { Method = "GET", Url = "{{baseUrl}}/v1/users", Name = "listUsers" },
            }
        };

        var drift = OpenApiDriftDetector.Compare(user, live);
        drift.Should().AllSatisfy(d => d.Kind.Should().Be(OpenApiDriftDetector.DriftKind.Unchanged));
    }

    [Fact]
    public void Folders_AreFlattenedIntoTheCompare()
    {
        var user = new Collection
        {
            Folders = new List<Folder>
            {
                new()
                {
                    Name = "users",
                    Requests = new List<RequestItem>
                    {
                        new() { Method = "GET", Url = "{{baseUrl}}/users", Name = "listUsers" },
                    },
                },
            },
        };
        var live = new Collection
        {
            Folders = new List<Folder>
            {
                new()
                {
                    Name = "users",
                    Requests = new List<RequestItem>
                    {
                        new() { Method = "GET", Url = "{{baseUrl}}/users", Name = "listUsers" },
                        new() { Method = "GET", Url = "{{baseUrl}}/users/{id}", Name = "getUser" },
                    },
                },
            },
        };

        var drift = OpenApiDriftDetector.Compare(user, live);
        drift.Should().Contain(d => d.Kind == OpenApiDriftDetector.DriftKind.AddedInSpec
                                 && d.Path == "/users/{id}");
    }
}
