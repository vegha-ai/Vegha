using System.Text.Json;
using Vegha.Core.Domain;
using Vegha.Core.Requests;

namespace Vegha.Core.Engine;

/// <summary>One send: the request, where it lives, and the environment it runs against.</summary>
public sealed record SendContext(
    Collection Collection,
    IReadOnlyList<Folder> FolderChain,
    RequestItem Request,
    IReadOnlyDictionary<string, string> EnvironmentVariables)
{
    /// <summary>Workspace-level vars + scripts layered under the collection.</summary>
    public RequestComposition.WorkspaceContext? Workspace { get; init; }

    /// <summary>Collection folder on disk — relative file-body paths and linked requests
    /// resolve against it. Null for ad-hoc requests.</summary>
    public string? CollectionRoot { get; init; }

    /// <summary>Append the exchange to the shared history.</summary>
    public bool RecordHistory { get; init; } = true;

    /// <summary>History scope (the owning workspace folder), so each workspace sees its own rows.</summary>
    public string? HistoryWorkspaceId { get; init; }
}

/// <summary>What a send produced. <see cref="Outputs"/> carries the response, tests, timing
/// and every variable change; environment changes are the host's to apply (see
/// <see cref="EnvironmentMutations"/>) because the host owns the active environments.</summary>
public sealed record SendResult(RequestPipeline.Outputs Outputs, long? HistoryId);

/// <summary>
/// The interactive send path for non-desktop hosts (VS Code sidecar, tests): runs
/// <see cref="RequestPipeline"/> with the session's runtime variables, linked-request response
/// cache, token cache and secret resolution, then records what the send set into the runtime
/// store and appends the exchange to history — the same session behavior the request editor has.
/// </summary>
public sealed class RequestEngine
{
    private static readonly IReadOnlyDictionary<string, string> NoVars = new Dictionary<string, string>();

    private readonly EngineServices _services;

    public RequestEngine(EngineServices services) => _services = services;

    public EngineServices Services => _services;

    public async Task<SendResult> SendAsync(SendContext ctx, CancellationToken ct = default)
    {
        var inputs = new RequestPipeline.Inputs(
            ctx.Collection,
            ctx.FolderChain,
            ctx.Request,
            ctx.EnvironmentVariables,
            NoVars,
            Workspace: ctx.Workspace,
            Chain: new ChainContext(ctx.Collection, ctx.CollectionRoot, _services.ResponseCache),
            RuntimeVariables: _services.RuntimeVariables.Snapshot());
        var options = _services.PipelineOptions with { BasePath = ctx.CollectionRoot };

        var outputs = await RequestPipeline.ExecuteAsync(
            inputs, _services.Http, _services.Scripting, ct, options).ConfigureAwait(false);

        if (outputs.RuntimeVariablesSet.Count > 0)
            _services.RuntimeVariables.SetMany(outputs.RuntimeVariablesSet, ctx.Request.Name);

        long? historyId = null;
        if (ctx.RecordHistory && _services.History is { } history && outputs.Response is not null)
        {
            try
            {
                historyId = await history.AppendAsync(
                    ctx.Request.Method,
                    outputs.ResolvedUrl,
                    outputs.StatusCode,
                    outputs.Response.ElapsedMilliseconds,
                    outputs.ResponseBody,
                    outputs.ErrorMessage,
                    ct,
                    // Same snapshot the editor stores, so History → Replay works across hosts.
                    requestBlob: JsonSerializer.Serialize(ctx.Request),
                    workspaceId: ctx.HistoryWorkspaceId,
                    requestKind: ctx.Request.Body.Mode == BodyMode.GraphQL ? "graphql" : null).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* best-effort — a locked or full history must not fail the send */ }
        }

        return new SendResult(outputs, historyId);
    }
}
