using System.Text.Json;
using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor;

/// <summary>Event odds counted from career logs, and what went into them.</summary>
public sealed record OddsCount(EventOdds Odds, int Files, int Results, int Ambiguous, int Unreadable);

/// <summary>
/// Where career logs live and how a run finds its career's log. Every run writes to one folder whichever exe it
/// starts from (<c>BLAZEUMA_LOGS</c>, else %USERPROFILE%\BlazeUma\logs), one file per career, so a career resumed
/// in a later run appends to the file its earlier runs wrote. Not under AppData\Local: the packaged Python install
/// does not see folders other programs create there, and research/scripts read these logs too.
/// </summary>
public static class CareerLogs
{
    public static string DefaultDirectory =>
        Environment.GetEnvironmentVariable("BLAZEUMA_LOGS") is { Length: > 0 } dir ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "BlazeUma", "logs");

    /// <summary>The career logs in a folder, oldest first; none when it does not exist.</summary>
    public static IReadOnlyList<string> Files(string directory) =>
        Directory.Exists(directory) ? Replay.Files(directory) : [];

    public static string NewPath(string directory, Character character, DateTime now) =>
        Path.Combine(directory, $"career-{character.CardId}-{now:yyyyMMdd-HHmmss}.jsonl");

    /// <summary>
    /// The log an earlier run wrote for the career <paramref name="now"/> belongs to: the newest log for this trainee,
    /// if its last snapshot is the same career (<see cref="SameCareer"/>). Null starts a new file.
    /// </summary>
    public static string? FindCareer(string directory, CareerSnapshot now)
    {
        if (now.Character is not { } c || !Directory.Exists(directory)) return null;
        string? newest = Directory.GetFiles(directory, $"career-{c.CardId}-*.jsonl")
            .MaxBy(f => Path.GetFileName(f), StringComparer.Ordinal);
        if (newest is null) return null;
        CareerSnapshot? last = null;
        foreach (var record in Read(newest))
            if (record.Snapshot is { Character: not null, Turn: not null } s) last = s;
        return last is not null && SameCareer(last, now) ? newest : null;
    }

    /// <summary>
    /// The game keeps no career id we read, so a career is recognised by what cannot change within one: trainee,
    /// scenario and deck (the borrowed friend card included), with the turn, fans and skill count never going back.
    /// A new career with the same trainee and deck restarts at turn 1, so it fails the turn check.
    /// </summary>
    public static bool SameCareer(CareerSnapshot before, CareerSnapshot now)
    {
        if (before.Character is not { } b || now.Character is not { } n || before.Turn is not int t0 || now.Turn is not int t1) return false;
        return b.CardId == n.CardId && b.ScenarioId == n.ScenarioId
            && b.Deck.Select(d => d.SupportCardId).Order().SequenceEqual(n.Deck.Select(d => d.SupportCardId).Order())
            && t0 <= t1 && b.Fans <= n.Fans && b.Skills.Count <= n.Skills.Count;
    }

    /// <summary>
    /// Turns a log's "turn" records say were raced (fans up on the next turn), to carry a race streak across runs
    /// and for logs written before snapshots carried <see cref="CareerSnapshot.RaceTurns"/>.
    /// </summary>
    public static IReadOnlyList<int> RaceTurns(string file) =>
        Read(file).Where(r => r is { Kind: "turn", Outcome: { InferredAction: "race" } o } && r.Outcome!.NextTurn == r.Outcome.Turn + 1)
            .Select(r => r.Outcome!.Turn).ToList();

    /// <summary>The races a log's "race-result" records show, with each race's final placing and runs.</summary>
    public static RaceSummary Races(string file) => RaceSummary.From(Read(file));

    /// <summary>Counts what each event choice did ("event-result" records); ambiguous results are left out.</summary>
    public static OddsCount CountEventOdds(IReadOnlyList<string> files)
    {
        var odds = new EventOdds();
        int results = 0, ambiguous = 0, unreadable = 0;
        foreach (string file in files)
            foreach (string text in Lines(file))
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
        return new OddsCount(odds, files.Count, results, ambiguous, unreadable);
    }

    /// <summary>
    /// Event odds for a run, rebuilt from every log in <paramref name="directory"/>; the odds file shipped beside the
    /// exe when the logs hold no event results yet.
    /// </summary>
    public static (EventOdds Odds, string Source) LearnedOdds(string directory)
    {
        var count = CountEventOdds(Files(directory));
        return count.Results > 0
            ? (count.Odds, $"event odds from {count.Results} logged results in {count.Files} logs")
            : (EventOdds.Default, "event odds: no logged results yet, using the shipped file");
    }

    /// <summary>
    /// A log's lines, read without blocking the bot that may be appending to it (File.ReadLines shares the file
    /// for reading only, so a running bot's next write failed while a replay read it).
    /// </summary>
    public static IEnumerable<string> Lines(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) yield return line;
    }

    private static IEnumerable<LogRecord> Read(string file)
    {
        foreach (string text in Lines(file))
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            LogRecord? record;
            try { record = JsonSerializer.Deserialize<LogRecord>(text, LogRecord.JsonOptions); }
            catch (JsonException) { continue; }
            if (record is not null) yield return record;
        }
    }
}
