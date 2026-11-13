using System.Globalization;
using System.Text.RegularExpressions;

namespace Skimr.Infrastructure.Feeds;

/// <summary>Lenient feed date parsing: RFC 822/1123 with real-world damage, and ISO 8601.</summary>
internal static partial class DateParser
{
    private static readonly Dictionary<string, string> Zones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UT"] = "+00:00",
        ["UTC"] = "+00:00",
        ["GMT"] = "+00:00",
        ["Z"] = "+00:00",
        ["EST"] = "-05:00",
        ["EDT"] = "-04:00",
        ["CST"] = "-06:00",
        ["CDT"] = "-05:00",
        ["MST"] = "-07:00",
        ["MDT"] = "-06:00",
        ["PST"] = "-08:00",
        ["PDT"] = "-07:00",
        ["BST"] = "+01:00",
        ["CET"] = "+01:00",
        ["CEST"] = "+02:00",
        ["EET"] = "+02:00",
        ["EEST"] = "+03:00",
        ["IST"] = "+05:30",
        ["JST"] = "+09:00",
        ["AEST"] = "+10:00",
        ["AEDT"] = "+11:00",
    };

    private static readonly string[] Formats =
    [
        "d MMM yyyy HH:mm:ss zzz", "d MMM yyyy HH:mm zzz", "d MMM yy HH:mm:ss zzz", "d MMM yy HH:mm zzz",
        "d MMMM yyyy HH:mm:ss zzz", "d MMMM yyyy HH:mm zzz",
        "d MMM yyyy HH:mm:ss", "d MMM yyyy HH:mm", "d MMM yyyy",
    ];

    public static bool TryParse(string? text, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = Whitespace().Replace(text.Trim(), " ");

        // ISO 8601 and anything else .NET understands (no zone means UTC).
        if (IsoLike().IsMatch(s)
            && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var iso))
        {
            utc = iso.UtcDateTime;
            return true;
        }

        // RFC 822: drop the day name (often wrong), normalise the zone to +hh:mm.
        s = DayName().Replace(s, "");
        s = TrailingZone().Replace(s, m =>
        {
            var zone = m.Groups["zone"].Value;
            if (Zones.TryGetValue(zone, out var offset))
            {
                return " " + offset;
            }

            return zone.Length == 5 && zone[0] is '+' or '-'
                ? $" {zone[..3]}:{zone[3..]}"
                : m.Value;
        });

        if (DateTimeOffset.TryParseExact(s, Formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var rfc))
        {
            utc = rfc.UtcDateTime;
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}")]
    private static partial Regex IsoLike();

    [GeneratedRegex(@"^[A-Za-z]+,?\s+(?=\d)")]
    private static partial Regex DayName();

    [GeneratedRegex(@"\s(?<zone>[A-Za-z]{1,4}|[+-]\d{4})$")]
    private static partial Regex TrailingZone();
}
