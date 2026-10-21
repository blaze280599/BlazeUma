namespace BlazeUma.Memory.Game;

public enum Mood { Awful = 1, Bad = 2, Normal = 3, Good = 4, Great = 5 }

/// <summary>TurnInfo._commandType values, confirmed against the URA home screen.</summary>
public enum CommandType { Training = 1, Recreation = 3, Race = 4, Rest = 7, Infirmary = 8 }

/// <summary>Parameter ids used by training gains and event previews.</summary>
public static class ParamIds
{
    public const int Speed = 1, Stamina = 2, Power = 3, Guts = 4, Wit = 5, Energy = 10, Mood = 20, SkillPoints = 30;

    public static string Name(int id) => id switch
    {
        Speed => "speed", Stamina => "stamina", Power => "power", Guts => "guts", Wit => "wit",
        Energy => "energy", Mood => "mood", SkillPoints => "skillPoints", _ => $"param{id}",
    };
}

/// <summary>Training command ids (URA).</summary>
public static class TrainingIds
{
    public const int Speed = 101, Power = 102, Guts = 103, Stamina = 105, Wit = 106;
}

public sealed record Stats(int Speed, int Stamina, int Power, int Guts, int Wit);

/// <summary>Aptitude grades as stored: 1 = G … 7 = A, 8 = S.</summary>
public sealed record Aptitudes(
    int Turf, int Dirt,
    int Sprint, int Mile, int Medium, int Long,
    int FrontRunner, int PaceChaser, int LateSurger, int EndCloser);

public sealed record Gain(int Value, int Bonus);

public sealed record Command(
    CommandType Type, int Id, bool Enabled, int FailureRate,
    IReadOnlyDictionary<string, Gain> Gains,
    IReadOnlyList<TrainingPartner> Partners);

public sealed record TrainingPartner(int Position, bool HasHint);

public sealed record DeckCard(int Position, int SupportCardId, int LimitBreak);

/// <summary>
/// Bond gauge; TargetId 1–6 is a deck position, larger ids are scenario NPCs. OutingUnlocked marks a friend
/// card whose outing appears in the Recreation dialog; OutingStep is its chain progress (0–5 chevrons).
/// </summary>
public sealed record Bond(int TargetId, int Value, int CharaId, bool OutingUnlocked, int OutingStep);

public sealed record AcquiredSkill(int SkillId, int Level);

/// <summary>A skill hint: resolve with skill_data(group_id, rarity).</summary>
public sealed record SkillHint(int GroupId, int Rarity, int Level);

public sealed record Character(
    int CardId, int ScenarioId, int RouteId, int TalentLevel,
    Stats Stats, Stats MaxStats,
    int Energy, int MaxEnergy, Mood Mood, int SkillPoints, int Fans,
    int RunningStyle, Aptitudes Aptitudes,
    IReadOnlyList<int> RouteRaceIds, int ReservedRaceProgramId,
    IReadOnlyList<int> Conditions,
    IReadOnlyList<DeckCard> Deck, IReadOnlyList<Bond> Bonds,
    IReadOnlyList<AcquiredSkill> Skills, IReadOnlyList<SkillHint> Hints);

public sealed record EventEffect(int DisplayId, int Value0, int Value1, int Value2);

public sealed record EventChoice(int Choice, IReadOnlyList<IReadOnlyList<EventEffect>> Outcomes);

/// <summary>
/// Last story the StoryManager loaded. StoryId persists after the event closes; <see cref="LooksActive"/> is a
/// two-sample heuristic (StoryViewController save/select flags), not yet a proven indicator.
/// </summary>
public sealed record StoryEvent(int StoryId, int StoryDataId, int CharaId, int SupportCharaId, int EventId, bool LooksActive);

public sealed record Screen(string? SceneController, string? ViewController, int NextViewId);

public sealed record ShopSkill(int SkillId, int Price, int BasePrice, int DiscountPercent, int HintLevel,
    bool Obtained, bool Locked, bool Selected);

public sealed record RaceEntry(int ProgramId, int RaceInstanceId, int NeedFans, int EntryState, bool IsGoal, bool IsReserved)
{
    public bool CanEnter => EntryState == 0;
}

public sealed record RaceList(int DisplayTurn, IReadOnlyList<RaceEntry> Races);

public sealed record SkillShop(int RemainingPoints, IReadOnlyList<ShopSkill> Skills);

public sealed record CareerSnapshot(
    DateTimeOffset ReadAt,
    int ProcessId,
    Screen? Screen,
    bool CareerActive,
    int? Turn,
    int? AlarmClocks,
    int? LastTrainingId,
    Character? Character,
    IReadOnlyList<Command>? Commands,
    StoryEvent? Story,
    IReadOnlyList<EventChoice>? EventChoices,
    SkillShop? SkillShop,
    RaceList? RaceList,
    IReadOnlyList<string> Errors);
