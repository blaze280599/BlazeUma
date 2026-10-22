using System.Text.Json;
using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor.Tests;

public class ReplayTests
{
    private readonly FakeGameData _data = new();

    /// <summary>A short career log as Advisor mode writes it: home, an event with choices, the next home.</summary>
    private IReadOnlyList<string> WriteLog()
    {
        var advisor = new CareerAdvisor(_data);
        var c = Snapshots.Daiwa(energy: 70);
        var story = new StoryEvent(501009715, 1, 1009, 0, 42, LooksActive: true);
        IReadOnlyList<EventChoice> choices =
        [
            new(1, [[new EventEffect(1, ParamIds.Speed, 10, 0)]]),
            new(2, [[new EventEffect(1, ParamIds.Energy, 30, 0)]]),
        ];
        var recorder = new TurnRecorder();
        var lines = new List<string>();
        foreach (var snapshot in new[]
        {
            Snapshots.Snapshot(c, turn: 14),
            Snapshots.Snapshot(c, turn: 14, story: story, choices: choices),
            Snapshots.Snapshot(c with { Energy = 49 }, turn: 15),
        })
        {
            lines.AddRange(recorder.Observe(snapshot, advisor.Advise(snapshot)).Select(r => JsonSerializer.Serialize(r, LogRecord.JsonOptions)));
        }
        return lines;
    }

    private string TempLog(IEnumerable<string> lines)
    {
        string path = Path.Combine(Path.GetTempPath(), $"blazeuma-replay-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void Snapshot_RoundTripsThroughLogJson()
    {
        string line = WriteLog().Last();
        var record = JsonSerializer.Deserialize<LogRecord>(line, LogRecord.JsonOptions)!;
        Assert.Equal(line, JsonSerializer.Serialize(record, LogRecord.JsonOptions));
        Assert.Equal(Mood.Normal, record.Snapshot.Character!.Mood);
        Assert.Equal(7, record.Snapshot.Commands!.Count);
    }

    [Fact]
    public void Replay_UnchangedAdvisor_ReportsSamePicks()
    {
        string path = TempLog(WriteLog());
        try
        {
            var result = Replay.Run([path], _data);

            Assert.Equal(["event", "turn"], result.Rows.Select(r => r.Kind));
            Assert.Equal(1, result.Skipped);
            Assert.Empty(result.Failures);
            Assert.Equal(0, result.Flips);
            Assert.All(result.Rows, r => Assert.NotEmpty(r.CurrentPick));
            Assert.Equal(14, result.Rows.Single(r => r.Kind == "turn").Turn);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Replay_ReportsFlipsAndBadLines()
    {
        var lines = WriteLog().ToList();
        int turn = lines.FindIndex(l => l.StartsWith("{\"Kind\":\"turn\""));
        lines[turn] = lines[turn].Replace("\"Summary\":\"", "\"Summary\":\"Rest (was) ");
        lines.Add("{not json");
        // An older record: no Errors list, which newer code expects.
        lines.Add(lines[turn].Replace(",\"Errors\":[]", ""));
        string path = TempLog(lines);
        try
        {
            var result = Replay.Run([path], _data);

            Assert.Equal(2, result.Flips);
            var failure = Assert.Single(result.Failures);
            Assert.Equal(lines.Count - 1, failure.Line);
            Assert.StartsWith("unreadable", failure.Reason);
        }
        finally { File.Delete(path); }
    }
}
