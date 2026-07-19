using System.Text.Json;
using Sipat.Core;
using Spectre.Console;

namespace Sipat.Commands;

/// <summary>
/// Finds sibling domains through certificate transparency logs (crt.sh).
/// Operators mass-register numbered variants and put them behind real
/// certificates, so a substring search over issued certs returns domains that
/// actually exist — unlike guessing numbers by hand.
/// </summary>
public static class DiscoverCommand
{
    private const int MinKeywordLength = 4;
    private const int TableCap = 100;

    /// <summary>How long a crt.sh result is served from cache without re-querying.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    public static async Task<int> RunAsync(
        string repoRoot, string keywordsCsv, string filePath, string outputPath, bool refresh)
    {
        var keywords = await GatherKeywordsAsync(keywordsCsv, filePath);
        if (keywords.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]error:[/] no usable keywords — use --keywords taya,jili or --file list.txt");
            return 1;
        }

        // crt.sh is routinely slow on wildcard queries; give it more room
        // than the default client would.
        using var http = HttpFactory.Create(timeout: TimeSpan.FromSeconds(90));

        var blocklist = await Blocklist.LoadAsync(repoRoot, http);

        // candidate domain -> keyword that found it
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        var sources = new List<string>();   // per-keyword provenance for display

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Querying crt.sh...", async ctx =>
            {
                foreach (var kw in keywords)
                {
                    var cacheName = $"crtsh-{kw}.txt";
                    var cached = Cache.Read(cacheName, out var age);

                    // Fresh cache answers without touching crt.sh at all;
                    // --refresh forces a live query.
                    if (!refresh && cached is not null && age < CacheTtl)
                    {
                        foreach (var d in cached.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                            found.TryAdd(d.Trim(), kw);
                        sources.Add($"{kw} (cache {Cache.Describe(age)})");
                        continue;
                    }

                    // crt.sh degrades often: 502s, and — worse — an empty []
                    // when its DB hits statement timeout, which is
                    // indistinguishable from a genuine no-match. Retry, then
                    // fall back to a stale cache before giving up, and report
                    // an empty result as inconclusive, never as "no siblings".
                    const int attempts = 3;
                    var ok = false;
                    for (var attempt = 1; attempt <= attempts && !ok; attempt++)
                    {
                        ctx.Status($"crt.sh: %{kw}% (attempt {attempt}/{attempts})...");
                        try
                        {
                            var domains = await QueryCrtShAsync(http, kw);
                            if (domains.Count > 0)
                            {
                                foreach (var d in domains) found.TryAdd(d, kw);
                                Cache.Write(cacheName, string.Join('\n', domains));
                                sources.Add($"{kw} (crt.sh)");
                                ok = true;
                            }
                        }
                        catch (Exception)
                        {
                            // retry below; final failure handled after the loop
                        }

                        if (!ok && attempt < attempts)
                            await Task.Delay(TimeSpan.FromSeconds(3 * attempt));
                    }

                    if (!ok)
                    {
                        if (cached is not null)
                        {
                            foreach (var d in cached.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                                found.TryAdd(d.Trim(), kw);
                            sources.Add($"{kw} (STALE cache {Cache.Describe(age)})");
                            AnsiConsole.MarkupLine(
                                $"[yellow]crt.sh unavailable for '{Markup.Escape(kw)}' — using cached results " +
                                $"from {Cache.Describe(age)} ago[/]");
                        }
                        else
                        {
                            failures.Add($"{kw}: no result after {attempts} attempts and no cache — inconclusive");
                            sources.Add($"{kw} (failed)");
                        }
                    }
                }
            });

        foreach (var f in failures)
            AnsiConsole.MarkupLine($"[yellow]crt.sh query failed:[/] {Markup.Escape(f)}");

        var protectedCount = 0;
        var listed = new List<string>();
        var fresh = new List<(string Domain, string Keyword)>();

        foreach (var (domain, kw) in found)
        {
            if (Policy.IsNeverBlock(domain)) { protectedCount++; continue; }
            if (blocklist.Contains(domain)) listed.Add(domain);
            else fresh.Add((domain, kw));
        }

        fresh.Sort((a, b) => string.CompareOrdinal(a.Domain, b.Domain));

        AnsiConsole.MarkupLine(
            $"[grey]keywords:[/] {Markup.Escape(string.Join(", ", sources))}   " +
            $"[grey]certs seen:[/] {found.Count} domains   [grey]blocklist via[/] {Markup.Escape(blocklist.Source)}");
        AnsiConsole.WriteLine();

        if (fresh.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"No new candidates — {listed.Count} already listed" +
                (protectedCount > 0 ? $", {protectedCount} protected" : "") + ".");
            // Inconclusive queries must not read as a clean result.
            return failures.Count > 0 ? 2 : 0;
        }

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("Candidate");
        table.AddColumn("Keyword");

        foreach (var (domain, kw) in fresh.Take(TableCap))
            table.AddRow(Markup.Escape(domain), Markup.Escape(kw));

        AnsiConsole.Write(table);
        if (fresh.Count > TableCap)
            AnsiConsole.MarkupLine($"[grey]... and {fresh.Count - TableCap} more (use --output to capture all)[/]");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[yellow]{fresh.Count}[/] new candidates, [green]{listed.Count}[/] already listed" +
            (protectedCount > 0 ? $", [blue]{protectedCount}[/] protected" : ""));

        // Discovery only proves a cert was issued — classification decides
        // whether anything is actually gambling infrastructure.
        AnsiConsole.WriteLine();
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            await File.WriteAllLinesAsync(outputPath, fresh.Select(f => f.Domain));
            AnsiConsole.MarkupLine($"[grey]Wrote {fresh.Count} candidates to[/] {Markup.Escape(outputPath)}");
            AnsiConsole.MarkupLine($"[grey]Classify with:[/] [blue]sipat scan -f {Markup.Escape(outputPath)}[/]");
        }
        else if (fresh.Count <= 20)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Classify with:[/] [blue]sipat scan -d {Markup.Escape(string.Join(",", fresh.Select(f => f.Domain)))}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[grey]Re-run with[/] [blue]--output candidates.txt[/] [grey]then[/] [blue]sipat scan -f candidates.txt[/]");
        }

        return 0;
    }

    /// <summary>
    /// Accepts brand keywords or domains; domains are reduced to their brand
    /// label (phtaya.com -> phtaya). Too-short keywords are dropped because a
    /// crt.sh substring search on them returns unrelated certs by the thousand.
    /// </summary>
    private static async Task<List<string>> GatherKeywordsAsync(string keywordsCsv, string filePath)
    {
        var raw = new List<string>(DomainName.SplitList(keywordsCsv));

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"keyword file not found: {filePath}");

            raw.AddRange((await File.ReadAllLinesAsync(filePath))
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#')));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keywords = new List<string>();
        foreach (var r in raw)
        {
            var kw = r.Contains('.')
                ? DomainName.Normalize(r)?.Split('.')[0] ?? ""
                : r.Trim().ToLowerInvariant();

            if (kw.Length < MinKeywordLength)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]skip keyword '{Markup.Escape(r)}':[/] shorter than {MinKeywordLength} chars, too noisy for CT search");
                continue;
            }
            if (seen.Add(kw)) keywords.Add(kw);
        }
        return keywords;
    }

    private static async Task<IReadOnlyList<string>> QueryCrtShAsync(HttpClient http, string keyword)
    {
        var url = $"https://crt.sh/?q={Uri.EscapeDataString($"%{keyword}%")}&output=json";
        var json = await http.GetStringAsync(url);

        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);

        foreach (var cert in doc.RootElement.EnumerateArray())
        {
            if (!cert.TryGetProperty("name_value", out var names)) continue;

            // name_value packs all SANs newline-separated; wildcards come as
            // *.domain. A cert can carry unrelated SANs, so each name is
            // re-checked against the keyword rather than trusted wholesale.
            foreach (var line in (names.GetString() ?? "").Split('\n'))
            {
                var name = line.Trim();
                if (name.StartsWith("*.", StringComparison.Ordinal)) name = name[2..];

                var d = DomainName.Normalize(name);
                if (d is not null && d.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    domains.Add(d);
            }
        }

        return [.. domains];
    }
}
