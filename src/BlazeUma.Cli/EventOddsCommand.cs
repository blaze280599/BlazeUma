using System.Text.Json;
using BlazeUma.Advisor;

namespace BlazeUma.Cli;

/// <summary>
/// <c>BlazeUma event-odds &lt;file.jsonl|dir&gt;... [--out path]</c>: counts what each event choice did in the
/// "event-result" records of career logs and writes the odds file the advisor loads (data/event-odds.json
/// next to the exe unless <c>--out</c> says otherwise). Results whose outcome was ambiguous are left out.
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
        if (paths.Count == 0)
        {
            Console.Error.WriteLine("usage: BlazeUma event-odds <file.jsonl|dir>... [--out path]");
            return 2;
        }
        var missing = paths.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
        if (missing.Count > 0)
        {
            Console.Error.WriteLine($"not found: {string.Join(", ", missing)}");
            return 2;
        }
        output ??= EventOdds.DefaultPath;

        var odds = new EventOdds();
        int results = 0, ambiguous = 0, unreadable = 0;
        foreach (string file in paths.SelectMany(Replay.Files))
            foreach (string text in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                LogRecord? record;
                try { record = JsonSerializer.Deserialize<LogRecord>(text, LogRecord.JsonOptions); }
                catch (JsonException) { unreadable++; continue; }
                if (record is not { Kind: "event-result", Result: { } result }) continue;
                if (result is { Choice: null } or { Outcome: null }) { ambiguous++; continue; }
                odds.Add(result);
                results++;
            }

        foreach (var (key, choices) in odds.Counts.OrderBy(k => k.Key))
            foreach (var (choice, counts) in choices.OrderBy(c => c.Key))
            {
                int n = counts.Sum();
                Console.WriteLine($"  story {key} choice {choice}: {string.Join(" / ", counts)} ({n} results)" +
                    (n < EventOdds.MinSamples ? $"  below {EventOdds.MinSamples}, not used yet" : ""));
            }
        odds.Save(output);
        Console.WriteLine();
        Console.WriteLine($"{results} event results counted, {ambiguous} ambiguous skipped, {unreadable} unreadable lines; wrote {output}");
        return 0;
    }
}
