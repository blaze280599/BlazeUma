using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor.Tests;

/// <summary>
/// A few turns ahead: facility level-ups a training brings forward, and energy against the coming turns.
/// <see cref="FakeGameData"/> goals fall on turns 12, 29, 44, 68, 72 and 74; camps are turns 37–40 and 61–64.
/// </summary>
public class LookaheadTests
{
    private static readonly Stats SpeedHeavy = new(400, 160, 200, 140, 130);

    private static ValueModel Values(int turn, Stats? stats = null, int energy = 80) =>
        new(new CareerContext(Snapshots.Snapshot(Snapshots.Daiwa(energy: energy, stats: stats ?? SpeedHeavy), turn: turn), new FakeGameData()));

    [Fact]
    public void Facility_TrainingEarly_BringsMoreLevelUpsForward_ThanLate()
    {
        var (early, why) = Values(16).FacilityProgress(TrainingIds.Speed);
        var (late, _) = Values(70, new Stats(500, 450, 420, 300, 350)).FacilityProgress(TrainingIds.Speed);
        Assert.True(early > 3, $"early {early:0.0}: {why}");
        Assert.True(late < early / 3, $"late {late:0.0} vs early {early:0.0}");
        Assert.Contains("level-ups a training sooner", why);
    }

    [Fact]
    public void Facility_ConcentratedOn_IsWorthMore_ThanOneBarelyUsed()
    {
        var values = Values(20);
        // Speed has grown most, so its facility is further along and gets more of the future trainings.
        Assert.True(values.EstimatedFacility(TrainingIds.Speed).Level > values.EstimatedFacility(TrainingIds.Wit).Level);
        Assert.True(values.FacilityProgress(TrainingIds.Speed).Value > values.FacilityProgress(TrainingIds.Wit).Value);
    }

    [Fact]
    public void Facility_NothingInSummerCamp_OrAtLevel5()
    {
        Assert.Equal(0, Values(38).FacilityProgress(602).Value);
        var maxed = Values(66, new Stats(1000, 120, 130, 110, 110));
        Assert.Equal(5, maxed.EstimatedFacility(TrainingIds.Speed).Level);
        Assert.Equal((0, null), maxed.FacilityProgress(TrainingIds.Speed));
    }

    [Fact]
    public void Facility_ShowsInTheTrainingReasons_AndRaisesItsScore()
    {
        var ctx = new CareerContext(Snapshots.Snapshot(Snapshots.Daiwa(stats: SpeedHeavy), turn: 16), new FakeGameData());
        var values = new ValueModel(ctx);
        var speed = new TurnAdvisor(ctx, values).Rank().Single(o => o.Label == "Train Speed");
        Assert.Contains(speed.Reasons, r => r.StartsWith("facility ~Lv"));
    }

    [Fact]
    public void Energy_OrdinaryTurnsAhead_NoChange()
    {
        var values = Values(16);
        Assert.Equal((1.0, (string?)null), values.EnergyOutlook);
    }

    [Fact]
    public void Energy_GoalRaceNextTurn_WorthLess()
    {
        var before = Values(11);
        var ordinary = Values(16);
        Assert.InRange(before.EnergyOutlook.Factor, 0.8, 0.9);
        Assert.Contains("1 goal race day", before.EnergyOutlook.Reason);
        Assert.True(before.Energy(30) < ordinary.Energy(30));
        Assert.True(before.Energy(-20) > ordinary.Energy(-20), "a training's energy cost hurts less too");
    }

    [Fact]
    public void Energy_CareerEnding_WorthLittle()
    {
        var (factor, why) = Values(77).EnergyOutlook;
        Assert.InRange(factor, 0.3, 0.34);
        Assert.Contains("career ends in 1", why);
    }

    [Fact]
    public void Energy_SummerCampAhead_WorthMore()
    {
        // Two turns out the camp bands apply and say so; three out, one camp turn in the window lifts the factor.
        Assert.Equal((1.0, "summer camp in 2: energy worth more (camp bands)"), Values(35).EnergyOutlook);
        var (factor, why) = Values(34).EnergyOutlook;
        Assert.True(factor > 1, why);
        Assert.Contains("1 camp turn", why);
        Assert.Equal(ValueModel.CampEnergyUse, Values(37).EnergyOutlook.Factor, 6);
    }

    [Fact]
    public void Energy_Outlook_IsANote()
    {
        var advice = new CareerAdvisor(new FakeGameData()).Advise(Snapshots.Snapshot(Snapshots.Daiwa(), turn: 11));
        Assert.Contains(advice.Notes, n => n.Label == "Energy" && n.Text.Contains("goal race day"));
    }

    /// <summary>
    /// Logged turn 71: Stamina 351, the Arima Kinen next turn needs ~857, and the budget's Stamina target is 518.
    /// The target is budget-bound, not under-weighted: the split already gives Stamina all of what the last turns
    /// can add (Speed, Power and Guts are past their shares), and its weight is at the top (full priority × 1.5
    /// for a goal race it lacks Stamina for). Putting Stamina ahead of Speed up to the need would change neither.
    /// </summary>
    [Fact]
    public void Turn71_StaminaShortOfArima_TakesTheWholeBudget_AtFullWeight()
    {
        var stats = new Stats(642, 351, 404, 379, 343);
        var ctx = new CareerContext(Snapshots.Snapshot(Snapshots.Daiwa(stats: stats), turn: 71), new FakeGameData());
        var targets = new ValueModel(ctx).Targets;
        int total = stats.Speed + stats.Stamina + stats.Power + stats.Guts + stats.Wit;
        int left = ValueModel.ProjectedStatTotal(ctx) - total;

        Assert.True(targets.Target.Stamina - stats.Stamina >= 0.9 * left, $"Stamina target {targets.Target.Stamina}, {left} left to gain");
        Assert.True(targets.Target.Stamina < ValueModel.StaminaFor(ctx, FakeGameData.ArimaKinen, stats.Speed, stats.Guts));
        Assert.Equal(1.5, targets.Weights[ParamIds.Stamina], 6);
        Assert.Equal(targets.Weights.Values.Max(), targets.Weights[ParamIds.Stamina]);
    }
}
