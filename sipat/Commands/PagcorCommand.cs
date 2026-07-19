using Sipat.Core;
using Spectre.Console;

namespace Sipat.Commands;

/// <summary>
/// Pulls CICC's PAGCOR-approved operator roster and reports which of those
/// operators Barikada does not yet block.
/// </summary>
public static class PagcorCommand
{
    public static async Task<int> RunAsync(string repoRoot, int concurrency, bool missingOnly)
    {
        using var http = HttpFactory.Create();
        var cicc = new CiccClient(http);

        IReadOnlyList<CiccOperator> roster = [];
        Blocklist? blocklist = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Fetching CICC roster...", async ctx =>
            {
                roster = await cicc.FetchRosterAsync();

                ctx.Status($"Resolving {roster.Count} /goto/ redirects...");
                await cicc.ResolveDomainsAsync(roster, concurrency);

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

        return 0;
    }

    private enum State { Blocked, Missing, Unresolved }

    private static State Classify(CiccOperator op, Blocklist list) =>
        op.Domain is null ? State.Unresolved
        : list.Contains(op.Domain) ? State.Blocked
        : State.Missing;
}
