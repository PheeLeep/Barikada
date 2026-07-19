using Sipat.Core;
using Spectre.Console;

namespace Sipat.Commands;

/// <summary>
/// Shows what Sipat has cached (blocklist copies with their ETags, crt.sh
/// results) and clears it on request.
/// </summary>
public static class CacheCommand
{
    public static Task<int> RunAsync(bool clear)
    {
        var dir = Cache.Dir;

        if (!Directory.Exists(dir) || Directory.GetFiles(dir).Length == 0)
        {
            AnsiConsole.MarkupLine($"[grey]cache dir:[/] {Markup.Escape(dir)}   (empty)");
            return Task.FromResult(0);
        }

        var files = Directory.GetFiles(dir)
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToList();

        if (clear)
        {
            foreach (var f in files) f.Delete();
            AnsiConsole.MarkupLine($"Cleared {files.Count} cached file(s) from {Markup.Escape(dir)}.");
            return Task.FromResult(0);
        }

        AnsiConsole.MarkupLine($"[grey]cache dir:[/] {Markup.Escape(dir)}");
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("File");
        table.AddColumn(new TableColumn("Size").RightAligned());
        table.AddColumn(new TableColumn("Age").RightAligned());

        foreach (var f in files)
        {
            table.AddRow(
                Markup.Escape(f.Name),
                f.Length switch
                {
                    < 1024 => $"{f.Length} B",
                    < 1024 * 1024 => $"{f.Length / 1024.0:F1} KB",
                    _ => $"{f.Length / (1024.0 * 1024):F1} MB",
                },
                Cache.Describe(DateTime.UtcNow - f.LastWriteTimeUtc));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey]Clear with:[/] [blue]sipat cache --clear[/]");
        return Task.FromResult(0);
    }
}
