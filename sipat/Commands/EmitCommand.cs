using System.Globalization;
using System.Text.RegularExpressions;
using Sipat.Core;
using Spectre.Console;

namespace Sipat.Commands;

/// <summary>
/// Appends confirmed domains to the three blocklists, mirroring how
/// add_scatters.sh and the maintainer insert by hand: under the dated line of
/// the target section, bumping that date to today.
/// <para>
/// Each list has two sections — PAGCOR-regulated at the top (anchored by an
/// "Updated last &lt;date&gt;" line) and POGO/illegal below (anchored by
/// "(updated Last &lt;date&gt;)"). Default target is the POGO section;
/// <c>--pagcor</c> switches to the licensed section. Dedup runs against both
/// the committed state of main and the working tree, so neither uncommitted
/// nor published entries are ever doubled.
/// </para>
/// </summary>
public static partial class EmitCommand
{
    // "! (updated Last October 25, 2025)" — POGO/illegal section anchor.
    [GeneratedRegex(@"^(?<p>[!#]) \(updated last .*\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PogoAnchor();

    // "! Updated Last July 18, 2026" / "# Updated last ..." — PAGCOR section anchor.
    [GeneratedRegex(@"^(?<p>[!#]) Updated last .*$", RegexOptions.IgnoreCase)]
    private static partial Regex PagcorAnchor();

    private static string Format(ListKind kind, string domain) => kind switch
    {
        ListKind.UBlock => $"||{domain}^$all",
        ListKind.Hosts => $"0.0.0.0 {domain}",
        ListKind.UBlacklist => $"*://*.{domain}/*",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static async Task<int> RunAsync(
        string repoRoot, string domainsCsv, string filePath, bool pagcorSection, bool dryRun)
    {
        var domains = await GatherInputAsync(domainsCsv, filePath);
        if (domains.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]error:[/] no domains given — use --domains a.com,b.com or --file list.txt");
            return 1;
        }

        // Hard policy gate before anything else touches the lists.
        var refused = domains.Where(Policy.IsNeverBlock).ToList();
        foreach (var d in refused)
            AnsiConsole.MarkupLine($"[red]refused (protected domain):[/] {Markup.Escape(d)}");
        domains = domains.Where(d => !Policy.IsNeverBlock(d)).ToList();
        if (domains.Count == 0) return 1;

        using var http = HttpFactory.Create();
        var canonical = await Blocklist.LoadAsync(repoRoot, http);

        // Working-tree state per list, so uncommitted additions also dedupe.
        var files = new Dictionary<ListKind, string[]>();
        var working = new Dictionary<ListKind, HashSet<string>>();
        foreach (var (kind, file) in Blocklist.Files)
        {
            var path = Path.Combine(repoRoot, file);
            if (!File.Exists(path))
            {
                AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(file)} not found in {Markup.Escape(repoRoot)}");
                return 1;
            }

            var lines = await File.ReadAllLinesAsync(path);
            files[kind] = lines;

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pattern = Blocklist.PatternFor(kind);
            foreach (var line in lines)
            {
                var m = pattern.Match(line.Trim());
                if (!m.Success) continue;
                var d = DomainName.Normalize(m.Groups["d"].Value);
                if (d is not null) set.Add(d);
            }
            working[kind] = set;
        }

        // Per-list plan: hosts has no wildcards so it always needs the exact
        // entry; the wildcard lists skip domains a parent entry already covers.
        var perList = new Dictionary<ListKind, List<string>>
        {
            [ListKind.UBlock] = [], [ListKind.Hosts] = [], [ListKind.UBlacklist] = [],
        };
        var added = new List<string>();

        foreach (var d in domains)
        {
            if (canonical.Contains(d) || working.Values.All(s => s.Contains(d)))
            {
                AnsiConsole.MarkupLine($"[grey]skip (already listed):[/] {Markup.Escape(d)}");
                continue;
            }

            var wanted = false;
            foreach (var (kind, set) in working)
            {
                var covered = kind == ListKind.Hosts
                    ? set.Contains(d)
                    : set.Contains(d) || HasParentIn(set, d);
                if (covered) continue;

                perList[kind].Add(d);
                wanted = true;
            }

            if (!wanted)
            {
                AnsiConsole.MarkupLine($"[grey]skip (already covered everywhere):[/] {Markup.Escape(d)}");
                continue;
            }

            if (perList[ListKind.Hosts].Contains(d) && !perList[ListKind.UBlock].Contains(d))
                AnsiConsole.MarkupLine(
                    $"[grey]note:[/] {Markup.Escape(d)} is parent-covered in the wildcard lists — adding to hosts only");
            added.Add(d);
        }

        if (added.Count == 0)
        {
            AnsiConsole.MarkupLine("Nothing to add.");
            return 0;
        }

        var today = DateTime.Now.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
        var section = pagcorSection ? "PAGCOR-regulated" : "POGO/illegal";

        AnsiConsole.MarkupLine(
            $"Adding [yellow]{added.Count}[/] domain(s) to the [bold]{section}[/] section, dated {today} " +
            $"[grey](deduped against {Markup.Escape(canonical.Source)} + working tree)[/]:");
        foreach (var d in added)
            AnsiConsole.MarkupLine($"  [green]+[/] {Markup.Escape(d)}");

        // Build every new file before writing any — a missing anchor must
        // abort the whole emit, not leave the three lists half-updated.
        var newContents = new Dictionary<ListKind, string[]>();
        foreach (var (kind, lines) in files)
        {
            if (perList[kind].Count == 0) continue;

            var updated = Insert(lines, kind, perList[kind], pagcorSection, today);
            if (updated is null)
            {
                AnsiConsole.MarkupLine(
                    $"[red]error:[/] no {section} date anchor found in {Blocklist.Files[kind]} — nothing written");
                return 1;
            }
            newContents[kind] = updated;
        }

        if (dryRun)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Dry run[/] — no files written. Entries would be:");
            foreach (var (kind, list) in perList.Where(kv => kv.Value.Count > 0))
                foreach (var d in list)
                    AnsiConsole.MarkupLine($"  {Markup.Escape(Blocklist.Files[kind])}: {Markup.Escape(Format(kind, d))}");
            return 0;
        }

        foreach (var (kind, content) in newContents)
        {
            await File.WriteAllLinesAsync(Path.Combine(repoRoot, Blocklist.Files[kind]), content);
            AnsiConsole.MarkupLine($"  [grey]updated[/] {Markup.Escape(Blocklist.Files[kind])}");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Done. Review with: [blue]git diff[/]");
        return 0;
    }

    /// <summary>Bumps the section's date line to today and inserts entries under it.</summary>
    private static string[]? Insert(
        string[] lines, ListKind kind, List<string> domains, bool pagcorSection, string today)
    {
        var anchor = pagcorSection ? PagcorAnchor() : PogoAnchor();

        for (var i = 0; i < lines.Length; i++)
        {
            var m = anchor.Match(lines[i]);
            if (!m.Success) continue;

            var prefix = m.Groups["p"].Value;
            var dateLine = pagcorSection
                ? $"{prefix} Updated last {today}"
                : $"{prefix} (updated Last {today})";

            return
            [
                .. lines[..i],
                dateLine,
                .. domains.Select(d => Format(kind, d)),
                .. lines[(i + 1)..],
            ];
        }

        return null;
    }

    private static bool HasParentIn(HashSet<string> set, string domain)
    {
        for (var i = domain.IndexOf('.'); i >= 0; i = domain.IndexOf('.', i + 1))
            if (set.Contains(domain[(i + 1)..]))
                return true;
        return false;
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
}
