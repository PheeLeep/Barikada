using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Sipat.Core;

/// <summary>One PAGCOR-approved operator as listed on the CICC block page.</summary>
public sealed record CiccOperator
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("normalized_name")] public string NormalizedName { get; init; } = "";
    [JsonPropertyName("url")] public string GotoPath { get; init; } = "";

    /// <summary>Domain the /goto/ redirect lands on; null until resolved.</summary>
    public string? Domain { get; set; }
}

/// <summary>
/// Reads the operator roster off CICC's block page.
/// <para>
/// The page embeds the roster as a single base64 blob assigned to a randomized
/// JS variable, so it is located by decoding rather than by variable name.
/// Each entry's link is an opaque per-request /goto/ token that has to be
/// followed to recover the operator's real domain.
/// </para>
/// </summary>
public sealed partial class CiccClient(HttpClient http)
{
    public const string BlockPage = "https://blocked.sbmd.cicc.gov.ph/";

    [GeneratedRegex(@"[A-Za-z0-9+/=]{300,}")]
    private static partial Regex Base64Blob();

    public async Task<IReadOnlyList<CiccOperator>> FetchRosterAsync(CancellationToken ct = default)
    {
        var html = await http.GetStringAsync(BlockPage, ct);

        foreach (Match m in Base64Blob().Matches(html))
        {
            if (!TryDecodeBase64(m.Value, out var json)) continue;
            if (!json.TrimStart().StartsWith('[')) continue;

            var ops = JsonSerializer.Deserialize<List<CiccOperator>>(json);
            if (ops is { Count: > 0 }) return ops;
        }

        throw new InvalidOperationException(
            $"no operator roster found on {BlockPage} — the page layout likely changed");
    }

    /// <summary>
    /// The page emits the roster as unpadded base64, so restore the padding
    /// before decoding rather than assuming a well-formed blob.
    /// </summary>
    private static bool TryDecodeBase64(string value, out string decoded)
    {
        decoded = "";

        var padding = value.Length % 4;
        if (padding == 1) return false;              // never valid base64
        if (padding != 0) value += new string('=', 4 - padding);

        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Follows each /goto/ token to its destination. Tokens appear to be minted
    /// per page load, so resolve promptly after fetching the roster.
    /// </summary>
    public async Task ResolveDomainsAsync(
        IReadOnlyList<CiccOperator> operators, int concurrency, CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(concurrency);

        await Task.WhenAll(operators.Select(async op =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var target = new Uri(new Uri(BlockPage), op.GotoPath);
                using var res = await http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct);
                var landed = res.RequestMessage?.RequestUri?.Host;
                var domain = landed is null ? null : DomainName.Normalize(landed);

                // A token that fails to redirect leaves us on the block page;
                // Policy keeps the government host out of the blocklists.
                op.Domain = domain is null || Policy.IsNeverBlock(domain) ? null : domain;
            }
            catch (Exception)
            {
                // Unreachable operator sites are reported as unresolved rather
                // than failing the whole run.
                op.Domain = null;
            }
            finally
            {
                gate.Release();
            }
        }));
    }
}
