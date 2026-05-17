namespace Vegha.Core.Scripting;

/// <summary>One outcome from a <c>test(name, fn)</c> call.</summary>
public sealed record TestOutcome(string Name, bool Passed, string? FailureMessage, double DurationMs);

/// <summary>
/// The <c>test</c>/<c>expect</c> surface exposed to post-response scripts.
/// Inspired by chai/jest with a tiny fluent matcher set: <c>toBe</c>, <c>toEqual</c>,
/// <c>toContain</c>, <c>toHaveLength</c>, <c>toBeNull</c>, <c>toBeTruthy</c>, <c>toBeFalsy</c>,
/// <c>toBeGreaterThan</c>, <c>toBeLessThan</c>.
/// </summary>
public sealed class TestApi
{
    private readonly List<TestOutcome> _outcomes = new();

    public IReadOnlyList<TestOutcome> Outcomes => _outcomes;

    /// <summary>Defines a test. <paramref name="fn"/> is the test body; mismatched expects throw and record failure.</summary>
    public void test(string name, Action fn)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            fn();
            sw.Stop();
            _outcomes.Add(new TestOutcome(name, true, null, sw.Elapsed.TotalMilliseconds));
        }
        catch (TestFailedException ex)
        {
            sw.Stop();
            _outcomes.Add(new TestOutcome(name, false, ex.Message, sw.Elapsed.TotalMilliseconds));
        }
        catch (Exception ex)
        {
            sw.Stop();
            _outcomes.Add(new TestOutcome(name, false, $"Unexpected error: {ex.Message}", sw.Elapsed.TotalMilliseconds));
        }
    }

    /// <summary>Returns a matcher around <paramref name="actual"/>. The returned object exposes
    /// both Jest-style matchers (<c>.toBe</c>, <c>.toEqual</c>, ...) and the Chai chain
    /// (<c>.to.equal(x)</c>, <c>.to.have.property(name)</c>, <c>.to.be.true</c>) — both APIs
    /// coexist so existing scripts and Postman-translated Bruno scripts work side by side.</summary>
    public Expectation expect(object? actual) => new(actual);
}

/// <summary>Fluent matcher returned by <see cref="TestApi.expect"/>. The <c>not</c> property
/// returns a negated copy so chained matchers like <c>expect(x).not.toBe(y)</c> work without
/// new matcher methods. All matchers throw <see cref="TestFailedException"/> on mismatch
/// (or, when negated, on match) which the surrounding <c>test</c> wrapper turns into a
/// failed <see cref="TestOutcome"/>.</summary>
public sealed class Expectation
{
    private readonly object? _actual;
    private readonly bool _negate;

    public Expectation(object? actual) : this(actual, negate: false) { }
    private Expectation(object? actual, bool negate) { _actual = actual; _negate = negate; }

    public Expectation not => new(_actual, !_negate);

    /// <summary>Chai entry point — <c>expect(x).to.equal(y)</c>. The returned chain shares
    /// the actual + negate state with this expectation, so <c>expect(x).not.to.equal(y)</c>
    /// works as expected.</summary>
    public ChaiChain to => new(_actual, _negate, deep: false);

    public void toBe(object? expected) =>
        Check(Equal(_actual, expected),
            $"expected {Format(_actual)} to be {Format(expected)}",
            $"expected {Format(_actual)} to NOT be {Format(expected)}");

    public void toEqual(object? expected) => toBe(expected);

    public void toContain(string substring)
    {
        var actualStr = _actual?.ToString() ?? string.Empty;
        Check(actualStr.Contains(substring),
            $"expected {Format(actualStr)} to contain {Format(substring)}",
            $"expected {Format(actualStr)} to NOT contain {Format(substring)}");
    }

    public void toHaveLength(double expected)
    {
        var len = _actual switch
        {
            string s => s.Length,
            System.Collections.ICollection c => c.Count,
            _ => -1
        };
        if (len < 0)
            throw Fail($"toHaveLength: actual ({_actual?.GetType().Name ?? "null"}) has no length");
        Check(Math.Abs(len - expected) < 0.0001,
            $"expected length {expected:0}, got {len}",
            $"expected length NOT to be {expected:0}");
    }

