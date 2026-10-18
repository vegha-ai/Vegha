using CommunityToolkit.Mvvm.ComponentModel;
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
        new(ChainRunMode.Always, "Always", string.Empty, false),
        new(ChainRunMode.WhenEmpty, "If variable is empty", "token", true),
        new(ChainRunMode.WhenStale, "If response is older than", "10m", true),
        new(ChainRunMode.When, "If condition", "{{role}} = admin", true),
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

/// <summary>One linked-request row (Chain section of the Pre-request tab).</summary>
public sealed partial class ChainLinkRow : ScriptlessRowBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlank))]
    private string _requestPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeOption), nameof(TakesArgument), nameof(ArgumentHint))]
    private ChainRunMode _mode = ChainRunMode.Always;

    [ObservableProperty] private string _argument = string.Empty;

    public ChainModeOption ModeOption
    {
        get => ScriptlessOptions.ChainModeFor(Mode);
        set { if (value is not null) Mode = value.Mode; }
    }

    public bool TakesArgument => ModeOption.TakesArgument;
    public string ArgumentHint => ModeOption.ArgumentHint;

    public override bool IsBlank => string.IsNullOrEmpty(RequestPath);

    public bool IsActive => Enabled && !IsBlank;

    public override bool IsDataProperty(string? propertyName) =>
        base.IsDataProperty(propertyName)
        && propertyName is not (nameof(ModeOption) or nameof(TakesArgument) or nameof(ArgumentHint));

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

    public KvPair ToKvPair() =>
        new(RequestPath.Trim(), new ChainLinkSpec(Mode, TakesArgument ? Argument.Trim() : string.Empty).ToStorage(), Enabled);
}
