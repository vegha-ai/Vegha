namespace Vegha.Core.Codegen;

/// <summary>Registry of available code emitters. Order here is the order the UI shows them in.</summary>
public static class CodegenRegistry
{
    public static IReadOnlyList<ICodegenEmitter> All { get; } = new ICodegenEmitter[]
    {
        new CurlEmitter(),
        new JavaScriptFetchEmitter(),
        new PythonRequestsEmitter(),
        new CSharpHttpClientEmitter(),
        new GoNetHttpEmitter(),
        new JavaOkHttpEmitter(),
    };

    public static ICodegenEmitter? Find(string language) =>
        All.FirstOrDefault(e => string.Equals(e.Language, language, StringComparison.OrdinalIgnoreCase));
}
