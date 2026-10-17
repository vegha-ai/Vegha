namespace Vegha.Integrations.Git;

/// <summary>Per-invocation environment for a <c>git</c> process. Config values are injected via
/// <c>GIT_CONFIG_COUNT</c> / <c>GIT_CONFIG_KEY_n</c> / <c>GIT_CONFIG_VALUE_n</c> (git ≥ 2.31)
/// rather than <c>-c key=value</c> arguments, so tokens and proxy passwords never appear in
/// the process list and never leak into the user's global gitconfig.</summary>
public sealed class GitEnvironment
{
    private readonly List<KeyValuePair<string, string>> _config = new();
    private readonly Dictionary<string, string> _vars = new(StringComparer.Ordinal);

    /// <summary>Config entries in the order they will be applied. Repeating a key appends a
    /// value (multi-valued keys such as <c>credential.helper</c>); an empty value resets the
    /// list, exactly like git config files.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Config => _config;

    public IReadOnlyDictionary<string, string> Variables => _vars;

    public GitEnvironment AddConfig(string key, string value)
    {
        _config.Add(new(key, value));
        return this;
    }

    public GitEnvironment Set(string name, string value)
    {
        _vars[name] = value;
        return this;
    }

    /// <summary>Merges another environment's entries after this one's (other wins on vars).</summary>
    public GitEnvironment Merge(GitEnvironment? other)
    {
        if (other is null) return this;
        _config.AddRange(other._config);
        foreach (var kv in other._vars) _vars[kv.Key] = kv.Value;
        return this;
    }

    /// <summary>Writes the baseline + this environment into <paramref name="target"/>.</summary>
    public void ApplyTo(IDictionary<string, string?> target)
    {
        // Baseline: never block on a tty prompt (a GUI app has none), keep messages in English
        // so GitErrorClassifier can parse them, and stream progress immediately.
        target["GIT_TERMINAL_PROMPT"] = "0";
        target["LC_ALL"] = "C";
        target["LANG"] = "C";
        target["GIT_PROGRESS_DELAY"] = "0";
        // GCM may still show its own GUI (browser / WAM / device code) — that's how SSO + MFA work.
        target.TryAdd("GCM_INTERACTIVE", "auto");

        foreach (var kv in _vars) target[kv.Key] = kv.Value;

        if (_config.Count > 0)
        {
            // Respect any GIT_CONFIG_COUNT already in the parent env by appending after it.
            var start = 0;
            if (target.TryGetValue("GIT_CONFIG_COUNT", out var existing) && int.TryParse(existing, out var n)) start = n;
            for (var i = 0; i < _config.Count; i++)
            {
                target[$"GIT_CONFIG_KEY_{start + i}"] = _config[i].Key;
                target[$"GIT_CONFIG_VALUE_{start + i}"] = _config[i].Value;
            }
            target["GIT_CONFIG_COUNT"] = (start + _config.Count).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
