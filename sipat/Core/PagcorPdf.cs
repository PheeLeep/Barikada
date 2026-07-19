using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Sipat.Core;

public sealed record PagcorPdfResult(
    DateOnly? AsOf, IReadOnlySet<string> Domains, string Provenance);

/// <summary>
/// PAGCOR's "List of Gaming System Service Providers, Approved Game Offerings
/// and Registered URLs" PDF — the breadth source: unlike CICC's roster (one
/// landing domain per operator), it carries every registered URL per licensee.
/// <para>
/// PAGCOR replaces this file in place without stable dating, so the "as of"
/// date is parsed out of the document and reported as provenance; the caller
/// decides how loudly to warn about staleness. Extracted text is cached for a
/// week with stale fallback, like the other remote sources.
/// </para>
/// </summary>
public static partial class PagcorPdf
{
    public const string PdfUrl =
        "https://www.pagcor.ph/regulatory/pdf/App%20Kits/List-of-Service-Providers-and-Game-Offerings.pdf";

    private const string CacheName = "pagcor-providers.txt";
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(7);

    [GeneratedRegex(@"as of (\d{1,2} \w+ \d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex AsOfDate();

    [GeneratedRegex(@"[a-zA-Z0-9][a-zA-Z0-9.-]*\.[a-zA-Z]{2,}")]
    private static partial Regex DomainToken();

    public static async Task<PagcorPdfResult> LoadAsync(
        HttpClient http, bool refresh, CancellationToken ct = default)
    {
        var cached = Cache.Read(CacheName, out var age);

        if (!refresh && cached is not null && age < Ttl)
            return Parse(cached, $"cache {Cache.Describe(age)}");

        try
        {
            var text = await FetchAndExtractAsync(http, ct);
            Cache.Write(CacheName, text);
            return Parse(text, "fetched");
        }
        catch (Exception) when (cached is not null)
        {
            return Parse(cached, $"STALE cache {Cache.Describe(age)}, fetch failed");
        }
    }

    private static async Task<string> FetchAndExtractAsync(HttpClient http, CancellationToken ct)
    {
        var pdfPath = Path.Combine(Path.GetTempPath(), $"sipat-pagcor-{Guid.NewGuid():N}.pdf");
        try
        {
            await File.WriteAllBytesAsync(pdfPath, await http.GetByteArrayAsync(PdfUrl, ct), ct);
            return RunPdfToText(pdfPath);
        }
        finally
        {
            File.Delete(pdfPath);
        }
    }

    /// <summary>
    /// Shells out to poppler's pdftotext rather than pulling in a PDF NuGet
    /// dependency; -layout keeps table columns readable enough to mine.
    /// </summary>
    private static string RunPdfToText(string pdfPath)
    {
        ProcessStartInfo psi = new("pdftotext")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-layout");
        psi.ArgumentList.Add(pdfPath);
        psi.ArgumentList.Add("-");   // stdout

        Process p;
        try
        {
            p = Process.Start(psi) ?? throw new InvalidOperationException("pdftotext did not start");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                "pdftotext not found — install poppler (poppler-utils) to parse the PAGCOR PDF");
        }

        using (p)
        {
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"pdftotext failed with exit code {p.ExitCode}");
            return text;
        }
    }

    private static PagcorPdfResult Parse(string text, string provenance)
    {
        DateOnly? asOf = null;
        var m = AsOfDate().Match(text);
        if (m.Success && DateTime.TryParseExact(
                m.Groups[1].Value, "d MMMM yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dt))
            asOf = DateOnly.FromDateTime(dt);

        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match token in DomainToken().Matches(text))
        {
            var d = DomainName.Normalize(token.Value);
            if (d is not null && !Policy.IsNeverBlock(d))
                domains.Add(d);
        }

        return new PagcorPdfResult(asOf, domains, provenance);
    }
}
