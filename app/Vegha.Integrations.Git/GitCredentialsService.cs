using System.Diagnostics;
using System.Text;

namespace Vegha.Integrations.Git;

/// <summary>Resolves Git credentials for libgit2sharp's transport when shelling out to
/// <c>git.exe</c> isn't an option. Uses <c>git credential fill</c> (which delegates to GCM /
/// the user's helper chain) when available; UI hosts can additionally subscribe a fallback
/// prompt that runs when the shell-out yields nothing.
///
/// API is intentionally synchronous: libgit2sharp's <see cref="LibGit2Sharp.CredentialsHandler"/>
/// runs inside libgit2 during fetch/push and can't await. Callers must invoke this off the UI
/// thread; the UI fallback marshals to <c>Dispatcher.UIThread.InvokeAsync(...).GetAwaiter().GetResult()</c>.</summary>
public sealed class GitCredentialsService
{
    /// <summary>Optional UI-thread prompt invoked when <c>git credential fill</c> can't satisfy
    /// the request. Implementations should show a modal dialog (Username + PAT / password)
    /// and return the entered values, or null on cancel. Marshaling to the UI thread is the
    /// implementation's responsibility.</summary>
    public Func<CredentialsRequest, CredentialsResponse?>? PromptFallback { get; set; }

    private readonly GitProcessRunner _runner;

    public GitCredentialsService(GitProcessRunner runner)
    {
        _runner = runner;
    }

    public bool TryGet(string url, string? usernameHint, out string username, out string secret)
    {
        username = string.Empty;
        secret = string.Empty;

        // Try git credential fill — this delegates to whatever helper chain the user has
        // configured (Windows: Git Credential Manager by default).
        if (_runner.IsAvailable && TryCredentialFill(url, usernameHint, out username, out secret))
            return true;

        // UI fallback.
        if (PromptFallback is { } prompt)
        {
            var response = prompt(new CredentialsRequest(url, usernameHint));
            if (response is not null)
            {
                username = response.Username;
                secret = response.Secret;
                if (response.Remember)
                    TryCredentialApprove(url, username, secret);
                return true;
            }
        }

        return false;
    }

    /// <summary>Stores credentials back through the helper chain. Best-effort.</summary>
    public void Forget(string url, string username)
    {
        if (!_runner.IsAvailable) return;
        try
        {
            var input = $"url={url}\nusername={username}\n\n";
            var psi = NewCredentialPsi("reject");
            using var p = Process.Start(psi);
            if (p is null) return;
            p.StandardInput.Write(input);
            p.StandardInput.Close();
            p.WaitForExit(3000);
        }
        catch { /* best-effort */ }
    }

    private bool TryCredentialFill(string url, string? usernameHint, out string username, out string secret)
    {
        username = string.Empty;
        secret = string.Empty;
        try
        {
            var input = new StringBuilder().Append("url=").Append(url).Append('\n');
            if (!string.IsNullOrEmpty(usernameHint))
                input.Append("username=").Append(usernameHint).Append('\n');
            input.Append('\n');

            var psi = NewCredentialPsi("fill");
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.StandardInput.Write(input.ToString());
            p.StandardInput.Close();

            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            if (p.ExitCode != 0) return false;

            foreach (var raw in output.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (string.IsNullOrEmpty(line)) break;
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                var key = line[..eq];
                var value = line[(eq + 1)..];
                if (key == "username") username = value;
                else if (key == "password") secret = value;
            }
            return !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(secret);
        }
        catch
        {
            return false;
        }
    }

    private void TryCredentialApprove(string url, string username, string secret)
    {
        if (!_runner.IsAvailable) return;
        try
        {
            var input = $"url={url}\nusername={username}\npassword={secret}\n\n";
            var psi = NewCredentialPsi("approve");
            using var p = Process.Start(psi);
            if (p is null) return;
            p.StandardInput.Write(input);
            p.StandardInput.Close();
            p.WaitForExit(3000);
        }
        catch { /* best-effort */ }
    }

    private static ProcessStartInfo NewCredentialPsi(string action) => new("git", $"credential {action}")
    {
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
}

public sealed record CredentialsRequest(string Url, string? UsernameHint);

public sealed record CredentialsResponse(string Username, string Secret, bool Remember);
