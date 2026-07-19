using ArgSharp;
using ArgSharp.Args;
using Sipat.Commands;
using Spectre.Console;

namespace Sipat;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // ArgSharp invokes subcommand actions during Parse(), before option
        // values are readable, so each action only records what to run and the
        // work happens afterwards.
        Func<Task<int>>? run = null;

        ArgSharpClass.Init(
            "sipat",
            "Sipat",
            "Scouts Philippine gambling domains and feeds the Barikada blocklists.",
            "Domains are given comma-separated: --domains a.com,b.com");

        ArgInvoke pagcor = null!;
        pagcor = ArgSharpClass.AddArgumentAction(["pagcor"],
            () => run = () => PagcorCommand.RunAsync(
                RepoRoot(),
                pagcor.GetValue<int>("--concurrency"),
                pagcor.GetValue<bool>("--missing-only"),
                pagcor.GetValue<bool>("--refresh")),
            "Report PAGCOR-approved operators that Barikada does not block yet.");
        pagcor.AddArgument(["--concurrency", "-c"], "N", "Parallel redirect lookups.", 16);
        pagcor.AddArgument(["--missing-only", "-m"], "", "Show only unblocked operators.", false);
        pagcor.AddArgument(["--refresh"], "", "Bypass the 7-day PAGCOR PDF cache.", false);
        // Every option has a default, so a bare `sipat pagcor` should run
        // rather than fall back to printing usage.
        pagcor.ArgumentZeroAction = ArgSharpClass.ArgZeroAction.TreatAsSuccess;

        ArgInvoke scan = null!;
        scan = ArgSharpClass.AddArgumentAction(["scan"],
            () => run = () => ScanCommand.RunAsync(
                RepoRoot(),
                scan.GetValue<string>("--domains"),
                scan.GetValue<string>("--file"),
                scan.GetValue<string>("--sinkhole"),
                scan.GetValue<int>("--concurrency"),
                scan.GetValue<bool>("--skip-canary")),
            "Classify domains against the CICC sinkhole and live infrastructure.");
        scan.AddArgument(["--domains", "-d"], "CSV", "Comma-separated domains or URLs.", "");
        scan.AddArgument(["--file", "-f"], "PATH", "File with one domain per line (# comments).", "");
        scan.AddArgument(["--sinkhole"], "CSV", "Extra sinkhole IPs beyond the built-in set.", "");
        scan.AddArgument(["--concurrency", "-c"], "N", "Parallel lookups.", 16);
        scan.AddArgument(["--skip-canary"], "", "Run without a PH-resolver vantage (liveness only).", false);

        ArgInvoke discover = null!;
        discover = ArgSharpClass.AddArgumentAction(["discover"],
            () => run = () => DiscoverCommand.RunAsync(
                RepoRoot(),
                discover.GetValue<string>("--keywords"),
                discover.GetValue<string>("--file"),
                discover.GetValue<string>("--output"),
                discover.GetValue<bool>("--refresh")),
            "Find sibling domains via certificate transparency logs (crt.sh).");
        discover.AddArgument(["--keywords", "-k"], "CSV", "Brand keywords or domains (phtaya.com -> phtaya).", "");
        discover.AddArgument(["--file", "-f"], "PATH", "File with one keyword/domain per line (# comments).", "");
        discover.AddArgument(["--output", "-o"], "PATH", "Write new candidates to a file for `sipat scan -f`.", "");
        discover.AddArgument(["--refresh"], "", "Bypass the 24h crt.sh cache and query live.", false);

        ArgInvoke audit = null!;
        audit = ArgSharpClass.AddArgumentAction(["audit"],
            () => run = () => AuditCommand.RunAsync(
                RepoRoot(),
                audit.GetValue<bool>("--dns"),
                audit.GetValue<int>("--concurrency")),
            "Report duplicates, malformed entries and drift across the three lists (report only).");
        audit.AddArgument(["--dns"], "", "Also resolve every domain: CICC overlap and dead entries.", false);
        audit.AddArgument(["--concurrency", "-c"], "N", "Parallel lookups for --dns.", 32);
        audit.ArgumentZeroAction = ArgSharpClass.ArgZeroAction.TreatAsSuccess;

        ArgInvoke cache = null!;
        cache = ArgSharpClass.AddArgumentAction(["cache"],
            () => run = () => CacheCommand.RunAsync(cache.GetValue<bool>("--clear")),
            "Show or clear cached blocklist copies and crt.sh results.");
        cache.AddArgument(["--clear"], "", "Delete all cached files.", false);
        cache.ArgumentZeroAction = ArgSharpClass.ArgZeroAction.TreatAsSuccess;

        ArgInvoke emit = null!;
        emit = ArgSharpClass.AddArgumentAction(["emit"],
            () => run = () => EmitCommand.RunAsync(
                RepoRoot(),
                emit.GetValue<string>("--domains"),
                emit.GetValue<string>("--file"),
                emit.GetValue<bool>("--pagcor"),
                emit.GetValue<bool>("--dry-run")),
            "Append confirmed domains to the three blocklists.");
        emit.AddArgument(["--domains", "-d"], "CSV", "Comma-separated domains or URLs.", "");
        emit.AddArgument(["--file", "-f"], "PATH", "File with one domain per line (# comments).", "");
        emit.AddArgument(["--pagcor"], "", "Insert into the PAGCOR-regulated section instead of POGO/illegal.", false);
        emit.AddArgument(["--dry-run", "-n"], "", "Show what would be written without touching the files.", false);

        if (!ArgSharpClass.Parse(args)) return 1;
        if (run is null) return 0;   // --help or no subcommand; ArgSharp printed usage.

        try
        {
            return await run();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    /// <summary>Repo root, so the tool works when run from sipat/ or from the root.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
