using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Core.Domain;
using Vegha.Core.Importers;
using Microsoft.Extensions.Logging;

namespace Vegha.App.ViewModels;

/// <summary>
/// Backs the unified Import wizard with four source tabs: File / URL / Git / GitHub.
/// All tabs funnel through <see cref="ImportPipeline"/> which sniffs format (Bruno
/// folder, Postman v2.1 / env, Insomnia v4 / v5, OpenAPI / Swagger, WSDL, ZIP) and
/// stages a <see cref="ImportResult"/>. Confirming the dialog invokes
/// <see cref="OnCollectionConfirmed"/> / <see cref="OnEnvironmentConfirmed"/> with the
/// staged value.
///
/// Destination is implicit: the host sets <see cref="ActiveWorkspaceCollectionsRoot"/>
/// before opening the dialog, and the import always lands at
/// <c>&lt;root&gt;/&lt;sanitized-name&gt;</c> with a <c>-2</c>/<c>-3</c> collision suffix.
/// </summary>
public partial class ImportWizardViewModel : ObservableObject
{
    private readonly ILogger<ImportWizardViewModel> _logger;

    // ---- File tab state --------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private string? _selectedPath;

    // ---- URL tab state ---------------------------------------------------------

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private bool _isFetching;

    // ---- Git tab state ---------------------------------------------------------

    [ObservableProperty]
    private string _gitUrl = string.Empty;

    [ObservableProperty]
    private string _gitBranch = string.Empty;

    /// <summary>Auth mode for Git tab: "none" / "https-pat" / "ssh-key". Bound to the
    /// dialog's auth picker; defaults to none for public repos.</summary>
    [ObservableProperty]
    private string _gitAuthMode = "none";

    [ObservableProperty]
    private string _gitUsername = string.Empty;

    [ObservableProperty]
    private string _gitPassword = string.Empty;

    [ObservableProperty]
    private string _gitSshKeyPath = string.Empty;

    // ---- GitHub tab state ------------------------------------------------------

    [ObservableProperty]
    private string _gitHubOwner = string.Empty;

    [ObservableProperty]
    private string _gitHubRepo = string.Empty;

    [ObservableProperty]
    private string _gitHubBranch = string.Empty;

    [ObservableProperty]
    private string _gitHubToken = string.Empty;

    public List<string> GitHubBranches { get; } = new();

    // ---- Shared state ----------------------------------------------------------

    [ObservableProperty]
    private string _detectedFormat = "Pick a source to begin";

    [ObservableProperty]
    private string _previewSummary = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>When true (default), Postman v2.1 script blocks are translated from
    /// <c>pm.*</c> calls to their <c>bru.*</c> / <c>req.*</c> / <c>res.*</c> equivalents
    /// at import time. Without this, imported scripts throw <c>ReferenceError: pm is not
    /// defined</c> on first Send. UI wires this to a checkbox visible only when the
    /// detected format is Postman.</summary>
    [ObservableProperty]
    private bool _translatePostmanScripts = true;

    /// <summary>True only when the staged batch contains at least one Postman v2.1
    /// collection. The XAML uses this to show/hide the translation checkbox so non-Postman
    /// imports don't see an irrelevant option.</summary>
    [ObservableProperty]
    private bool _isPostmanFormatDetected;

    /// <summary>Diagnostics from the most recent translation pass — populated when
    /// <see cref="TranslatePostmanScripts"/> is on and one or more requests had script
    /// patterns the translator couldn't rewrite. Surfaced in the preview pane so the
    /// user knows which scripts may still fail at run time.</summary>
    public ObservableCollection<TranslationDiagnostic> TranslationDiagnostics { get; } = new();

    /// <summary>Convenience for the UI binding — true when at least one diagnostic exists.</summary>
    public bool HasTranslationDiagnostics => TranslationDiagnostics.Count > 0;

    /// <summary>Builds the PostmanImportOptions snapshot used by every staging path
    /// (file / URL / Git / GitHub). Captures the current checkbox state and routes
    /// translator diagnostics into <see cref="TranslationDiagnostics"/>.</summary>
    private PostmanImportOptions BuildPostmanOptions() => new(
        TranslateScripts: TranslatePostmanScripts,
        OnDiagnostic: d => TranslationDiagnostics.Add(d));

    /// <summary>Set by the host before opening the dialog. The destination of every
    /// import is <c>Path.Combine(ActiveWorkspaceCollectionsRoot, &lt;name&gt;)</c> with
    /// a collision suffix when needed.</summary>
    public string ActiveWorkspaceCollectionsRoot { get; set; } = string.Empty;

