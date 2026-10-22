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
        if (CurrentPath is null || records.Count == 0) return;
        Directory.CreateDirectory(directory);
        File.AppendAllLines(CurrentPath, records.Select(r => JsonSerializer.Serialize(r, json)));
    }

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
