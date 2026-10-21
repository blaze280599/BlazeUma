using BlazeUma.Data;
using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor.Tests;

/// <summary>Small in-memory game data: Daiwa Scarlet's first goals and a few skills and races.</summary>
internal sealed class FakeGameData : IGameData
{
    public static readonly RaceProgram Debut = new(1067, 904183, "Junior Make Debut", 900, 2000, Ground.Turf, 6, 2, 1, 0, 700);
    public static readonly RaceProgram TulipSho = new(185, 0, "Tulip Sho", 200, 1600, Ground.Turf, 3, 1, 2, 1750, 6000);
    public static readonly RaceProgram ShukaSho = new(167, 0, "Shuka Sho", 100, 2000, Ground.Turf, 10, 2, 2, 7500, 20000);
    public static readonly RaceProgram TennoShoAutumn = new(76, 0, "Tenno Sho (Autumn)", 100, 2000, Ground.Turf, 10, 2, 3, 20000, 25000);
    public static readonly RaceProgram ArimaKinen = new(81, 0, "Arima Kinen", 100, 2500, Ground.Turf, 12, 2, 3, 25000, 30000);
    public static readonly RaceProgram Hakodate = new(628, 0, "Hakodate Junior Stakes", 300, 1200, Ground.Turf, 7, 2, 1, 350, 3100);
    public static readonly RaceProgram Niigata = new(629, 0, "Niigata Junior Stakes", 300, 1600, Ground.Turf, 8, 2, 1, 350, 3100);

    public List<RaceProgram> TurnRaces { get; } = [];

    private readonly Dictionary<int, SkillInfo> _skills = new[]
    {
        new SkillInfo(200712, "Rosy Outlook", 1, 20071, 217, 160, "distance_type==3&phase_random==1&order<=3", 9),
        new SkillInfo(200711, "Trackblazer", 2, 20071, 508, 160, "distance_type==3&phase_random==1&order<=3", 9),
        new SkillInfo(200722, "Up-Tempo", 1, 20072, 217, 160, "distance_type==3&phase_random==1&order_rate<=50", 27),
        new SkillInfo(200572, "Preferred Position", 1, 20057, 217, 180, "running_style==2&phase_random==1&order_rate<=50", 9),
        new SkillInfo(200282, "Competitive Spirit ○", 1, 20028, 129, 90, "running_style_count_same_rate>=40", 3),
        new SkillInfo(200012, "Sprint Thing", 1, 20001, 217, 110, "distance_type==1", 27),
    }.ToDictionary(s => s.Id);

    public TurnDate Turn(int turn) => new(turn, turn <= 24 ? 1 : turn <= 48 ? 2 : 3, 7, 1);

    public IReadOnlyList<GoalRace> Goals(IReadOnlyList<int> routeRaceIds) =>
    [
        new GoalRace(22, 1, 12, 0, Debut.Name, Debut),
        new GoalRace(23, 2, 29, 5, TulipSho.Name, TulipSho),
        new GoalRace(26, 5, 44, 3, ShukaSho.Name, ShukaSho),
        new GoalRace(29, 8, 68, 1, TennoShoAutumn.Name, TennoShoAutumn),
        new GoalRace(30, 9, 72, 1, ArimaKinen.Name, ArimaKinen),
        new GoalRace(31, 10, 74, 1, "URA Finale Qualifier", null),
    ];

