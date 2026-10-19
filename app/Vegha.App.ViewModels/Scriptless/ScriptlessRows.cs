using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Core.Domain;
using Vegha.Core.Scripting.Testing;

namespace Vegha.App.ViewModels.Scriptless;

/// <summary>A choice in the capture / assertion "Source" dropdown.</summary>
public sealed record SourceOption(ResponseSource Source, string Label, string PathHint, bool TakesPath)
{
    public override string ToString() => Label;
}

/// <summary>A choice in the capture "Scope" dropdown.</summary>
public sealed record ScopeOption(CaptureScope Scope, string Label, string Description)
{
    public override string ToString() => Label;
}

/// <summary>A choice in the linked-request "Run when" dropdown.</summary>
public sealed record ChainModeOption(ChainRunMode Mode, string Label, string ArgumentHint, bool TakesArgument)
{
    public override string ToString() => Label;
}

/// <summary>Dropdown catalogs shared by the scriptless editors.</summary>
public static class ScriptlessOptions
{
    public static readonly IReadOnlyList<SourceOption> Sources = new SourceOption[]
    {
        new(ResponseSource.Body, "JSON body", "data.items[0].id", true),
        new(ResponseSource.Header, "Header", "Content-Type", true),
        new(ResponseSource.Cookie, "Cookie", "session_id", true),
        new(ResponseSource.Status, "Status code", "| optional filters", false),
        new(ResponseSource.ResponseTime, "Response time (ms)", "| optional filters", false),
        new(ResponseSource.BodyText, "Body text", "| optional filters, e.g. | length", false),
        new(ResponseSource.JsonPath, "JSONPath", "$..items[?@.active].id", true),
        new(ResponseSource.XPath, "XPath (XML)", "//token/text()", true),
        new(ResponseSource.Regex, "Regex on body", "token=([a-z0-9]+)", true),
        new(ResponseSource.Literal, "Fixed value", "any text", true),
        new(ResponseSource.Script, "JavaScript", "res.body.items.find(i => i.main).id", true),
    };

    public static readonly IReadOnlyList<ScopeOption> Scopes = new ScopeOption[]
    {
        new(CaptureScope.Runtime, "Runtime", "This session only — never written to disk"),
        new(CaptureScope.Environment, "Environment", "The active environment"),
        new(CaptureScope.Global, "Global", "The global environment"),
    };

    public static readonly IReadOnlyList<AssertionOperator> Operators = AssertionOperators.All;

    public static readonly IReadOnlyList<ChainModeOption> ChainModes = new ChainModeOption[]
    {
        new(ChainRunMode.Always, "Run always", string.Empty, false),
        new(ChainRunMode.WhenEmpty, "Only when a variable is empty", "token", true),
        new(ChainRunMode.WhenStale, "Only when the last run is older than…", "10m", true),
        new(ChainRunMode.When, "Only when a variable value matches…", "{{role}} = admin", true),
    };

    public static SourceOption SourceFor(ResponseSource s) => Sources.First(o => o.Source == s);
    public static ScopeOption ScopeFor(CaptureScope s) => Scopes.First(o => o.Scope == s);
    public static ChainModeOption ChainModeFor(ChainRunMode m) => ChainModes.First(o => o.Mode == m);
}

/// <summary>How a row's live preview / last result should be styled.</summary>
public enum RowResultKind
{
    None,
    Ok,
    Missing,
    Error,
}

/// <summary>Base for scriptless rows: <see cref="IsDataProperty"/> separates user edits (which
/// dirty the request) from evaluation feedback (previews, pass/fail chips), which must not.</summary>
public abstract partial class ScriptlessRowBase : ObservableObject
{
    [ObservableProperty] private bool _enabled = true;

