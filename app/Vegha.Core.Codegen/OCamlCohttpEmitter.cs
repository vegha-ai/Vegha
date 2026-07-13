using System.Text;
using Vegha.Core.Domain;

namespace Vegha.Core.Codegen;

/// <summary>Emits an OCaml snippet using cohttp-lwt-unix.</summary>
public sealed class OCamlCohttpEmitter : ICodegenEmitter
{
    public string Language => "ocaml";
    public string DisplayName => "OCaml - Cohttp";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();
        sb.AppendLine("open Lwt");
        sb.AppendLine("open Cohttp");
        sb.AppendLine("open Cohttp_lwt_unix");
        sb.AppendLine();
        if (ctx.Body is not null)
        {
            sb.AppendLine($"let post_data = {CodegenText.Json(ctx.Body)};;");
            sb.AppendLine();
        }
        sb.AppendLine("let req_body =");
        sb.AppendLine($"  let uri = Uri.of_string {CodegenText.Json(ctx.Url)} in");
        var headers = ctx.HeadersWithContentType();
        if (headers.Count > 0)
        {
            sb.AppendLine("  let headers = Header.init ()");
            foreach (var h in headers)
                sb.AppendLine($"    |> fun h -> Header.add h {CodegenText.Json(h.Name)} {CodegenText.Json(h.Value)}");
            sb.AppendLine("  in");
        }
        if (ctx.Body is not null)
            sb.AppendLine("  let body = Cohttp_lwt.Body.of_string post_data in");
        sb.Append("  Client.call");
        if (headers.Count > 0) sb.Append(" ~headers");
        if (ctx.Body is not null) sb.Append(" ~body");
        sb.AppendLine($" `{ctx.Method} uri >>= fun (_resp, body) ->");
        sb.AppendLine("  body |> Cohttp_lwt.Body.to_string >|= fun body -> body");
        sb.AppendLine();
        sb.AppendLine("let () =");
        sb.AppendLine("  let resp_body = Lwt_main.run req_body in");
        sb.Append("  print_endline resp_body");
        return sb.ToString();
    }
}
