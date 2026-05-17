using Vegha.Core.Domain;
using Vegha.Core.FileFormat;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.FileFormat;

public class CollectionStoreTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "vegha-fileformat-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void Save_Then_Load_RoundTrips_FullCollection()
    {
        var original = SampleCollection();
        CollectionStore.Save(_tempRoot, original);

        var loaded = CollectionStore.Load(_tempRoot);

        loaded.Name.Should().Be(original.Name);
        loaded.Version.Should().Be(original.Version);
        loaded.Variables.Should().BeEquivalentTo(original.Variables);
        loaded.Auth.Should().BeEquivalentTo(original.Auth);
        // Environments get an auto-generated Id on first serialize when the source had none
        // (back-compat with envs constructed without a stable identity). Compare every other
        // field, but skip Id since the fixture intentionally doesn't set one.
        loaded.Environments.Should().BeEquivalentTo(original.Environments, o => o.Excluding(e => e.Id));

        // Root-level requests
        loaded.Requests.Should().HaveCount(original.Requests.Count);
        loaded.Requests[0].Name.Should().Be(original.Requests[0].Name);
        loaded.Requests[0].Url.Should().Be(original.Requests[0].Url);

        // Folder structure preserved
        loaded.Folders.Should().HaveCount(original.Folders.Count);
        loaded.Folders[0].Name.Should().Be(original.Folders[0].Name);
        loaded.Folders[0].Requests.Should().HaveCount(original.Folders[0].Requests.Count);
    }

    [Fact]
    public void NestedFolder_Requests_AreReadFromCorrectSubdir()
    {
        var collection = new Collection
        {
            Name = "Nested",
            Requests = new List<RequestItem>(),
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
                                new() { Name = "deep", Method = "POST", Url = "https://x.test/deep" }
                            }
                        }
                    }
                }
            }
        };

        CollectionStore.Save(_tempRoot, collection);

        File.Exists(Path.Combine(_tempRoot, "outer", "inner", "deep" + CollectionJson.RequestSuffix))
            .Should().BeTrue();

        var loaded = CollectionStore.Load(_tempRoot);
        loaded.Folders.Should().ContainSingle();
        loaded.Folders[0].Folders.Should().ContainSingle();
        loaded.Folders[0].Folders[0].Requests[0].Name.Should().Be("deep");
        loaded.Folders[0].Folders[0].Requests[0].Method.Should().Be("POST");
    }

    [Fact]
    public void Body_AllModes_RoundTrip()
    {
        var modes = new[] { BodyMode.None, BodyMode.Json, BodyMode.Text, BodyMode.Xml,
                            BodyMode.GraphQL, BodyMode.FormUrlEncoded, BodyMode.MultipartForm,
                            BodyMode.Binary, BodyMode.Sparql };
        foreach (var mode in modes)
        {
            var c = new Collection
            {
                Name = "BodyTest_" + mode,
                Requests = new List<RequestItem>
                {
                    new() {
                        Name = "r", Url = "https://x.test", Method = "POST",
                        Body = new BodyConfig { Mode = mode, Content = $"content-for-{mode}" }
                    }
                }
            };
            var dir = Path.Combine(_tempRoot, mode.ToString());
            CollectionStore.Save(dir, c);
            var loaded = CollectionStore.Load(dir);
            loaded.Requests[0].Body.Mode.Should().Be(mode);
            // Body is null'd on disk only when Mode==None and Content is empty.
            if (mode != BodyMode.None) loaded.Requests[0].Body.Content.Should().Be($"content-for-{mode}");
        }
    }

    [Fact]
    public void AuthAllTypes_RoundTrip()
    {
        var types = Enum.GetValues<AuthType>();
        foreach (var t in types)
        {
            var c = new Collection
            {
                Name = "AuthTest_" + t,
                Requests = new List<RequestItem>
                {
                    new() {
                        Name = "r", Url = "https://x.test",
                        Auth = new AuthConfig
                        {
                            Type = t,
                            Parameters = new Dictionary<string, string> { ["key"] = "value-" + t }
                        }
                    }
                }
            };
            var dir = Path.Combine(_tempRoot, t.ToString());
            CollectionStore.Save(dir, c);
            var loaded = CollectionStore.Load(dir);
            loaded.Requests[0].Auth.Should().NotBeNull();
            loaded.Requests[0].Auth!.Type.Should().Be(t);
            loaded.Requests[0].Auth!.Parameters["key"].Should().Be("value-" + t);
        }
    }

    [Fact]
    public void Environments_RoundTrip_WithSecretFlags()
    {
        var c = new Collection
        {
            Name = "EnvTest",
            Environments = new List<Vegha.Core.Domain.Environment>
            {
                new()
                {
                    Name = "prod",
                    Variables = new List<KvPair>
                    {
                        new("baseUrl", "https://prod.acme.io"),
                        new("apiKey", "PRODKEY", true) { Description = "secret" }
                    },
                    SecretVariables = new List<string> { "apiKey" }
                }
            }
        };

        CollectionStore.Save(_tempRoot, c);
        var loaded = CollectionStore.Load(_tempRoot);

        loaded.Environments.Should().ContainSingle();
        loaded.Environments[0].Name.Should().Be("prod");
        loaded.Environments[0].Variables.Should().HaveCount(2);
        loaded.Environments[0].SecretVariables.Should().Contain("apiKey");
    }

    [Fact]
    public void Inheritance_Fields_RoundTrip_OnCollectionAndFolder()
    {
        var c = new Collection
        {
            Name = "Inh",
            Headers = new List<KvPair> { new("X-Coll", "C") },
            PreRequestScript = "// coll pre",
            TestsScript = "// coll tests",
            Docs = "# Collection docs",
            Folders = new List<Folder>
            {
                new()
                {
                    Name = "fA",
                    Variables = new List<KvPair> { new("baseUrl", "https://a.test") },
                    Headers = new List<KvPair> { new("X-Folder-A", "A") },
                    PreRequestScript = "// folder A pre",
                    TestsScript = "// folder A tests",
                    Docs = "## Folder A docs",
                    Auth = new AuthConfig
                    {
                        Type = AuthType.Bearer,
                        Parameters = new Dictionary<string, string> { ["token"] = "FOLDERTOKEN" }
                    },
                    Requests = new List<RequestItem>
                    {
                        new() { Name = "ping", Url = "{{baseUrl}}/ping" }
                    }
                }
            }
        };

        CollectionStore.Save(_tempRoot, c);
        var loaded = CollectionStore.Load(_tempRoot);

        loaded.Headers.Should().ContainSingle().Which.Name.Should().Be("X-Coll");
        loaded.PreRequestScript.Should().Be("// coll pre");
        loaded.TestsScript.Should().Be("// coll tests");
        loaded.Docs.Should().Be("# Collection docs");

        var fa = loaded.Folders.Single();
        fa.Variables.Should().ContainSingle().Which.Value.Should().Be("https://a.test");
        fa.Headers.Should().ContainSingle().Which.Name.Should().Be("X-Folder-A");
        fa.PreRequestScript.Should().Be("// folder A pre");
        fa.TestsScript.Should().Be("// folder A tests");
        fa.Docs.Should().Be("## Folder A docs");
        fa.Auth.Should().NotBeNull();
        fa.Auth!.Type.Should().Be(AuthType.Bearer);
        fa.Auth.Parameters["token"].Should().Be("FOLDERTOKEN");
    }

    [Fact]
    public void Load_MissingManifest_Throws()
    {
        Directory.CreateDirectory(_tempRoot);
        var act = () => CollectionStore.Load(_tempRoot);
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void EmptyFields_AreOmittedFromOnDiskJson_ForReadability()
    {
        var c = new Collection
        {
            Name = "Compact",
            Requests = new List<RequestItem>
            {
                new() { Name = "min", Method = "GET", Url = "https://x.test/y" }
            }
        };
        CollectionStore.Save(_tempRoot, c);
        var json = File.ReadAllText(Path.Combine(_tempRoot, "min" + CollectionJson.RequestSuffix));
        // No empty arrays in the on-disk file when collections are unset.
        json.Should().NotContain("\"params\": []");
        json.Should().NotContain("\"headers\": []");
    }

    private static Collection SampleCollection() => new()
    {
        Name = "Sample",
        Version = "1.0",
        Variables = new List<KvPair> { new("baseUrl", "https://api.test") },
        Auth = new AuthConfig
        {
            Type = AuthType.Bearer,
            Parameters = new Dictionary<string, string> { ["token"] = "{{token}}" }
        },
        Requests = new List<RequestItem>
        {
            new()
            {
                Name = "ping", Method = "GET", Url = "{{baseUrl}}/ping",
                Headers = new List<KvPair> { new("X-Trace", "abc") },
                Settings = new RequestSettingsConfig { FollowRedirects = false }
            }
        },
        Folders = new List<Folder>
        {
            new()
            {
                Name = "users",
                Requests = new List<RequestItem>
                {
                    new()
                    {
                        Name = "list-users", Method = "GET", Url = "{{baseUrl}}/users",
                        Params = new List<KvPair> { new("limit", "10") }
                    },
                    new()
                    {
                        Name = "create-user", Method = "POST", Url = "{{baseUrl}}/users",
                        Body = new BodyConfig { Mode = BodyMode.Json, Content = "{\"name\":\"A\"}" },
                        PreRequestScript = "bru.setVar('uid', 'X')",
                        Tests = "test('ok', () => expect(res.status).toBe(201))"
                    }
                }
            }
        },
        Environments = new List<Vegha.Core.Domain.Environment>
        {
            new()
            {
                Name = "Local",
                Variables = new List<KvPair> { new("token", "LOCAL_TOKEN") }
            }
        }
    };
}
