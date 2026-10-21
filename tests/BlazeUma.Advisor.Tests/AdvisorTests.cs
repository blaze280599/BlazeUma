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
        Assert.Equal("Recreation: trainee", advice.Best!.Label);
        Assert.DoesNotContain(advice.Options, o => o.Label == "Recreation: Light Hello");
    }

    [Fact]
    public void UnlockedFriendOuting_IsPreferredOverSoloRecreation()
    {
        var advice = Advise(Snapshots.Snapshot(Snapshots.Daiwa(energy: 70, mood: Mood.Awful, lightHelloOuting: true)));
        Assert.Equal("Recreation: Light Hello", advice.Best!.Label);
        Assert.Contains(advice.Options, o => o.Label == "Recreation: trainee");
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
    public void ActiveEventWithoutChoices_SaysContinue()
    {
        var story = new StoryEvent(809008001, 7498, 0, 9008, 10003, LooksActive: true);
        var advice = Advise(Snapshots.Snapshot(Snapshots.Daiwa(), story: story, choices: []));
        Assert.Equal(Situation.Event, advice.Situation);
        Assert.Contains("no choice", advice.Summary);
        Assert.Empty(advice.Options);
    }

    [Fact]
    public void ChoicesBuiltForAnotherEvent_AreFlagged()
    {
        var story = new StoryEvent(501009713, 1363, 1009, 0, 10002, LooksActive: true);
        var choices = new List<EventChoice> { new(1, [[new EventEffect(1, 1, 10, 0)]]) };
        var advice = Advise(Snapshots.Snapshot(Snapshots.Daiwa(), story: story, choices: choices, choicesFor: 7014));
        Assert.Equal("Warning", advice.Notes[0].Label);
    }

    [Fact]
    public void InactiveStory_IsIgnored()
    {
        var story = new StoryEvent(501009713, 1363, 1009, 0, 10002, LooksActive: false);
        var choices = new List<EventChoice> { new(1, [[new EventEffect(1, 1, 10, 0)]]) };
        Assert.Equal(Situation.TrainingTurn, Advise(Snapshots.Snapshot(Snapshots.Daiwa(), story: story, choices: choices)).Situation);
    }

    private RaceEvaluator Races(int turn = 16, Mood mood = Mood.Normal)
    {
        var ctx = new CareerContext(Snapshots.Snapshot(Snapshots.Daiwa(fans: 800, mood: mood), turn: turn), _data);
        return new RaceEvaluator(ctx, new ValueModel(ctx));
    }

    [Fact]
    public void FanShortfall_MakesFittingRaceAttractive_AndBadAptitudeRaceNot()
    {
        _data.TurnRaces.AddRange([FakeGameData.Hakodate, FakeGameData.Niigata]);
        var evaluator = Races();

        var niigata = evaluator.Evaluate(FakeGameData.Niigata);   // 1600m mile, aptitude A
        var hakodate = evaluator.Evaluate(FakeGameData.Hakodate); // 1200m sprint, aptitude E

        Assert.True(niigata.Score > 0, niigata.ToString());
        Assert.True(hakodate.Score < 0, hakodate.ToString());
        Assert.Equal("Niigata Junior Stakes", evaluator.BestPredicted()!.Label);
    }

    [Fact]
    public void Fans_AreUrgentOnlyWhenFewSuitableRacesRemain()
    {
        double lastChance = Races().Evaluate(FakeGameData.Niigata).Score;   // no other races before Tulip Sho
        _data.TurnRaces.Add(FakeGameData.Niigata);                         // a suitable race every turn
        double manyChances = Races().Evaluate(FakeGameData.Niigata).Score;
        Assert.True(lastChance > manyChances + 10, $"{lastChance} vs {manyChances}");
    }

    [Fact]
    public void FanDeadlineClose_RacingBeatsAStrongTraining()
    {
        // Turn 26, Tulip Sho on 29, 950 fans short, no other races before it: the race is the last real chance.
        var wakagoma = new RaceProgram(51, 0, "Wakagoma Stakes", 400, 2000, Ground.Turf, 1, 2, 2, 350, 2000);
        _data.TurnRaces.Add(wakagoma);
        var commands = new List<Command>
        {
            Snapshots.Train(TrainingIds.Stamina, 0, -19, ("stamina", 12), ("guts", 8), ("skillPoints", 4)),
            Snapshots.Simple(CommandType.Race, 401),
        };
        var advice = Advise(Snapshots.Snapshot(Snapshots.Daiwa(fans: 800, mood: Mood.Great), turn: 26, commands: commands));
        Assert.Equal("Race: Wakagoma Stakes", advice.Best!.Label);
        Assert.Contains(advice.Notes, n => n.Label == "Warning" && n.Text.Contains("950"));
    }

    [Fact]
    public void FansFromAnEarlierGoalRace_CountTowardALaterRequirement()
    {
        // 6,000 fans: Shuka Sho (turn 44) needs 7,500; the Tulip Sho on turn 29 is raced anyway (~+2,400).
        var ctx = new CareerContext(Snapshots.Snapshot(Snapshots.Daiwa(fans: 6000), turn: 28), _data);
        var race = new RaceEvaluator(ctx, new ValueModel(ctx)).Evaluate(FakeGameData.Niigata);
        Assert.Contains(race.Reasons, r => r.Contains("earlier goal races"));
        Assert.True(race.Score < 15, race.ToString());

        var advice = Advise(Snapshots.Snapshot(Snapshots.Daiwa(fans: 6000), turn: 28));
        Assert.DoesNotContain(advice.Notes, n => n.Label == "Warning");
    }

    [Fact]
    public void BadMood_LowersRaceValue() =>
        Assert.True(Races(mood: Mood.Bad).Evaluate(FakeGameData.Niigata).Score < Races().Evaluate(FakeGameData.Niigata).Score);
}

