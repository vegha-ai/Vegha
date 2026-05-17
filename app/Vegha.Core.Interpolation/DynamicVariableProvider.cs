using System.Globalization;

namespace Vegha.Core.Interpolation;

/// <summary>
/// Resolver for Postman-compatible dynamic variables — placeholders that start with <c>$</c>
/// (e.g. <c>{{$randomUUID}}</c>, <c>{{$timestamp}}</c>, <c>{{$randomInt}}</c>). Mirrors the
/// Postman dynamic-variable list documented at
/// https://learning.postman.com/docs/tests-and-scripts/write-scripts/variables-list/.
///
/// Bruno calls these "mock-data functions" and ships them in <c>@usebruno/common</c>.
///
/// Each call to <see cref="TryResolve"/> draws fresh randomness — that's intentional and
/// matches Postman's semantics. Repeated <c>{{$randomUUID}}</c> in the same template yields
/// different values.
/// </summary>
public static class DynamicVariableProvider
{
    private static readonly Random Rng = new();
    private static readonly object Lock = new();

    /// <summary>Returns true and emits a value if <paramref name="name"/> matches a known
    /// dynamic variable; otherwise returns false and leaves <paramref name="value"/> null so
    /// the caller falls back to its dictionary lookup. Names are matched case-insensitively
    /// without the leading <c>$</c> (Postman is case-insensitive too).</summary>
    public static bool TryResolve(string name, out string? value)
    {
        value = null;
        if (string.IsNullOrEmpty(name) || name[0] != '$') return false;
        var key = name.AsSpan(1).ToString();
        value = key.ToLowerInvariant() switch
        {
            // ----- identifiers / time -----
            "guid"          => Guid.NewGuid().ToString(),
            "randomuuid"    => Guid.NewGuid().ToString(),
            "timestamp"     => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            "isotimestamp"  => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffK", CultureInfo.InvariantCulture),
            "randomdatetime"=> RandomPastDateTime(),
            "randomdaterecent" => DateTime.UtcNow.AddHours(-Next(1, 720)).ToString("o", CultureInfo.InvariantCulture),

            // ----- numbers / booleans / colors -----
            "randomint"     => Next(0, 1001).ToString(CultureInfo.InvariantCulture),
            "randomalphanumeric" => RandomAlphanumeric(1),
            "randomboolean" => (Next(0, 2) == 1) ? "true" : "false",
            "randomcolor"   => Pick(Colors),
            "randomhexcolor" => "#" + Next(0, 0x1000000).ToString("X6", CultureInfo.InvariantCulture),

            // ----- names / contact -----
            "randomfirstname" => Pick(FirstNames),
            "randomlastname"  => Pick(LastNames),
            "randomfullname"  => Pick(FirstNames) + " " + Pick(LastNames),
            "randomusername"  => Pick(FirstNames).ToLowerInvariant() + Next(10, 1000).ToString(CultureInfo.InvariantCulture),
            "randomemail"     => RandomEmail(),
            "randomphonenumber" => RandomPhone(),

            // ----- web / network -----
            "randomurl"        => "https://" + Pick(Domains),
            "randomdomainname" => Pick(Domains),
            "randomip"         => $"{Next(1,255)}.{Next(0,256)}.{Next(0,256)}.{Next(1,255)}",
            "randomipv6"       => RandomIpv6(),
            "randommacaddress" => RandomMac(),
            "randomuseragent"  => Pick(UserAgents),
            "randompassword"   => RandomAlphanumeric(12),

            // ----- locale / geo -----
            "randomcity"      => Pick(Cities),
            "randomcountry"   => Pick(Countries),
            "randomcountrycode" => Pick(CountryCodes),
            "randomstreetname" => Pick(Streets),
            "randomstreetaddress" => Next(1, 9999).ToString(CultureInfo.InvariantCulture) + " " + Pick(Streets),

            // ----- lorem -----
            "randomloremword"     => Pick(Lorem),
            "randomloremsentence" => RandomLoremSentence(),
            "randomloremparagraph"=> RandomLoremParagraph(),

            // ----- misc -----
            "randomfilepath" => "/" + Pick(Lorem) + "/" + Pick(Lorem) + "." + Pick(FileExts),
            "randommimetype" => Pick(MimeTypes),
            "randomimageurl" => $"https://picsum.photos/{Next(100, 800)}/{Next(100, 800)}",
            _ => null
        };
        return value is not null;
    }

    // ----- helpers -----

    private static int Next(int minInclusive, int maxExclusive)
    {
        lock (Lock) return Rng.Next(minInclusive, maxExclusive);
    }

