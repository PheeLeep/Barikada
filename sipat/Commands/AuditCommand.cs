using Sipat.Core;
using Spectre.Console;

namespace Sipat.Commands;

/// <summary>
/// Report-only health check of the three blocklists. Audits the working-tree
/// files (what you are about to commit), unlike scan/pagcor which read the
/// committed state of main. Never proposes deletions — findings are printed
/// for the maintainer to act on.
/// </summary>
public static class AuditCommand
{
    private sealed record Entry(int LineNo, string Raw, string? Normalized);

    private enum SiteCategory { Pagcor, Pogo }

    private sealed class FileAudit
    {
        public required ListKind Kind { get; init; }
        public required string FileName { get; init; }
        public List<Entry> Entries { get; } = [];
        public List<(int LineNo, string Line)> Malformed { get; } = [];
        public List<(string Domain, int Count)> Duplicates { get; } = [];
        public List<string> Suspicious { get; } = [];
        public HashSet<string> Domains { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, SiteCategory> Categories { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static async Task<int> RunAsync(string repoRoot, bool dns, int concurrency)
    {
        var audits = new List<FileAudit>();
        foreach (var (kind, file) in Blocklist.Files)
        {
            var path = Path.Combine(repoRoot, file);
            if (!File.Exists(path))
            {
                AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(file)} not found in {Markup.Escape(repoRoot)}");
                return 1;
            }
            audits.Add(AuditFile(kind, file, await File.ReadAllLinesAsync(path)));
        }

        ReportSummary(audits);

        var findings = 0;
        findings += ReportPerFile(audits);
        findings += ReportProtected(audits);
        findings += ReportCrossFile(audits);

        if (dns) await ReportDnsAsync(audits, concurrency);

        AnsiConsole.WriteLine();
        if (findings == 0)
        {
            AnsiConsole.MarkupLine("[green]No structural findings.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"[yellow]{findings}[/] structural finding(s) — report only, nothing was modified.");
        return 2;
    }

    private static FileAudit AuditFile(ListKind kind, string fileName, string[] lines)
    {
        var audit = new FileAudit { Kind = kind, FileName = fileName };
        var pattern = Blocklist.PatternFor(kind);
        var commentPrefix = kind == ListKind.UBlock ? '!' : '#';
        var rawCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Each list opens with the PAGCOR-regulated section; a "POGO" section
        // header switches every entry after it to the offshore bucket.
        var category = SiteCategory.Pagcor;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (line[0] == commentPrefix)
            {
                if (line.Contains("POGO", StringComparison.OrdinalIgnoreCase))
                    category = SiteCategory.Pogo;
                else if (line.Contains("PAGCOR", StringComparison.OrdinalIgnoreCase))
                    category = SiteCategory.Pagcor;
                continue;
            }

            // uBlacklist carries a YAML frontmatter block the parser must not
            // count as malformed.
            if (kind == ListKind.UBlacklist && (line == "---" || line.StartsWith("name:", StringComparison.Ordinal)))
                continue;

            var m = pattern.Match(line);
            if (!m.Success)
            {
                audit.Malformed.Add((i + 1, line));
                continue;
            }

            var raw = m.Groups["d"].Value.ToLowerInvariant();
            var normalized = DomainName.Normalize(raw);

            audit.Entries.Add(new Entry(i + 1, raw, normalized));
            rawCounts[raw] = rawCounts.GetValueOrDefault(raw) + 1;

            if (normalized is null)
            {
                // Matches the entry syntax but isn't domain-shaped once the
                // extras are stripped (path entries and the like).
                audit.Suspicious.Add(raw);
            }
            else
            {
                audit.Domains.Add(normalized);
                audit.Categories.TryAdd(normalized, category);

                // Real TLDs are alphabetic (or punycode xn--). A hyphen or
                // digit in the final label means a mangled entry, e.g.
                // "okadaonlinecasino.commain-lobby".
                var tld = normalized[(normalized.LastIndexOf('.') + 1)..];
                if (!tld.StartsWith("xn--", StringComparison.Ordinal) && !tld.All(char.IsAsciiLetter))
                    audit.Suspicious.Add(raw);
            }
        }

        foreach (var (raw, count) in rawCounts.Where(kv => kv.Value > 1).OrderBy(kv => kv.Key))
            audit.Duplicates.Add((raw, count));

        return audit;
    }

    /// <summary>
    /// Category totals over the union of the three lists (hosts carries www.
    /// variants, so per-file counts would double-count the same site).
    /// </summary>
    private static void ReportSummary(List<FileAudit> audits)
    {
        var categories = new Dictionary<string, SiteCategory>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in audits)
            foreach (var (domain, category) in a.Categories)
                categories.TryAdd(domain, category);

        var total = categories.Count;
        var pagcor = categories.Count(kv => kv.Value == SiteCategory.Pagcor);
        var pogo = total - pagcor;

        static string Ratio(int n, int total) =>
            total == 0 ? "-" : $"{n}/{total} ({100.0 * n / total:0.0}%)";

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title("[bold]:bar_chart: Summary[/]");
        table.AddColumn("Category");
        table.AddColumn(new TableColumn("Domains").RightAligned());
        table.AddColumn(new TableColumn("Ratio").RightAligned());

        table.AddRow(":slot_machine: Total gambling sites", $"[bold]{total}[/]", "");
        table.AddRow(":classical_building: PAGCOR-licensed", pagcor.ToString(), Ratio(pagcor, total));
        table.AddRow(":globe_showing_asia_australia: POGO (offshore)", pogo.ToString(), Ratio(pogo, total));

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static int ReportPerFile(List<FileAudit> audits)
    {
        var findings = 0;

        var overview = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title("[bold]:clipboard: Blocklists[/]");
        overview.AddColumn("File");
        overview.AddColumn(new TableColumn("Entries").RightAligned());
        overview.AddColumn(new TableColumn("Unique domains").RightAligned());
        overview.AddColumn(new TableColumn("Malformed").RightAligned());
        overview.AddColumn(new TableColumn("Duplicates").RightAligned());
        overview.AddColumn(new TableColumn("Suspicious").RightAligned());

        foreach (var a in audits)
        {
            var suspicious = a.Suspicious.Distinct().Count();
            overview.AddRow(
                Markup.Escape(a.FileName),
                a.Entries.Count.ToString(),
                a.Domains.Count.ToString(),
                a.Malformed.Count == 0 ? "[green]0[/]" : $"[red]{a.Malformed.Count}[/]",
                a.Duplicates.Count == 0 ? "[green]0[/]" : $"[yellow]{a.Duplicates.Count}[/]",
                suspicious == 0 ? "[green]0[/]" : $"[orange1]{suspicious}[/]");
            findings += a.Malformed.Count + a.Duplicates.Count + suspicious;
        }

        AnsiConsole.Write(overview);
        AnsiConsole.WriteLine();

        foreach (var a in audits)
        {
            if (a.Malformed.Count == 0 && a.Duplicates.Count == 0 && a.Suspicious.Count == 0)
                continue;

            AnsiConsole.Write(new Rule($"[bold]{a.FileName}[/]").LeftJustified().RuleStyle("grey"));

            foreach (var (lineNo, line) in a.Malformed)
                AnsiConsole.MarkupLine($"  [red]malformed[/] line {lineNo}: {Markup.Escape(line)}");

            foreach (var (domain, count) in a.Duplicates)
                AnsiConsole.MarkupLine($"  [yellow]duplicate[/] x{count}: {Markup.Escape(domain)}");

            foreach (var raw in a.Suspicious.Distinct())
                AnsiConsole.MarkupLine($"  [orange1]suspicious[/]: {Markup.Escape(raw)}");

            AnsiConsole.WriteLine();
        }

        return findings;
    }

    private static int ReportProtected(List<FileAudit> audits)
    {
        var hits = audits
            .SelectMany(a => a.Domains.Where(Policy.IsNeverBlock).Select(d => (a.FileName, Domain: d)))
            .ToList();

        foreach (var (file, domain) in hits)
            AnsiConsole.MarkupLine($"[red]protected domain listed[/] in {file}: {Markup.Escape(domain)}");

        return hits.Count;
    }

    /// <summary>
    /// uBlock's <c>||domain^</c> and uBlacklist's <c>*://*.domain/*</c> match
    /// subdomains implicitly; a hosts file cannot, so it carries explicit
    /// subdomain lines. Coverage therefore means "the domain or any parent of
    /// it is listed" for the wildcard formats, exact match for hosts.
    /// </summary>
    private static bool Covers(FileAudit audit, string domain)
    {
        if (audit.Domains.Contains(domain)) return true;
        if (audit.Kind == ListKind.Hosts) return false;

        for (var i = domain.IndexOf('.'); i >= 0; i = domain.IndexOf('.', i + 1))
            if (audit.Domains.Contains(domain[(i + 1)..]))
                return true;
        return false;
    }

    private static int ReportCrossFile(List<FileAudit> audits)
    {
        var union = new HashSet<string>(audits.SelectMany(a => a.Domains), StringComparer.OrdinalIgnoreCase);
        var drift = union
            .Select(d => (Domain: d, Missing: audits.Where(a => !Covers(a, d)).Select(a => a.FileName).ToList()))
            .Where(x => x.Missing.Count > 0)
            .OrderBy(x => x.Domain, StringComparer.Ordinal)
            .ToList();

        if (drift.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]cross-file:[/] all three lists cover the same domains");
            return 0;
        }

        AnsiConsole.Write(new Rule("[bold]cross-file drift[/]").LeftJustified().RuleStyle("grey"));
        const int cap = 30;
        foreach (var (domain, missing) in drift.Take(cap))
            AnsiConsole.MarkupLine($"  {Markup.Escape(domain)} [grey]missing from[/] {string.Join(", ", missing)}");
        if (drift.Count > cap)
            AnsiConsole.MarkupLine($"  [grey]... and {drift.Count - cap} more[/]");

        AnsiConsole.MarkupLine($"[yellow]{drift.Count}[/] domain(s) not present in all three lists");
        return drift.Count;
    }

    /// <summary>
    /// DNS pass over every listed domain: how many does CICC also block, how
    /// many no longer resolve. Informational — dead gambling domains get
    /// re-registered, so nothing is proposed for removal.
    /// </summary>
    private static async Task ReportDnsAsync(List<FileAudit> audits, int concurrency)
    {
        var domains = audits
            .SelectMany(a => a.Domains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(d => !Policy.IsNeverBlock(d))
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();

        using var http = HttpFactory.Create();
        var sinkhole = await Sinkhole.BuildAsync(http, []);
        var canaryHits = await sinkhole.CountCanaryHitsAsync();

        if (canaryHits == 0)
            AnsiConsole.MarkupLine(
                "[yellow]warning:[/] no PH-resolver vantage (VPN?) — sinkhole counts will read as zero");

        int sinkholed = 0, live = 0, dead = 0, errors = 0;
        var deadList = new List<string>();
        var gate = new SemaphoreSlim(concurrency);

        await AnsiConsole.Progress()
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new RemainingTimeColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask($"resolving {domains.Count} domains", maxValue: domains.Count);

                await Task.WhenAll(domains.Select(async d =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        var localTask = Resolver.SystemAsync(d);
                        var publicTask = Resolver.PublicAsync(http, d);
                        await Task.WhenAll(localTask, publicTask);

                        if (localTask.Result.Addresses.Any(sinkhole.Contains))
                            Interlocked.Increment(ref sinkholed);
                        else if (publicTask.Result.Status is ResolveStatus.NxDomain or ResolveStatus.NoAnswer)
                        {
                            Interlocked.Increment(ref dead);
                            lock (deadList) deadList.Add(d);
                        }
                        else if (publicTask.Result.Status == ResolveStatus.Ok)
                            Interlocked.Increment(ref live);
                        else
                            Interlocked.Increment(ref errors);
                    }
                    finally
                    {
                        gate.Release();
                        task.Increment(1);
                    }
                }));
            });

        gate.Dispose();

        AnsiConsole.Write(new Rule($"[bold]DNS health (informational)[/] ([grey]canaries {canaryHits}/{Sinkhole.Canaries.Length}[/])").LeftJustified().RuleStyle("grey"));

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("Status");
        table.AddColumn(new TableColumn("Domains").RightAligned());
        table.AddRow(":prohibited: CICC-sinkholed", $"[red]{sinkholed}[/]");
        table.AddRow(":green_circle: Live (not CICC-blocked)", $"[yellow]{live}[/]");
        table.AddRow(":skull: Dead links", $"[grey]{dead}[/]");
        table.AddRow(":warning: Errors during lookup", errors.ToString());
        AnsiConsole.Write(table);

        if (deadList.Count > 0)
        {
            deadList.Sort(StringComparer.Ordinal);
            const int cap = 50;
            AnsiConsole.MarkupLine("  [grey]dead (no DNS — left listed, domains get re-registered):[/]");
            foreach (var d in deadList.Take(cap))
                AnsiConsole.MarkupLine($"    {Markup.Escape(d)}");
            if (deadList.Count > cap)
                AnsiConsole.MarkupLine($"    [grey]... and {deadList.Count - cap} more[/]");
        }
    }
}
