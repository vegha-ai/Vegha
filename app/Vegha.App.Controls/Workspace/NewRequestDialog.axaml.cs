using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Vegha.App.ViewModels;

namespace Vegha.App.Controls.Workspace;

/// <summary>
/// Modal "New Request" dialog. The host opens it from the Collections tree's
/// "New Request" command, then on <c>Create</c> reads <see cref="Result"/> and
/// either creates a .bru on disk (HTTP / GraphQL / WebSocket / gRPC / SOAP) or
/// runs the cURL importer (From cURL).
/// </summary>
public partial class NewRequestDialog : Window
{
    public NewRequestResult? Result { get; private set; }

    public NewRequestDialog()
    {
        InitializeComponent();

        // Show / hide URL vs cURL sections based on the selected type so the form
        // only asks for what's relevant.
        TypeFromCurl.IsCheckedChanged += (_, _) => SyncSections();
        TypeHttp.IsCheckedChanged += (_, _) => SyncSections();
        TypeGraphQL.IsCheckedChanged += (_, _) => SyncSections();
        TypeWebSocket.IsCheckedChanged += (_, _) => SyncSections();
        TypeGrpc.IsCheckedChanged += (_, _) => SyncSections();
        TypeSoap.IsCheckedChanged += (_, _) => SyncSections();
    }

    /// <summary>Seeds the form from the owning collection's presets: pre-checks the request
    /// type radio and pre-fills the URL. Applied before the dialog is shown so the user can
    /// still override any field. No-op for empty presets.</summary>
    public void ApplyPresets(string? requestType, string? baseUrl)
    {
        switch ((requestType ?? "http").ToLowerInvariant())
        {
            case "graphql":   TypeGraphQL.IsChecked = true; break;
            case "grpc":      TypeGrpc.IsChecked = true; break;
            case "websocket": TypeWebSocket.IsChecked = true; break;
            default:          TypeHttp.IsChecked = true; break;
        }
        if (!string.IsNullOrEmpty(baseUrl)) UrlBox.Text = baseUrl;
        SyncSections();
    }

    private void SyncSections()
    {
        var isCurl = TypeFromCurl.IsChecked == true;
        // URL row applies to HTTP / GraphQL / WebSocket. gRPC / SOAP / cURL hide it
        // (gRPC/SOAP infer endpoint differently; cURL provides one in the command).
        var hasUrlField = TypeHttp.IsChecked == true
            || TypeGraphQL.IsChecked == true
            || TypeWebSocket.IsChecked == true;
        UrlSection.IsVisible = hasUrlField && !isCurl;
        CurlSection.IsVisible = isCurl;
        // The name field is irrelevant for cURL (the importer derives a name from the URL),
        // but we keep it visible so the user can override.
    }

    private void OnCreate_Click(object? sender, RoutedEventArgs e)
    {
        Result = new NewRequestResult(
            Kind: ResolveKind(),
            Name: NameBox.Text?.Trim() ?? string.Empty,
            Method: ResolveMethod(),
            Url: UrlBox.Text?.Trim() ?? string.Empty,
            CurlCommand: CurlBox.Text ?? string.Empty,
            FromCurl: TypeFromCurl.IsChecked == true);
        Close(true);
    }

    private void OnCancel_Click(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close(false);
    }

    private NewRequestKind ResolveKind()
    {
        if (TypeFromCurl.IsChecked == true) return NewRequestKind.FromCurl;
        if (TypeGraphQL.IsChecked == true)  return NewRequestKind.GraphQL;
        if (TypeWebSocket.IsChecked == true) return NewRequestKind.WebSocket;
        if (TypeGrpc.IsChecked == true)      return NewRequestKind.Grpc;
        if (TypeSoap.IsChecked == true)      return NewRequestKind.Soap;
        return NewRequestKind.Http;
    }

    private string ResolveMethod()
    {
        if (MethodBox.SelectedItem is ComboBoxItem item && item.Content is string s) return s;
        return "GET";
    }
}

public sealed record NewRequestResult(
    NewRequestKind Kind,
    string Name,
    string Method,
    string Url,
    string CurlCommand,
    bool FromCurl);
