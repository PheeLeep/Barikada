using Sipat.Core;
using Spectre.Console;

namespace Sipat.Commands;

/// <summary>
/// Classifies domains from two DNS vantage points: the ISP resolver (where the
/// CICC sinkhole is visible) and a public DoH resolver (where the operator's
/// real infrastructure is). Sinkholed means the Philippine government has
/// already classified the domain as illegal gambling.
/// </summary>
public static class ScanCommand
{
    private enum Verdict { Protected, Sinkholed, SinkholedBackup, Live, Dead, Unknown }

    private sealed record Row(string Domain, Verdict Verdict, string LocalIp, string PublicIp, bool Listed);

    public static async Task<int> RunAsync(
        string repoRoot, string domainsCsv, string filePath,
        string extraSinkholeCsv, int concurrency, bool skipCanary)
    {
        var domains = await GatherInputAsync(domainsCsv, filePath);
        if (domains.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]error:[/] no domains given — use --domains a.com,b.com or --file list.txt");
            return 1;
        }

        using var http = HttpFactory.Create();

        var sinkhole = await Sinkhole.BuildAsync(http, DomainName.SplitList(extraSinkholeCsv));

        // Preflight: without a PH-resolver vantage every sinkholed domain
        // resolves to its real IP and silently reads as clean.
        var canaryHits = await sinkhole.CountCanaryHitsAsync();
        if (canaryHits == 0 && !skipCanary)
        {
            AnsiConsole.MarkupLine("[red]error:[/] none of the canary domains resolve to the CICC sinkhole.");
            AnsiConsole.MarkupLine("This machine has no PH-resolver vantage (VPN active, or foreign DNS configured),");
            AnsiConsole.MarkupLine("so sinkhole verdicts would be silently wrong. Disable the VPN / custom DNS,");
            AnsiConsole.MarkupLine("or pass [blue]--skip-canary[/] to classify liveness only.");
            return 1;
        }

        // The committed blocklist is loaded once up front; every row checks
        // against this cached set.
        var blocklist = await Blocklist.LoadAsync(repoRoot, http);

