using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor.Tests;

/// <summary>
/// Each condition's own value: its effect per turn over the turns it lasts. <see cref="FakeGameData"/> goals fall on
/// turns 12, 29, 44, 68, 72 and 74; camps are turns 37–40 and 61–64.
/// </summary>
public class ConditionValueTests
{
    private static CareerContext Context(int turn, int energy = 80, IReadOnlyList<int>? conditions = null, IReadOnlyList<Command>? commands = null) =>
        new(Snapshots.Snapshot(Snapshots.Daiwa(energy: energy) with { Conditions = conditions ?? [] }, turn: turn, commands: commands),
            new FakeGameData());

    private static ValueModel Values(int turn, int energy = 80, IReadOnlyList<int>? conditions = null) =>
        new(Context(turn, energy, conditions));

    [Fact]
    public void PracticePoor_CostsLessAgainstNightOwl_WhenGoalRacesFillTheTurnsAhead()
    {
        // Turns 18–37 (to camp) hold one goal race; turns 66–78 three, turns with no training to fail. Night
        // Owl's energy and mood losses come on any turn.
        double Ratio(int turn) => Values(turn).ConditionValue(ConditionIds.PracticePoor) / Values(turn).ConditionValue(ConditionIds.NightOwl);
        Assert.True(Ratio(65) < Ratio(17) * 0.85, $"Practice Poor / Night Owl: turn 17 {Ratio(17):0.00}, turn 65 {Ratio(65):0.00}");
    }

    [Fact]
    public void BadConditions_DontWearOff_SoEarlyOnesCostTheInfirmaryTurn()
    {
        // Left alone, Practice Poor lasts until camp (20 turns); Slacker costs more than a cure, so it is capped.
        var values = Values(17);
        Assert.True(values.ConditionValue(ConditionIds.PracticePoor) < Values(34).ConditionValue(ConditionIds.PracticePoor) * 4);
        Assert.True(values.ConditionValue(ConditionIds.Slacker) > -25);
    }

    [Fact]
    public void NightOwl_CostsMore_AtLowEnergy()
    {
        Assert.True(Values(14, energy: 30).ConditionValue(ConditionIds.NightOwl) < Values(14, energy: 80).ConditionValue(ConditionIds.NightOwl));
    }

    [Fact]
    public void Cure_IsWorthLess_InTheLastFewTurns()
    {
        double mid = Values(50).CureValue(ConditionIds.PracticePoor), late = Values(76).CureValue(ConditionIds.PracticePoor);
        Assert.True(mid > 0 && late < mid / 2, $"turn 50 {mid:0.00}, turn 76 {late:0.00}");
        Assert.Equal(0, Values(CareerContext.FinalTurn).CureValue(ConditionIds.SkinOutbreak));
    }

    [Fact]
    public void Infirmary_IsWorthLess_JustBeforeSummerCampRestAndRecreation()
    {
        IReadOnlyList<Command> commands = [.. Snapshots.StandardCommands(), Snapshots.Simple(CommandType.Infirmary, 801)];
        double Infirmary(int turn)
        {
            var ctx = Context(turn, conditions: [ConditionIds.Migraine], commands: commands);
            return new TurnAdvisor(ctx, new ValueModel(ctx)).Rank().Single(o => o.Label == "Infirmary").Score;
        }
        // Turn 36: camp's Rest & Recreation next turn cures it anyway; turn 26: six turns of Migraine ahead.
        Assert.True(Infirmary(36) < Infirmary(26) - 3, $"turn 36 {Infirmary(36):0.0}, turn 26 {Infirmary(26):0.0}");
    }

    [Fact]
    public void GoodConditions_AreWorthTheRestOfTheCareer()
    {
        var values = Values(14);
        Assert.True(values.ConditionValue(ConditionIds.PracticePerfectGreat) > values.ConditionValue(ConditionIds.PracticePerfect));
        Assert.True(values.ConditionValue(ConditionIds.Charming) > Values(60).ConditionValue(ConditionIds.Charming) * 3);
        Assert.False(values.IsBadCondition(ConditionIds.Charming));
    }

    [Fact]
    public void UnknownCondition_FallsBackToTheFlatValue()
    {
        Assert.Equal(ValueModel.BadConditionValue, Values(14).ConditionValue(9999));
    }

    [Fact]
    public void Events_ValueBecomeAndCure_ByTheCondition()
    {
        var ctx = Context(20, conditions: [ConditionIds.SkinOutbreak]);
        var values = new ValueModel(ctx);
        var events = new EventAdvisor(ctx, values);
        Assert.Equal(values.ConditionValue(ConditionIds.Slacker), events.Describe(new EventEffect(9, ConditionIds.Slacker, 0, 0)).Value);
        Assert.Equal(0, events.Describe(new EventEffect(9, ConditionIds.SkinOutbreak, 0, 0)).Value);   // already has it
        Assert.Equal(values.CureValue(ConditionIds.SkinOutbreak), events.Describe(new EventEffect(15, 0, 0, 0)).Value);
        Assert.Equal(0, new EventAdvisor(Context(20), new ValueModel(Context(20))).Describe(new EventEffect(16, 1, 0, 0)).Value);
    }
}
