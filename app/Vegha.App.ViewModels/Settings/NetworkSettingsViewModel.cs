using CommunityToolkit.Mvvm.ComponentModel;
using Vegha.Core.Persistence;

namespace Vegha.App.ViewModels.Settings;

/// <summary>Network page VM. Proxy mode + protocol + host/port/auth, SSL session caching,
/// and the existing custom-CA list. Passwords are kept in cleartext in the VM and
/// re-encrypted via <see cref="ProxyCredentialProtector"/> on Save.</summary>
public partial class NetworkSettingsViewModel : SettingsPageBase
{
    public override string Id => "network";
    public override string Title => "Network";
    public override string IconKey => "Globe";

    public IReadOnlyList<string> ProxyModes { get; } = new[] { "off", "on", "system", "pac" };
    public IReadOnlyList<string> ProxyProtocols { get; } = new[] { "http", "https", "socks4", "socks5" };

    [ObservableProperty] private string _proxyMode = "off";
    [ObservableProperty] private string _proxyProtocol = "http";
    [ObservableProperty] private string _proxyHost = "";
    [ObservableProperty] private int _proxyPort = 0;
    [ObservableProperty] private bool _proxyAuthEnabled;
    [ObservableProperty] private string _proxyUsername = "";
    [ObservableProperty] private string _proxyPassword = "";
    [ObservableProperty] private string _proxyBypass = "";
    [ObservableProperty] private string? _proxyPacUrl;

    [ObservableProperty] private bool _cacheSslSessions = true;

    /// <summary>Multi-line text where each non-empty line is either an absolute path to a
    /// PEM cert file or an inline <c>-----BEGIN CERTIFICATE-----</c> block.</summary>
    [ObservableProperty] private string _customTrustCAsText = string.Empty;

    [ObservableProperty] private int _requestTimeoutSeconds = 100;

    public override void ReadFrom(AppSettings s)
    {
        ProxyMode = s.ProxyMode;
        ProxyProtocol = s.ProxyProtocol;
        ProxyHost = s.ProxyHost;
        ProxyPort = s.ProxyPort;
        ProxyAuthEnabled = s.ProxyAuthEnabled;
        ProxyUsername = s.ProxyUsername;
        ProxyPassword = ProxyCredentialProtector.Unprotect(s.ProxyPasswordEncrypted);
        ProxyBypass = s.ProxyBypass;
        ProxyPacUrl = s.ProxyPacUrl;
        CacheSslSessions = s.CacheSslSessions;
        CustomTrustCAsText = s.CustomTrustCAs is null ? string.Empty : string.Join("\n", s.CustomTrustCAs);
        RequestTimeoutSeconds = s.RequestTimeoutSeconds;
    }

    public override AppSettings WriteTo(AppSettings e)
    {
        var trust = ParseTrustList(CustomTrustCAsText);
        return e with
        {
            ProxyMode = ProxyMode,
            ProxyProtocol = ProxyProtocol,
            ProxyHost = ProxyHost ?? "",
            ProxyPort = ProxyPort < 0 ? 0 : ProxyPort,
            ProxyAuthEnabled = ProxyAuthEnabled,
            ProxyUsername = ProxyUsername ?? "",
            ProxyPasswordEncrypted = ProxyCredentialProtector.Protect(ProxyPassword),
            ProxyBypass = ProxyBypass ?? "",
            ProxyPacUrl = string.IsNullOrWhiteSpace(ProxyPacUrl) ? null : ProxyPacUrl,
            CacheSslSessions = CacheSslSessions,
            CustomTrustCAs = trust.Count == 0 ? null : trust,
            RequestTimeoutSeconds = Math.Clamp(RequestTimeoutSeconds, 1, 3600),
        };
    }

    /// <summary>Splits the multi-line trust list into individual entries. Each non-empty
    /// non-comment line is preserved verbatim — file paths and inline PEM blocks both work.</summary>
    public static IReadOnlyList<string> ParseTrustList(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        var entries = new List<string>();
        var lines = raw.Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i].TrimEnd('\r').Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#')) { i++; continue; }

            if (line.StartsWith("-----BEGIN CERTIFICATE-----"))
            {
                var sb = new System.Text.StringBuilder();
                while (i < lines.Length)
                {
                    var l = lines[i].TrimEnd('\r');
                    sb.AppendLine(l);
                    if (l.Trim() == "-----END CERTIFICATE-----") { i++; break; }
                    i++;
                }
                entries.Add(sb.ToString().TrimEnd());
                continue;
            }
            entries.Add(line);
            i++;
        }
        return entries;
    }
}