    public void toBeNull() =>
        Check(_actual is null,
            $"expected null, got {Format(_actual)}",
            "expected NOT null");

    public void toBeUndefined() => toBeNull();
    public void toBeDefined() =>
        Check(_actual is not null,
            "expected defined, got null",
            $"expected NOT defined, got {Format(_actual)}");

    public void toBeTruthy() =>
        Check(IsTruthy(_actual),
            $"expected truthy, got {Format(_actual)}",
            $"expected falsy, got {Format(_actual)}");

    public void toBeFalsy() =>
        Check(!IsTruthy(_actual),
            $"expected falsy, got {Format(_actual)}",
            $"expected truthy, got {Format(_actual)}");

    public void toBeGreaterThan(double expected) =>
        Check(TryNumber(_actual, out var n) && n > expected,
            $"expected {Format(_actual)} > {expected}",
            $"expected {Format(_actual)} NOT > {expected}");

    public void toBeGreaterThanOrEqual(double expected) =>
        Check(TryNumber(_actual, out var n) && n >= expected,
            $"expected {Format(_actual)} >= {expected}",
            $"expected {Format(_actual)} NOT >= {expected}");

    public void toBeLessThan(double expected) =>
        Check(TryNumber(_actual, out var n) && n < expected,
            $"expected {Format(_actual)} < {expected}",
            $"expected {Format(_actual)} NOT < {expected}");

    public void toBeLessThanOrEqual(double expected) =>
        Check(TryNumber(_actual, out var n) && n <= expected,
            $"expected {Format(_actual)} <= {expected}",
            $"expected {Format(_actual)} NOT <= {expected}");

    public void toMatch(string pattern)
    {
        var actualStr = _actual?.ToString() ?? string.Empty;
        var match = System.Text.RegularExpressions.Regex.IsMatch(actualStr, pattern);
        Check(match,
            $"expected {Format(actualStr)} to match /{pattern}/",
            $"expected {Format(actualStr)} to NOT match /{pattern}/");
    }

    /// <summary>Asserts that <c>actual</c> (a JSON-shaped value) validates against
    /// <paramref name="schema"/>, a JSON Schema document. Accepts the schema as either
    /// a JSON string (Jint passes most objects through as ObjectInstances which we
    /// JSON-stringify first) or anything that round-trips through JsonSerializer.</summary>
    public void toMatchSchema(object? schema)
    {
        var actualJson = ToJsonNode(_actual);
        if (actualJson is null) { throw Fail("toMatchSchema: actual is null"); }

        var schemaJson = SchemaToText(schema);
        global::Json.Schema.JsonSchema parsedSchema;
        try { parsedSchema = global::Json.Schema.JsonSchema.FromText(schemaJson); }
        catch (Exception ex) { throw Fail("toMatchSchema: invalid schema — " + ex.Message); }

        var result = parsedSchema.Evaluate(actualJson);
        Check(result.IsValid,
            "expected actual to match schema (" + SummarizeErrors(result) + ")",
            "expected actual to NOT match schema, but it did");
    }

    private static System.Text.Json.Nodes.JsonNode? ToJsonNode(object? value)
    {
        switch (value)
        {
            case null: return null;
            case System.Text.Json.Nodes.JsonNode existing: return existing;
            case string s when LooksLikeJson(s):
                try { return System.Text.Json.Nodes.JsonNode.Parse(s); } catch { return System.Text.Json.Nodes.JsonValue.Create(s); }
            case string s: return System.Text.Json.Nodes.JsonValue.Create(s);
            default:
                var raw = System.Text.Json.JsonSerializer.Serialize(value);
                return System.Text.Json.Nodes.JsonNode.Parse(raw);
        }
    }

    private static bool LooksLikeJson(string s)
    {
        var t = s.AsSpan().TrimStart();
        return t.Length > 0 && (t[0] == '{' || t[0] == '[' || t[0] == '"' || t[0] == 't' || t[0] == 'f' || t[0] == 'n' || char.IsDigit(t[0]) || t[0] == '-');
    }

