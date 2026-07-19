namespace Sipat.Core;

/// <summary>
/// The set of IP addresses that mean "the ISP's resolver handed back CICC's
/// block page instead of the real site".
/// </summary>
public sealed class Sinkhole
{
    /// <summary>The address PH ISP resolvers currently return for sinkholed domains.</summary>
    public static readonly string[] KnownAddresses = ["121.54.78.47"];

    /// <summary>
    /// Hostname of the block page itself. Its current A records are folded
    /// into the set as a backup, so the classifier keeps working if CICC moves
    /// the static address.
    /// </summary>
    public const string BlockPageHost = "blocked.sbmd.cicc.gov.ph";

    /// <summary>
    /// Domains verified to be sinkholed, used as canaries: if none of them
    /// resolve into the sinkhole set, this machine has no PH-resolver vantage
    /// (VPN, foreign DNS) and sinkhole verdicts would silently read as clean.
    /// </summary>
    public static readonly string[] Canaries = ["phtaya1.com", "superph3.com", "phcrown5.com"];

    private readonly HashSet<string> _static;
    private readonly HashSet<string> _dynamic;

    private Sinkhole(HashSet<string> @static, HashSet<string> @dynamic)
    {
        _static = @static;
        _dynamic = @dynamic;
    }

    public IReadOnlyCollection<string> StaticAddresses => _static;
    public IReadOnlyCollection<string> DynamicAddresses => _dynamic;

    public bool Contains(string ip) => _static.Contains(ip) || _dynamic.Contains(ip);

    /// <summary>True when the match came only from the block page's own A records.</summary>
    public bool IsBackupMatch(string ip) => !_static.Contains(ip) && _dynamic.Contains(ip);

    public static async Task<Sinkhole> BuildAsync(
        HttpClient http, IEnumerable<string> extraAddresses, CancellationToken ct = default)
    {
        var @static = new HashSet<string>(KnownAddresses);
        foreach (var ip in extraAddresses) @static.Add(ip);

        // The block page sits behind an AWS load balancer whose addresses
        // rotate, so they are resolved fresh each run and kept separate: an
        // ALB address CICC uses today can be handed to an unrelated AWS
        // customer later, so a backup-only match is flagged as such rather
        // than trusted like the static address.
        var @dynamic = new HashSet<string>();
        var fromSystem = await Resolver.SystemAsync(BlockPageHost, ct);
        var fromPublic = await Resolver.PublicAsync(http, BlockPageHost, ct);
        foreach (var ip in fromSystem.Addresses.Concat(fromPublic.Addresses))
            if (!@static.Contains(ip))
                @dynamic.Add(ip);

        return new Sinkhole(@static, @dynamic);
    }

    /// <summary>
    /// Resolves the canary domains through the system resolver and reports how
    /// many landed in the sinkhole set. Zero means scan verdicts can't be
    /// trusted from this network position.
    /// </summary>
    public async Task<int> CountCanaryHitsAsync(CancellationToken ct = default)
    {
        var results = await Task.WhenAll(Canaries.Select(c => Resolver.SystemAsync(c, ct)));
        return results.Count(r => r.Addresses.Any(Contains));
    }
}