    /// <summary>Preview / result text shown at the end of the row ("\"abc123\"", "expected 200, got 404").</summary>
    [ObservableProperty] private string? _resultText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOk), nameof(IsMissing), nameof(IsError), nameof(HasResult))]
    private RowResultKind _resultKind;

    public bool IsOk => ResultKind == RowResultKind.Ok;
    public bool IsMissing => ResultKind == RowResultKind.Missing;
    public bool IsError => ResultKind == RowResultKind.Error;
    public bool HasResult => ResultKind != RowResultKind.None;

    public abstract bool IsBlank { get; }

    /// <summary>Path-box autocomplete items (JSON paths / header names from the last response),
    /// refreshed by the editor when the source or the response changes.</summary>
    [ObservableProperty] private IReadOnlyList<string> _pathSuggestions = Array.Empty<string>();

    public void SetResult(RowResultKind kind, string? text)
    {
        ResultKind = kind;
        ResultText = text;
    }

    public void ClearResult() => SetResult(RowResultKind.None, null);

    /// <summary>True for properties that are part of the saved request.</summary>
    public virtual bool IsDataProperty(string? propertyName) =>
        propertyName is not (nameof(ResultText) or nameof(ResultKind) or nameof(IsOk) or nameof(IsMissing)
            or nameof(IsError) or nameof(HasResult) or nameof(IsBlank) or nameof(PathSuggestions));
}

/// <summary>One "set variable from response" row (Capture section).</summary>
public sealed partial class CaptureRow : ScriptlessRowBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlank))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlank), nameof(SourceOption), nameof(PathHint))]
    private ResponseSource _source = ResponseSource.Body;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlank))]
    private string _pathText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeOption))]
    private CaptureScope _scope = CaptureScope.Runtime;

    public SourceOption SourceOption
    {
        get => ScriptlessOptions.SourceFor(Source);
        set { if (value is not null) Source = value.Source; }
    }

    public ScopeOption ScopeOption
    {
        get => ScriptlessOptions.ScopeFor(Scope);
        set { if (value is not null) Scope = value.Scope; }
    }

    public string PathHint => SourceOption.PathHint;

    public override bool IsBlank => string.IsNullOrEmpty(Name) && string.IsNullOrEmpty(PathText) && Source == ResponseSource.Body;

    public bool IsActive => Enabled && !string.IsNullOrWhiteSpace(Name);

    public ResponseExpression Expression => ResponseExpression.FromEditor(Source, PathText);

    public override bool IsDataProperty(string? propertyName) =>
        base.IsDataProperty(propertyName) && propertyName is not (nameof(SourceOption) or nameof(ScopeOption) or nameof(PathHint));

    public static CaptureRow FromStorage(KvPair pair, CaptureScope scope)
    {
        var expr = ResponseExpression.Parse(pair.Value);
        return new CaptureRow
        {
            Name = pair.Name,
            Source = expr.Source,
            PathText = expr.ToEditorText(),
            Scope = scope,
            Enabled = pair.Enabled,
        };
    }

    public KvPair ToKvPair() => new(Name.Trim(), Expression.ToStorage(), Enabled);

    public CaptureRule ToRule() => new(Name.Trim(), Expression.ToStorage(), Scope, Enabled);
}

/// <summary>One scriptless assertion row (Assertions section).</summary>
public sealed partial class AssertionRow : ScriptlessRowBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlank), nameof(SourceOption), nameof(PathHint))]
    private ResponseSource _source = ResponseSource.Body;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlank))]
    private string _pathText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TakesExpected), nameof(ExpectedHint), nameof(IsBlank))]
    private AssertionOperator _operator = AssertionOperators.All[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlank))]
    private string _expected = string.Empty;

    public SourceOption SourceOption
    {
        get => ScriptlessOptions.SourceFor(Source);
        set { if (value is not null) Source = value.Source; }
    }

    public string PathHint => SourceOption.PathHint;
    public bool TakesExpected => !Operator.Unary;
    public string ExpectedHint => Operator.Hint ?? "expected value or {{variable}}";

    /// <summary>Untouched ghost row: whole-body source, no path, default operator, no expected
    /// value. (A whole-body check such as <c>res.body: isJson</c> differs by its operator.)</summary>
    public override bool IsBlank =>
        Source == ResponseSource.Body && string.IsNullOrEmpty(PathText) && string.IsNullOrEmpty(Expected)
        && ReferenceEquals(Operator, AssertionOperators.All[0]);

    public bool IsActive => Enabled && !IsBlank;

    public string ExpressionStorage => ResponseExpression.FromEditor(Source, PathText).ToStorage();

    public string SpecStorage => new AssertionSpec(Operator.Token, TakesExpected ? Expected.Trim() : string.Empty).ToStorage();

    public override bool IsDataProperty(string? propertyName) =>
        base.IsDataProperty(propertyName)
        && propertyName is not (nameof(SourceOption) or nameof(PathHint) or nameof(TakesExpected) or nameof(ExpectedHint));

    public static AssertionRow FromStorage(KvPair pair)
    {
        var expr = ResponseExpression.Parse(pair.Name);
        var spec = AssertionSpec.Parse(pair.Value);
        return new AssertionRow
        {
            Source = expr.Source,
            PathText = expr.ToEditorText(),
            Operator = AssertionOperators.Find(spec.Operator) ?? AssertionOperators.All[0],
            Expected = spec.Operand,
            Enabled = pair.Enabled,
        };
    }

    public KvPair ToKvPair() => new(ExpressionStorage, SpecStorage, Enabled);
}

