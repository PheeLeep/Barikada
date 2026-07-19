using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Sipat.Core;

public enum ListKind { UBlock, Hosts, UBlacklist }

/// <summary>
/// The canonical view of what Barikada already blocks.
/// <para>
/// Entries are read from the committed state of <c>main</c> rather than the
/// working tree, so a dirty checkout or a feature branch can't cause Sipat to
/// re-add domains that are already published.
/// </para>
/// </summary>
public sealed partial class Blocklist
{
    public const string Remote = "https://raw.githubusercontent.com/PheeLeep/Barikada/main";
    private const string Branch = "main";

    public static readonly IReadOnlyDictionary<ListKind, string> Files = new Dictionary<ListKind, string>
    {
        [ListKind.UBlock] = "anti_scatter.txt",
        [ListKind.Hosts] = "barikada_hosts.txt",
        [ListKind.UBlacklist] = "ublacklist_antiscatter.txt",
    };

    [GeneratedRegex(@"^\|\|(?<d>[^\^]+)\^\$all$")] private static partial Regex UBlockEntry();
    [GeneratedRegex(@"^0\.0\.0\.0 (?<d>.+)$")] private static partial Regex HostsEntry();
    [GeneratedRegex(@"^\*://\*\.?(?<d>[^/]+)/\*$")] private static partial Regex UBlacklistEntry();

    /// <summary>Entry pattern for one list format; the domain is group "d".</summary>
    public static Regex PatternFor(ListKind kind) => kind switch
    {
        ListKind.UBlock => UBlockEntry(),
        ListKind.Hosts => HostsEntry(),
        ListKind.UBlacklist => UBlacklistEntry(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private readonly HashSet<string> _domains = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every domain present in any of the three lists.</summary>
    public IReadOnlySet<string> Domains => _domains;

    public bool Contains(string domain) => _domains.Contains(domain);

    /// <summary>
    /// True when the domain or any parent of it is listed — uBlock's
    /// <c>||domain^</c> and uBlacklist's <c>*://*.domain/*</c> cover
    /// subdomains implicitly, so <c>play.bingoplus.com</c> is covered by a
    /// <c>bingoplus.com</c> entry.
    /// </summary>
    public bool Covers(string domain)
    {
        if (_domains.Contains(domain)) return true;
        for (var i = domain.IndexOf('.'); i >= 0; i = domain.IndexOf('.', i + 1))
            if (_domains.Contains(domain[(i + 1)..]))
                return true;
        return false;
    }

    /// <summary>Source the load actually came from, for display.</summary>
    public string Source { get; private init; } = "";

    public static async Task<Blocklist> LoadAsync(string repoRoot, HttpClient http, CancellationToken ct = default)
    {
        var (texts, source) = await ReadCanonicalAsync(repoRoot, http, ct);
        var list = new Blocklist { Source = source };

        foreach (var (kind, text) in texts)
        {
            var pattern = PatternFor(kind);

            foreach (var line in text.Split('\n'))
            {
                var m = pattern.Match(line.Trim());
                if (!m.Success) continue;

                // Hosts entries carry www. variants the other two lists omit.
                var d = DomainName.Normalize(m.Groups["d"].Value);
                if (d is not null) list._domains.Add(d);
            }
        }

        return list;
    }

    private static async Task<(Dictionary<ListKind, string>, string)> ReadCanonicalAsync(
        string repoRoot, HttpClient http, CancellationToken ct)
    {
        var texts = new Dictionary<ListKind, string>();

        if (TryGit(repoRoot, out _, "rev-parse", "--git-dir"))
        {
            foreach (var (kind, file) in Files)
            {
                if (!TryGit(repoRoot, out var text, "show", $"{Branch}:{file}"))
                    throw new InvalidOperationException($"could not read {file} from {Branch}");
                texts[kind] = text;
            }

            // The commit hash pins exactly which published state this run
            // classified against.
            TryGit(repoRoot, out var sha, "rev-parse", "--short", Branch);
            return (texts, $"git {Branch} @ {sha.Trim()}");
        }

        var notes = new List<string>();
        foreach (var (kind, file) in Files)
            texts[kind] = await FetchWithCacheAsync(http, file, notes, ct);

        return (texts, $"remote ({string.Join(", ", notes.Distinct())})");
    }

    /// <summary>
    /// Conditional GET against raw.githubusercontent using the ETag from the
    /// previous fetch (GitHub answers 304 when the file is unchanged). When
    /// the network is down entirely, the last cached copy is used and the
    /// source string says so instead of failing the run.
    /// </summary>
    private static async Task<string> FetchWithCacheAsync(
        HttpClient http, string file, List<string> notes, CancellationToken ct)
    {
        var etag = Cache.Read($"{file}.etag", out _);
        var cached = Cache.Read(file, out var age);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{Remote}/{file}");
            if (etag is not null && cached is not null)
                req.Headers.TryAddWithoutValidation("If-None-Match", etag.Trim());

            using var res = await http.SendAsync(req, ct);

            if (res.StatusCode == System.Net.HttpStatusCode.NotModified && cached is not null)
            {
                notes.Add("etag-validated cache");
                return cached;
            }

            res.EnsureSuccessStatusCode();
            var body = await res.Content.ReadAsStringAsync(ct);

            Cache.Write(file, body);
            var newTag = res.Headers.ETag?.Tag;
            if (newTag is not null) Cache.Write($"{file}.etag", newTag);

            notes.Add("fetched");
            return body;
        }
        catch (Exception) when (cached is not null)
        {
            notes.Add($"STALE cache {Cache.Describe(age)}, network failed");
            return cached;
        }
    }

    private static bool TryGit(string repoRoot, out string output, params string[] args)
    {
        output = "";
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return false;

            output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (Exception)
        {
            // git missing or not a checkout — caller falls back to the remote.
            return false;
        }
    }
}
