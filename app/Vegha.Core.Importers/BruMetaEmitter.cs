using System.Text;
using Vegha.Core.Domain;

namespace Vegha.Core.Importers;

/// <summary>
/// Emits Bruno <c>collection.bru</c> and <c>folder.bru</c> files. These hold the
/// inheritance-chain fields: variables, headers, auth, pre-request script, tests,
/// docs. Used by the Properties dialog when the user edits collection- or
/// folder-level settings; complements <see cref="BruEmitter"/> which handles the
/// per-request file format.
/// </summary>
public static class BruMetaEmitter
{
    /// <summary>Emits a <c>collection.bru</c> body for a <see cref="Collection"/>.</summary>
    public static string EmitCollection(Collection c)
    {
        var sb = new StringBuilder();
        EmitMeta(sb, c.Name);
        EmitAuth(sb, c.Auth);
        EmitDictBlock(sb, "headers", c.Headers);
        EmitDictBlock(sb, "vars", c.Variables);
        EmitTextBlock(sb, "script:pre-request", c.PreRequestScript);
        EmitTextBlock(sb, "script:post-response", c.PostResponseScript);
        EmitTextBlock(sb, "tests", c.TestsScript);
        EmitTextBlock(sb, "docs", c.Docs);
        EmitPresets(sb, c.Presets);
        return sb.ToString();
    }

    /// <summary>Emits a <c>folder.bru</c> body for a <see cref="Folder"/>.</summary>
    public static string EmitFolder(Folder f)
    {
        var sb = new StringBuilder();
        EmitMeta(sb, f.Name);
        EmitAuth(sb, f.Auth);
        EmitDictBlock(sb, "headers", f.Headers);
        EmitDictBlock(sb, "vars", f.Variables);
        EmitTextBlock(sb, "script:pre-request", f.PreRequestScript);
        EmitTextBlock(sb, "script:post-response", f.PostResponseScript);
        EmitTextBlock(sb, "tests", f.TestsScript);
        EmitTextBlock(sb, "docs", f.Docs);
        return sb.ToString();
    }

    private static void EmitMeta(StringBuilder sb, string name)
    {
        sb.AppendLine("meta {");
        if (!string.IsNullOrEmpty(name)) sb.Append("  name: ").AppendLine(name);
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void EmitAuth(StringBuilder sb, AuthConfig? auth)
    {
        if (auth is null || auth.Type == AuthType.None || auth.Type == AuthType.Inherit) return;
        var blockName = auth.Type switch
        {
            AuthType.ApiKey => "auth:apikey",
            AuthType.Bearer => "auth:bearer",
            AuthType.Basic => "auth:basic",
            AuthType.Digest => "auth:digest",
            AuthType.OAuth1 => "auth:oauth1",
            AuthType.OAuth2 => "auth:oauth2",
            AuthType.AwsV4 => "auth:awsv4",
            AuthType.Ntlm => "auth:ntlm",
            AuthType.Wsse => "auth:wsse",
            _ => null,
        };
        if (blockName is null || auth.Parameters.Count == 0) return;
        sb.Append(blockName).AppendLine(" {");
        foreach (var (k, v) in auth.Parameters) sb.Append("  ").Append(k).Append(": ").AppendLine(v);
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void EmitDictBlock(StringBuilder sb, string blockName, IList<KvPair> pairs)
    {
        if (pairs.Count == 0) return;
        sb.Append(blockName).AppendLine(" {");
        foreach (var p in pairs)
        {
            sb.Append("  ");
            if (!p.Enabled) sb.Append('~');
            sb.Append(p.Name).Append(": ").AppendLine(p.Value);
        }
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void EmitTextBlock(StringBuilder sb, string blockName, string? content)
    {
        if (string.IsNullOrEmpty(content)) return;
        sb.Append(blockName).AppendLine(" {");
        sb.AppendLine(content.TrimEnd('\n', '\r'));
        sb.AppendLine("}");
        sb.AppendLine();
    }

    /// <summary>Emits the <c>presets { type: …, url: … }</c> block (new-request defaults).
    /// Skipped entirely for empty presets so an untouched collection.bru stays clean.</summary>
    private static void EmitPresets(StringBuilder sb, RequestPresets? presets)
    {
        if (presets is null || presets.IsEmpty) return;
        sb.AppendLine("presets {");
        if (!string.IsNullOrEmpty(presets.RequestType))
            sb.Append("  type: ").AppendLine(presets.RequestType);
        if (!string.IsNullOrEmpty(presets.BaseUrl))
            sb.Append("  url: ").AppendLine(presets.BaseUrl);
        sb.AppendLine("}");
        sb.AppendLine();
    }
}