    private static string SchemaToText(object? schema) => schema switch
    {
        null => "{}",
        string s => s,
        _ => System.Text.Json.JsonSerializer.Serialize(schema),
    };

    private static string SummarizeErrors(global::Json.Schema.EvaluationResults r)
    {
        if (r.IsValid) return "ok";
        var msgs = new List<string>();
        Walk(r);
        return msgs.Count == 0 ? "validation failed" : string.Join("; ", msgs.Take(3));

        void Walk(global::Json.Schema.EvaluationResults x)
        {
            if (!x.IsValid && x.HasErrors && x.Errors is not null)
                foreach (var kv in x.Errors) msgs.Add($"{kv.Key}: {kv.Value}");
            if (x.Details is not null) foreach (var d in x.Details) Walk(d);
        }
    }

    /// <summary>Asserts the wrapped function throws when invoked. Useful only when the
    /// caller passes <c>expect(() => ...)</c> rather than a value.</summary>
    public void toThrow()
    {
        if (_actual is not Delegate d)
            throw Fail("toThrow: actual must be a function");
        var threw = false;
        try { d.DynamicInvoke(); }
        catch { threw = true; }
        Check(threw,
            "expected function to throw",
            "expected function NOT to throw");
    }

    private void Check(bool condition, string failPositive, string failNegative)
    {
        // condition=true means matcher succeeded; under not, that's a failure.
        var pass = _negate ? !condition : condition;
        if (!pass) throw Fail(_negate ? failNegative : failPositive);
    }

    // ---- helpers ----

    private static bool Equal(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        // Coerce JS numbers (always double) to match int comparisons.
        if (TryNumber(a, out var na) && TryNumber(b, out var nb)) return Math.Abs(na - nb) < 0.0001;
        return a.Equals(b) || a.ToString() == b.ToString();
    }

    private static bool IsTruthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        string s => !string.IsNullOrEmpty(s),
        double d => d != 0,
        int i => i != 0,
        _ => true
    };

    private static bool TryNumber(object? v, out double n)
    {
        switch (v)
        {
            case double d: n = d; return true;
            case int i: n = i; return true;
            case long l: n = l; return true;
            case string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed): n = parsed; return true;
            default: n = 0; return false;
        }
    }

    private static string Format(object? v) => v switch
    {
        null => "null",
        string s => "\"" + s + "\"",
        _ => v.ToString() ?? string.Empty
    };

    private static TestFailedException Fail(string msg) => new(msg);
}

internal sealed class TestFailedException : Exception
{
    public TestFailedException(string message) : base(message) { }
}

/// <summary>
/// Chai-style assertion chain returned by <c>expect(x).to</c>. Mirrors the chain shape
/// Bruno and Postman scripts assume: <c>.equal</c> / <c>.have.property</c> / <c>.be.true</c>
/// / <c>.have.status(N)</c> / <c>.deep.equal(x)</c> / etc. Chain heads (<c>be</c>, <c>have</c>,
/// <c>is</c>, <c>that</c>, <c>which</c>, <c>and</c>) all return <c>this</c> so multi-word
/// chains parse cleanly; <c>not</c> returns a flipped-flag copy; <c>deep</c> sets a flag
/// the comparison matchers consume.
///
/// Terminal-as-property assertions (<c>.true</c>, <c>.false</c>, <c>.null</c>,
/// <c>.undefined</c>, <c>.exist</c>, <c>.empty</c>, <c>.ok</c>) trigger the check on
/// property read and return <c>this</c> so chains can continue.
/// </summary>
public sealed class ChaiChain
{
    private readonly object? _actual;
    private readonly bool _negate;
    private readonly bool _deep;

    internal ChaiChain(object? actual, bool negate, bool deep)
    {
        _actual = actual;
        _negate = negate;
        _deep = deep;
    }

    // ----- chain heads (no-op grammar words) -----

