namespace Skimr.Infrastructure.Fetching;

internal static class StoredText
{
    /// <summary>
    /// Makes untrusted text safe to store: drops C0 control characters (Postgres rejects NUL,
    /// and the rest are never meaningful in feed text) and cuts to <paramref name="max"/>
    /// characters without splitting a surrogate pair.
    /// </summary>
    public static string? Fit(string? value, int max)
    {
        if (value is null)
        {
            return null;
        }

        if (value.AsSpan().IndexOfAnyInRange('\0', '\u001F') >= 0)
        {
            value = new string(value.Where(c => c >= ' ' || c is '\t' or '\n' or '\r').ToArray());
        }

        if (value.Length <= max)
        {
            return value;
        }

        var cut = char.IsHighSurrogate(value[max - 1]) ? max - 1 : max;
        return value[..cut];
    }
}
