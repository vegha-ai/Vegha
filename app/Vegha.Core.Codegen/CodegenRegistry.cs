namespace Vegha.Core.Codegen;

/// <summary>Registry of available code emitters. Order here is the order the UI shows them
/// in — alphabetical by display name, matching Postman's code-snippet dropdown.</summary>
public static class CodegenRegistry
{
    public static IReadOnlyList<ICodegenEmitter> All { get; } = new ICodegenEmitter[]
    {
        new CLibcurlEmitter(),          // C - libcurl
        new CSharpHttpClientEmitter(),  // C# - HttpClient
        new CSharpRestSharpEmitter(),   // C# - RestSharp
        new CurlEmitter(),              // cURL
        new DartDioEmitter(),           // Dart - dio
        new DartHttpEmitter(),          // Dart - http
        new GoNetHttpEmitter(),         // Go - Native
        new HttpRawEmitter(),           // HTTP
        new JavaOkHttpEmitter(),        // Java - OkHttp
        new JavaUnirestEmitter(),       // Java - Unirest
        new JavaScriptFetchEmitter(),   // JavaScript - Fetch
        new JavaScriptJQueryEmitter(),  // JavaScript - jQuery
        new JavaScriptXhrEmitter(),     // JavaScript - XHR
        new KotlinOkHttpEmitter(),      // Kotlin - OkHttp
        new NodeJsAxiosEmitter(),       // NodeJs - Axios
        new NodeJsNativeEmitter(),      // NodeJs - Native
        new NodeJsRequestEmitter(),     // NodeJs - Request
        new NodeJsUnirestEmitter(),     // NodeJs - Unirest
        new ObjectiveCEmitter(),        // Objective-C - NSURLSession
        new OCamlCohttpEmitter(),       // OCaml - Cohttp
        new PhpCurlEmitter(),           // PHP - cURL
        new PhpGuzzleEmitter(),         // PHP - Guzzle
        new PhpHttpRequest2Emitter(),   // PHP - HTTP_Request2
        new PowerShellRestMethodEmitter(), // PowerShell - RestMethod
        new PythonHttpClientEmitter(),  // Python - http.client
        new PythonRequestsEmitter(),    // Python - Requests
        new RHttrEmitter(),             // R - httr
        new RRCurlEmitter(),            // R - RCurl
        new RubyNetHttpEmitter(),       // Ruby - Net::HTTP
        new ShellHttpieEmitter(),       // Shell - HTTPie
        new ShellWgetEmitter(),         // Shell - wget
        new SwiftUrlSessionEmitter(),   // Swift - URLSession
    };

    /// <summary>First emitter of a language family (e.g. "python" → Python - http.client).
    /// Multiple emitters share a family id; look up by display name for an exact variant.</summary>
    public static ICodegenEmitter? Find(string language) =>
        All.FirstOrDefault(e => string.Equals(e.Language, language, StringComparison.OrdinalIgnoreCase));

    /// <summary>Exact lookup by the UI display name (e.g. "NodeJs - Axios").</summary>
    public static ICodegenEmitter? FindByDisplayName(string displayName) =>
        All.FirstOrDefault(e => string.Equals(e.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));
}
