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
    public void CardBond_IsDerivedFromTheCard_AndNpcBondKeepsHalfTheFlatEstimate()
    {
        double alone = Score(TrainingIds.Speed).Score;
        double card = Score(TrainingIds.Speed, 1).Score - alone;    // Tanino Gimlet, bond 20
        double npc = Score(TrainingIds.Speed, 102).Score - alone;   // Director Akikawa, bond 16
        var ctx = new CareerContext(Snapshots.Snapshot(Snapshots.Daiwa()), new FakeGameData());
        Assert.Equal(new ValueModel(ctx).BondTrainingValue(20, TrainingIds.Power, new SupportCardEffects(30, 20, 0)), card, 3);
        Assert.Equal(ValueModel.BondValuePerPartner * (1 - 14 / 48.0) * 0.5, npc, 3);
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

/// <summary>Rules taken from uma.guide: 1400-cap targets, summer camp, Happy Meek, recurring events.</summary>
public class UmaGuideRuleTests
{
    private static CareerContext Context(int turn = 14, int energy = 50, Mood mood = Mood.Normal, IReadOnlyList<Command>? commands = null,
        StoryEvent? story = null, IReadOnlyList<EventChoice>? choices = null) =>
        new(Snapshots.Snapshot(Snapshots.Daiwa(energy, mood), turn: turn, commands: commands, story: story, choices: choices), new FakeGameData());

    private static Advice Advise(int turn, int energy, IReadOnlyList<EventChoice> choices, int eventKey) =>
        new CareerAdvisor(new FakeGameData()).Advise(Snapshots.Snapshot(Snapshots.Daiwa(energy), turn: turn,
            story: new StoryEvent(501_009_000 + eventKey, 1, 1009, 0, 1, LooksActive: true), choices: choices));

    [Fact]
    public void Targets_CapSpeed_AndRaisePowerAndWit()
    {
        var target = new ValueModel(Context()).Targets.Target;
        Assert.Equal(1404, target.Speed);
        Assert.Equal(900, target.Power);   // pace chaser
        Assert.Equal(1000, target.Wit);
    }

    [Fact]
    public void SummerCamp_IsFoundFromTheCalendar()
    {
        Assert.True(Context(turn: 37).InSummerCamp);
        Assert.False(Context(turn: 36).InSummerCamp);
        Assert.Equal(2, Context(turn: 35).TurnsUntilSummerCamp);
        Assert.Null(Context(turn: 38).TurnsUntilSummerCamp);
        Assert.Equal(61 - 41, Context(turn: 41).TurnsUntilSummerCamp);
    }

    [Fact]
    public void EnergyBeforeCamp_IsWorthMore()
    {
        Assert.True(new ValueModel(Context(turn: 36, energy: 70)).Energy(20) > new ValueModel(Context(turn: 30, energy: 70)).Energy(20));
    }

    [Fact]
    public void RestInCamp_IsRestAndRecreation()
    {
        var rest = Rank(Context(turn: 38, energy: 40, mood: Mood.Bad)).Single(o => o.Label == "Rest");
        var normal = Rank(Context(turn: 30, energy: 40, mood: Mood.Bad)).Single(o => o.Label == "Rest");
        Assert.Contains("Rest & Recreation", rest.Reasons[0]);
        Assert.True(rest.Score > normal.Score);
    }

    private static IReadOnlyList<ScoredOption> Rank(CareerContext ctx) => new TurnAdvisor(ctx, new ValueModel(ctx)).Rank();

    private static double SpeedWith(int turn, params int[] partners)
    {
        var command = Snapshots.Train(TrainingIds.Speed, 0, -20, ("speed", 10)) with
        {
            Partners = partners.Select(p => new TrainingPartner(p, false)).ToList(),
        };
        return Rank(Context(turn: turn, commands: [command])).Single().Score;
    }

    [Fact]
    public void HappyMeek_AddsDuelValue_ButNotInCamp()
    {
        Assert.InRange(SpeedWith(30, TurnAdvisor.HappyMeekPosition) - SpeedWith(30), 5, 20);
        Assert.Equal(SpeedWith(38), SpeedWith(38, TurnAdvisor.HappyMeekPosition), 3);
    }

    private static EventEffect Param(int param, int amount) => new(1, param, amount, 0);

    private static readonly IReadOnlyList<EventChoice> ClassicNewYear =
    [
        new(1, [[Param(ParamIds.Speed, 10)]]),
        new(2, [[Param(ParamIds.Energy, 20)]]),
        new(3, [[Param(ParamIds.SkillPoints, 20)]]),
    ];

    [Theory]
    [InlineData(25, 60, "Bottom choice")]        // Classic: energy fits
    [InlineData(25, 90, "Choice 3")]        // Classic: energy would overflow → SP
    [InlineData(49, 70, "Choice 3")]        // Senior: Raffle next turn → SP
    [InlineData(49, 30, "Bottom choice")]        // Senior: too low to train
    public void NewYear_FollowsTheGuide(int turn, int energy, string expected)
    {
        Assert.Equal(expected, Advise(turn, energy, ClassicNewYear, 101).Best!.Label);
    }

    [Fact]
    public void ClassicSummerEvent_PicksPower()
    {
        var choices = new List<EventChoice> { new(1, [[Param(ParamIds.Guts, 10)]]), new(2, [[Param(ParamIds.Power, 10)]]) };
        var best = Advise(38, 50, choices, 104).Best!;
        Assert.Equal("Bottom choice", best.Label);
        Assert.StartsWith("uma.guide", best.Reasons[0]);
    }

    [Fact]
    public void Acupuncturist_TakesWinningChakra()
    {
        var all20 = new List<EventEffect> { new(13, 20, 0, 0) };
        var choices = Enumerable.Range(1, 5).Select(i => new EventChoice(i, [i == 1 ? all20 : [Param(ParamIds.Energy, 10)]])).ToList();
        Assert.Equal("Bottom choice", Advise(30, 50, choices, 720).Best!.Label);
    }
}

public class BondValueTests
{
    private static ValueModel Model(int turn = 14) =>
        new(new CareerContext(Snapshots.Snapshot(Snapshots.Daiwa(), turn: turn), new FakeGameData()));

    private static readonly SupportCardEffects Typical = new(50, 25, 50);

    [Fact]
    public void HigherFriendshipBonus_AndSpecialtyPriority_AreWorthMore()
    {
        var model = Model();
        double typical = model.BondTrainingValue(20, TrainingIds.Speed, Typical);
        Assert.True(model.BondTrainingValue(20, TrainingIds.Speed, Typical with { FriendshipBonus = 35 }) > typical);
        Assert.True(model.BondTrainingValue(20, TrainingIds.Speed, Typical with { SpecialtyPriority = 100 }) > typical);
        Assert.InRange(typical, 1, 10);   // same scale as the flat 3 it replaces
    }

    [Fact]
    public void CardOnAWantedStat_IsWorthMoreThanOneOnAnUnwantedStat()
    {
        var model = Model();
        Assert.True(model.BondTrainingValue(20, TrainingIds.Speed, Typical) > model.BondTrainingValue(20, TrainingIds.Guts, Typical));
    }

    [Fact]
    public void NoValue_AtFriendship_OrWhenItCannotBeReachedInTime()
    {
        Assert.Equal(0, Model().BondTrainingValue(80, TrainingIds.Speed, Typical));
        Assert.Equal(0, Model(turn: 70).BondTrainingValue(0, TrainingIds.Speed, Typical));
        Assert.True(Model(turn: 70).BondTrainingValue(76, TrainingIds.Speed, Typical) > 0);
    }
}