    public RaceProgram? Program(int programId) =>
        new[] { Debut, TulipSho, ShukaSho, TennoShoAutumn, ArimaKinen, Hakodate, Niigata }.FirstOrDefault(p => p.ProgramId == programId);
    public IReadOnlyList<RaceProgram> ProgramsForTurn(int turn) => TurnRaces;
    public SkillInfo? Skill(int skillId) => _skills.GetValueOrDefault(skillId);
    public SkillInfo? SkillForHint(int groupId, int rarity) => _skills.Values.FirstOrDefault(s => s.GroupId == groupId && s.Rarity == rarity);
    public IReadOnlyList<int> BaseSkillIds(int cardId, int talentLevel) => [200282, 200722, 200572];
    public SupportCardInfo? SupportCard(int supportCardId) => supportCardId switch
    {
        30145 => new SupportCardInfo(30145, "[Welcome to Umayuru] Tanino Gimlet", 1084, 1, 102),
        10002 => new SupportCardInfo(10002, "[Tracen Academy] Silence Suzuka", 1002, 1, 101),
        _ => null,
    };
    public ConditionInfo? Condition(int conditionId) => conditionId switch
    {
        6 => new ConditionInfo(6, "Practice Poor", true),
        10 => new ConditionInfo(10, "Practice Perfect ○", false),
        _ => null,
    };
    public string? StoryTitle(int storyId) => storyId == 501009713 ? "Get Well Soon!" : null;
    public string? CharacterName(int charaId) => charaId switch
    {
        9008 => "Light Hello", 1084 => "Tanino Gimlet", 1002 => "Silence Suzuka", 9002 => "Director Akikawa", _ => null,
    };
    public EventEffectFormat? EventEffect(int displayId) => null;
}

internal static class Snapshots
{
    public static Character Daiwa(int energy = 80, Mood mood = Mood.Normal, Stats? stats = null, int fans = 800,
        IReadOnlyList<SkillHint>? hints = null, bool lightHelloOuting = false) => new(
        CardId: 100901, ScenarioId: 1, RouteId: 3, TalentLevel: 1,
        Stats: stats ?? new Stats(200, 100, 140, 140, 110),
        MaxStats: new Stats(1404, 1404, 1408, 1404, 1404),
        Energy: energy, MaxEnergy: 100, Mood: mood, SkillPoints: 200, Fans: fans,
        RunningStyle: 2,
        Aptitudes: new Aptitudes(7, 2, 3, 7, 7, 7, 7, 7, 3, 1),
        RouteRaceIds: [22, 23, 30, 31], ReservedRaceProgramId: 0, Conditions: [],
        Deck: [new DeckCard(1, 30145, 0), new DeckCard(2, 10002, 0)],
        Bonds:
        [
            new Bond(1, 20, 1084, false, 0), new Bond(2, 90, 1002, false, 0), new Bond(6, 44, 9008, lightHelloOuting, 0),
            new Bond(102, 16, 9002, false, 0),
        ],
        Skills: [new AcquiredSkill(10091, 2)],
        Hints: hints ?? []);

    public static Command Train(int id, int failure, int energyCost, params (string Param, int Value)[] gains) => new(
        CommandType.Training, id, true, failure,
        gains.Append(("energy", energyCost)).ToDictionary(g => g.Item1, g => new Gain(g.Item2, 0)), []);

    public static Command Simple(CommandType type, int id) => new(type, id, true, 0, new Dictionary<string, Gain>(), []);

    public static CareerSnapshot Snapshot(Character character, int turn = 14, IReadOnlyList<Command>? commands = null,
        StoryEvent? story = null, IReadOnlyList<EventChoice>? choices = null, int lastTraining = 101, int? choicesFor = null) => new(
        DateTimeOffset.Now, 1, new Screen("Gallop.SingleModeSceneController", "Gallop.SingleModeMainViewController", 1101),
        true, turn, 3, lastTraining, character, commands ?? StandardCommands(), story, choices,
        choicesFor ?? story?.EventId, null, null, []);

    public static IReadOnlyList<Command> StandardCommands(int failure = 0) =>
    [
        Train(TrainingIds.Speed, failure, -21, ("speed", 12), ("power", 6), ("skillPoints", 4)),
        Train(TrainingIds.Stamina, failure, -19, ("stamina", 10), ("guts", 7), ("skillPoints", 4)),
        Train(TrainingIds.Power, failure, -18, ("stamina", 7), ("power", 11), ("skillPoints", 5)),
        Train(TrainingIds.Guts, failure, -22, ("speed", 5), ("power", 5), ("guts", 10), ("skillPoints", 4)),
        Train(TrainingIds.Wit, 0, 5, ("speed", 2), ("wit", 10), ("skillPoints", 5)),
        Simple(CommandType.Recreation, 301),
        Simple(CommandType.Rest, 701),
    ];
}
