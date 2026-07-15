using System.Text;
using Vegha.Core.Domain;

namespace Vegha.Core.Codegen;

/// <summary>Emits an Objective-C snippet using NSURLSession.</summary>
public sealed class ObjectiveCEmitter : ICodegenEmitter
{
    public string Language => "objc";
    public string DisplayName => "Objective-C - NSURLSession";

    public string Emit(RequestItem request, IReadOnlyDictionary<string, string>? vars = null)
    {
        var ctx = CodegenContext.From(request, vars);
        var sb = new StringBuilder();
        sb.AppendLine("#import <Foundation/Foundation.h>");
        sb.AppendLine();
        sb.AppendLine("dispatch_semaphore_t sema = dispatch_semaphore_create(0);");
        sb.AppendLine();
        sb.AppendLine($"NSMutableURLRequest *request = [NSMutableURLRequest requestWithURL:[NSURL URLWithString:@{CodegenText.CString(ctx.Url)}]");
        sb.AppendLine("  cachePolicy:NSURLRequestUseProtocolCachePolicy");
        sb.AppendLine("  timeoutInterval:10.0];");

        var headers = ctx.HeadersWithContentType();
        if (headers.Count > 0)
        {
            sb.AppendLine("NSDictionary *headers = @{");
            foreach (var h in headers)
                sb.AppendLine($"  @{CodegenText.CString(h.Name)}: @{CodegenText.CString(h.Value)},");
            sb.AppendLine("};");
            sb.AppendLine();
            sb.AppendLine("[request setAllHTTPHeaderFields:headers];");
        }
        if (ctx.Body is not null)
        {
            sb.AppendLine($"NSData *postData = [@{CodegenText.CString(ctx.Body)} dataUsingEncoding:NSUTF8StringEncoding];");
            sb.AppendLine("[request setHTTPBody:postData];");
        }
        sb.AppendLine();
        sb.AppendLine($"[request setHTTPMethod:@{CodegenText.CString(ctx.Method)}];");
        sb.AppendLine();
        sb.AppendLine("NSURLSession *session = [NSURLSession sharedSession];");
        sb.AppendLine("NSURLSessionDataTask *dataTask = [session dataTaskWithRequest:request");
        sb.AppendLine("completionHandler:^(NSData *data, NSURLResponse *response, NSError *error) {");
        sb.AppendLine("  if (error) {");
        sb.AppendLine("    NSLog(@\"%@\", error);");
        sb.AppendLine("  } else {");
        sb.AppendLine("    NSString *body = [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding];");
        sb.AppendLine("    NSLog(@\"%@\", body);");
        sb.AppendLine("  }");
        sb.AppendLine("  dispatch_semaphore_signal(sema);");
        sb.AppendLine("}];");
        sb.AppendLine("[dataTask resume];");
        sb.Append("dispatch_semaphore_wait(sema, DISPATCH_TIME_FOREVER);");
        return sb.ToString();
    }
}
