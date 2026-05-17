using DomainEnv = Vegha.Core.Domain.Environment;

namespace Vegha.Core.FileFormat;

/// <summary>
/// Workspace-level inheritance payload: envs at <c>&lt;workspace&gt;/environments/*.env.json</c>
/// and scripts at <c>&lt;workspace&gt;/scripts/{pre-request,tests}.js</c>. Merged underneath
/// the active collection's envs and scripts at execution time.
/// </summary>
public sealed record WorkspaceModel
{
    public IList<DomainEnv> Environments { get; init; } = new List<DomainEnv>();
    public string? PreRequestScript { get; init; }
    public string? PostResponseScript { get; init; }
    public string? TestsScript { get; init; }

    public static WorkspaceModel Empty { get; } = new();
}

public static class WorkspaceModelLoader
{
    public const string EnvironmentsFolder = "environments";
    public const string ScriptsFolder = "scripts";
    public const string PreRequestScriptFile = "pre-request.js";
    public const string PostResponseScriptFile = "post-response.js";
    public const string TestsScriptFile = "tests.js";

    /// <summary>Loads the workspace-level inheritance payload. Missing files yield empty
    /// fields; malformed envs are skipped silently so one bad file can't poison the merge.</summary>
    public static WorkspaceModel Load(string workspaceFolder)
    {
        if (string.IsNullOrEmpty(workspaceFolder) || !Directory.Exists(workspaceFolder))
            return WorkspaceModel.Empty;

        var envs = new List<DomainEnv>();
        var envDir = Path.Combine(workspaceFolder, EnvironmentsFolder);
        if (Directory.Exists(envDir))
        {
            foreach (var path in Directory.EnumerateFiles(envDir, "*" + CollectionJson.EnvironmentSuffix))
            {
                try
                {
                    var file = CollectionJson.DeserializeEnvironment(File.ReadAllText(path));
                    if (file is not null) envs.Add(EnvironmentFile.ToDomain(file));
                }
                catch { /* one malformed env shouldn't bring down the whole workspace */ }
            }
        }

        var scriptsDir = Path.Combine(workspaceFolder, ScriptsFolder);
        string? preRequest = ReadIfExists(Path.Combine(scriptsDir, PreRequestScriptFile));
        string? postResponse = ReadIfExists(Path.Combine(scriptsDir, PostResponseScriptFile));
        string? tests = ReadIfExists(Path.Combine(scriptsDir, TestsScriptFile));

        return new WorkspaceModel
        {
            Environments = envs,
            PreRequestScript = preRequest,
            PostResponseScript = postResponse,
            TestsScript = tests,
        };
    }

    private static string? ReadIfExists(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }
}