    public ChaiChain to => this;
    public ChaiChain be => this;
    public ChaiChain been => this;
    public ChaiChain is_ => this; // C# `is` is a keyword; user writes `.is` in JS — see `is` below
    public ChaiChain have => this;
    public ChaiChain has => this;
    public ChaiChain with => this;
    public ChaiChain that => this;
    public ChaiChain which => this;
    public ChaiChain and => this;
    public ChaiChain still => this;
    public ChaiChain also => this;
    public ChaiChain @is => this;

    public ChaiChain not => new(_actual, !_negate, _deep);
    public ChaiChain deep => new(_actual, _negate, deep: true);
    public ChaiChain nested => this; // we don't differentiate; property() handles top-level only

    // ----- terminal-as-property assertions -----

    /// <summary>Asserts <c>actual === true</c> (or with .not, that it's not true).</summary>
    public ChaiChain @true
    {
        get
        {
            CheckBool(_actual is bool b && b,
                $"expected {Format(_actual)} to be true",
                $"expected {Format(_actual)} to NOT be true");
            return this;
        }
    }

    public ChaiChain @false
    {
        get
        {
            CheckBool(_actual is bool b && !b,
                $"expected {Format(_actual)} to be false",
                $"expected {Format(_actual)} to NOT be false");
            return this;
        }
    }

    public ChaiChain @null
    {
        get
        {
            CheckBool(_actual is null,
                $"expected {Format(_actual)} to be null",
                "expected NOT null");
            return this;
        }
    }

    public ChaiChain undefined
    {
        get
        {
            CheckBool(_actual is null,
                $"expected {Format(_actual)} to be undefined",
                "expected NOT undefined");
            return this;
        }
    }

    public ChaiChain exist
    {
        get
        {
            CheckBool(_actual is not null,
                "expected actual to exist (be non-null)",
                "expected actual NOT to exist");
            return this;
        }
    }

    public ChaiChain empty
    {
        get
        {
            var isEmpty = _actual switch
            {
                null => true,
                string s => s.Length == 0,
                System.Collections.ICollection c => c.Count == 0,
                _ => false
            };
            CheckBool(isEmpty,
                $"expected {Format(_actual)} to be empty",
                $"expected {Format(_actual)} NOT to be empty");
            return this;
        }
    }

    public ChaiChain ok
    {
        get
        {
            CheckBool(IsTruthy(_actual),
                $"expected {Format(_actual)} to be truthy",
                $"expected {Format(_actual)} to be falsy");
            return this;
        }
    }

    // ----- callable assertions -----

    public ChaiChain equal(object? expected) => DoEqual(expected, "equal");
    public ChaiChain equals(object? expected) => DoEqual(expected, "equal");
    public ChaiChain eq(object? expected) => DoEqual(expected, "equal");
    public ChaiChain eql(object? expected) => DoEqual(expected, "eql"); // deep by definition

    private ChaiChain DoEqual(object? expected, string name)
    {
        var pass = (_deep || name == "eql") ? DeepEqual(_actual, expected) : ShallowEqual(_actual, expected);
        CheckBool(pass,
            $"expected {Format(_actual)} to {name} {Format(expected)}",
            $"expected {Format(_actual)} to NOT {name} {Format(expected)}");
        return this;
    }

    /// <summary>Asserts the value's runtime type matches a JS-style type name
    /// (<c>"string"</c>, <c>"number"</c>, <c>"boolean"</c>, <c>"object"</c>, <c>"array"</c>).</summary>
    public ChaiChain a(string typeName) => CheckType(typeName);
    public ChaiChain an(string typeName) => CheckType(typeName);

    private ChaiChain CheckType(string typeName)
    {
        var t = typeName?.ToLowerInvariant() ?? string.Empty;
        var pass = t switch
        {
            "string" => _actual is string,
            "number" => _actual is double or int or long or float or decimal,
            "boolean" or "bool" => _actual is bool,
            "array" => _actual is System.Collections.IList && _actual is not string,
            "object" => _actual is not null && _actual is not string && _actual is not bool
                        && _actual is not double && _actual is not int && _actual is not long,
            "function" => _actual is Delegate,
            "null" => _actual is null,
            "undefined" => _actual is null,
            _ => false
        };
        CheckBool(pass,
            $"expected {Format(_actual)} to be a {typeName}",
            $"expected {Format(_actual)} NOT to be a {typeName}");
        return this;
    }

