using BlazeUma.Advisor;
using BlazeUma.Data;

namespace BlazeUma.Cli;

/// <summary>
/// <c>BlazeUma replay [file.jsonl|dir]... [--all] [--db master.mdb] [--baseline git-ref]</c>: re-runs the advisor on
/// logged snapshots (the shared log folder when no path is given) and lists the picks it would now change. No game needed; game data comes from the master.mdb
/// copy the app keeps in %LOCALAPPDATA%\BlazeUma. With --baseline the picks are compared with that commit's advisor
/// (<see cref="ReplayBaseline"/>) instead of the logged ones, so only this change's flips show.
/// </summary>
internal static class ReplayCommand
{
    public static int Run(string[] args)
    {
        bool all = false;
        string? db = null, baseline = null;
        var paths = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--all") all = true;
            else if (args[i] == "--db" && i + 1 < args.Length) db = args[++i];
            else if (args[i] == "--baseline" && i + 1 < args.Length) baseline = args[++i];
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
        if (baseline is not null)
        {
            var picks = ReplayBaseline.Run(baseline, paths, db);
            if (picks is null) return 2;
            result = AgainstBaseline(result, picks, out int unmatched);
            Console.WriteLine($"Picks compared with {baseline}'s advisor, not the logged ones" +
                (unmatched > 0 ? $" ({unmatched} records missing from {baseline}'s replay are compared with the log)" : "") + ".");
        }

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

    /// <summary>
    /// The rows with each logged pick replaced by the baseline's pick for the same record: the next unused baseline
    /// row of the same file, turn and kind (both runs replay a file's records in order).
    /// </summary>
    private static ReplayResult AgainstBaseline(ReplayResult result, Dictionary<string, List<(int Turn, string Kind, string Pick)>> picks,
        out int unmatched)
    {
        var used = new Dictionary<string, int>();
        int missing = 0;
        var rows = result.Rows.Select(row =>
        {
            var list = picks.GetValueOrDefault(row.File) ?? [];
            int from = used.GetValueOrDefault(row.File);
            int at = list.FindIndex(from, p => p.Turn == row.Turn && p.Kind == row.Kind);
            if (at < 0) { missing++; return row; }
            used[row.File] = at + 1;
            return row with { LoggedPick = list[at].Pick };
        }).ToList();
        unmatched = missing;
        return result with { Rows = rows };
    }

    private static string Top3(IReadOnlyList<ScoredOption> options) =>
        options.Count == 0 ? "(no options)" : string.Join(" · ", options.Take(3).Select(o => $"{o.Label} {o.Score:0.0}"));
}
