using System.Text.Json;
using BlazeUma.Advisor;
using BlazeUma.Memory.Game;

namespace BlazeUma.Cli;

/// <summary>
/// Appends <see cref="TurnRecorder"/> records to one JSONL file per career in the shared log folder
/// (<see cref="CareerLogs.DefaultDirectory"/>). A career resumed in a later run appends to its earlier file, and the
/// races that file records carry over into the race streak.
/// </summary>
internal sealed class CareerLog(string directory, JsonSerializerOptions json)
{
    private readonly TurnRecorder _recorder = new();
    private readonly RaceHistory _races = new();

    /// <summary>The log in the shared folder, written as Advisor mode and the auto bot write it.</summary>
    public static CareerLog Create() => new(CareerLogs.DefaultDirectory, LogRecord.JsonOptions);

    public string? CurrentPath { get; private set; }

    /// <summary>True when <see cref="CurrentPath"/> is an earlier run's log of this career, now appended to.</summary>
    public bool Resumed { get; private set; }

    /// <summary>Stamps the turns raced so far on a snapshot, including those an earlier run of this career logged.</summary>
    public CareerSnapshot Stamp(CareerSnapshot snapshot)
    {
        if (CurrentPath is null && snapshot is { Character: not null, Turn: not null }) Open(snapshot);
        return _races.Stamp(snapshot);
    }

    public void Observe(CareerSnapshot snapshot, Advice advice)
    {
        var records = _recorder.Observe(snapshot, advice);
        if (snapshot.Character is { } character)
        {
            if (CurrentPath is null) Open(snapshot);
            else if (_recorder.NewCareer)
            {
                CurrentPath = CareerLogs.NewPath(directory, character, DateTime.Now);
                Resumed = false;
            }
        }
        Write(records);
    }

    /// <summary>
    /// Logs the race on an open results list (<see cref="TurnRecorder.ObserveRace"/>); the auto bot calls it from the
    /// race screens, where it reads no advice. Returns the placing logged, or null when it was already logged.
    /// </summary>
    public RaceRecord? RecordRace(CareerSnapshot snapshot)
    {
        var records = _recorder.ObserveRace(Stamp(snapshot));
        Write(records);
        return records.FirstOrDefault()?.Race;
    }

    /// <summary>This career's races so far, from its log.</summary>
    public RaceSummary Races() => CurrentPath is { } path && File.Exists(path) ? CareerLogs.Races(path) : RaceSummary.Empty;

    private void Write(IReadOnlyList<LogRecord> records)
    {
        if (CurrentPath is null || records.Count == 0) return;
        Directory.CreateDirectory(directory);
        var lines = records.Select(r => JsonSerializer.Serialize(r, json)).ToList();
        // Another program reading the log (a replay, an editor) can hold it for a moment; wait rather than stop the bot.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream);
                foreach (string line in lines) writer.WriteLine(line);
                return;
            }
            catch (IOException) when (attempt < WriteAttempts)
            {
                Thread.Sleep(WriteRetryDelay);
            }
        }
    }

    private const int WriteAttempts = 20;
    private static readonly TimeSpan WriteRetryDelay = TimeSpan.FromMilliseconds(250);

    private void Open(CareerSnapshot snapshot)
    {
        if (CareerLogs.FindCareer(directory, snapshot) is { } path)
        {
            CurrentPath = path;
            Resumed = true;
            _races.Seed(CareerLogs.RaceTurns(path));
        }
        else
        {
            CurrentPath = CareerLogs.NewPath(directory, snapshot.Character!, DateTime.Now);
            Resumed = false;
        }
    }
}
