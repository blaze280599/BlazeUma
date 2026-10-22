using BlazeUma.Data;
using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor.Tests;

/// <summary><see cref="FakeGameData"/> with a route of the given goal races (turns 40, 52, 64 …) and the finals.</summary>
internal sealed class RouteData(params RaceProgram[] races) : IGameData
{
    private readonly FakeGameData _inner = new();

    public IReadOnlyList<GoalRace> Goals(IReadOnlyList<int> routeRaceIds) =>
        races.Select((r, i) => new GoalRace(i + 1, i + 1, 40 + 12 * i, 3, r.Name, r))
            .Append(new GoalRace(99, 99, 74, 1, "URA Finale Qualifier", null)).ToList();

    public TurnDate Turn(int turn) => _inner.Turn(turn);
    public RaceProgram? Program(int programId) => races.FirstOrDefault(r => r.ProgramId == programId);
    public IReadOnlyList<RaceProgram> ProgramsForTurn(int turn) => [];
    public SkillInfo? Skill(int skillId) => _inner.Skill(skillId);
    public SkillInfo? SkillForHint(int groupId, int rarity) => _inner.SkillForHint(groupId, rarity);
    public IReadOnlyList<int> BaseSkillIds(int cardId, int talentLevel) => _inner.BaseSkillIds(cardId, talentLevel);
    public SupportCardInfo? SupportCard(int supportCardId) => _inner.SupportCard(supportCardId);
    public SupportCardEffects? SupportCardEffects(int supportCardId, int limitBreak, int? exp = null) =>
        _inner.SupportCardEffects(supportCardId, limitBreak, exp);
    public SupportCardHints? SupportCardHints(int supportCardId) => _inner.SupportCardHints(supportCardId);
    public ConditionInfo? Condition(int conditionId) => _inner.Condition(conditionId);
    public string? StoryTitle(int storyId) => _inner.StoryTitle(storyId);
    public string? CharacterName(int charaId) => _inner.CharacterName(charaId);
    public EventEffectFormat? EventEffect(int displayId) => _inner.EventEffect(displayId);
}

/// <summary>Achievable end-of-career targets: a projected stat budget split by uma.guide's priority.</summary>
public class StatTargetTests
{
    private static RaceProgram Race(int id, int distance) => new(id, 0, $"Race {distance} m", 100, distance, Ground.Turf, 6, 1, 3, 0, 10000);

    private static readonly Stats MidCareer = new(450, 300, 300, 250, 280);

    private static CareerContext Context(Stats stats, int turn = 30, IGameData? data = null, int style = 2) =>
        new(Snapshots.Snapshot(Snapshots.Daiwa(stats: stats) with { RunningStyle = style }, turn: turn), data ?? new FakeGameData());

    [Fact]
    public void MidCareer_TargetsSplitTheProjectedTotal_UnderTheCaps()
    {
        var ctx = Context(MidCareer);
        var target = new ValueModel(ctx).Targets.Target;
        var max = ctx.Character.MaxStats;
        int sum = target.Speed + target.Stamina + target.Power + target.Guts + target.Wit;

        // Turn 30, 48 turns left, best two standard trainings give 20 and 18: 1580 + 48 × (19 × 0.7 + 5).
        Assert.InRange(ValueModel.ProjectedStatTotal(ctx), 2400, 2500);
        Assert.InRange(sum, ValueModel.ProjectedStatTotal(ctx) - 5, ValueModel.ProjectedStatTotal(ctx));
        Assert.True(target.Speed < max.Speed, "a 2450-total career can't reach the Speed cap");
        Assert.True(target.Speed <= max.Speed && target.Stamina <= max.Stamina && target.Power <= max.Power
            && target.Guts <= max.Guts && target.Wit <= max.Wit);
        // uma.guide order: Stamina ≈ Speed > Power ≈ Wit > Guts.
        Assert.True(target.Speed >= target.Power && target.Power >= target.Guts && target.Wit >= target.Guts);
    }

    [Fact]
    public void StrongCareer_CapsSpeed_AndSharesTheRest()
    {
        var ctx = Context(new Stats(1300, 1000, 900, 450, 900), turn: 60);
        var target = new ValueModel(ctx).Targets.Target;
        Assert.Equal(ctx.Character.MaxStats.Speed, target.Speed);
        Assert.True(target.Power > 800 && target.Wit > 800 && target.Guts >= 400);
    }

    [Fact]
    public void Sprinter_GetsMorePower_LongRunnerMoreGuts()
    {
        var sprint = new ValueModel(Context(MidCareer, data: new RouteData(Race(1, 1200), Race(2, 1400), Race(3, 1200)))).Targets.Target;
        var stayer = new ValueModel(Context(MidCareer, data: new RouteData(Race(1, 3000), Race(2, 2500), Race(3, 3200)))).Targets.Target;
        Assert.True(sprint.Power > stayer.Power, $"sprint Power {sprint.Power} vs long {stayer.Power}");
        Assert.True(stayer.Guts > sprint.Guts, $"long Guts {stayer.Guts} vs sprint {sprint.Guts}");
        // A sprint route needs far less Stamina, and what it doesn't need goes to the other stats.
        Assert.True(sprint.Stamina < stayer.Stamina && sprint.Speed >= stayer.Speed);
    }

    [Fact]
    public void Closer_GetsMorePower_ThanPaceChaser()
    {
        var chaser = new ValueModel(Context(MidCareer, style: 2)).Targets.Target;
        var closer = new ValueModel(Context(MidCareer, style: 4)).Targets.Target;
        Assert.True(closer.Power > chaser.Power);
    }

    [Fact]
    public void Weights_FallAsAStatNearsItsTarget()
    {
        double behind = new ValueModel(Context(MidCareer with { Speed = 300 })).Weight(ParamIds.Speed);
        double onPlan = new ValueModel(Context(MidCareer with { Speed = 600 })).Weight(ParamIds.Speed);
        double ahead = new ValueModel(Context(MidCareer with { Speed = 1000 })).Weight(ParamIds.Speed);
        Assert.True(behind > onPlan && onPlan > ahead, $"{behind:0.00} > {onPlan:0.00} > {ahead:0.00}");
    }

    [Fact]
    public void Weights_DontCollapseLateForAStatOnPlan()
    {
        // Late in the career every target is close to the stats; a stat with its share still to gain keeps weight.
        var model = new ValueModel(Context(new Stats(700, 690, 380, 260, 350), turn: 70));
        Assert.InRange(model.Weight(ParamIds.Speed), 0.6, 1.25);
    }
}
