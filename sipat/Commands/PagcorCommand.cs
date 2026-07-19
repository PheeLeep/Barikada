using Sipat.Core;
using Spectre.Console;

namespace Sipat.Commands;

/// <summary>
/// Pulls CICC's PAGCOR-approved operator roster and reports which of those
/// operators Barikada does not yet block.
/// </summary>
public static class PagcorCommand
{
    public static async Task<int> RunAsync(string repoRoot, int concurrency, bool missingOnly, bool refresh)
    {
        using var http = HttpFactory.Create();
        var cicc = new CiccClient(http);

        IReadOnlyList<CiccOperator> roster = [];
        Blocklist? blocklist = null;
        PagcorPdfResult? pdf = null;
        string? pdfError = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Fetching CICC roster...", async ctx =>
            {
                roster = await cicc.FetchRosterAsync();

                ctx.Status($"Resolving {roster.Count} /goto/ redirects...");
                await cicc.ResolveDomainsAsync(roster, concurrency);

                ctx.Status("Fetching PAGCOR provider PDF...");
                try { pdf = await PagcorPdf.LoadAsync(http, refresh); }
                catch (Exception e) { pdfError = e.Message; }

                ctx.Status("Reading Barikada blocklist...");
                blocklist = await Blocklist.LoadAsync(repoRoot, http);
            });

        if (blocklist is null) return 1;

        AnsiConsole.MarkupLine(
            $"[grey]roster:[/] {roster.Count} operators   " +
            $"[grey]blocklist:[/] {blocklist.Domains.Count} domains [grey]via[/] {blocklist.Source}");
        AnsiConsole.WriteLine();

        // Classify the whole roster; --missing-only narrows the table but the
        // summary below always reports against every operator.
        var all = roster
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .Select(o => (Op: o, State: Classify(o, blocklist)))
            .ToList();

        var rows = all.Where(r => !missingOnly || r.State == State.Missing).ToList();

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("Operator");
        table.AddColumn("Domain");
        table.AddColumn("Status");

        foreach (var (op, state) in rows)
        {
            table.AddRow(
                Markup.Escape(op.Name),
                op.Domain is null ? "[grey]unresolved[/]" : Markup.Escape(op.Domain),
                state switch
                {
                    State.Missing => "[red]MISSING[/]",
                    State.Blocked => "[green]blocked[/]",
                    _ => "[yellow]unresolved[/]",
                });
        }

        AnsiConsole.Write(table);

        var missing = all.Count(r => r.State == State.Missing);
        var unresolved = all.Count(r => r.State == State.Unresolved);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[red]{missing}[/] not yet blocked, [green]{all.Count(r => r.State == State.Blocked)}[/] already blocked" +
            (unresolved > 0 ? $", [yellow]{unresolved}[/] unresolved" : ""));

        if (missing > 0)
        {
            var csv = string.Join(",", all.Where(r => r.State == State.Missing).Select(r => r.Op.Domain));
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]Add them with:[/]");
            AnsiConsole.MarkupLine($"  [blue]./add_scatters.sh[/] {Markup.Escape(csv.Replace(",", " "))}");
        }

        ReportPdf(pdf, pdfError, roster, blocklist);
        return 0;
    }

    /// <summary>
    /// The PDF is the breadth source (every registered URL per licensee) but a
    /// dated snapshot — its "as of" date is always shown, and freshness is
    /// judged by how much of the live CICC roster it already knows about.
    /// </summary>
    private static void ReportPdf(
        PagcorPdfResult? pdf, string? pdfError, IReadOnlyList<CiccOperator> roster, Blocklist blocklist)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold]PAGCOR provider PDF[/]").LeftJustified().RuleStyle("grey"));

        if (pdf is null)
        {
            AnsiConsole.MarkupLine($"[yellow]unavailable:[/] {Markup.Escape(pdfError ?? "unknown error")}");
            return;
        }

        var asOf = pdf.AsOf?.ToString("dd MMMM yyyy") ?? "unknown date";
        var ageMonths = pdf.AsOf is { } asOfDate
            ? (DateOnly.FromDateTime(DateTime.UtcNow).DayNumber - asOfDate.DayNumber) / 30
            : (int?)null;

        AnsiConsole.MarkupLine(
            $"  [grey]as of:[/] {asOf}" +
            (ageMonths is > 6 ? $" [yellow](~{ageMonths} months old)[/]" : "") +
            $"   [grey]registered domains:[/] {pdf.Domains.Count}   [grey]source:[/] {Markup.Escape(pdf.Provenance)}");

        // How stale is the snapshot, measured against the live roster?
        var ciccDomains = roster.Where(o => o.Domain is not null).Select(o => o.Domain!).ToList();
        var unknownToPdf = ciccDomains.Count(cd => !pdf.Domains.Contains(cd) &&
            !pdf.Domains.Any(p => cd.EndsWith("." + p, StringComparison.OrdinalIgnoreCase)));
        if (unknownToPdf > 0)
            AnsiConsole.MarkupLine(
                $"  [yellow]{unknownToPdf}[/] of {ciccDomains.Count} live CICC operator domains are absent " +
                "from the PDF — expected for a stale snapshot; trust CICC for those");

        var uncovered = pdf.Domains
            .Where(d => !blocklist.Covers(d))
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();

        if (uncovered.Count == 0)
        {
            AnsiConsole.MarkupLine("  [green]every PDF-registered domain is already covered by the blocklist[/]");
            return;
        }

        AnsiConsole.MarkupLine($"  [red]{uncovered.Count}[/] registered domain(s) not covered:");
        foreach (var d in uncovered)
            AnsiConsole.MarkupLine($"    {Markup.Escape(d)}");
        AnsiConsole.MarkupLine(
            $"  [grey]Add with:[/] [blue]./add_scatters.sh[/] {Markup.Escape(string.Join(" ", uncovered))}");
    }

    private enum State { Blocked, Missing, Unresolved }

    private static State Classify(CiccOperator op, Blocklist list) =>
        op.Domain is null ? State.Unresolved
        : list.Covers(op.Domain) ? State.Blocked
        : State.Missing;
}
