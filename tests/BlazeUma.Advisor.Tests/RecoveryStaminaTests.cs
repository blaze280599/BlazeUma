using BlazeUma.Data;
using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor.Tests;

/// <summary><see cref="RouteData"/> plus recovery skills with their real master.mdb values (float_ability_value_1_1).</summary>
internal sealed class RecoveryData(params RaceProgram[] races) : IGameData
{
    public static readonly SkillInfo SwingingMaestro = new(200351, "Swinging Maestro", 2, 20035, 508, 180,
        "corner_random==1@corner_random==2@corner_random==3@corner_random==4", 9, 550);
    public static readonly SkillInfo CornerRecovery = new(200352, "Corner Recovery ○", 1, 20035, 217, 180,
        "corner_random==1@corner_random==2@corner_random==3@corner_random==4", 9, 150);
    public static readonly SkillInfo Cooldown = new(200741, "Cooldown", 2, 20074, 508, 160, "distance_type==4&phase_random==1", 9, 550);
    public static readonly SkillInfo CalmAndCollected = new(200561, "Calm and Collected", 2, 20056, 508, 180,
        "running_style==2&phase_laterhalf_random==0&order_rate<=50", 9, 550);

    private static readonly SkillInfo[] Skills = [SwingingMaestro, CornerRecovery, Cooldown, CalmAndCollected];
    private readonly RouteData _inner = new(races);

    public SkillInfo? Skill(int skillId) => Skills.FirstOrDefault(s => s.Id == skillId) ?? _inner.Skill(skillId);
    public SkillInfo? SkillForHint(int groupId, int rarity) =>
        Skills.FirstOrDefault(s => s.GroupId == groupId && s.Rarity == rarity) ?? _inner.SkillForHint(groupId, rarity);

    public TurnDate Turn(int turn) => _inner.Turn(turn);
    public IReadOnlyList<GoalRace> Goals(IReadOnlyList<int> routeRaceIds) => _inner.Goals(routeRaceIds);
    public RaceProgram? Program(int programId) => _inner.Program(programId);
    public IReadOnlyList<RaceProgram> ProgramsForTurn(int turn) => [];
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

/// <summary>Recovery skills restore HP mid-race, so they lower the Stamina a full last spurt needs.</summary>
public class RecoveryStaminaTests
{
    private static readonly RaceProgram Long = new(1, 0, "Arima Kinen", 100, 2500, Ground.Turf, 12, 2, 3, 0, 30000);
    private static readonly RaceProgram Medium = new(2, 0, "Tenno Sho (Autumn)", 100, 2000, Ground.Turf, 10, 2, 3, 0, 25000);

    private static CareerContext Context(RaceProgram race, SkillInfo[] owned, SkillHint[]? hints = null, int style = 2) =>
        new(Snapshots.Snapshot(Snapshots.Daiwa(stats: new Stats(900, 700, 600, 350, 500), hints: hints) with
        {
            RunningStyle = style,
            Skills = [.. owned.Select(s => new AcquiredSkill(s.Id, 1))],
        }, turn: 50), new RecoveryData(race));

    private static int Need(CareerContext ctx, RaceProgram race) => ValueModel.StaminaFor(ctx, race, 1200, 400);
    private static int NoSkills(CareerContext ctx, RaceProgram race) => ValueModel.StaminaFor(ctx, race.Distance, 1200, 400);

    [Fact]
    public void RecoveryValues_ComeFromMasterData()
    {
        Assert.Equal(0.055, RecoveryData.SwingingMaestro.RecoveryFraction, 6);
        Assert.Equal(0.015, RecoveryData.CornerRecovery.RecoveryFraction, 6);
        Assert.Equal(0, new SkillInfo(200353, "Corner Recovery ×", 1, 20035, -100, 0, "corner_random==1", 9, -200).RecoveryFraction);
    }

