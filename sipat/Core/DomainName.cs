using System.Text.RegularExpressions;

namespace Sipat.Core;

/// <summary>
/// Normalizes user- and page-supplied URLs down to the bare domain the
/// blocklists store. Mirrors the logic in add_scatters.sh so both tools agree
/// on what counts as the same entry.
/// </summary>
public static partial class DomainName
{
    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$")]
    private static partial Regex ValidDomain();

    /// <summary>
    /// https://www.Foo.PH:8443/lobby?ref=x -> foo.ph. Returns null when the
    /// input does not reduce to something domain-shaped.
    /// </summary>
    public static string? Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var d = raw.Trim().ToLowerInvariant();

        var scheme = d.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) d = d[(scheme + 3)..];

        var at = d.IndexOf('@');
        if (at >= 0) d = d[(at + 1)..];

        foreach (var sep in new[] { '/', '?', '#', ':' })
        {
            var i = d.IndexOf(sep);
            if (i >= 0) d = d[..i];
        }

        if (d.StartsWith("www.", StringComparison.Ordinal)) d = d[4..];
        d = d.TrimEnd('.');

        return ValidDomain().IsMatch(d) ? d : null;
    }

    /// <summary>Splits the comma-separated form accepted on the command line.</summary>
    public static IEnumerable<string> SplitList(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
