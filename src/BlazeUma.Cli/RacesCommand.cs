using BlazeUma.Advisor;
using BlazeUma.Data;

namespace BlazeUma.Cli;

/// <summary>
/// <c>BlazeUma races [file.jsonl|dir]... [--db master.mdb]</c>: each logged career's races (placing, runs, alarm
/// clocks left, goal met or missed) and a one-line verdict, from the "race-result" records. No game needed; game data
/// comes from the master.mdb copy the app keeps in %LOCALAPPDATA%\BlazeUma. Logs written before race results were
/// logged show "no race results logged".
/// </summary>
internal static class RacesCommand
{
    public static int Run(string[] args)
    {
        string? db = null;
        var paths = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--db" && i + 1 < args.Length) db = args[++i];
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
        foreach (string file in paths.SelectMany(p => Directory.Exists(p) ? CareerLogs.Files(p) : [p]))
        {
            var races = CareerLogs.Races(file);
            string trainee = races.Character is { } c ? data.CharacterName(c.CardId / 100) ?? $"card {c.CardId}" : "";
            Console.WriteLine($"{Path.GetFileName(file)}  {trainee}");
            foreach (string line in races.Lines(data)) Console.WriteLine("  " + line);
            Console.WriteLine($"  {races.Headline(data)}");
            Console.WriteLine();
        }
        return 0;
    }
}
