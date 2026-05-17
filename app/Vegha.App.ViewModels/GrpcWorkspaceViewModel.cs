using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Integrations.Grpc;
using Microsoft.Extensions.Logging;

namespace Vegha.App.ViewModels;

/// <summary>
/// gRPC workspace VM. Connects to a server, lists services via reflection,
/// lets the user pick a fully-qualified method, sends a unary call, and
/// shows the result. Streaming + descriptor-driven message editing arrive
/// in follow-up commits — this VM is wired around the existing executors.
/// </summary>
public partial class GrpcWorkspaceViewModel : ObservableObject
{
    private readonly GrpcUnaryExecutor _unary = new();
    private readonly ILogger<GrpcWorkspaceViewModel> _logger;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ListServicesCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _address = "https://grpc.test:443";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _method = "/package.Service/Method";

    [ObservableProperty]
    private string _requestPayloadHex = string.Empty;

    /// <summary>JSON form of the request, encoded against the resolved descriptor when the
    /// user clicks Send. Populated by <see cref="LoadMethodSchemaAsync"/>.</summary>
    [ObservableProperty]
    private string _requestJson = string.Empty;

    /// <summary>JSON form of the most recent response, decoded via the descriptor.</summary>
    [ObservableProperty]
    private string _responseJson = string.Empty;

    [ObservableProperty]
    private string _selectedInputType = string.Empty;

    [ObservableProperty]
    private string _selectedOutputType = string.Empty;

    private GrpcDescriptorIndex? _descriptorIndex;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _responseStatus;

    [ObservableProperty]
    private string? _responseBytes;

    [ObservableProperty]
    private long _responseElapsedMs;

    [ObservableProperty]
    private bool _isBusy;

    public ObservableCollection<string> Services { get; } = new();

    public GrpcWorkspaceViewModel(ILogger<GrpcWorkspaceViewModel> logger)
    {
        _logger = logger;
    }

    private bool CanListServices() => !IsBusy && !string.IsNullOrWhiteSpace(Address);
    private bool CanSend() => !IsBusy && !string.IsNullOrWhiteSpace(Address) && !string.IsNullOrWhiteSpace(Method);

    [RelayCommand(CanExecute = nameof(CanListServices))]
    private async Task ListServicesAsync(CancellationToken ct)
    {
        IsBusy = true;
        StatusMessage = "Listing services…";
        Services.Clear();
        try
        {
            await using var client = new GrpcReflectionClient(Address);
            var services = await client.ListServicesAsync(ct);
            foreach (var s in services) Services.Add(s);
            StatusMessage = $"Found {services.Count} service(s).";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reflection failed");
            StatusMessage = $"Reflection failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    /// <summary>Resolves the input/output message types for the configured method via
    /// reflection and seeds <see cref="RequestJson"/> with a skeleton of the request type.
    /// Method must be in <c>/package.Service/Method</c> form.</summary>
    [RelayCommand]
    private async Task LoadMethodSchemaAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Method) || string.IsNullOrWhiteSpace(Address))
        {
            StatusMessage = "Set address + method first.";
            return;
        }
        IsBusy = true;
        StatusMessage = "Resolving descriptors…";
        try
        {
            var slash = Method.LastIndexOf('/');
            if (slash <= 0) { StatusMessage = "Method must be /package.Service/Method"; return; }
            var serviceFullName = Method[1..slash];      // strip leading '/'
            var methodName = Method[(slash + 1)..];

            await using var client = new GrpcReflectionClient(Address);
            var protos = await client.GetFileContainingSymbolAsync(serviceFullName, ct);
            if (protos.Count == 0) { StatusMessage = "Reflection returned no descriptors."; return; }

            _descriptorIndex = GrpcDescriptorIndex.FromFileDescriptorProtos(protos);
            if (!_descriptorIndex.Services.TryGetValue(serviceFullName, out var svc))
            {
                StatusMessage = $"Service {serviceFullName} not in returned descriptors.";
                return;
            }
            var method = svc.FindMethodByName(methodName);
            if (method is null) { StatusMessage = $"Method {methodName} not found on {serviceFullName}."; return; }

            SelectedInputType = method.InputType.FullName;
            SelectedOutputType = method.OutputType.FullName;
            RequestJson = _descriptorIndex.CreateJsonSkeleton(SelectedInputType);
            StatusMessage = $"Schema loaded — input {SelectedInputType}, output {SelectedOutputType}.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Schema load failed");
            StatusMessage = $"Schema load failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync(CancellationToken ct)
    {
        IsBusy = true;
        StatusMessage = "Sending…";
        ResponseStatus = null;
        ResponseBytes = null;
        ResponseJson = string.Empty;
        try
        {
            byte[] payload;
            if (_descriptorIndex is not null && !string.IsNullOrWhiteSpace(SelectedInputType)
                && !string.IsNullOrWhiteSpace(RequestJson))
            {
                // Descriptor path: encode the JSON form to wire bytes.
                payload = _descriptorIndex.EncodeJsonToWire(SelectedInputType, RequestJson);
            }
            else
            {
                // Legacy hex path — kept so the old workflow still works while the user
                // hasn't loaded a schema.
                payload = string.IsNullOrEmpty(RequestPayloadHex)
                    ? Array.Empty<byte>()
                    : ParseHex(RequestPayloadHex);
            }

            var request = new GrpcUnaryRequest(Address, Method, payload);
            var result = await _unary.ExecuteAsync(request, ct);

            ResponseStatus = result.StatusCode + (string.IsNullOrEmpty(result.StatusDetail) ? string.Empty : " · " + result.StatusDetail);
            ResponseElapsedMs = result.ElapsedMs;
            ResponseBytes = result.Bytes is null ? string.Empty : ToHex(result.Bytes);

            if (_descriptorIndex is not null && !string.IsNullOrWhiteSpace(SelectedOutputType)
                && result.Bytes is not null && result.Bytes.Length > 0)
            {
                try { ResponseJson = _descriptorIndex.DecodeWireToJson(SelectedOutputType, result.Bytes); }
                catch (Exception decodeEx) { ResponseJson = $"// decode failed: {decodeEx.Message}"; }
            }
            StatusMessage = result.ErrorMessage is null ? "OK" : $"Error: {result.ErrorMessage}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "gRPC send failed");
            StatusMessage = $"Send failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private static byte[] ParseHex(string hex)
    {
        // Accept "AA BB CC" or "aabbcc"; strip whitespace + 0x prefixes.
        var clean = hex.Replace(" ", "").Replace("\n", "").Replace("\r", "");
        if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) clean = clean[2..];
        if (clean.Length % 2 != 0) throw new FormatException("Hex string must have an even number of characters.");
        var bytes = new byte[clean.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static string ToHex(byte[] bytes) =>
        bytes.Length == 0 ? string.Empty : Convert.ToHexString(bytes);
}