    /// <summary>When false (set by the "Import collection" entry point), Postman environment
    /// files are rejected with a redirect message pointing at the Environments panel's own
    /// Import button. Default true keeps the wizard generic for any future "Import anything"
    /// entry point.</summary>
    public bool AcceptEnvironments { get; set; } = true;

    /// <summary>Batch of staged import results — one entry per file when the user picks /
    /// drops multiple files, exactly one for URL/Git/GitHub sources. Each entry is invoked
    /// through <see cref="OnCollectionConfirmed"/> / <see cref="OnEnvironmentConfirmed"/>
    /// in order when the user clicks Import.</summary>
    private readonly List<ImportResult> _staged = new();

    /// <summary>Wired by the host: called once per imported collection. The second arg is
    /// the staged folder path (when the import is a Bruno tree / extracted ZIP / Git clone)
    /// so the host can fold-copy rather than re-serialize; null for parsed-only sources
    /// (Postman / Insomnia / OpenAPI JSON).</summary>
    public Action<Collection, string?>? OnCollectionConfirmed { get; set; }

    /// <summary>Wired by the host: called once per imported environment.</summary>
    public Action<Vegha.Core.Domain.Environment>? OnEnvironmentConfirmed { get; set; }

    public ImportWizardViewModel(ILogger<ImportWizardViewModel> logger)
    {
        _logger = logger;
    }

    // -------------- File tab routing --------------

    /// <summary>File tab: a path was set via direct binding (rare — typically the user
    /// picks or drops files, which now route through <see cref="StageFiles"/>).
    /// The guard skips re-staging when <see cref="StageFiles"/> itself updated the displayed
    /// path so the staged batch isn't rebuilt from a single file.</summary>
    partial void OnSelectedPathChanged(string? value)
    {
        if (_suppressSelectedPathStaging) return;
        if (string.IsNullOrEmpty(value)) { ResetState(); return; }
        StageFiles(new[] { value });
    }

    /// <summary>Stage one or many files. Each file is detected independently; the batch is
    /// imported in one go when the user clicks Import. Successes accumulate; per-file
    /// failures are reported in the summary so the user can still confirm the recognized
    /// subset rather than having the whole drop rejected.</summary>
    public void StageFiles(IEnumerable<string> paths)
    {
        var list = paths.Where(p => !string.IsNullOrEmpty(p)).ToList();
        ResetState();
        if (list.Count == 0) return;

        var failures = new List<string>();
        foreach (var p in list)
        {
            try
            {
                _staged.Add(ImportPipeline.DetectAndImportPath(p, BuildPostmanOptions()));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Import detection failed for {Path}", p);
                failures.Add($"{Path.GetFileName(p)}: {ex.Message}");
            }
        }

        // Display the last-picked path so the existing TextBlock binding stays meaningful
        // even when more than one file is staged. The number-of-files summary lives in
        // PreviewSummary, computed below.
        _suppressSelectedPathStaging = true;
        try { SelectedPath = list[^1]; }
        finally { _suppressSelectedPathStaging = false; }

        UpdatePreview(failures);
    }

    /// <summary>Guards <see cref="OnSelectedPathChanged"/> from re-entering the staging
    /// path when <see cref="StageFiles"/> is updating the displayed path.</summary>
    private bool _suppressSelectedPathStaging;

    // -------------- URL tab routing --------------

