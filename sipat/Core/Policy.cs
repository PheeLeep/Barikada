namespace Sipat.Core;

/// <summary>
/// Domains Sipat must never propose for blocking, regardless of what DNS or
/// content checks say about them. Consulted by every path that could put a
/// domain in front of the blocklists.
/// </summary>
public static class Policy
{
    /// <summary>
    /// Philippine government domains, and the regulator's own site (which
    /// appears throughout its PDFs). A failed /goto/ redirect, a sinkhole
    /// match, or a PDF header must never turn into a blocklist entry for the
    /// institutions doing the regulating.
    /// </summary>
    public static bool IsNeverBlock(string domain) =>
        IsOrUnder(domain, "gov.ph") || IsOrUnder(domain, "pagcor.ph");

    private static bool IsOrUnder(string domain, string root) =>
        domain.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        domain.EndsWith("." + root, StringComparison.OrdinalIgnoreCase);
}
