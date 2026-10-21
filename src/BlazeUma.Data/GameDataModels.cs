namespace BlazeUma.Data;

public enum Ground { Turf = 1, Dirt = 2 }

/// <summary>Distance categories as the game uses them (also skill_data distance_type values).</summary>
public enum DistanceCategory { Sprint = 1, Mile = 2, Medium = 3, Long = 4 }

/// <summary>race.grade codes.</summary>
public enum RaceGrade { G1 = 100, G2 = 200, G3 = 300, OP = 400, PreOP = 700, Maiden = 800, Debut = 900 }

/// <summary>Running styles as stored on the character and in skill conditions (running_style).</summary>
public enum RunningStyle { FrontRunner = 1, PaceChaser = 2, LateSurger = 3, EndCloser = 4 }

public static class Distances
{
    public static DistanceCategory Categorize(int meters) => meters switch
    {
        <= 1400 => DistanceCategory.Sprint,
        <= 1800 => DistanceCategory.Mile,
        <= 2400 => DistanceCategory.Medium,
        _ => DistanceCategory.Long,
    };
}

/// <summary>Career calendar position: Year 1 = Junior, 2 = Classic, 3 = Senior; Half 1 = early, 2 = late.</summary>
public sealed record TurnDate(int Turn, int Year, int Month, int Half)
{
    public override string ToString() =>
        $"{(Year switch { 1 => "Junior", 2 => "Classic", 3 => "Senior", _ => "Finale" })} {(Half == 1 ? "Early" : "Late")} {new DateTime(2000, Math.Clamp(Month, 1, 12), 1):MMM}";
}

/// <summary>A single_mode_program row joined with its race and course.</summary>
public sealed record RaceProgram(
    int ProgramId, int RaceInstanceId, string Name, int Grade, int Distance, Ground Ground,
    int Month, int Half, int Permission, int NeedFans, int FirstPlaceFans)
{
    public DistanceCategory Category => Distances.Categorize(Distance);

    /// <summary>
    /// race_permission: 1 junior, 2 classic, 3 classic + senior, 4 senior, 5 scenario finale period
    /// (year 4; every permission-5 program is a URA Finale race, held in "January").
    /// </summary>
    public bool OpenInYear(int year) => Permission switch
    {
        1 => year == 1,
        2 => year == 2,
        3 => year is 2 or 3,
        4 => year == 3,
        5 => year == 4,
        _ => false,
    };

    public string GradeName => Enum.IsDefined(typeof(RaceGrade), Grade) ? ((RaceGrade)Grade).ToString() : $"grade{Grade}";
}

/// <summary>A career goal from single_mode_route_race. Race is null for scenario finals with no fixed program.</summary>
public sealed record GoalRace(int RouteRaceId, int SortId, int Turn, int RequiredPlace, string Name, RaceProgram? Race)
{
    public bool MustWin => RequiredPlace == 1;
}

public sealed record SkillInfo(
    int Id, string Name, int Rarity, int GroupId, int GradeValue, int BaseCost,
    string Condition, int AbilityType)
{
    public bool IsGold => Rarity >= 2 && Rarity < 3;
}

/// <summary>support_card_type: 1 = stat card, 2 = friend, 3 = group. TrainingCommandId is the card's specialty (101 …).</summary>
public sealed record SupportCardInfo(int Id, string Name, int CharaId, int Type, int TrainingCommandId);

/// <summary>single_mode_chara_effect: good (effect_type 1) or bad (effect_type 2) condition.</summary>
public sealed record ConditionInfo(int Id, string Name, bool IsBad);

/// <summary>Event choice preview format (text category 394) and the meaning of each value slot.</summary>
public sealed record EventEffectFormat(int DisplayId, string Template, int ValueType0, int ValueType1, int ValueType2);

public interface IGameData
{
    TurnDate Turn(int turn);
    IReadOnlyList<GoalRace> Goals(IReadOnlyList<int> routeRaceIds);
    RaceProgram? Program(int programId);
    /// <summary>Regular (non-goal) programs held on a turn and open for that career year.</summary>
    IReadOnlyList<RaceProgram> ProgramsForTurn(int turn);
    SkillInfo? Skill(int skillId);
    SkillInfo? SkillForHint(int groupId, int rarity);
    /// <summary>Skills the trainee can learn without hints (available_skill_set, gated by awakening level).</summary>
    IReadOnlyList<int> BaseSkillIds(int cardId, int talentLevel);
    SupportCardInfo? SupportCard(int supportCardId);
    ConditionInfo? Condition(int conditionId);
    string? StoryTitle(int storyId);
    string? CharacterName(int charaId);
    EventEffectFormat? EventEffect(int displayId);
}