    private static string Pick(string[] arr)
    {
        lock (Lock) return arr[Rng.Next(arr.Length)];
    }

    private static string RandomAlphanumeric(int length)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var buf = new char[length];
        lock (Lock)
        {
            for (var i = 0; i < length; i++) buf[i] = alphabet[Rng.Next(alphabet.Length)];
        }
        return new string(buf);
    }

    private static string RandomPastDateTime()
    {
        var daysBack = Next(0, 365 * 5);
        return DateTime.UtcNow.AddDays(-daysBack).ToString("o", CultureInfo.InvariantCulture);
    }

    private static string RandomEmail() =>
        Pick(FirstNames).ToLowerInvariant() + "." + Pick(LastNames).ToLowerInvariant() +
        Next(1, 100).ToString(CultureInfo.InvariantCulture) + "@" + Pick(Domains);

    private static string RandomPhone() =>
        $"({Next(200, 1000)}) {Next(200, 1000):D3}-{Next(0, 10000):D4}";

    private static string RandomIpv6()
    {
        var parts = new string[8];
        lock (Lock)
        {
            for (var i = 0; i < 8; i++) parts[i] = Rng.Next(0, 0x10000).ToString("x4", CultureInfo.InvariantCulture);
        }
        return string.Join(":", parts);
    }

    private static string RandomMac()
    {
        var parts = new string[6];
        lock (Lock)
        {
            for (var i = 0; i < 6; i++) parts[i] = Rng.Next(0, 256).ToString("X2", CultureInfo.InvariantCulture);
        }
        return string.Join(":", parts);
    }

    private static string RandomLoremSentence()
    {
        var n = Next(5, 11);
        var words = new string[n];
        for (var i = 0; i < n; i++) words[i] = Pick(Lorem);
        return char.ToUpperInvariant(words[0][0]) + words[0][1..] + " " + string.Join(" ", words.Skip(1)) + ".";
    }

    private static string RandomLoremParagraph()
    {
        var n = Next(3, 6);
        var sentences = new string[n];
        for (var i = 0; i < n; i++) sentences[i] = RandomLoremSentence();
        return string.Join(" ", sentences);
    }

    // ----- pools (kept small; users with stronger needs can supply their own vars) -----

    private static readonly string[] FirstNames = { "Alice", "Bob", "Carol", "Dave", "Eve", "Frank", "Grace", "Heidi", "Ivan", "Judy", "Karl", "Lena", "Mallory", "Niaj", "Olivia", "Peggy", "Quinn", "Rupert", "Sybil", "Trent", "Uma", "Victor", "Wendy", "Xenia", "Yvonne", "Zane" };
    private static readonly string[] LastNames  = { "Smith", "Jones", "Taylor", "Brown", "Wilson", "Davies", "Evans", "Thomas", "Roberts", "Walker", "White", "Hall", "King", "Lee", "Wright", "Hughes", "Green", "Clark", "Hill", "Wood" };
    private static readonly string[] Domains    = { "example.com", "example.org", "example.net", "test.io", "sample.dev", "demo.app" };
    private static readonly string[] Cities     = { "Springfield", "Riverside", "Madison", "Georgetown", "Franklin", "Clinton", "Greenville", "Bristol", "Salem", "Fairview" };
    private static readonly string[] Countries  = { "United States", "United Kingdom", "Germany", "France", "Japan", "Brazil", "India", "Canada", "Australia", "Netherlands" };
    private static readonly string[] CountryCodes = { "US", "GB", "DE", "FR", "JP", "BR", "IN", "CA", "AU", "NL" };
    private static readonly string[] Streets    = { "Main St", "Oak Ave", "Elm St", "Pine Rd", "Cedar Ln", "Maple Dr", "Birch Way", "Park Pl", "King St", "Queen St" };
    private static readonly string[] Colors     = { "red", "green", "blue", "yellow", "purple", "orange", "pink", "black", "white", "gray" };
    private static readonly string[] FileExts   = { "txt", "json", "xml", "csv", "pdf", "png", "jpg", "html", "md", "log" };
    private static readonly string[] MimeTypes  = { "application/json", "application/xml", "text/plain", "text/html", "image/png", "image/jpeg", "application/pdf", "text/csv" };
    private static readonly string[] UserAgents = { "Mozilla/5.0 (compatible; Vegha/1.0)", "curl/8.0.1", "PostmanRuntime/7.32.0" };
    private static readonly string[] Lorem = { "lorem", "ipsum", "dolor", "sit", "amet", "consectetur", "adipiscing", "elit", "sed", "do", "eiusmod", "tempor", "incididunt", "ut", "labore", "magna", "aliqua", "veniam", "nostrud", "exercitation" };
}
