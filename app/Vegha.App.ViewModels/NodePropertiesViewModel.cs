using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Core.Domain;

namespace Vegha.App.ViewModels;

/// <summary>
/// Backs the Properties dialog for collections and folders. Both kinds carry the same
/// inheritance surface — variables, headers, auth, pre-request script, tests script, docs —
/// so a single VM serves both with a <see cref="NodeKind"/> selector for the title bar.
///
/// On Save, the VM doesn't write directly: it raises <see cref="SaveRequested"/> with the
/// rebuilt domain model so the host (CollectionsViewModel) can run the Bru emit + reload
/// dance. Cancel raises <see cref="CancelRequested"/> for symmetry.
/// </summary>
public partial class NodePropertiesViewModel : ObservableObject
{
    public enum Kind { Collection, Folder }

    public Kind NodeKind { get; }
    public string NodeLabel => NodeKind == Kind.Collection ? "Collection" : "Folder";
    public string Title => $"{NodeLabel} properties — {Name}";

    [ObservableProperty] private string _name = string.Empty;

    public ObservableCollection<KvEntry> Variables { get; } = new();
    public ObservableCollection<KvEntry> Headers { get; } = new();

    [ObservableProperty] private string _preRequestScript = string.Empty;
    [ObservableProperty] private string _postResponseScript = string.Empty;
    [ObservableProperty] private string _testsScript = string.Empty;
    [ObservableProperty] private string _docs = string.Empty;

    public IReadOnlyList<string> AvailableAuthTypes { get; } = new[]
    {
        "none", "inherit", "apikey", "bearer", "basic", "digest",
        "oauth1", "oauth2", "awsv4", "ntlm", "wsse"
    };

    [ObservableProperty] private string _authType = "none";
    [ObservableProperty] private string _authParametersText = string.Empty;

    // ---- Presets (collection-only; new-request defaults) ----
    public IReadOnlyList<string> AvailablePresetTypes { get; } = new[]
    {
        "http", "graphql", "grpc", "websocket"
    };

    /// <summary>Default request type for NEW requests created in this collection. Bruno's
    /// "Presets → Request Type". Only meaningful for collections (folders ignore it).</summary>
    [ObservableProperty] private string _presetRequestType = "http";

    /// <summary>Base URL pre-filled into new requests. Bruno's "Presets → Base URL".</summary>
    [ObservableProperty] private string _presetBaseUrl = string.Empty;

    public event EventHandler<NodePropertiesSaveEventArgs>? SaveRequested;
    public event EventHandler? CancelRequested;

    public NodePropertiesViewModel(Kind nodeKind, Collection collection) : this(nodeKind)
    {
        Name = collection.Name;
        SeedKv(Variables, collection.Variables);
        SeedKv(Headers, collection.Headers);
        PreRequestScript = collection.PreRequestScript ?? string.Empty;
        PostResponseScript = collection.PostResponseScript ?? string.Empty;
        TestsScript = collection.TestsScript ?? string.Empty;
        Docs = collection.Docs ?? string.Empty;
        ApplyAuth(collection.Auth);
        if (collection.Presets is { } presets)
        {
            PresetRequestType = string.IsNullOrEmpty(presets.RequestType) ? "http" : presets.RequestType;
            PresetBaseUrl = presets.BaseUrl;
        }
    }

    public NodePropertiesViewModel(Kind nodeKind, Folder folder) : this(nodeKind)
    {
        Name = folder.Name;
        SeedKv(Variables, folder.Variables);
        SeedKv(Headers, folder.Headers);
        PreRequestScript = folder.PreRequestScript ?? string.Empty;
        PostResponseScript = folder.PostResponseScript ?? string.Empty;
        TestsScript = folder.TestsScript ?? string.Empty;
        Docs = folder.Docs ?? string.Empty;
        ApplyAuth(folder.Auth);
    }

    private NodePropertiesViewModel(Kind nodeKind)
    {
        NodeKind = nodeKind;
        // Ghost-row UX: typing into the trailing blank row spawns the next one — no
        // "+ Add" click needed. BuildSnapshot's empty-name filter keeps ghosts out of
        // the emitted .bru.
        KvAutoAppend.Wire(Variables, () => new KvEntry(), r => r.IsBlank);
        KvAutoAppend.Wire(Headers, () => new KvEntry(), r => r.IsBlank);
    }

    /// <summary>Copies domain pairs into rows and seeds the trailing ghost row.</summary>
    private static void SeedKv(ObservableCollection<KvEntry> sink, IList<KvPair> source)
    {
        foreach (var p in source) sink.Add(new KvEntry(p.Name, p.Value, p.Enabled));
        KvAutoAppend.EnsureTrailingBlank(sink, () => new KvEntry(), r => r.IsBlank);
    }

    private void ApplyAuth(AuthConfig? auth)
    {
        if (auth is null) { AuthType = "none"; return; }
        AuthType = auth.Type.ToString().ToLowerInvariant();
        AuthParametersText = string.Join("\n",
            auth.Parameters.Select(kv => $"{kv.Key}: {kv.Value}"));
    }

