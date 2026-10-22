using BlazeUma.Data;
using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor.Tests;

/// <summary>
/// A partner's hint is worth the discount it may put on one of the card's hint skills, for this trainee. Daiwa
/// Scarlet is a Pace Chaser with Mile, Medium and Long goals; Tanino Gimlet is deck position 1.
/// </summary>
public class HintValueTests
{
    private static readonly SkillInfo FrontRunnerCorners = new(201232, "Front Runner Corners ○", 1, 20123, 217, 130, "running_style==1&corner_random==1", 27);
    private static readonly SkillInfo SprintCorners = new(201162, "Sprint Corners ○", 1, 20116, 217, 130, "distance_type==1&corner_random==1", 27);

    private static ScoredOption Train(SupportCardHints? hints, IReadOnlyList<AcquiredSkill>? owned = null)
    {
        var data = new FakeGameData().WithSkills(FrontRunnerCorners, SprintCorners);
        if (hints is not null) data.CardHints[30145] = hints;
        var command = Snapshots.Train(TrainingIds.Power, 0, -20, ("power", 10)) with { Partners = [new TrainingPartner(1, true)] };
        var character = Snapshots.Daiwa() with { Skills = owned ?? [new AcquiredSkill(10091, 2)] };
        var ctx = new CareerContext(Snapshots.Snapshot(character, commands: [command]), data);
        return new TurnAdvisor(ctx, new ValueModel(ctx)).Rank().Single(o => o.Label.StartsWith("Train"));
    }

    private static SupportCardHints Skills(params int[] ids) => new(ids, []);

    [Fact]
    public void FittingHintSkills_BeatOffDistanceAndOffStyleOnes()
    {
        double fitting = Train(Skills(200722, 200572)).Score;            // Up-Tempo (Medium), Preferred Position (Pace Chaser)
        double off = Train(Skills(SprintCorners.Id, FrontRunnerCorners.Id)).Score;
        Assert.True(fitting > off + 3, $"fitting {fitting:0.0}, off {off:0.0}");
        Assert.Contains(Train(Skills(200722, 200572)).Reasons, r => r.StartsWith("Tanino Gimlet hint: likely ") && r.Contains("Preferred Position"));
    }

    [Fact]
    public void OwnedSkills_CountAsNothing()
    {
        double none = Train(Skills()).Score;   // no options listed: the flat fallback
        double owned = Train(Skills(200722, 200572), [new AcquiredSkill(200722, 1), new AcquiredSkill(200572, 1)]).Score;
        double open = Train(Skills(200722, 200572)).Score;
        Assert.True(owned < open, $"owned {owned:0.0}, open {open:0.0}");
        Assert.Equal(Train(null).Score, none, 6);
        Assert.Contains(Train(Skills(200722, 200572), [new AcquiredSkill(200722, 1), new AcquiredSkill(200572, 1)]).Reasons,
            r => r == "Tanino Gimlet hint: nothing new (+0.0)");
    }

    [Fact]
    public void MaxedHint_CountsAsNothing_AndStatBonusIsOneOption()
    {
        var ctx = new CareerContext(Snapshots.Snapshot(Snapshots.Daiwa(hints: [new SkillHint(20072, 1, 5)])), new FakeGameData());
        var upTempo = ctx.Data.Skill(200722)!;
        Assert.Equal(0, new SkillAdvisor(ctx).HintWorth(upTempo));
        Assert.True(new SkillAdvisor(ctx).HintWorth(ctx.Data.Skill(200572)!) > 0);

        // Speed +6 / Power +2 alongside two skills: a third of each.
        double stats = Train(new SupportCardHints([200722, 200572], [new Dictionary<int, int> { [ParamIds.Speed] = 6, [ParamIds.Power] = 2 }])).Score;
        Assert.True(stats > Train(Skills(200722, 200572, SprintCorners.Id)).Score);
    }

    [Fact]
    public void EventHint_ValuesTheNamedSkill()
    {
        var ctx = new CareerContext(Snapshots.Snapshot(Snapshots.Daiwa()), new FakeGameData().WithSkills(SprintCorners));
        var events = new EventAdvisor(ctx, new ValueModel(ctx));
        var (text, upTempo) = events.Describe(new EventEffect(6, 200722, 3, 0));
        Assert.Equal("hint Up-Tempo +3", text);
        double fit = new SkillAdvisor(ctx).Fit(ctx.Data.Skill(200722)!.Condition);
        Assert.Equal(0.30 * 160 * ValueModel.SkillPointValue * fit, upTempo, 3);   // 30% off 160 SP
        Assert.True(events.Describe(new EventEffect(6, SprintCorners.Id, 3, 0)).Value < upTempo / 5);
        Assert.Equal(3 * ValueModel.HintValue, events.Describe(new EventEffect(6, 999999, 3, 0)).Value);
    }
}