public class TrainingPartnerTests
{
    private static ScoredOption Score(int trainingId, params int[] partnerPositions)
    {
        var command = Snapshots.Train(trainingId, 0, -20, ("speed", 10)) with
        {
            Partners = partnerPositions.Select(p => new TrainingPartner(p, false)).ToList(),
        };
        var ctx = new CareerContext(Snapshots.Snapshot(Snapshots.Daiwa(), commands: [command]), new FakeGameData());
        return new TurnAdvisor(ctx, new ValueModel(ctx)).Rank().Single(o => o.Label.StartsWith("Train"));
    }

    [Fact]
    public void NpcBond_CountsHalfOfACardsBond()
    {
        double alone = Score(TrainingIds.Speed).Score;
        double card = Score(TrainingIds.Speed, 1).Score - alone;    // Tanino Gimlet, bond 20
        double npc = Score(TrainingIds.Speed, 102).Score - alone;   // Director Akikawa, bond 16
        Assert.True(card > 0);
        Assert.Equal(card / 2, npc, 3);
    }

    [Fact]
    public void Reasons_NamePartners_AndFlagFriendshipTraining()
    {
        // Silence Suzuka: speed card at bond 90 on Speed training.
        var speed = Score(TrainingIds.Speed, 2, 102);
        Assert.Contains(speed.Reasons, r => r.Contains("Silence Suzuka 90 friendship!") && r.Contains("Director Akikawa 16"));

        // Same card on Power training is not a friendship training.
        var power = Score(TrainingIds.Power, 2);
        Assert.DoesNotContain(power.Reasons, r => r.Contains("friendship!"));
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
    public void StaminaUrgency_AppliesOnce_EvenWithSeveralGoalsAhead()
    {
        // Turn 28 with low stamina: Tulip Sho, and later goals within 12 turns in the real route, must not stack.
        var ctx = new CareerContext(Snapshots.Snapshot(Snapshots.Daiwa(stats: new Stats(300, 166, 186, 243, 166)), turn: 28),
            new FakeGameData());
        double weight = new ValueModel(ctx).Weight(ParamIds.Stamina);
        Assert.InRange(weight, 0.5, 1.5 * 1.0);   // at most one ×1.5 on a priority-1.0 stat
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
    public void Fit_DiscountsSituationalAndSkillCountConditions()
    {
        var advisor = Advisor();   // Daiwa owns one skill (Red Ace)
        Assert.Equal(0.25, advisor.Fit("activate_count_later_half>=2"));      // Racing Spirit: Wit
        Assert.Equal(0.6, advisor.Fit("phase==3&is_overtake==1&is_lastspurt==1"), 3);   // Racing Spirit: Power
        Assert.Equal(1, advisor.Fit("phase==3&is_lastspurt==1"));
    }

    [Fact]
    public void Predicted_AppliesHintDiscount()
    {
        var rosy = Advisor().Predicted().Single(c => c.Skill.Id == 200712);
        Assert.Equal(144, rosy.Price);
    }

    [Fact]
    public void Predicted_GoldSkillIncludesItsWhiteVersion()
    {
        // Gold hint lv1 (160 → 144) plus the unowned white version at full price (160): 304, as the live shop charges.
        var ctx = new CareerContext(Snapshots.Snapshot(Snapshots.Daiwa(hints: [new SkillHint(20071, 2, 1)])), new FakeGameData());
        var gold = new SkillAdvisor(ctx).Predicted().Single(c => c.Skill.Id == 200711);
        Assert.Equal(144 + 160, gold.Price);
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
        // Tulip Sho is a 1600 m mile: medium-only skills wait for a medium race.
        Assert.DoesNotContain(buy, b => b.Skill.Name is "Rosy Outlook" or "Up-Tempo");
        Assert.Contains(buy, b => b.Skill.Name == "Preferred Position");   // pace chaser, any distance
    }
}
