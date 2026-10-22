using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor.Tests;

public class RaceStreakTests
{
    private readonly FakeGameData _data = new();

    private CareerContext Context(int turn, IReadOnlyList<int>? raceTurns, int fans = 800) =>
        new(Snapshots.Snapshot(Snapshots.Daiwa(fans: fans), turn: turn) with { RaceTurns = raceTurns }, _data);

    private ScoredOption Niigata(int turn, IReadOnlyList<int>? raceTurns)
    {
        var ctx = Context(turn, raceTurns);
        return new RaceEvaluator(ctx, new ValueModel(ctx)).Evaluate(FakeGameData.Niigata);
    }

    private static bool HasStreak(ScoredOption race) => race.Reasons.Any(r => r.Contains("in a row"));

    [Fact]
    public void NoLog_OnlyGoalRacesCount()
    {
        Assert.Equal(0, RaceEvaluator.StreakBefore(Context(16, null)));
        Assert.Equal(1, RaceEvaluator.StreakBefore(Context(13, null)));   // Make Debut on turn 12
        Assert.False(HasStreak(Niigata(16, null)));
    }

    [Fact]
    public void ThirdRaceInARow_IsPenalised_AndALongerStreakMore()
    {
        var fresh = Niigata(16, []);
        var third = Niigata(16, [14, 15]);
        var fifth = Niigata(16, [13, 14, 15]);   // Make Debut on 12 starts it

        Assert.False(HasStreak(fresh));
        Assert.Contains(third.Reasons, r => r.StartsWith("race 3 in a row"));
        Assert.Contains(fifth.Reasons, r => r.StartsWith("race 5 in a row"));
        Assert.True(third.Score < fresh.Score, $"{third.Score} vs {fresh.Score}");
        Assert.True(fifth.Score < third.Score, $"{fifth.Score} vs {third.Score}");
    }

    [Fact]
    public void SecondRaceInARow_IsNotPenalised()
    {
        Assert.False(HasStreak(Niigata(16, [15])));
    }

    [Fact]
    public void ATurnWithoutARace_EndsTheStreak()
    {
        Assert.Equal(1, RaceEvaluator.StreakBefore(Context(16, [13, 15])));
        Assert.False(HasStreak(Niigata(16, [13, 15])));
    }

    [Fact]
    public void AGoalRaceCountsTowardTheStreak()
    {
        // Tulip Sho is the goal on turn 29; an optional race on 30 makes 31 the third in a row.
        Assert.Equal(2, RaceEvaluator.StreakBefore(Context(31, [30])));
        Assert.Contains(Niigata(31, [30]).Reasons, r => r.StartsWith("race 3 in a row"));
    }

    [Fact]
    public void AGoalNextTurn_AddsNoStreakCost()
    {
        // Racing on 28 after a race on 27 makes the turn-29 goal the third in a row, which goal races are exempt from.
        Assert.False(HasStreak(Niigata(28, [27])));
    }

    [Fact]
    public void History_RecordsATurnWhenFansRiseIntoTheNext()
    {
        var history = new RaceHistory();
        history.Stamp(Snapshots.Snapshot(Snapshots.Daiwa(fans: 800), turn: 14));
        history.Stamp(Snapshots.Snapshot(Snapshots.Daiwa(fans: 800), turn: 14));
        var after = history.Stamp(Snapshots.Snapshot(Snapshots.Daiwa(fans: 1500), turn: 15));
        var trained = history.Stamp(Snapshots.Snapshot(Snapshots.Daiwa(fans: 1500), turn: 16));

        Assert.Equal([14], after.RaceTurns!);
        Assert.Equal([14], trained.RaceTurns!);
    }

    [Fact]
    public void History_SkipsGaps_AndStartsOverForANewCareer()
    {
        var history = new RaceHistory();
        history.Stamp(Snapshots.Snapshot(Snapshots.Daiwa(fans: 800), turn: 14));
        var gap = history.Stamp(Snapshots.Snapshot(Snapshots.Daiwa(fans: 1500), turn: 16));
        Assert.Empty(gap.RaceTurns!);

        history.Stamp(Snapshots.Snapshot(Snapshots.Daiwa(fans: 2200), turn: 17));
        var restarted = history.Stamp(Snapshots.Snapshot(Snapshots.Daiwa(fans: 0), turn: 1));
        Assert.Empty(restarted.RaceTurns!);
        Assert.Empty(history.Turns);
    }
}