/// <summary>A choice in the "older than" unit dropdown of a <see cref="ChainRunMode.WhenStale"/> link.</summary>
public sealed record AgeUnitOption(string Suffix, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A comparison in a <see cref="ChainRunMode.When"/> link's condition (Thunder Client operators).</summary>
public sealed record ConditionOperatorOption(string Token, string Label)
{
    public string Display => $"{Label}  ({Token})";
    public override string ToString() => Display;
}

/// <summary>One linked-request row (Chain section of the Pre-request tab). Stored as
/// <c>path: condition</c>; the editor builds both halves from pickers — see
/// <see cref="ChainLinksEditor"/> for the request catalog the row resolves against.</summary>
public sealed partial class ChainLinkRow : ScriptlessRowBase
{
    public static readonly IReadOnlyList<AgeUnitOption> AgeUnits = new AgeUnitOption[]
    {
        new("s", "seconds"), new("m", "minutes"), new("h", "hours"), new("d", "days"),
    };

    public static readonly IReadOnlyList<ConditionOperatorOption> ConditionOperators = new ConditionOperatorOption[]
    {
        new("=", "equals"), new("!=", "does not equal"), new("*=", "contains"), new("^=", "starts with"),
        new("$=", "ends with"), new(">", "is greater than"), new(">=", "is at least"), new("<", "is less than"),
        new("<=", "is at most"),
    };

    // Longest first, as the engine's FilterCondition splits them.
    private static readonly string[] ParseOrder = { ">=", "<=", "!=", "*=", "^=", "$=", "=", ">", "<" };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlank), nameof(StoredText))]
    private string _requestPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeOption), nameof(TakesArgument), nameof(ArgumentHint),
        nameof(IsWhenEmpty), nameof(IsWhenStale), nameof(IsWhenCondition), nameof(StoredText), nameof(ShowEmptySuggestion))]
    private ChainRunMode _mode = ChainRunMode.Always;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StoredText), nameof(VariableName), nameof(StaleAmount), nameof(StaleUnit),
        nameof(ConditionVariable), nameof(ConditionOperator), nameof(ConditionValue))]
    private string _argument = string.Empty;

    // ---- resolved against the editor's catalog (not saved)
    [ObservableProperty] private int _stepNumber;
    [ObservableProperty] private bool _isFirst;
    [ObservableProperty] private bool _isLast;
    [ObservableProperty] private IReadOnlyList<ChainCandidate> _availableCandidates = Array.Empty<ChainCandidate>();
    [ObservableProperty] private IReadOnlyList<string> _variableSuggestions = Array.Empty<string>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCandidate), nameof(HasCaptures), nameof(CapturesText), nameof(ShowNoCaptures),
        nameof(NoCapturesText), nameof(ShowEmptySuggestion), nameof(EmptySuggestionText), nameof(IsUnresolved), nameof(UnresolvedText))]
    private ChainCandidate? _target;

    /// <summary>Set by <see cref="ChainLinksEditor"/> when the row joins it.</summary>
    public ChainLinksEditor? Editor { get; internal set; }

    public ChainModeOption ModeOption
    {
        get => ScriptlessOptions.ChainModeFor(Mode);
        set
        {
            if (value is null || value.Mode == Mode) return;
            // Switching conditions starts from a useful argument: the linked request's capture.
            var capture = Target?.Captures.FirstOrDefault() ?? string.Empty;
            Argument = value.Mode switch
            {
                ChainRunMode.WhenEmpty => capture,
                ChainRunMode.WhenStale => "10m",
                ChainRunMode.When => FormatCondition(capture, "=", string.Empty),
                _ => string.Empty,
            };
            Mode = value.Mode;
        }
    }

    public bool TakesArgument => ModeOption.TakesArgument;
    public string ArgumentHint => ModeOption.ArgumentHint;
    public bool IsWhenEmpty => Mode == ChainRunMode.WhenEmpty;
    public bool IsWhenStale => Mode == ChainRunMode.WhenStale;
    public bool IsWhenCondition => Mode == ChainRunMode.When;

    public string StepLabel => $"Request {StepNumber}";
    partial void OnStepNumberChanged(int value) => OnPropertyChanged(nameof(StepLabel));

    /// <summary>The request picker's selection. Choosing another request keeps the condition, but
    /// a "variable is empty" condition follows the new request's capture when it pointed at the
    /// old one's.</summary>
    public ChainCandidate? SelectedCandidate
    {
        get => Target;
        set
        {
            if (value is null || string.Equals(value.Path, RequestPath, StringComparison.OrdinalIgnoreCase)) return;
            var old = Target;
            if (Mode == ChainRunMode.WhenEmpty && value.HasCaptures
                && (string.IsNullOrWhiteSpace(Argument) || (old?.Captures.Contains(VariableName) ?? false)))
                Argument = value.Captures[0];
            RequestPath = value.Path;
        }
    }

    public bool HasCaptures => Target?.HasCaptures ?? false;
    public string CapturesText => Target?.CapturesText ?? string.Empty;
    public bool ShowNoCaptures => !IsBlank && Target is { HasCaptures: false };
    public string NoCapturesText => Target is null ? string.Empty
        : $"{Target.Name} doesn't capture any variables yet — open it and add one in Tests → Capture.";
    public bool ShowEmptySuggestion => Mode == ChainRunMode.Always && HasCaptures;
    public string EmptySuggestionText => HasCaptures ? $"Run only when {{{{{Target!.Captures[0]}}}}} is empty" : string.Empty;
    public bool IsUnresolved => !IsBlank && Target is null && (Editor?.HasCandidates ?? false);
    public string UnresolvedText => $"“{RequestPath}” isn't a request in this collection — choose one.";

    /// <summary>How the link is saved in the .bru file.</summary>
    public string StoredText => IsBlank ? string.Empty : $"{RequestPath}: {ToSpec().ToStorage()}";

    // ---- when-empty: the variable
    public string VariableName
    {
        get => BareName(Argument);
        set => Argument = BareName(value ?? string.Empty);
    }

    // ---- when-stale: amount + unit
    public string StaleAmount
    {
        get => ParseAge(Argument).Amount;
        set => Argument = (string.IsNullOrWhiteSpace(value) ? "0" : value.Trim()) + ParseAge(Argument).Unit;
    }

    public AgeUnitOption StaleUnit
    {
        get => AgeUnits.FirstOrDefault(u => u.Suffix == ParseAge(Argument).Unit) ?? AgeUnits[1];
        set { if (value is not null) Argument = ParseAge(Argument).Amount + value.Suffix; }
    }

    // ---- when: variable, operator, value
    public string ConditionVariable
    {
        get => ParseCondition(Argument).Variable;
        set { var c = ParseCondition(Argument); Argument = FormatCondition(value ?? string.Empty, c.Op, c.Value); }
    }

    public ConditionOperatorOption ConditionOperator
    {
        get => ConditionOperators.FirstOrDefault(o => o.Token == ParseCondition(Argument).Op) ?? ConditionOperators[0];
        set { if (value is null) return; var c = ParseCondition(Argument); Argument = FormatCondition(c.Variable, value.Token, c.Value); }
    }

    public string ConditionValue
    {
        get => ParseCondition(Argument).Value;
        set { var c = ParseCondition(Argument); Argument = FormatCondition(c.Variable, c.Op, value ?? string.Empty); }
    }

    [RelayCommand]
    private void RunWhenEmpty()
    {
        if (Target?.Captures.FirstOrDefault() is not { } capture) return;
        Argument = capture;
        Mode = ChainRunMode.WhenEmpty;
    }

    public override bool IsBlank => string.IsNullOrEmpty(RequestPath);

    public bool IsActive => Enabled && !IsBlank;

    /// <summary>Only the stored halves (and Enabled) are user edits; everything else is UI state.</summary>
    public override bool IsDataProperty(string? propertyName) =>
        propertyName is nameof(RequestPath) or nameof(Mode) or nameof(Argument) or nameof(Enabled);

    partial void OnRequestPathChanged(string value) => RefreshFromEditor();

    internal void SetPosition(int step, bool first, bool last)
    {
        StepNumber = step;
        IsFirst = first;
        IsLast = last;
    }

    /// <summary>Re-resolves the target and the pickers' lists against <see cref="Editor"/>.</summary>
    internal void RefreshFromEditor()
    {
        if (Editor is null) return;
        Target = Editor.Resolve(RequestPath);
        AvailableCandidates = Editor.AvailableFor(this);
        VariableSuggestions = (Target?.Captures ?? Array.Empty<string>())
            .Concat(Editor.KnownVariables)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        OnPropertyChanged(nameof(ShowNoCaptures));
        OnPropertyChanged(nameof(IsUnresolved));
    }

    public static ChainLinkRow FromStorage(KvPair pair)
    {
        var spec = ChainLinkSpec.Parse(pair.Value);
        return new ChainLinkRow
        {
            RequestPath = pair.Name,
            Mode = spec.Mode,
            Argument = spec.Argument,
            Enabled = pair.Enabled,
        };
    }

    private ChainLinkSpec ToSpec() => new(Mode, TakesArgument ? Argument.Trim() : string.Empty);

    public KvPair ToKvPair() => new(RequestPath.Trim(), ToSpec().ToStorage(), Enabled);

    // ------------------------------------------------------------------ argument parsing

    /// <summary><c>{{ token }}</c> → <c>token</c>; anything else stays as typed.</summary>
    private static string BareName(string text)
    {
        var t = text.Trim();
        if (t.Length > 4 && t.StartsWith("{{", StringComparison.Ordinal) && t.EndsWith("}}", StringComparison.Ordinal))
        {
            var inner = t[2..^2].Trim();
            if (inner.Length > 0 && inner.IndexOfAny(new[] { '{', '}', ' ' }) < 0) return inner;
        }
        return t;
    }

    /// <summary><c>30m</c> → ("30", "m"); bare numbers are minutes, as the engine reads them.</summary>
    private static (string Amount, string Unit) ParseAge(string arg)
    {
        var a = arg.Trim().ToLowerInvariant();
        if (a.Length == 0) return ("10", "m");
        var hasUnit = char.IsLetter(a[^1]);
        var unit = hasUnit ? a[^1].ToString() : "m";
        var num = hasUnit ? a[..^1].Trim() : a;
        if (!"smhd".Contains(unit, StringComparison.Ordinal)) unit = "m";
        return double.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)
            ? (num, unit)
            : ("10", "m");
    }

    private static (string Variable, string Op, string Value) ParseCondition(string text)
    {
        foreach (var op in ParseOrder)
        {
            for (var i = text.IndexOf(op, StringComparison.Ordinal); i >= 0; i = text.IndexOf(op, i + 1, StringComparison.Ordinal))
            {
                if (op.Length == 1 && i > 0 && "<>!*^$".Contains(text[i - 1])) continue;
                if (op.Length == 1 && op != "=" && i + 1 < text.Length && text[i + 1] == '=') continue;
                return (BareName(text[..i]), op, text[(i + op.Length)..].Trim());
            }
        }
        return (BareName(text), "=", string.Empty);
    }

    private static string FormatCondition(string variable, string op, string value)
    {
        var name = variable.Trim();
        var left = name.Length == 0 || name.Contains("{{", StringComparison.Ordinal) ? name : "{{" + name + "}}";
        return $"{left} {op} {value}".Trim();
    }
}
