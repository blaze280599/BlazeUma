using BlazeUma.Advisor;
using BlazeUma.Data;

namespace BlazeUma.Cli;

/// <summary>
/// <c>BlazeUma replay [file.jsonl|dir]... [--all] [--db master.mdb]</c>: re-runs the advisor on logged
/// snapshots (the shared log folder when no path is given) and lists the picks it would now change. No game needed; game data comes from the master.mdb
/// copy the app keeps in %LOCALAPPDATA%\BlazeUma.
/// </summary>
internal static class ReplayCommand
{
    public static int Run(string[] args)
    {
        bool all = false;
        string? db = null;
        var paths = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--all") all = true;
            else if (args[i] == "--db" && i + 1 < args.Length) db = args[++i];
            else paths.Add(args[i]);
        }
        if (paths.Count == 0) paths.Add(CareerLogs.DefaultDirectory);
        db ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlazeUma", "master.mdb");
        if (!File.Exists(db))
        {
            Console.Error.WriteLine($"master.mdb not found at {db}; run the app once with the game open, or pass --db");
            return 2;
        }
        var missing = paths.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
        if (missing.Count > 0)
        {
            Console.Error.WriteLine($"not found: {string.Join(", ", missing)}");
            return 2;
        }

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var data = MasterDatabase.Open(db);
        var result = Replay.Run(paths.SelectMany(Replay.Files), data);

        string? file = null;
        foreach (var row in result.Rows)
        {
            if (row.File != file) Console.WriteLine(file = row.File);
            Console.WriteLine(row.Flipped
                ? $"  {row.Turn,3} {row.Kind,-5} FLIP {row.LoggedPick}\n                  -> {row.CurrentPick}"
                : $"  {row.Turn,3} {row.Kind,-5}      {row.LoggedPick}");
            if (all)
            {
                Console.WriteLine($"            old: {Top3(row.LoggedOptions)}");
                Console.WriteLine($"            new: {Top3(row.CurrentOptions)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{result.Rows.Count} records replayed ({result.Rows.Count(r => r.Kind == "turn")} turn, " +
            $"{result.Rows.Count(r => r.Kind == "event")} event), {result.Flips} flipped, {result.Failures.Count} failed, " +
            $"{result.Skipped} skipped (event-result)");
        foreach (var f in result.Failures) Console.WriteLine($"  failed {f.File}:{f.Line}: {f.Reason}");
        return 0;
    }

    private static string Top3(IReadOnlyList<ScoredOption> options) =>
        options.Count == 0 ? "(no options)" : string.Join(" · ", options.Take(3).Select(o => $"{o.Label} {o.Score:0.0}"));
}
