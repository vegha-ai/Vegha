using Vegha.Core.History;
using Vegha.Core.Persistence;
using Vegha.Core.Requests;

namespace Vegha.Core.Engine;

/// <summary>Pushes the user's <see cref="AppSettings"/> into the live send services. Every
/// host reads the same <c>settings.json</c>, so the desktop app and the VS Code sidecar send
/// through the same proxy, CAs, timeouts and history retention policy.</summary>
public static class EngineSettings
{
    public static void Apply(AppSettings s, HttpExecutor? executor, HistoryStore? history)
    {
        // HTTP executor: proxy, SSL session caching, request timeout, max body size, custom CAs.
        if (executor is not null)
        {
            executor.UpdateTrustedCAs(CertificateLoader.Parse(s.CustomTrustCAs));
            executor.UpdateProxy(ProxyResolver.Build(s));
            executor.UpdateCacheSslSessions(s.CacheSslSessions);
            executor.UpdateTimeout(TimeSpan.FromSeconds(Math.Max(1, s.RequestTimeoutSeconds)));
            executor.UpdateMaxBodyBytes((long)Math.Max(1, s.MaxBodySizeMb) * 1024 * 1024);
        }

        // History store: persistence enable + preview cap + retention policy.
        if (history is not null)
        {
            history.Enabled = s.SaveResponsesToHistory;
            // PreviewMaxChars caps how much of the response we persist as the body preview
            // column. Multiply by 1024 (rough chars per KB for UTF-8 prose) and clamp to a
            // reasonable upper bound so a 2 GB body-cap doesn't try to write a 2 GB row.
            history.MaxPreviewChars = Math.Min(int.MaxValue, Math.Max(1, s.MaxBodySizeMb) * 1024 * 1024);
            history.MaxRetained = Math.Max(1, s.HistoryRetentionMaxEntries);
            history.MaxAge = s.HistoryRetentionDays > 0
                ? TimeSpan.FromDays(s.HistoryRetentionDays)
                : TimeSpan.Zero;
        }
    }
}
