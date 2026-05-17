using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Core.Requests;
using Vegha.Integrations.Wsdl;
using Microsoft.Extensions.Logging;

namespace Vegha.App.ViewModels;

/// <summary>
/// SOAP workspace VM. Loads a WSDL (URL or pasted text), populates an operation
/// dropdown, auto-fills the endpoint URL + SOAPAction header from the parsed
/// document, lets the user edit the inner body XML, then wraps it via
/// <see cref="SoapEnvelopeBuilder"/> and sends through the shared
/// <see cref="HttpExecutor"/>. WSSE / mTLS auth come from the inheritance chain
/// at execution time (handled by the shared executor + HttpRequestOptions).
/// </summary>
public partial class SoapWorkspaceViewModel : ObservableObject
{
    private readonly HttpExecutor _executor;
    private readonly ILogger<SoapWorkspaceViewModel> _logger;

    // Last successfully loaded WSDL XML (raw text). Cached so the JSON-paste-to-XML
    // conversion can reuse the parsed schema tree without re-fetching.
    private string? _wsdlXml;
    private JsonToWsdlBodyConverter? _jsonConverter;

    public SoapWorkspaceViewModel(HttpExecutor executor, ILogger<SoapWorkspaceViewModel> logger)
    {
        _executor = executor;
        _logger = logger;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadWsdlCommand))]
    private string _wsdlSource = string.Empty;

    [ObservableProperty]
    private string _endpointUrl = string.Empty;

    [ObservableProperty]
    private string _soapAction = string.Empty;

    [ObservableProperty]
    private string _bodyXml = string.Empty;

    [ObservableProperty]
    private string _responseXml = string.Empty;

    [ObservableProperty]
    private string _responseBodyOnly = string.Empty;

    [ObservableProperty]
    private int _responseStatusCode;

    [ObservableProperty]
    private long _responseElapsedMs;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _useSoap12;

    public ObservableCollection<WsdlOperation> Operations { get; } = new();

    [ObservableProperty]
    private WsdlOperation? _selectedOperation;

    partial void OnSelectedOperationChanged(WsdlOperation? value)
    {
        if (value is null) return;
        SoapAction = value.SoapAction;
        // Seed a stub body when the user picks an operation so they have something to
        // edit. The stub uses the operation's input message-element local name.
        if (string.IsNullOrWhiteSpace(BodyXml))
        {
            var localName = value.InputMessage.Contains(':')
                ? value.InputMessage[(value.InputMessage.IndexOf(':') + 1)..]
                : value.InputMessage;
            BodyXml = $"<{localName} xmlns=\"http://example.org/\">\n  <!-- TODO: parameters -->\n</{localName}>";
        }
    }

    private bool CanLoadWsdl() => !IsBusy && !string.IsNullOrWhiteSpace(WsdlSource);

    [RelayCommand(CanExecute = nameof(CanLoadWsdl))]
    private async Task LoadWsdlAsync(CancellationToken ct)
    {
        IsBusy = true;
        StatusMessage = "Loading WSDL…";
        Operations.Clear();
        SelectedOperation = null;
        try
        {
            var xml = WsdlSource.TrimStart().StartsWith('<')
                ? WsdlSource
                : await FetchWsdlAsync(WsdlSource, ct);

            var doc = WsdlParser.Parse(xml);
            EndpointUrl = doc.EndpointUrl;
            foreach (var op in doc.Operations) Operations.Add(op);
            SelectedOperation = Operations.FirstOrDefault();
            _wsdlXml = xml;
            _jsonConverter = null; // rebuilt lazily on next conversion
            StatusMessage = $"Loaded {doc.ServiceName} ({doc.Operations.Count} operations)";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WSDL parse failed");
            StatusMessage = $"WSDL load failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SendAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(EndpointUrl) || string.IsNullOrWhiteSpace(BodyXml))
        {
            StatusMessage = "Set endpoint URL + body first.";
            return;
        }
        IsBusy = true;
        StatusMessage = "Sending…";
        ResponseXml = string.Empty;
        ResponseBodyOnly = string.Empty;
        try
        {
            var version = UseSoap12 ? SoapEnvelopeBuilder.Version.Soap12 : SoapEnvelopeBuilder.Version.Soap11;
            var built = SoapEnvelopeBuilder.Build(BodyXml, SoapAction, version);

            var request = new HttpExecutionRequest(
                Method: HttpMethod.Post,
                Url: new Uri(EndpointUrl),
                Headers: built.Headers,
                Body: built.Body,
                ContentType: built.ContentType);

            var result = await _executor.ExecuteAsync(request, ct);
            ResponseStatusCode = result.StatusCode;
            ResponseElapsedMs = result.ElapsedMilliseconds;
            ResponseXml = result.Body ?? string.Empty;
            ResponseBodyOnly = SoapEnvelopeBuilder.ExtractBodyContents(ResponseXml);
            StatusMessage = $"{result.StatusCode} · {result.ElapsedMilliseconds} ms";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SOAP send failed");
            StatusMessage = $"Send failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    /// <summary>Attempts to convert a JSON document into the inner SOAP body XML for the
    /// currently selected operation, using namespaces drawn from the loaded WSDL's XSD
    /// schemas. Returns false (with <paramref name="xml"/> empty) if no WSDL has been
    /// loaded, no operation is selected, or the JSON can't be parsed/converted.</summary>
    public bool TryConvertJsonToBody(string json, out string xml)
    {
        xml = string.Empty;
        if (string.IsNullOrWhiteSpace(_wsdlXml)) return false;
        if (SelectedOperation is null) return false;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            _jsonConverter ??= new JsonToWsdlBodyConverter(_wsdlXml);
            var converted = _jsonConverter.Convert(SelectedOperation.Name, json);
            if (string.IsNullOrEmpty(converted)) return false;
            xml = converted;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JSON→XML conversion failed");
            return false;
        }
    }

    private static async Task<string> FetchWsdlAsync(string url, CancellationToken ct)
    {
        if (File.Exists(url)) return await File.ReadAllTextAsync(url, ct);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        return await http.GetStringAsync(url, ct);
    }
}
