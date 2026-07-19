namespace Sipat.Core;

/// <summary>
/// Domains Sipat must never propose for blocking, regardless of what DNS or
/// content checks say about them. Consulted by every path that could put a
/// domain in front of the blocklists.
/// </summary>
public static class Policy
{
    /// <summary>
    /// Philippine government domains. A failed /goto/ redirect or a sinkhole
    /// match must never turn into a blocklist entry for the government host
    /// doing the blocking.
    /// </summary>
    public static bool IsNeverBlock(string domain) =>
        domain.Equals("gov.ph", StringComparison.OrdinalIgnoreCase) ||
        domain.EndsWith(".gov.ph", StringComparison.OrdinalIgnoreCase);
}
