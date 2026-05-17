using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Core.Requests;
using Microsoft.Extensions.Logging;

namespace Vegha.App.ViewModels;

/// <summary>
/// Drives the WebSocket workspace: URL bar + Connect/Disconnect + outgoing message
/// box + scrollback of sent/received frames. One executor per session — the user
/// disconnects and reconnects to start fresh.
/// </summary>
public partial class WebSocketViewModel : ObservableObject
{
    private readonly ILogger<WebSocketViewModel> _logger;
    private WebSocketExecutor? _executor;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendTextCommand))]
    private string _url = "wss://echo.websocket.org";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendTextCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool _isConnecting;

    [ObservableProperty]
    private string _outgoingMessage = string.Empty;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Comma- or whitespace-separated subprotocol names (Sec-WebSocket-Protocol).</summary>
    [ObservableProperty]
    private string _subprotocols = string.Empty;

    /// <summary>Connection-time request headers, one per line as <c>Key: Value</c>. Used for
    /// auth (e.g. <c>Authorization: Bearer …</c>) and other negotiation hints.</summary>
    [ObservableProperty]
    private string _requestHeadersText = string.Empty;

    /// <summary>"text" sends the box as UTF-8, "json" pretty-prints + sends as text,
    /// "binary" parses the box as a hex string and sends a binary frame.</summary>
    [ObservableProperty]
    private string _outgoingFormat = "text";

    public IReadOnlyList<string> OutgoingFormatOptions { get; } = new[] { "text", "json", "binary" };

    /// <summary>When true, received text frames that parse as JSON are reformatted with
    /// indentation before the row is appended.</summary>
    [ObservableProperty]
    private bool _autoFormatReceivedJson = true;

    /// <summary>When true, the reader loop will attempt to reconnect after an unexpected
    /// close. Honors a small back-off so we don't hammer a broken endpoint.</summary>
    [ObservableProperty]
    private bool _reconnectOnError;

    public ObservableCollection<WebSocketMessageRow> Messages { get; } = new();

    public WebSocketViewModel(ILogger<WebSocketViewModel> logger)
    {
        _logger = logger;
    }

    private bool CanConnect() => !IsConnected && !IsConnecting && Uri.TryCreate(Url, UriKind.Absolute, out _);
    private bool CanDisconnect() => IsConnected;
    private bool CanSend() => IsConnected;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync(CancellationToken ct)
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri))
        {
            StatusMessage = "URL is not a valid absolute URI.";
            return;
        }

        _executor = new WebSocketExecutor();
        IsConnecting = true;
        StatusMessage = "Connecting…";

        try
        {
            await _executor.ConnectAsync(uri,
                headers: ParseHeaders(RequestHeadersText),
                subprotocols: ParseSubprotocols(Subprotocols),
                cancellationToken: ct);
            IsConnected = true;
            StatusMessage = $"Connected · {uri}";
            _ = ConsumeEventsAsync(_executor);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Connect failed: {ex.Message}";
            await _executor.DisposeAsync();
            _executor = null;
        }
        finally
        {
            IsConnecting = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        if (_executor is null) return;
        try { await _executor.CloseAsync("user disconnect"); }
        catch (Exception ex) { _logger.LogWarning(ex, "Close failed"); }
        finally
        {
            await _executor.DisposeAsync();
            _executor = null;
            IsConnected = false;
            StatusMessage = "Disconnected.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendTextAsync()
    {
        if (_executor is null) return;
        try
        {
            switch (OutgoingFormat)
            {
                case "binary":
                    await _executor.SendBinaryAsync(ParseHex(OutgoingMessage));
                    break;
                case "json":
                    var formatted = TryFormatJson(OutgoingMessage) ?? OutgoingMessage;
                    await _executor.SendTextAsync(formatted);
                    break;
                default:
                    await _executor.SendTextAsync(OutgoingMessage);
                    break;
            }
        }
        catch (Exception ex) { StatusMessage = $"Send failed: {ex.Message}"; }
        OutgoingMessage = string.Empty;
    }

    private static byte[] ParseHex(string hex)
    {
        var clean = (hex ?? string.Empty).Replace(" ", "").Replace("\n", "").Replace("\r", "");
        if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) clean = clean[2..];
        if (clean.Length % 2 != 0) throw new FormatException("Hex string must have an even number of characters.");
        var bytes = new byte[clean.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static string? TryFormatJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var node = JsonNode.Parse(raw);
            return node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException) { return null; }
    }

    private static IEnumerable<string> ParseSubprotocols(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;
        foreach (var token in raw.Split(new[] { ',', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            yield return token.Trim();
    }

    private static IEnumerable<KeyValuePair<string, string>> ParseHeaders(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r').Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var colon = trimmed.IndexOf(':');
            if (colon <= 0) continue;
            yield return new KeyValuePair<string, string>(
                trimmed[..colon].Trim(),
                trimmed[(colon + 1)..].Trim());
        }
    }

    [RelayCommand]
    private void Clear()
    {
        Messages.Clear();
    }

    private async Task ConsumeEventsAsync(WebSocketExecutor executor)
    {
        var hadError = false;
        try
        {
            await foreach (var msg in executor.Events.ReadAllAsync())
            {
                var row = WebSocketMessageRow.From(msg);
                if (AutoFormatReceivedJson && msg.Direction == WebSocketMessageDirection.Received && !msg.IsBinary)
                {
                    var pretty = TryFormatJson(msg.Payload);
                    if (pretty is not null) row = row with { Payload = pretty };
                }
                Messages.Add(row);
            }
        }
        catch (Exception ex)
        {
            hadError = true;
            _logger.LogWarning(ex, "WebSocket reader loop ended unexpectedly");
        }
        IsConnected = false;

        // Reconnect-on-error: only when the user opted in AND the loop ended with an error
        // (not a clean user-initiated disconnect). Small back-off so we don't spin on a
        // fully broken endpoint.
        if (hadError && ReconnectOnError && _executor is not null)
        {
            StatusMessage = "Reconnecting after error…";
            await Task.Delay(TimeSpan.FromSeconds(2));
            await _executor.DisposeAsync();
            _executor = null;
            await ConnectAsync(CancellationToken.None);
        }
    }
}

public sealed record WebSocketMessageRow(
    string DirectionLabel,
    string Payload,
    string Timestamp,
    bool IsSent,
    bool IsReceived,
    bool IsSystem)
{
    public static WebSocketMessageRow From(WebSocketMessageRecord r)
    {
        var label = r.Direction switch
        {
            WebSocketMessageDirection.Sent => "→",
            WebSocketMessageDirection.Received => "←",
            _ => "·"
        };
        return new WebSocketMessageRow(
            DirectionLabel: label,
            Payload: r.Payload,
            Timestamp: r.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff"),
            IsSent: r.Direction == WebSocketMessageDirection.Sent,
            IsReceived: r.Direction == WebSocketMessageDirection.Received,
            IsSystem: r.Direction == WebSocketMessageDirection.System);
    }
}
