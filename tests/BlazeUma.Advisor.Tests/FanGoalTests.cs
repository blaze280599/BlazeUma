using BlazeUma.Data;

namespace BlazeUma.Advisor.Tests;

/// <summary>
/// "Earn 5000 fans" goals (condition_type 3) aren't races: turn 30 of a live career was read as a race day
/// ("run Goal 0 (need top 5000)") and the bot looked for a goal race that wasn't in the list.
/// </summary>
public class FanGoalTests
{
    private static readonly GoalRace EarnFans = new(56, 4, 30, 0, "Earn 5000 fans", null, GoalKind.Fans, 5000);

    private static CareerContext Context(int turn, int fans)
    {
        var data = new FakeGameData();
        data.ExtraGoals.Add(EarnFans);
        return new CareerContext(Snapshots.Snapshot(Snapshots.Daiwa(fans: fans), turn: turn), data);
    }

    [Fact]
    public void FanGoal_IsNotARaceGoal()
    {
        var ctx = Context(turn: 30, fans: 10943);
        Assert.DoesNotContain(EarnFans, ctx.Goals);
        Assert.Contains(EarnFans, ctx.FanGoals);
        Assert.Null(ctx.GoalToday);
        Assert.NotEqual(Situation.GoalRaceDay, new CareerAdvisor(ctx.Data).Advise(ctx.Snapshot).Situation);
    }

    [Fact]
    public void FanGoal_NotMet_IsTheFanShortfall()
    {
        var ctx = Context(turn: 20, fans: 2000);   // Tulip Sho's 1750 is met; the 5000 by turn 30 isn't
        var (goal, shortfall) = ctx.FanShortfall()!.Value;
        Assert.Equal(EarnFans, goal);
        Assert.Equal(3000, shortfall);
    }

    [Fact]
    public void FanGoal_Met_IsNoLongerTheShortfall() =>
        // Tenno Sho (Autumn)'s 20,000 to enter is what's still short.
        Assert.Equal(FakeGameData.TennoShoAutumn, Context(turn: 30, fans: 10943).FanShortfall()!.Value.Goal.Race);
}