    [RelayCommand]
    private void AddVariable() => Variables.Add(new KvEntry(string.Empty, string.Empty));

    [RelayCommand]
    private void RemoveVariable(KvEntry? row)
    {
        if (row is not null) Variables.Remove(row);
    }

    [RelayCommand]
    private void AddHeader() => Headers.Add(new KvEntry(string.Empty, string.Empty));

    [RelayCommand]
    private void RemoveHeader(KvEntry? row)
    {
        if (row is not null) Headers.Remove(row);
    }

    [RelayCommand]
    private void Save()
    {
        var snapshot = BuildSnapshot();
        SaveRequested?.Invoke(this, new NodePropertiesSaveEventArgs(snapshot));
    }

    [RelayCommand]
    private void Cancel() => CancelRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Materializes the current editor state into the appropriate domain record.
    /// The host applies it via BruMetaEmitter + reload.</summary>
    public NodeSnapshot BuildSnapshot()
    {
        var headerList = Headers
            .Where(h => !string.IsNullOrEmpty(h.Name))
            .Select(h => new KvPair(h.Name, h.Value, h.Enabled)).ToList();
        var varList = Variables
            .Where(v => !string.IsNullOrEmpty(v.Name))
            .Select(v => new KvPair(v.Name, v.Value, v.Enabled)).ToList();
        var auth = BuildAuthConfig();

        if (NodeKind == Kind.Collection)
        {
            var presets = new RequestPresets
            {
                RequestType = string.IsNullOrEmpty(PresetRequestType) ? "http" : PresetRequestType,
                BaseUrl = PresetBaseUrl ?? string.Empty,
            };
            var collection = new Collection
            {
                Name = Name,
                Headers = headerList,
                Variables = varList,
                PreRequestScript = string.IsNullOrEmpty(PreRequestScript) ? null : PreRequestScript,
                PostResponseScript = string.IsNullOrEmpty(PostResponseScript) ? null : PostResponseScript,
                TestsScript = string.IsNullOrEmpty(TestsScript) ? null : TestsScript,
                Docs = string.IsNullOrEmpty(Docs) ? null : Docs,
                Auth = auth,
                Presets = presets.IsEmpty ? null : presets,
            };
            return new NodeSnapshot(NodeKind, collection, null);
        }
        else
        {
            var folder = new Folder
            {
                Name = Name,
                Headers = headerList,
                Variables = varList,
                PreRequestScript = string.IsNullOrEmpty(PreRequestScript) ? null : PreRequestScript,
                PostResponseScript = string.IsNullOrEmpty(PostResponseScript) ? null : PostResponseScript,
                TestsScript = string.IsNullOrEmpty(TestsScript) ? null : TestsScript,
                Docs = string.IsNullOrEmpty(Docs) ? null : Docs,
                Auth = auth,
            };
            return new NodeSnapshot(NodeKind, null, folder);
        }
    }

    private AuthConfig? BuildAuthConfig()
    {
        if (string.Equals(AuthType, "none", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(AuthType, "inherit", StringComparison.OrdinalIgnoreCase))
            return new AuthConfig { Type = Vegha.Core.Domain.AuthType.Inherit };
        var parsed = AuthType.ToLowerInvariant() switch
        {
            "apikey" => Vegha.Core.Domain.AuthType.ApiKey,
            "bearer" => Vegha.Core.Domain.AuthType.Bearer,
            "basic" => Vegha.Core.Domain.AuthType.Basic,
            "digest" => Vegha.Core.Domain.AuthType.Digest,
            "oauth1" => Vegha.Core.Domain.AuthType.OAuth1,
            "oauth2" => Vegha.Core.Domain.AuthType.OAuth2,
            "awsv4" => Vegha.Core.Domain.AuthType.AwsV4,
            "ntlm" => Vegha.Core.Domain.AuthType.Ntlm,
            "wsse" => Vegha.Core.Domain.AuthType.Wsse,
            _ => Vegha.Core.Domain.AuthType.None,
        };
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(AuthParametersText))
        {
            foreach (var line in AuthParametersText.Split('\n'))
            {
                var t = line.TrimEnd('\r').Trim();
                if (t.Length == 0 || t.StartsWith('#')) continue;
                var idx = t.IndexOf(':');
                if (idx <= 0) continue;
                parameters[t[..idx].Trim()] = t[(idx + 1)..].Trim();
            }
        }
        return new AuthConfig { Type = parsed, Parameters = parameters };
    }
}

public sealed record NodeSnapshot(NodePropertiesViewModel.Kind Kind, Collection? Collection, Folder? Folder);

public sealed class NodePropertiesSaveEventArgs : EventArgs
{
    public NodeSnapshot Snapshot { get; }
    public NodePropertiesSaveEventArgs(NodeSnapshot snapshot) { Snapshot = snapshot; }
}
