using BlazeUma.Data;
using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor.Tests;

/// <summary>
/// Skills that roll against Wit fire (100 − 9000 / Wit)% of the time, at least 20%: their value and the HP recovery
/// skills restore scale with it, and Wit is worth more the more such skills there are and the lower Wit is.
/// </summary>
public class WitActivationTests
{
    private static readonly SkillInfo GoldRecovery = new(900, "Gold recovery", 2, 90, 508, 200, "", 9, 550, WitCheck: true);

    private static CareerContext Context(int wit = 110, int turn = 14, FakeGameData? data = null, IReadOnlyList<AcquiredSkill>? skills = null)
    {
        var daiwa = Snapshots.Daiwa(stats: new Stats(200, 100, 140, 140, wit));
        if (skills is not null) daiwa = daiwa with { Skills = skills };
        return new CareerContext(Snapshots.Snapshot(daiwa, turn: turn), data ?? new FakeGameData());
    }

    [Theory]
    [InlineData(100, 0.2)]   // 10% by the formula, floored at 20%
    [InlineData(300, 0.7)]
    [InlineData(900, 0.9)]
    public void ActivationChance_FollowsWit(int wit, double chance) =>
        Assert.Equal(chance, SkillAdvisor.ActivationChance(wit), 3);

    [Fact]
    public void WitRolledSkill_IsWorthItsActivationChance()
    {
        var ctx = Context();
        var advisor = new SkillAdvisor(ctx);
        var rolled = new SkillInfo(1, "rolled", 1, 1, 217, 160, "", 27, WitCheck: true);
        var passive = rolled with { Id = 2, WitCheck = false };

        double activation = advisor.Activation(rolled);
        Assert.InRange(activation, 0.2, 0.99);
        Assert.Equal(1, advisor.Activation(passive));
        Assert.Equal(advisor.Score(passive, 160).Value * activation, advisor.Score(rolled, 160).Value, 6);
    }

    [Fact]
    public void Activation_UsesTheWitExpectedOnRaceDay()
    {
        var ctx = Context(wit: 110);
        int raceWit = ValueModel.WitAt(ctx, 72);
        Assert.True(raceWit > 110);
        Assert.True(new SkillAdvisor(ctx).Activation(GoldRecovery) > SkillAdvisor.ActivationChance(110));
    }

    [Fact]
    public void Recovery_CountsOnlyWhenTheSkillFires()
    {
        var data = new FakeGameData().WithSkills(GoldRecovery);
        var ctx = Context(data: data, skills: [new AcquiredSkill(900, 1)]);
        var recovery = Assert.Single(new SkillAdvisor(ctx).RecoveryFor(FakeGameData.ArimaKinen));
        double chance = SkillAdvisor.ActivationChance(ValueModel.WitAt(ctx, 72));   // Arima Kinen is turn 72
        Assert.Equal(0.055 * chance, recovery.Fraction, 6);
    }

    [Fact]
    public void SkillWitValue_FallsAsWitRises()
    {
        double low = ValueModel.SkillWitValue(Context(wit: 110));
        double high = ValueModel.SkillWitValue(Context(wit: 700));
        Assert.True(low > 0);
        Assert.True(high < low / 2);
    }

    [Fact]
    public void SkillWitValue_CountsOwnedWitRolledSkills()
    {
        var data = new FakeGameData().WithSkills(GoldRecovery);
        double without = ValueModel.SkillWitValue(Context(data: data, skills: []));
        double with = ValueModel.SkillWitValue(Context(data: data, skills: [new AcquiredSkill(900, 1)]));
        Assert.True(with > without);
    }

    [Fact]
    public void SkillWitValue_IsNothingOnceTheGoalsAreRun() =>
        Assert.Equal(0, ValueModel.SkillWitValue(Context(turn: 78)));
}
