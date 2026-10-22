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
    public void TurnBeforeGoalRace_LeadsWithBuyingSkills()
    {
        // Tulip Sho is turn 29: on turn 28 the planned skills come before the turn's action.
        var advice = Advise(Snapshots.Snapshot(Snapshots.Daiwa(energy: 100, mood: Mood.Good), turn: 28));
        Assert.NotEmpty(advice.SkillsToBuy!);
        Assert.StartsWith("Buy ", advice.Summary);
        Assert.Contains("first, then train", advice.Summary);

        var noSkills = Advise(Snapshots.Snapshot(Snapshots.Daiwa(energy: 100, mood: Mood.Good)));
        Assert.Empty(noSkills.SkillsToBuy!);
        Assert.Equal("Train Speed", noSkills.Summary);
    }

    [Fact]
    public void SkillShop_CountsSelectedSkillsBackIntoThePoints()
    {
        // Competitive Spirit (90) selected with + but not learned: the screen shows 10 points left of 100.
        var shop = new SkillShop(10, [new ShopSkill(200282, 90, 90, 0, 0, Obtained: false, Locked: false, Selected: true)]);
        var snapshot = Snapshots.Snapshot(Snapshots.Daiwa(), turn: 28) with
        {
            Screen = new Screen(null, "Gallop.SingleModeSkillLearningViewController", 0), SkillShop = shop,
        };
        var advice = Advise(snapshot);
        Assert.Equal(Situation.SkillShop, advice.Situation);
        Assert.Equal([200282], advice.SkillsToBuy!);
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
        // Same commands as Score: stat targets (and so weights) depend on the gains on offer.
        var ctx = new CareerContext(Snapshots.Snapshot(Snapshots.Daiwa(), commands: [Snapshots.Train(TrainingIds.Speed, 0, -20, ("speed", 10))]),
            new FakeGameData());
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
        var model = Model(stats: new Stats(1404, 600, 700, 300, 700));
        Assert.Equal(0, model.Weight(ParamIds.Speed));
        // Arima Kinen 2500 m at capped Speed drives the target: a pace chaser needs ~1100+ (checked in Umalator).
        // The projected budget (~4900) covers it, so the race requirement, not Stamina's share, sets the target.
        Assert.InRange(model.Targets.Target.Stamina, 1100, 1404);
    }
}

public class SkillAdvisorTests
{
    private static SkillAdvisor Advisor(int turn = 14) =>
        new(new CareerContext(Snapshots.Snapshot(Snapshots.Daiwa(hints: [new SkillHint(20071, 1, 1)]), turn: turn), new FakeGameData()));