    /// <summary>Re-targets the chain at the named property of <paramref name="name"/>.
    /// Subsequent <c>.equal</c> / <c>.that</c> calls work on the property value.</summary>
    public ChaiChain property(string name)
    {
        var (found, value) = TryReadProperty(_actual, name);
        // The "has property" assertion itself.
        CheckBool(found,
            $"expected actual to have property '{name}'",
            $"expected actual NOT to have property '{name}'");
        // Re-target subsequent chain calls at the property value. Don't flip negate — the
        // chain after .property is a fresh assertion on the value.
        return new ChaiChain(value, negate: false, _deep);
    }

    public ChaiChain length(int expected) => CheckLength(expected);
    public ChaiChain lengthOf(int expected) => CheckLength(expected);

    private ChaiChain CheckLength(int expected)
    {
        var len = _actual switch
        {
            string s => s.Length,
            System.Collections.ICollection c => c.Count,
            _ => -1
        };
        if (len < 0) throw new TestFailedException("length: actual is neither string nor collection");
        CheckBool(len == expected,
            $"expected length {expected}, got {len}",
            $"expected length NOT to be {expected}");
        return this;
    }

    /// <summary>Asserts the actual has a numeric <c>status</c> property equal to
    /// <paramref name="code"/>. Chai-HTTP idiom — works against <see cref="ResponseApi"/>
    /// (which exposes <c>status</c>) and any plain JS object with a <c>status</c> field.</summary>
    public ChaiChain status(int code)
    {
        var (_, st) = TryReadProperty(_actual, "status");
        var actualCode = ToInt(st);
        CheckBool(actualCode == code,
            $"expected status {code}, got {actualCode}",
            $"expected status NOT to be {code}");
        return this;
    }

    public ChaiChain contain(object? needle) => CheckContain(needle);
    public ChaiChain include(object? needle) => CheckContain(needle);
    public ChaiChain contains(object? needle) => CheckContain(needle);
    public ChaiChain includes(object? needle) => CheckContain(needle);

    private ChaiChain CheckContain(object? needle)
    {
        bool pass;
        if (_actual is string s && needle is not null)
        {
            pass = s.Contains(needle.ToString() ?? string.Empty, StringComparison.Ordinal);
        }
        else if (_actual is System.Collections.IEnumerable e && _actual is not string)
        {
            pass = false;
            foreach (var item in e)
            {
                if (ShallowEqual(item, needle)) { pass = true; break; }
            }
        }
        else pass = false;

        CheckBool(pass,
            $"expected {Format(_actual)} to contain {Format(needle)}",
            $"expected {Format(_actual)} NOT to contain {Format(needle)}");
        return this;
    }

    public ChaiChain match(string pattern)
    {
        var s = _actual?.ToString() ?? string.Empty;
        var pass = System.Text.RegularExpressions.Regex.IsMatch(s, pattern);
        CheckBool(pass,
            $"expected {Format(s)} to match /{pattern}/",
            $"expected {Format(s)} NOT to match /{pattern}/");
        return this;
    }

    public ChaiChain above(double bound) => Compare(bound, (a, b) => a > b, "above");
    public ChaiChain greaterThan(double bound) => above(bound);
    public ChaiChain below(double bound) => Compare(bound, (a, b) => a < b, "below");
    public ChaiChain lessThan(double bound) => below(bound);
    public ChaiChain least(double bound) => Compare(bound, (a, b) => a >= b, "at least");
    public ChaiChain atLeast(double bound) => least(bound);
    public ChaiChain most(double bound) => Compare(bound, (a, b) => a <= b, "at most");
    public ChaiChain atMost(double bound) => most(bound);