    [RelayCommand]
    private async Task FetchUrlAsync()
    {
        ResetState();
        if (string.IsNullOrWhiteSpace(Url))
        {
            ErrorMessage = "Enter a URL to fetch.";
            return;
        }
        IsFetching = true;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.Add("User-Agent", "Vegha");
            var bytes = await http.GetByteArrayAsync(Url);
            Stage(ImportPipeline.DetectAndImport(bytes, hintFilename: Path.GetFileName(new Uri(Url).LocalPath), postmanOptions: BuildPostmanOptions()));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "URL fetch failed for {Url}", Url);
            ErrorMessage = $"Fetch failed: {ex.Message}";
            DetectedFormat = "Unrecognized format";
        }
        finally { IsFetching = false; }
    }

    // -------------- Git tab routing --------------

    [RelayCommand]
    private async Task CloneGitAsync()
    {
        ResetState();
        if (string.IsNullOrWhiteSpace(GitUrl))
        {
            ErrorMessage = "Enter a Git URL.";
            return;
        }
        IsFetching = true;
        try
        {
            await Task.Run(() => CloneAndStageImpl(
                GitUrl,
                string.IsNullOrWhiteSpace(GitBranch) ? null : GitBranch,
                BuildGitCredentials()));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Git clone failed for {Url}", GitUrl);
            ErrorMessage = $"Clone failed: {ex.Message}";
            DetectedFormat = "Unrecognized format";
        }
        finally { IsFetching = false; }
    }

    // -------------- GitHub tab routing --------------

    [RelayCommand]
    private async Task LoadGithubBranchesAsync()
    {
        ResetState();
        GitHubBranches.Clear();
        OnPropertyChanged(nameof(GitHubBranches));
        if (string.IsNullOrWhiteSpace(GitHubOwner) || string.IsNullOrWhiteSpace(GitHubRepo))
        {
            ErrorMessage = "Enter owner and repo.";
            return;
        }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Add("User-Agent", "Vegha");
            if (!string.IsNullOrEmpty(GitHubToken))
                http.DefaultRequestHeaders.Add("Authorization", "Bearer " + GitHubToken);
            var url = $"https://api.github.com/repos/{Uri.EscapeDataString(GitHubOwner)}/{Uri.EscapeDataString(GitHubRepo)}/branches";
            var json = await http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            foreach (var br in doc.RootElement.EnumerateArray())
            {
                if (br.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    GitHubBranches.Add(n.GetString()!);
            }
            OnPropertyChanged(nameof(GitHubBranches));
            if (GitHubBranches.Count > 0 && string.IsNullOrEmpty(GitHubBranch))
                GitHubBranch = GitHubBranches[0];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GitHub branch listing failed for {Owner}/{Repo}", GitHubOwner, GitHubRepo);
            ErrorMessage = $"Branch lookup failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CloneGithubAsync()
    {
        ResetState();
        if (string.IsNullOrWhiteSpace(GitHubOwner) || string.IsNullOrWhiteSpace(GitHubRepo))
        {
            ErrorMessage = "Enter owner and repo.";
            return;
        }
        var url = $"https://github.com/{GitHubOwner}/{GitHubRepo}.git";
        IsFetching = true;
        try
        {
            var creds = string.IsNullOrEmpty(GitHubToken)
                ? null
                : new GitCloneCredentials("https-pat", "x-access-token", GitHubToken, null);
            await Task.Run(() => CloneAndStageImpl(
                url,
                string.IsNullOrEmpty(GitHubBranch) ? null : GitHubBranch,
                creds));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GitHub clone failed for {Owner}/{Repo}", GitHubOwner, GitHubRepo);
            ErrorMessage = $"Clone failed: {ex.Message}";
            DetectedFormat = "Unrecognized format";
        }
        finally { IsFetching = false; }
    }

    private void CloneAndStageImpl(string url, string? branch, GitCloneCredentials? creds)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "Vegha-clone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        Vegha.Integrations.Git.GitCloneService.Clone(url, tempRoot, branch, ToServiceCreds(creds));

        var result = ImportPipeline.ScanDirectory(tempRoot, BuildPostmanOptions());
        // Override the auto-derived name with the repo name when we have one — the user
        // expects the new collection to be named after the repo, not the wrapper folder
        // ScanDirectory descended into.
        if (result.Success && result.Collection is { } col)
        {
            var renamed = col with { Name = DeriveRepoName(url) };
            result = result with { Collection = renamed };
        }
        Stage(result);
    }

    private GitCloneCredentials? BuildGitCredentials() => GitAuthMode switch
    {
        "https-pat" => new GitCloneCredentials("https-pat",
            string.IsNullOrEmpty(GitUsername) ? "x-access-token" : GitUsername,
            GitPassword, null),
        "ssh-key" => new GitCloneCredentials("ssh-key", null, null, GitSshKeyPath),
        _ => null,
    };

    private static Vegha.Integrations.Git.GitCloneCredentials? ToServiceCreds(GitCloneCredentials? c)
    {
        if (c is null) return null;
        return c.Mode switch
        {
            "https-pat" => Vegha.Integrations.Git.GitCloneCredentials.HttpsPat(c.Username ?? "x-access-token", c.Password ?? ""),
            "ssh-key"   => Vegha.Integrations.Git.GitCloneCredentials.SshKey(c.SshKeyPath ?? ""),
            _ => null,
        };
    }

    private static string DeriveRepoName(string url)
    {
        try
        {
            var trimmed = url.TrimEnd('/');
            if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed[..^4];
            var slash = trimmed.LastIndexOf('/');
            return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        }
        catch { return "imported"; }
    }

    // -------------- Confirm / staging --------------

    /// <summary>Single-source stage (URL / Git / GitHub). Clears any prior batch.</summary>
    private void Stage(ImportResult result)
    {
        _staged.Clear();
        _staged.Add(result);
        UpdatePreview(failures: null);
    }

    /// <summary>Recomputes the displayed format + summary based on the current batch.
    /// Per-file detect failures from <see cref="StageFiles"/> appear in the summary so the
    /// user can see what was skipped without losing the recognized files.</summary>
    private void UpdatePreview(IReadOnlyList<string>? failures)
    {
        var successes = _staged.Where(r => r.Success).ToList();
        var failed = _staged.Count - successes.Count;
        var total = _staged.Count + (failures?.Count ?? 0);

        // Surface Postman-detection so the wizard XAML can show/hide the translation checkbox.
        IsPostmanFormatDetected = successes.Any(r =>
            r.FormatLabel?.StartsWith("Postman v2", StringComparison.OrdinalIgnoreCase) == true);
        OnPropertyChanged(nameof(HasTranslationDiagnostics));

        if (_staged.Count == 0 && (failures is null || failures.Count == 0))
        {
            ResetState();
            return;
        }

        // Collection-only import context — if every recognized file is a Postman env
        // (Collection is null), redirect the user to the Environments panel rather than
        // silently importing into the wrong place.
        if (!AcceptEnvironments && successes.Count > 0 &&
            successes.All(r => r.Collection is null && r.Environment is not null))
        {
            DetectedFormat = "Unrecognized format";
            PreviewSummary = string.Empty;
            ErrorMessage = "This is a Postman environment file, not a collection. " +
                           "To import an environment, use the Import button in the Environments panel (left sidebar).";
            ImportCommand.NotifyCanExecuteChanged();
            return;
        }

        if (total == 1 && successes.Count == 1)
        {
            // Single recognized file — preserve the original single-file UX.
            DetectedFormat = successes[0].FormatLabel;
            PreviewSummary = successes[0].Summary;
            ErrorMessage = null;
        }
        else if (successes.Count == 0)
        {
            DetectedFormat = "Unrecognized format";
            PreviewSummary = string.Empty;
            ErrorMessage = failures is { Count: > 0 }
                ? string.Join("\n", failures)
                : (_staged.FirstOrDefault()?.Error ?? "Unrecognized format.");
        }
        else
        {
            // Multi-file batch.
            var colCount = successes.Count(r => r.Collection is not null);
            var envCount = successes.Count(r => r.Environment is not null);
            DetectedFormat = $"{successes.Count} of {total} file(s) recognized";
            var parts = new List<string>();
            if (colCount > 0) parts.Add($"{colCount} collection{(colCount == 1 ? "" : "s")}");
            if (envCount > 0) parts.Add($"{envCount} environment{(envCount == 1 ? "" : "s")}");
            var skipped = failed + (failures?.Count ?? 0);
            if (skipped > 0) parts.Add($"{skipped} skipped");
            PreviewSummary = string.Join(", ", parts);
            ErrorMessage = failures is { Count: > 0 } ? string.Join("\n", failures) : null;
        }

        ImportCommand.NotifyCanExecuteChanged();
    }

    private void ResetState()
    {
        ErrorMessage = null;
        _staged.Clear();
        TranslationDiagnostics.Clear();
        OnPropertyChanged(nameof(HasTranslationDiagnostics));
        IsPostmanFormatDetected = false;
        DetectedFormat = "Pick a source to begin";
        PreviewSummary = string.Empty;
        ImportCommand.NotifyCanExecuteChanged();
    }

    private bool CanImport() =>
        _staged.Any(r => r.Success && (AcceptEnvironments || r.Collection is not null));

    [RelayCommand(CanExecute = nameof(CanImport))]
    private void Import()
    {
        foreach (var result in _staged.Where(r => r.Success))
        {
            if (result.Collection is not null)
                OnCollectionConfirmed?.Invoke(result.Collection, result.FolderPath);
            if (result.Environment is not null && AcceptEnvironments)
                OnEnvironmentConfirmed?.Invoke(result.Environment);
        }
    }

    public static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string((name ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(s) ? "imported" : s;
    }
}

/// <summary>Local DTO that crosses the VM/integration boundary. Mirrors
/// <c>Vegha.Integrations.Git.GitCloneCredentials</c> so the VM can build credentials
/// without taking a project reference change in this file's compilation unit.</summary>
public sealed record GitCloneCredentials(string Mode, string? Username, string? Password, string? SshKeyPath);
