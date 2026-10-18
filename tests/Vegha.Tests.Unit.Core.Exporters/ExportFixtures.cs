using System.IO.Compression;
using Vegha.Core.Domain;
using DomainEnv = Vegha.Core.Domain.Environment;

namespace Vegha.Tests.Unit.Core.Exporters;

/// <summary>A collection exercising every surface the exporters map: collection vars /
/// headers / auth / scripts, nested folders with folder headers, path + query params,
/// JSON / form / GraphQL bodies, per-request auth overrides, a secret env var, and a
/// non-HTTP (gRPC) request that HTTP-centric formats must skip.</summary>
internal static class ExportFixtures
{
    public const string SecretValue = "s3cret-value";

    public static Collection PetStore() => new()
    {
        Name = "Pet Store",
        Version = "2.0",
        Docs = "Pets API",
        Variables = { new KvPair("baseUrl", "https://api.example.com/v1") },
        Headers = { new KvPair("X-Client", "vegha") },
        Auth = new AuthConfig { Type = AuthType.Bearer, Parameters = new Dictionary<string, string> { ["token"] = "{{token}}" } },
        PreRequestScript = "bru.setVar('ts', Date.now());",
        Environments =
        {
            new DomainEnv
            {
                Id = "env-prod",
                Name = "Prod",
                Variables = { new KvPair("baseUrl", "https://api.example.com/v1"), new KvPair("token", SecretValue) },
                SecretVariables = { "token" },
            },
        },
        Requests =
        {
            new RequestItem
            {
                Name = "List pets",
                Method = "GET",
                Url = "{{baseUrl}}/pets?limit=10",
                Sequence = 1,
                Params = { new KvPair("limit", "10") },
                Tests = "test('ok', () => expect(res.getStatus()).to.equal(200));",
            },
        },
        Folders =
        {
            new Folder
            {
                Name = "Pets",
                Docs = "Pet operations",
                Headers = { new KvPair("X-Folder", "1") },
                Requests =
                {
                    new RequestItem
                    {
                        Name = "Get pet",
                        Method = "GET",
                        Url = "{{baseUrl}}/pets/:id",
                        Sequence = 1,
                        PathParams = { new KvPair("id", "42") },
                    },
                    new RequestItem
                    {
                        Name = "Create pet",
                        Method = "POST",
                        Url = "{{baseUrl}}/pets",
                        Sequence = 2,
                        Headers = { new KvPair("Content-Type", "application/json") },
                        Body = new BodyConfig { Mode = BodyMode.Json, Content = "{\"name\":\"Rex\",\"age\":3}" },
                        Auth = new AuthConfig
                        {
                            Type = AuthType.Basic,
                            Parameters = new Dictionary<string, string> { ["username"] = "user", ["password"] = "pass" },
                        },
                    },
                    new RequestItem
                    {
                        Name = "Login",
                        Method = "POST",
                        Url = "{{baseUrl}}/login",
                        Sequence = 3,
                        Body = new BodyConfig
                        {
                            Mode = BodyMode.FormUrlEncoded,
                            FormData = { new KvPair("user", "a"), new KvPair("pass", "b") },
                        },
                        Auth = new AuthConfig
                        {
                            Type = AuthType.ApiKey,
                            Parameters = new Dictionary<string, string> { ["key"] = "X-API-Key", ["value"] = "abc", ["placement"] = "header" },
                        },
                    },
                },
                Folders =
                {
                    new Folder
                    {
                        Name = "Admin",
                        Requests =
                        {
                            new RequestItem
                            {
                                Name = "Pets query",
                                Kind = RequestKind.GraphQL,
                                Method = "POST",
                                Url = "{{baseUrl}}/graphql",
                                Sequence = 1,
                                Body = new BodyConfig { Mode = BodyMode.GraphQL, GraphQLQuery = "{ pets { id } }", GraphQLVariables = "{\"a\":1}" },
                            },
                        },
                    },
                },
            },
            new Folder
            {
                Name = "Streams",
                Requests =
                {
                    new RequestItem { Name = "Watch", Kind = RequestKind.Grpc, Url = "grpc://localhost:5000", Sequence = 1 },
                },
            },
        },
    };

    /// <summary>Every request in the tree, depth-first.</summary>
    public static List<RequestItem> AllRequests(Collection c)
    {
        var list = new List<RequestItem>(c.Requests);
        void Walk(IList<Folder> folders)
        {
            foreach (var f in folders) { list.AddRange(f.Requests); Walk(f.Folders); }
        }
        Walk(c.Folders);
        return list;
    }

    public static Dictionary<string, byte[]> Unzip(byte[] zip)
    {
        using var ms = new MemoryStream(zip);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var e in archive.Entries)
        {
            using var s = e.Open();
            using var buf = new MemoryStream();
            s.CopyTo(buf);
            map[e.FullName] = buf.ToArray();
        }
        return map;
    }

    public static string Text(byte[] bytes) => System.Text.Encoding.UTF8.GetString(bytes);

    public static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vegha-export-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
