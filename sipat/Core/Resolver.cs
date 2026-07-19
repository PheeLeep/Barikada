using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Sipat.Core;

public enum ResolveStatus { Ok, NxDomain, NoAnswer, Error }

public sealed record ResolveResult(ResolveStatus Status, IReadOnlyList<string> Addresses)
{
    public static readonly ResolveResult NxDomain = new(ResolveStatus.NxDomain, []);
    public static readonly ResolveResult Failed = new(ResolveStatus.Error, []);
}

/// <summary>
/// The two DNS vantage points the classifier compares.
/// <para>
/// The system resolver is the ISP's — the only place the CICC sinkhole is
/// visible. The public vantage goes over DoH to Cloudflare so it returns the
/// operator's real infrastructure without needing a second resolver
/// configured, and without any extra NuGet dependency.
/// </para>
/// </summary>
public static class Resolver
{
    private const string DohEndpoint = "https://cloudflare-dns.com/dns-query";

    public static async Task<ResolveResult> SystemAsync(string domain, CancellationToken ct = default)
    {
        try
        {
            var ips = await Dns.GetHostAddressesAsync(domain, AddressFamily.InterNetwork, ct);
            return ips.Length == 0
                ? new ResolveResult(ResolveStatus.NoAnswer, [])
                : new ResolveResult(ResolveStatus.Ok, ips.Select(i => i.ToString()).ToArray());
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.HostNotFound)
        {
            return ResolveResult.NxDomain;
        }
        catch (Exception)
        {
            return ResolveResult.Failed;
        }
    }

    public static async Task<ResolveResult> PublicAsync(HttpClient http, string domain, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{DohEndpoint}?name={Uri.EscapeDataString(domain)}&type=A");
            req.Headers.Accept.ParseAdd("application/dns-json");

            using var res = await http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var status = doc.RootElement.GetProperty("Status").GetInt32();

            if (status == 3) return ResolveResult.NxDomain;   // NXDOMAIN
            if (status != 0) return ResolveResult.Failed;

            if (!doc.RootElement.TryGetProperty("Answer", out var answers))
                return new ResolveResult(ResolveStatus.NoAnswer, []);

            var ips = answers.EnumerateArray()
                .Where(a => a.GetProperty("type").GetInt32() == 1)   // A records only
                .Select(a => a.GetProperty("data").GetString()!)
                .ToArray();

            return ips.Length == 0
                ? new ResolveResult(ResolveStatus.NoAnswer, [])
                : new ResolveResult(ResolveStatus.Ok, ips);
        }
        catch (Exception)
        {
            return ResolveResult.Failed;
        }
    }
}
