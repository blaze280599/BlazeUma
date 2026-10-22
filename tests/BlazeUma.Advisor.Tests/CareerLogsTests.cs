using System.Text.Json;
using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor.Tests;

public class CareerLogsTests : IDisposable
{
    private readonly FakeGameData _data = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"blazeuma-logs-{Guid.NewGuid():N}");

    public CareerLogsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Writes a log as a run would: one snapshot per turn, the trainee racing on turn 14 (fans up on 15).</summary>
    private string WriteCareer(string stamp, params CareerSnapshot[] snapshots)
    {
        var advisor = new CareerAdvisor(_data);
        var recorder = new TurnRecorder();
        var lines = snapshots.SelectMany(s => recorder.Observe(s, advisor.Advise(s)))
            .Select(r => JsonSerializer.Serialize(r, LogRecord.JsonOptions)).ToList();
        string path = Path.Combine(_dir, $"career-100901-{stamp}.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static CareerSnapshot At(int turn, int fans = 800) => Snapshots.Snapshot(Snapshots.Daiwa(fans: fans), turn: turn);

    [Fact]
    public void SameCareer_LaterTurnSameDeck()
    {
        Assert.True(CareerLogs.SameCareer(At(14), At(20, fans: 3000)));
    }

    [Fact]
    public void SameCareer_NotWhenTurnFansOrDeckGoBack()
    {
        Assert.False(CareerLogs.SameCareer(At(20), At(1)));
        Assert.False(CareerLogs.SameCareer(At(14, fans: 3000), At(20, fans: 800)));
        var otherDeck = Snapshots.Daiwa() with { Deck = [new DeckCard(1, 30145, 0), new DeckCard(2, 10099, 0)] };
        Assert.False(CareerLogs.SameCareer(At(14), Snapshots.Snapshot(otherDeck, turn: 20)));
    }

    [Fact]
    public void FindCareer_ResumesTheNewestLogOfThisCareer()
    {
        WriteCareer("20261001-000000", At(30), At(31));   // an older career, further along
        string current = WriteCareer("20261020-000000", At(14), At(15, fans: 2800), At(16, fans: 2800));

        Assert.Equal(current, CareerLogs.FindCareer(_dir, At(18, fans: 2800)));
        Assert.Null(CareerLogs.FindCareer(_dir, At(1)));   // a new career with the same trainee and deck
    }

    [Fact]
    public void RaceTurns_ComeFromFansUpOnTheNextTurn()
    {
        string path = WriteCareer("20261020-000000", At(14), At(15, fans: 2800), At(16, fans: 2800));
        Assert.Equal([14], CareerLogs.RaceTurns(path));
    }

    [Fact]
    public void RaceHistory_SeedCarriesEarlierRaces()
    {
        var races = new RaceHistory();
        races.Seed([13, 14]);
        Assert.Equal([13, 14], races.Stamp(At(15)).RaceTurns);
    }

    [Fact]
    public void LearnedOdds_FallBackToTheShippedFileWithoutResults()
    {
        var (odds, source) = CareerLogs.LearnedOdds(Path.Combine(_dir, "missing"));
        Assert.Same(EventOdds.Default, odds);
        Assert.Contains("shipped", source);
    }
}