        List<Row> rows = [];
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"Scanning {domains.Count} domain(s)...", async _ =>
            {
                rows = await ClassifyAllAsync(domains, sinkhole, blocklist, http, concurrency, canaryHits > 0);
            });

        Render(rows, sinkhole, canaryHits, blocklist.Source);
        return 0;
    }

    private static async Task<List<string>> GatherInputAsync(string domainsCsv, string filePath)
    {
        var raw = new List<string>(DomainName.SplitList(domainsCsv));

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"domain file not found: {filePath}");

            raw.AddRange((await File.ReadAllLinesAsync(filePath))
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#')));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var domains = new List<string>();
        foreach (var r in raw)
        {
            var d = DomainName.Normalize(r);
            if (d is null)
                AnsiConsole.MarkupLine($"[grey]skip (not a domain):[/] {Markup.Escape(r)}");
            else if (seen.Add(d))
                domains.Add(d);
        }
        return domains;
    }

    private static async Task<List<Row>> ClassifyAllAsync(
        IReadOnlyList<string> domains, Sinkhole sinkhole, Blocklist blocklist,
        HttpClient http, int concurrency, bool sinkholeVantage)
    {
        using var gate = new SemaphoreSlim(concurrency);

        var rows = await Task.WhenAll(domains.Select(async d =>
        {
            await gate.WaitAsync();
            try
            {
                return await ClassifyAsync(d, sinkhole, blocklist, http, sinkholeVantage);
            }
            finally
            {
                gate.Release();
            }
        }));

        return rows.ToList();
    }

    private static async Task<Row> ClassifyAsync(
        string domain, Sinkhole sinkhole, Blocklist blocklist, HttpClient http, bool sinkholeVantage)
    {
        var listed = blocklist.Contains(domain);

        // Never scanned, never proposed — the policy check comes before DNS.
        if (Policy.IsNeverBlock(domain))
            return new Row(domain, Verdict.Protected, "-", "-", listed);

        var localTask = Resolver.SystemAsync(domain);
        var publicTask = Resolver.PublicAsync(http, domain);
        await Task.WhenAll(localTask, publicTask);
        var (local, @public) = (localTask.Result, publicTask.Result);

        var localIp = local.Addresses.FirstOrDefault() ?? StatusLabel(local.Status);
        var publicIp = @public.Addresses.FirstOrDefault() ?? StatusLabel(@public.Status);

        Verdict verdict;
        var sinkholedIp = local.Addresses.FirstOrDefault(sinkhole.Contains);
        if (sinkholeVantage && sinkholedIp is not null)
            verdict = sinkhole.IsBackupMatch(sinkholedIp) ? Verdict.SinkholedBackup : Verdict.Sinkholed;
        else if (@public.Status is ResolveStatus.NxDomain or ResolveStatus.NoAnswer)
            verdict = Verdict.Dead;
        else if (@public.Status is ResolveStatus.Ok)
            verdict = Verdict.Live;
        else
            verdict = Verdict.Unknown;

        return new Row(domain, verdict, localIp, publicIp, listed);
    }

    private static string StatusLabel(ResolveStatus s) => s switch
    {
        ResolveStatus.NxDomain => "NXDOMAIN",
        ResolveStatus.NoAnswer => "no A record",
        _ => "error",
    };

    private static void Render(List<Row> rows, Sinkhole sinkhole, int canaryHits, string blocklistSource)
    {
        AnsiConsole.MarkupLine(
            $"[grey]sinkhole:[/] {string.Join(", ", sinkhole.StaticAddresses)}" +
            (sinkhole.DynamicAddresses.Count > 0
                ? $" [grey]+ backup[/] {string.Join(", ", sinkhole.DynamicAddresses)}"
                : "") +
            $"   [grey]canaries:[/] {canaryHits}/{Sinkhole.Canaries.Length}   [grey]blocklist via[/] {blocklistSource}");
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("Domain");
        table.AddColumn("Local DNS");
        table.AddColumn("Public DNS");
        table.AddColumn("Verdict");
        table.AddColumn("Listed");

        foreach (var r in rows.OrderBy(r => r.Verdict).ThenBy(r => r.Domain, StringComparer.Ordinal))
        {
            table.AddRow(
                Markup.Escape(r.Domain),
                Markup.Escape(r.LocalIp),
                Markup.Escape(r.PublicIp),
                r.Verdict switch
                {
                    Verdict.Protected => "[blue]protected (.gov.ph)[/]",
                    Verdict.Sinkholed => "[red]SINKHOLED[/]",
                    Verdict.SinkholedBackup => "[orange1]SINKHOLED (backup ip)[/]",
                    Verdict.Live => "[yellow]live[/]",
                    Verdict.Dead => "[grey]dead[/]",
                    _ => "[grey]unknown[/]",
                },
                r.Listed ? "[green]yes[/]" : "no");
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        var confirmed = rows
            .Where(r => r.Verdict is Verdict.Sinkholed or Verdict.SinkholedBackup && !r.Listed)
            .Select(r => r.Domain)
            .ToList();
        var review = rows
            .Where(r => r.Verdict == Verdict.Live && !r.Listed)
            .Select(r => r.Domain)
            .ToList();

        AnsiConsole.MarkupLine(
            $"[red]{rows.Count(r => r.Verdict is Verdict.Sinkholed or Verdict.SinkholedBackup)}[/] sinkholed, " +
            $"[yellow]{rows.Count(r => r.Verdict == Verdict.Live)}[/] live, " +
            $"[grey]{rows.Count(r => r.Verdict == Verdict.Dead)}[/] dead, " +
            $"{rows.Count(r => r.Listed)} already listed");

        if (confirmed.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]CICC-confirmed and not yet listed — add with:[/]");
            AnsiConsole.MarkupLine($"  [blue]./add_scatters.sh[/] {Markup.Escape(string.Join(" ", confirmed))}");
        }

        if (review.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"[grey]Live but not CICC-flagged — needs manual review before listing:[/] " +
                Markup.Escape(string.Join(", ", review)));
        }
    }
}
