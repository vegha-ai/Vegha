using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Vegha.Core.Interpolation;

/// <summary>
/// Thunder Client–style system variables — placeholders that start with <c>#</c> and may take
/// comma-separated arguments:
/// <c>{{#guid}}</c>, <c>{{#name}}</c>, <c>{{#string}}</c>, <c>{{#email}}</c>, <c>{{#bool}}</c>,
/// <c>{{#number}}</c> / <c>{{#number, 100, 999}}</c>, <c>{{#enum, red, green, blue}}</c>,
/// <c>{{#date}}</c> (unix ms) / <c>{{#date, 'YYYY-MM-DD'}}</c> / <c>{{#date, 'X'}}</c> (unix s) /
/// <c>{{#date, 'YYYY-MM-DD', {day: -1}}}</c>, <c>{{#dateISO}}</c> / <c>{{#dateISO, {year: 1}}}</c>.
/// Date offsets use the keys <c>year mon day hour min sec</c>. Each use draws fresh values.
/// </summary>
public static class SystemVariableProvider
{
    private static readonly Random Rng = new();
    private static readonly object Lock = new();

    /// <summary>Names the autocomplete offers after <c>{{#</c>.</summary>
    public static readonly IReadOnlyList<(string Name, string Example)> Catalog = new[]
    {
        ("#guid", "Random UUID"),
        ("#name", "Random full name"),
        ("#string", "Random 10-character string"),
        ("#number", "Random number 1–1000000  ·  {{#number, 100, 999}}"),
        ("#email", "Random email address"),
        ("#bool", "true or false"),
        ("#enum", "One of the listed values  ·  {{#enum, red, green, blue}}"),
        ("#date", "Unix ms  ·  {{#date, 'YYYY-MM-DD'}}  ·  {{#date, 'X'}}  ·  {{#date, 'YYYY-MM-DD', {day: -1}}}"),
        ("#dateISO", "ISO-8601 now  ·  {{#dateISO, {year: 1}}}"),
    };

    public static bool TryResolve(string name, out string? value, Func<DateTimeOffset>? clock = null)
    {
        value = null;
        if (string.IsNullOrEmpty(name) || name[0] != '#') return false;
        var parts = SplitArgs(name[1..]);
        if (parts.Count == 0) return false;
        var args = parts.Skip(1).ToList();
        var now = clock?.Invoke() ?? DateTimeOffset.Now;

        value = parts[0].Trim() switch
        {
            "guid" => Guid.NewGuid().ToString(),
            "name" => DynamicValue("$randomFullName"),
            "string" => RandomString(10),
            "email" => DynamicValue("$randomEmail"),
            "bool" => Next(0, 2) == 1 ? "true" : "false",
            "number" => RandomNumber(args),
            "enum" => args.Count == 0 ? null : Unquote(args[Next(0, args.Count)].Trim()),
            "date" => FormatDate(now, args, iso: false),
            "dateISO" => FormatDate(now, args, iso: true),
            _ => null,
        };
        return value is not null;
    }

    private static string? DynamicValue(string name) =>
        DynamicVariableProvider.TryResolve(name, out var v) ? v : null;

    private static string RandomNumber(List<string> args)
    {
        long min = 1, max = 1_000_000;
        if (args.Count >= 1 && long.TryParse(args[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var a)) min = a;
        if (args.Count >= 2 && long.TryParse(args[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var b)) max = b;
        if (max < min) (min, max) = (max, min);
        lock (Lock) return Rng.NextInt64(min, max + 1).ToString(CultureInfo.InvariantCulture);
    }

    private static string RandomString(int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var sb = new StringBuilder(length);
        lock (Lock) for (var i = 0; i < length; i++) sb.Append(chars[Rng.Next(chars.Length)]);
        return sb.ToString();
    }

    private static int Next(int min, int max) { lock (Lock) return Rng.Next(min, max); }

    // ------------------------------------------------------------------ dates

    private static string FormatDate(DateTimeOffset now, List<string> args, bool iso)
    {
        string? format = null;
        foreach (var raw in args)
        {
            var a = raw.Trim();
            if (a.StartsWith('{')) now = ApplyOffset(now, a);
            else if (a.Length > 0) format = Unquote(a);
        }
        if (iso) return now.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        if (format is null) return now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        if (format == "X") return now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return now.ToString(ToDotNetFormat(format), CultureInfo.InvariantCulture);
    }

    /// <summary><c>{year: -1, day: 3}</c> → shifted date.</summary>
    private static DateTimeOffset ApplyOffset(DateTimeOffset d, string obj)
    {
        foreach (Match m in Regex.Matches(obj, @"(\w+)\s*:\s*(-?\d+)"))
        {
            var n = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            d = m.Groups[1].Value.ToLowerInvariant() switch
            {
                "year" or "years" => d.AddYears(n),
                "mon" or "month" or "months" => d.AddMonths(n),
                "day" or "days" => d.AddDays(n),
                "hour" or "hours" => d.AddHours(n),
                "min" or "minute" or "minutes" => d.AddMinutes(n),
                "sec" or "second" or "seconds" => d.AddSeconds(n),
                _ => d,
            };
        }
        return d;
    }

    /// <summary>moment-style tokens (YYYY MM DD hh mm ss fff) → .NET custom format. Literal
    /// text is quoted so letters that aren't tokens pass through unchanged.</summary>
    public static string ToDotNetFormat(string moment)
    {
        var tokens = new (string Moment, string Net)[]
        {
            ("YYYY", "yyyy"), ("YY", "yy"), ("MMMM", "MMMM"), ("MMM", "MMM"), ("MM", "MM"),
            ("DD", "dd"), ("D", "d"), ("HH", "HH"), ("hh", "HH"), ("H", "H"), ("h", "H"),
            ("mm", "mm"), ("ss", "ss"), ("SSS", "fff"), ("fff", "fff"), ("A", "tt"), ("Z", "zzz"),
        };
        var sb = new StringBuilder();
        var literal = new StringBuilder();
        void FlushLiteral()
        {
            if (literal.Length == 0) return;
            sb.Append('\'').Append(literal.ToString().Replace("'", "''")).Append('\'');
            literal.Clear();
        }
        for (var i = 0; i < moment.Length;)
        {
            var hit = tokens.FirstOrDefault(t => string.CompareOrdinal(moment, i, t.Moment, 0, t.Moment.Length) == 0);
            if (hit.Moment is not null)
            {
                FlushLiteral();
                sb.Append(hit.Net);
                i += hit.Moment.Length;
            }
            else
            {
                literal.Append(moment[i]);
                i++;
            }
        }
        FlushLiteral();
        return sb.ToString();
    }

    // ------------------------------------------------------------------ args

    /// <summary>Splits on commas outside quotes and braces.</summary>
    private static List<string> SplitArgs(string s)
    {
        var parts = new List<string>();
        var depth = 0;
        char quote = '\0';
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '\'' or '"') quote = c;
            else if (c == '{') depth++;
            else if (c == '}') depth--;
            else if (c == ',' && depth == 0) { parts.Add(s[start..i]); start = i + 1; }
        }
        parts.Add(s[start..]);
        return parts;
    }

    private static string Unquote(string s) =>
        s.Length >= 2 && (s[0] == '\'' || s[0] == '"') && s[^1] == s[0] ? s[1..^1] : s;
}