    [Fact]
    public void FromShop_OffersOnlyTheNextSkillInEachRow()
    {
        ShopSkill Skill(int id, int row, int step, bool obtained = false) => new(id, 100, 100, 0, 0, obtained, false, false, row, step);
        // Row 0 is a ○ → ◎ pair (◎ needs ○ first); row 1 a single skill.
        var shop = new SkillShop(500, [Skill(200712, 0, 0), Skill(200722, 0, 1), Skill(200572, 1, 0)]);
        Assert.Equal([200572, 200712], Advisor().FromShop(shop).Select(c => c.Skill.Id).Order());

        var afterFirst = new SkillShop(500, [Skill(200712, 0, 0, obtained: true), Skill(200722, 0, 1), Skill(200572, 1, 0)]);
        Assert.Equal([200572, 200722], Advisor().FromShop(afterFirst).Select(c => c.Skill.Id).Order());
    }

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
    public void Targets_FollowTheStatPriority_WithinReach()
    {
        // Turn 14 with ~20-stat trainings projects ~1900 in all: Speed ≈ Stamina > Power ≈ Wit > Guts, Speed short of the cap.
        var target = new ValueModel(Context()).Targets.Target;
        Assert.True(target.Speed < 1404 && target.Speed >= target.Power && target.Power >= target.Guts && target.Wit >= target.Guts);
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
    public void Camp_UsesItsOwnCommandIds_AndRestAndRecreation()
    {
        var camp = Rank(Context(turn: 38, energy: 40, mood: Mood.Bad, commands: Snapshots.CampCommands()));
        Assert.Equal(new HashSet<string> { "Train Speed", "Train Stamina", "Train Power", "Train Guts", "Train Wit" },
            camp.Where(o => o.Label.StartsWith("Train ")).Select(o => o.Label).ToHashSet());
        Assert.DoesNotContain(camp, o => o.Label.StartsWith("Train Training") || o.Label == "Rest" || o.Label.StartsWith("Recreation"));

        var rest = camp.Single(o => o.Label == TurnAdvisor.RestAndRecreationLabel);
        var normal = Rank(Context(turn: 30, energy: 40, mood: Mood.Bad)).Single(o => o.Label == "Rest");
        Assert.Contains("Rest & Recreation", rest.Reasons[0]);
        Assert.True(rest.Score > normal.Score);
    }

    private static IReadOnlyList<ScoredOption> Rank(CareerContext ctx) => new TurnAdvisor(ctx, new ValueModel(ctx)).Rank();

    [Fact]
    public void LowEnergyLastTurnBeforeCamp_RestsRatherThanTrainingWit()
    {
        // Live turn 36: energy 32, risky trainings, Wit narrowly beat Rest before the camp premium.
        var before = Rank(Context(turn: 36, energy: 32, mood: Mood.Great, commands: Snapshots.StandardCommands(failure: 30)));
        Assert.Equal("Rest", before[0].Label);
        Assert.Contains(before[0].Reasons, r => r.Contains("summer camp in 1"));

        var ordinary = Rank(Context(turn: 30, energy: 32, mood: Mood.Great)).Single(o => o.Label == "Rest");
        Assert.DoesNotContain(ordinary.Reasons, r => r.Contains("summer camp"));
        var high = Rank(Context(turn: 36, energy: 90, mood: Mood.Great)).Single(o => o.Label == "Rest");
        Assert.DoesNotContain(high.Reasons, r => r.Contains("summer camp"));
    }

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

/// <summary>Stamina requirements from the race HP formulas, pinned to Umalator runs (no skills, Normal mood, firm, A aptitude).</summary>
public class RaceStaminaTests
{
    private const int A = 7;

    [Fact]
    public void LastSpurtSpeed_MatchesUmalatorsTopSpeed()
    {
        // Umalator max m/s, Nakayama 2500, Speed 1200 Guts 400: pace chaser 23.71, late surger 24.10 (includes downhills).
        Assert.InRange(RaceStamina.LastSpurtSpeed(2500, RunningStyle.PaceChaser, 1200, 400, A), 23.3, 23.8);
        Assert.InRange(RaceStamina.LastSpurtSpeed(2500, RunningStyle.LateSurger, 1200, 400, A), 23.7, 24.2);
    }

    [Theory]
    // Umalator spurt rates: PC 2500 — 81% at 1050, 96% at 1100, 98% at 1200; LS 2500 — 94% at 1000, 98% at 1100;
    // PC 1600 — 87% at 500, 97% at 600. The requirement should land near the 95% point.
    [InlineData(2500, RunningStyle.PaceChaser, 1050, 1200)]
    [InlineData(2500, RunningStyle.LateSurger, 1000, 1100)]
    [InlineData(1600, RunningStyle.PaceChaser, 500, 600)]
    public void Required_LandsNearUmalatorsFullSpurtPoint(int distance, RunningStyle style, int low, int high)
    {
        Assert.InRange(RaceStamina.Required(distance, style, 1200, 400, A), low, high);
    }

    [Fact]
    public void LongRace_IsOutOfReachWithoutRecoverySkills()
    {
        // Kyoto 3000 pace chaser: Umalator spurt rate 0% even at 1400 Stamina.
        Assert.True(RaceStamina.Required(3000, RunningStyle.PaceChaser, 1200, 400, A) > 1400);
    }

    [Fact]
    public void PaceChasers_NeedMoreThanLateSurgers_AndSlowerTraineesLess()
    {
        Assert.True(RaceStamina.Required(2500, RunningStyle.PaceChaser, 1200, 400, A) > RaceStamina.Required(2500, RunningStyle.LateSurger, 1200, 400, A));
        Assert.True(RaceStamina.Required(2000, RunningStyle.PaceChaser, 400, 200, A) < RaceStamina.Required(2000, RunningStyle.PaceChaser, 1200, 200, A));
    }
}
