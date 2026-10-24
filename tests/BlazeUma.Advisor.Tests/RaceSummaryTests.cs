using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor.Tests;

public class RaceSummaryTests
{
    private static CareerSnapshot Results(int turn, int seed, int place, int clocks = 3) =>
        Snapshots.Snapshot(Snapshots.Daiwa(), turn: turn) with { AlarmClocks = clocks, RaceResult = new RaceResult(100201, seed, place, 18, 21561) };

    [Fact]
    public void ObserveRace_LogsEachRunOnce_AndCountsRetries()
    {
        var recorder = new TurnRecorder();
        var records = new List<LogRecord>();
        records.AddRange(recorder.ObserveRace(Results(24, seed: 7, place: 4)));
        records.AddRange(recorder.ObserveRace(Results(24, seed: 7, place: 4)));   // the same results list, read again
        records.AddRange(recorder.ObserveRace(Results(24, seed: 9, place: 1, clocks: 2)));   // Try Again
        records.AddRange(recorder.ObserveRace(Snapshots.Snapshot(Snapshots.Daiwa(), turn: 25)));   // no results list

        Assert.Equal([1, 2], records.Select(r => r.Race!.Attempt));
        Assert.All(records, r => Assert.Equal("race-result", r.Kind));
        Assert.Equal("4th of 18", records[0].Summary);
    }

    [Fact]
    public void From_KeepsTheLastRun_AndDropsRunsLoggedTwice()
    {
        var first = new TurnRecorder().ObserveRace(Results(24, seed: 7, place: 4));
        var resumed = new TurnRecorder();   // a later run of the bot sees the same results list again
        var again = resumed.ObserveRace(Results(24, seed: 7, place: 4));
        var retry = resumed.ObserveRace(Results(24, seed: 9, place: 1, clocks: 2));
        var later = new TurnRecorder().ObserveRace(Results(30, seed: 11, place: 2));

        var summary = RaceSummary.From([.. first, .. again, .. retry, .. later]);

        Assert.Equal([(24, 1, 2, 2), (30, 2, 1, 3)], summary.Races.Select(r => (r.Turn, r.Place, r.Runs, r.AlarmClocks ?? -1)));
        Assert.Equal((1, 1), (summary.Wins, summary.Retries));
        Assert.Equal("1 win from 2 races, 1 retry", summary.Headline());
    }

    [Theory]
    [InlineData(1, "1st"), InlineData(2, "2nd"), InlineData(3, "3rd"), InlineData(4, "4th"), InlineData(11, "11th"), InlineData(12, "12th"),
     InlineData(13, "13th"), InlineData(21, "21st")]
    public void Ordinal(int n, string text) => Assert.Equal(text, RaceSummary.Ordinal(n));
}
