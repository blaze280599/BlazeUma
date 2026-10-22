using BlazeUma.Advisor;

namespace BlazeUma.Cli;

/// <summary>
/// <c>BlazeUma event-odds [file.jsonl|dir]... [--out path]</c>: counts what each event choice did in the
/// "event-result" records of career logs (the shared log folder when no path is given) and writes the odds file
/// shipped beside the exe (data/event-odds.json unless <c>--out</c> says otherwise). The bot and Advisor mode
/// rebuild the same odds from the shared folder when they start; the file is their fallback. Results whose outcome
/// was ambiguous are left out.
/// </summary>
internal static class EventOddsCommand
{
    public static int Run(string[] args)
    {
        string? output = null;
        var paths = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length) output = args[++i];
            else paths.Add(args[i]);
        }
        if (paths.Count == 0) paths.Add(CareerLogs.DefaultDirectory);
        var missing = paths.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
        if (missing.Count > 0)
        {
            Console.Error.WriteLine($"not found: {string.Join(", ", missing)}");
            return 2;
        }
        output ??= EventOdds.DefaultPath;

        var (odds, files, results, ambiguous, unreadable) = CareerLogs.CountEventOdds(paths.SelectMany(Replay.Files).ToList());
        foreach (var (key, choices) in odds.Counts.OrderBy(k => k.Key))
            foreach (var (choice, counts) in choices.OrderBy(c => c.Key))
            {
                int n = counts.Sum();
                Console.WriteLine($"  story {key} choice {choice}: {string.Join(" / ", counts)} ({n} results)" +
                    (n < EventOdds.MinSamples ? $"  below {EventOdds.MinSamples}, not used yet" : ""));
            }
        odds.Save(output);
        Console.WriteLine();
        Console.WriteLine($"{results} event results counted from {files} logs, {ambiguous} ambiguous skipped, {unreadable} unreadable lines; wrote {output}");
        return 0;
    }
}
