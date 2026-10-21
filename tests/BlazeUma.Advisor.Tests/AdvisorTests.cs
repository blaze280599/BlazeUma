using BlazeUma.Data;
using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor.Tests;

public class AdvisorTests
{
    private readonly FakeGameData _data = new();

    private Advice Advise(CareerSnapshot snapshot) => new CareerAdvisor(_data).Advise(snapshot);

    [Fact]
    public void FullEnergy_GoodMood_TrainsSpeed()
    {
        var advice = Advise(Snapshots.Snapshot(Snapshots.Daiwa(energy: 100, mood: Mood.Good)));
        Assert.Equal(Situation.TrainingTurn, advice.Situation);
        Assert.Equal("Train Speed", advice.Best!.Label);
    }

    [Fact]
    public void AwfulMood_PrefersRecreation()
    {
        var advice = Advise(Snapshots.Snapshot(Snapshots.Daiwa(energy: 70, mood: Mood.Awful)));
        Assert.Equal("Recreation", advice.Best!.Label);
    }

    [Fact]
    public void LowEnergy_HighFailure_AvoidsRiskyTraining()
    {
        var advice = Advise(Snapshots.Snapshot(Snapshots.Daiwa(energy: 20, mood: Mood.Good), commands: Snapshots.StandardCommands(failure: 35)));
        Assert.Contains(advice.Best!.Label, new[] { "Rest", "Train Wit" });
    }

    [Fact]
    public void GoalTurn_IsRaceDay()
    {
        var advice = Advise(Snapshots.Snapshot(Snapshots.Daiwa(), turn: 12));
        Assert.Equal(Situation.GoalRaceDay, advice.Situation);
        Assert.Contains("Junior Make Debut", advice.Summary);
    }

    [Fact]
    public void GetWellSoon_UsesKnownOdds_AndPicksTop()
    {
        EventEffect Mood(int n) => new(2, 20, n, 0);
        EventEffect LastStat(int n) => new(20, n, 0, 0);
        EventEffect Become(int id) => new(id == 6 ? 37 : 9, id, 0, 0);
        var choices = new List<EventChoice>
        {
            new(1, [[Mood(1), LastStat(5)], [Mood(1), Become(6), LastStat(5)]]),
            new(2, [[Mood(1), LastStat(10)], [LastStat(10), Become(6), Mood(1)], [Become(10)]]),
        };
        var story = new StoryEvent(501009713, 1363, 1009, 0, 10002, LooksActive: true);

        var advice = Advise(Snapshots.Snapshot(Snapshots.Daiwa(), story: story, choices: choices));

        Assert.Equal(Situation.Event, advice.Situation);
        Assert.Equal("Top choice", advice.Best!.Label);
        Assert.Contains("92%", advice.Best.Reasons[0]);
    }

    [Fact]
    public void InactiveStory_IsIgnored()
    {
        var story = new StoryEvent(501009713, 1363, 1009, 0, 10002, LooksActive: false);
        var choices = new List<EventChoice> { new(1, [[new EventEffect(1, 1, 10, 0)]]) };
        Assert.Equal(Situation.TrainingTurn, Advise(Snapshots.Snapshot(Snapshots.Daiwa(), story: story, choices: choices)).Situation);
    }

    [Fact]
    public void FanShortfall_MakesFittingRaceAttractive_AndBadAptitudeRaceNot()
    {
        _data.TurnRaces.AddRange([FakeGameData.Hakodate, FakeGameData.Niigata]);
        var ctx = new CareerContext(Snapshots.Snapshot(Snapshots.Daiwa(fans: 800), turn: 16), _data);
        var evaluator = new RaceEvaluator(ctx, new ValueModel(ctx));

        var niigata = evaluator.Evaluate(FakeGameData.Niigata);   // 1600m mile, aptitude A
        var hakodate = evaluator.Evaluate(FakeGameData.Hakodate); // 1200m sprint, aptitude E

        Assert.True(niigata.Score > 20, niigata.ToString());
        Assert.True(hakodate.Score < 0, hakodate.ToString());
        Assert.Equal("Niigata Junior Stakes", evaluator.BestPredicted()!.Label);
    }
}

public class ValueModelTests
{
    private static ValueModel Model(int energy = 50, Mood mood = Mood.Normal, Stats? stats = null)
    {
        var ctx = new CareerContext(Snapshots.Snapshot(Snapshots.Daiwa(energy, mood, stats)), new FakeGameData());
        return new ValueModel(ctx);
    }

    [Fact]
    public void Energy_IsWorthMoreWhenLow_AndCappedAtMax()
    {
        Assert.True(Model(energy: 20).Energy(20) > Model(energy: 70).Energy(20));
        Assert.Equal(Model(energy: 95).Energy(5), Model(energy: 95).Energy(50), 3);
        Assert.Equal(-Model(energy: 40).Energy(20, from: 20), Model(energy: 40).Energy(-20), 3);
    }

    [Fact]
    public void Mood_CannotGoAboveGreat()
    {
        Assert.Equal(0, Model(mood: Mood.Great).Mood(1));
        Assert.True(Model(mood: Mood.Awful).Mood(1) > Model(mood: Mood.Normal).Mood(1));
    }

    [Fact]
    public void StatAtCap_HasNoValue_AndStaminaTargetFollowsLongestGoal()
    {
        var model = Model(stats: new Stats(1404, 100, 140, 140, 110));
        Assert.Equal(0, model.Weight(ParamIds.Speed));
        Assert.Equal(ValueModel.StaminaFor(2500), model.Targets.Target.Stamina);
    }
}

public class SkillAdvisorTests
{
    private static SkillAdvisor Advisor(int turn = 14) =>
        new(new CareerContext(Snapshots.Snapshot(Snapshots.Daiwa(hints: [new SkillHint(20071, 1, 1)]), turn: turn), new FakeGameData()));

    [Fact]
    public void Fit_FollowsGoalDistances_AndRunningStyle()
    {
        var advisor = Advisor();
        Assert.True(advisor.Fit("distance_type==3&phase_random==1") > 0.4);   // medium-heavy route
        Assert.Equal(0, advisor.Fit("distance_type==1"));                     // no sprint goals
        Assert.Equal(1, advisor.Fit("running_style==2"));                     // pace chaser
        Assert.Equal(0, advisor.Fit("running_style==4"));
        Assert.Equal(1, advisor.Fit("running_style==4@running_style==2"));    // OR branch
    }

    [Fact]
    public void Predicted_AppliesHintDiscount()
    {
        var rosy = Advisor().Predicted().Single(c => c.Skill.Id == 200712);
        Assert.Equal(144, rosy.Price);
    }

    [Fact]
    public void Plan_SavesUntilGoalIsClose_ThenBuysFittingSkills()
    {
        Assert.Empty(Advisor(turn: 14).Plan(Advisor(turn: 14).Predicted(), 400).Buy);

        var nearGoal = Advisor(turn: 28);
        var buy = nearGoal.Plan(nearGoal.Predicted(), 400).Buy;
        Assert.NotEmpty(buy);
        Assert.True(buy.Sum(b => b.Price) <= 400);
        Assert.DoesNotContain(buy, b => b.Skill.Name == "Sprint Thing");
    }
}