    private ChaiChain Compare(double bound, Func<double, double, bool> cmp, string verb)
    {
        if (!TryNumber(_actual, out var n))
            throw new TestFailedException($"{verb}: actual is not a number");
        CheckBool(cmp(n, bound),
            $"expected {n} to be {verb} {bound}",
            $"expected {n} NOT to be {verb} {bound}");
        return this;
    }

    /// <summary>Asserts the wrapped function throws. <c>expect(fn).to.throw()</c>.</summary>
    public ChaiChain @throw()
    {
        if (_actual is not Delegate d)
            throw new TestFailedException("throw: actual must be a function");
        var threw = false;
        try { d.DynamicInvoke(); } catch { threw = true; }
        CheckBool(threw,
            "expected function to throw",
            "expected function NOT to throw");
        return this;
    }

    public ChaiChain @throws() => @throw();

    // ----- unsupported (stubbed) -----

    public ChaiChain respondTo(string method) => Unsupported(nameof(respondTo));
    public ChaiChain satisfy(object? _) => Unsupported(nameof(satisfy));
    public ChaiChain closeTo(double _, double __) => Unsupported(nameof(closeTo));
    public ChaiChain oneOf(object? _) => Unsupported(nameof(oneOf));
    public ChaiChain change(object? _, string __) => Unsupported(nameof(change));

    private ChaiChain Unsupported(string name) =>
        throw new TestFailedException($"chai matcher '{name}' is not supported in Vegha");

    // ----- helpers -----

    private void CheckBool(bool condition, string failPositive, string failNegative)
    {
        var pass = _negate ? !condition : condition;
        if (!pass) throw new TestFailedException(_negate ? failNegative : failPositive);
    }

    private static bool ShallowEqual(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (TryNumber(a, out var na) && TryNumber(b, out var nb)) return Math.Abs(na - nb) < 0.0001;
        return a.Equals(b) || a.ToString() == b.ToString();
    }

    private static bool DeepEqual(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;

        // Dictionaries
        if (a is IDictionary<string, object?> da && b is IDictionary<string, object?> db)
        {
            if (da.Count != db.Count) return false;
            foreach (var (k, v) in da)
            {
                if (!db.TryGetValue(k, out var bv)) return false;
                if (!DeepEqual(v, bv)) return false;
            }
            return true;
        }
        // Lists / enumerables (not strings)
        if (a is System.Collections.IEnumerable ae && a is not string &&
            b is System.Collections.IEnumerable be && b is not string)
        {
            var ai = ae.GetEnumerator();
            var bi = be.GetEnumerator();
            while (true)
            {
                var anext = ai.MoveNext();
                var bnext = bi.MoveNext();
                if (anext != bnext) return false;
                if (!anext) return true;
                if (!DeepEqual(ai.Current, bi.Current)) return false;
            }
        }
        return ShallowEqual(a, b);
    }

    private static (bool Found, object? Value) TryReadProperty(object? target, string name)
    {
        if (target is null || string.IsNullOrEmpty(name)) return (false, null);
        if (target is IDictionary<string, object?> dict)
        {
            return dict.TryGetValue(name, out var v) ? (true, v) : (false, null);
        }
        // POCO / record with the named property.
        var pi = target.GetType().GetProperty(name) ?? target.GetType().GetProperty(
            char.ToUpperInvariant(name[0]) + name[1..]);
        if (pi is not null) return (true, pi.GetValue(target));
        return (false, null);
    }

    private static int ToInt(object? v) => v switch
    {
        null => 0,
        int i => i,
        long l => (int)l,
        double d => (int)d,
        string s when int.TryParse(s, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => 0
    };

    private static bool IsTruthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        string s => !string.IsNullOrEmpty(s),
        double d => d != 0,
        int i => i != 0,
        _ => true
    };

    private static bool TryNumber(object? v, out double n)
    {
        switch (v)
        {
            case double d: n = d; return true;
            case int i: n = i; return true;
            case long l: n = l; return true;
            case string s when double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed): n = parsed; return true;
            default: n = 0; return false;
        }
    }

    private static string Format(object? v) => v switch
    {
        null => "null",
        string s => "\"" + s + "\"",
        _ => v.ToString() ?? string.Empty
    };
}