    [Fact]
    public void RecoveryValues_MatchTheInstalledMasterDatabase()
    {
        string path = MasterDatabase.PathInGameDirectory(@"C:\Program Files (x86)\Steam\steamapps\common\UmamusumePrettyDerby");
        if (!File.Exists(path)) return;
        string cache = Path.Combine(Path.GetTempPath(), "BlazeUmaTests-" + Guid.NewGuid().ToString("N"));
        using (var db = MasterDatabase.Open(path, cache))
        {
            Assert.Equal(0.055, db.Skill(200351)!.RecoveryFraction, 6);   // Swinging Maestro
            Assert.Equal(0.015, db.Skill(200352)!.RecoveryFraction, 6);   // Corner Recovery ○
            Assert.Equal(0, db.Skill(200353)!.RecoveryFraction);           // Corner Recovery ×
        }
        try { Directory.Delete(cache, true); } catch (IOException) { }
    }

    [Fact]
    public void OwnedGoldRecovery_LowersThe2500mTarget()
    {
        var ctx = Context(Long, [RecoveryData.SwingingMaestro]);
        int without = NoSkills(ctx, Long), with = Need(ctx, Long);
        // 5.5% of a ~3300 max HP is ~180 HP; at 0.8 × 0.89 HP per Stamina that is ~250 Stamina for a pace chaser.
        Assert.InRange(without - with, 200, 300);
        var note = new ValueModel(ctx).Targets.StaminaNote;
        Assert.NotNull(note);
        Assert.Contains("Swinging Maestro, owned", note);
        Assert.Contains("Arima Kinen", note);
    }

    [Fact]
    public void RaceStamina_SolvesMaxHpTimesOnePlusRecovery()
    {
        // MaxHP (1 + f) ≥ HpNeeded: recovery equal to the whole shortfall over the distance term leaves nothing to train.
        double need = RaceStamina.HpNeeded(2500, RunningStyle.PaceChaser, 1200, 400, 7);
        Assert.Equal(0, RaceStamina.Required(2500, RunningStyle.PaceChaser, 1200, 400, 7, recovery: need / 2500 - 1));
        Assert.True(RaceStamina.Required(2500, RunningStyle.PaceChaser, 1200, 400, 7, recovery: 0.055)
            < RaceStamina.Required(2500, RunningStyle.PaceChaser, 1200, 400, 7));
    }

    [Fact]
    public void RecoveryThatDoesntFitTheRace_GivesNothing()
    {
        // Cooldown is long-distance only; Calm and Collected is for pace chasers, and this trainee is a late surger.
        var medium = Context(Medium, [RecoveryData.Cooldown]);
        Assert.Empty(new SkillAdvisor(medium).RecoveryFor(Medium));
        Assert.Equal(NoSkills(medium, Medium), Need(medium, Medium));
        Assert.Null(new ValueModel(medium).Targets.StaminaNote);

        var surger = Context(Long, [RecoveryData.CalmAndCollected], style: 3);
        Assert.Equal(NoSkills(surger, Long), Need(surger, Long));
    }

    [Fact]
    public void HintedRecovery_CountsHalf_AndGoldOverOwnedWhiteOnlyTheDifference()
    {
        var owned = Context(Long, [RecoveryData.SwingingMaestro]);
        var hinted = Context(Long, [], [new SkillHint(20035, 2, 1)]);
        int full = NoSkills(owned, Long) - Need(owned, Long), half = NoSkills(hinted, Long) - Need(hinted, Long);
        Assert.InRange(half, full * 0.4, full * 0.6);
        Assert.Contains("hinted ×50%", new ValueModel(hinted).Targets.StaminaNote);

        var upgrade = new SkillAdvisor(Context(Long, [RecoveryData.CornerRecovery], [new SkillHint(20035, 2, 1)])).RecoveryFor(Long);
        Assert.Equal(0.015 + 0.5 * 0.04, upgrade.Sum(r => r.Fraction), 6);
    }
}
