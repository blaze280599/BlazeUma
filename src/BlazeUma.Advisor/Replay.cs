using System.Text.Json;
using BlazeUma.Data;

namespace BlazeUma.Advisor;

/// <summary>One logged decision re-run with the current advisor.</summary>
public sealed record ReplayRow(
    string File, int Line, string Kind, int Turn,
    string LoggedPick, string CurrentPick,
    IReadOnlyList<ScoredOption> LoggedOptions, IReadOnlyList<ScoredOption> CurrentOptions)
{
    public bool Flipped => LoggedPick != CurrentPick;
}

/// <summary>A log line that could not be read or advised; <paramref name="Reason"/> says why.</summary>
public sealed record ReplayFailure(string File, int Line, string Reason);

/// <param name="Skipped">Records with nothing to re-decide (event-result).</param>
public sealed record ReplayResult(IReadOnlyList<ReplayRow> Rows, IReadOnlyList<ReplayFailure> Failures, int Skipped)
{
    public int Flips => Rows.Count(r => r.Flipped);
}

/// <summary>
/// Re-runs <see cref="CareerAdvisor.Advise"/> on the snapshots of career logs, to see which picks a change to
/// the advisor would flip. "turn" and "event" records are replayed; the pick compared is the advice summary.
/// </summary>
public static class Replay
{
    /// <summary>The .jsonl files at a path: the file itself, or every career log in a directory, oldest first.</summary>
    public static IReadOnlyList<string> Files(string path) =>
        Directory.Exists(path)
            ? Directory.GetFiles(path, "*.jsonl").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToList()
            : [path];

    public static ReplayResult Run(IEnumerable<string> files, IGameData data)
    {
        var advisor = new CareerAdvisor(data);
        var rows = new List<ReplayRow>();
        var failures = new List<ReplayFailure>();
        int skipped = 0;
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            var raceTurns = CareerLogs.RaceTurns(file);
            int line = 0;
            foreach (string text in CareerLogs.Lines(file))
            {
                line++;
                if (string.IsNullOrWhiteSpace(text)) continue;
                LogRecord? record;
                try { record = JsonSerializer.Deserialize<LogRecord>(text, LogRecord.JsonOptions); }
                catch (JsonException e) { failures.Add(new ReplayFailure(name, line, $"unreadable: {e.Message}")); continue; }
                if (record is null) { failures.Add(new ReplayFailure(name, line, "unreadable: null record")); continue; }
                if (record.Kind is not ("turn" or "event")) { skipped++; continue; }
                if (record.Snapshot is null) { failures.Add(new ReplayFailure(name, line, "no snapshot")); continue; }
                try
                {
                    // Older logs may lack lists added since; read them as empty rather than fail.
                    var snapshot = record.Snapshot with
                    {
                        Errors = record.Snapshot.Errors ?? [],
                        RaceTurns = record.Snapshot.RaceTurns ?? raceTurns.Where(t => t < record.Turn).ToList(),
                    };
                    var advice = advisor.Advise(snapshot);
                    rows.Add(new ReplayRow(name, line, record.Kind, record.Turn, record.Summary ?? "",
                        advice.Summary, record.Options ?? [], advice.Options));
                }
                catch (Exception e)
                {
                    failures.Add(new ReplayFailure(name, line, $"advise failed: {e.GetType().Name}: {e.Message}"));
                }
            }
        }
        return new ReplayResult(rows, failures, skipped);
    }
}
