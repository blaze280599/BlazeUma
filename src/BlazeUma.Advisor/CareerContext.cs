using BlazeUma.Data;
using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor;

/// <summary>A snapshot joined with the static game data the engine needs to reason about it.</summary>
public sealed class CareerContext
{
    public const int FinalTurn = 78;

    public CareerSnapshot Snapshot { get; }
    public Character Character { get; }
    public IGameData Data { get; }
    public int Turn { get; }
    public TurnDate Date { get; }

    /// <summary>One goal per goal slot; where the route offers alternatives (Oaks / Derby) the first is used.</summary>
    public IReadOnlyList<GoalRace> Goals { get; }
    public IReadOnlyList<GoalRace> RemainingGoals { get; }
    public GoalRace? NextGoal => RemainingGoals.FirstOrDefault();
    public GoalRace? GoalToday => NextGoal is { } g && g.Turn == Turn ? g : null;
    public int TurnsLeft => Math.Max(0, FinalTurn - Turn);
    public RunningStyle Style => Enum.IsDefined(typeof(RunningStyle), Character.RunningStyle) ? (RunningStyle)Character.RunningStyle : RunningStyle.PaceChaser;

    /// <summary>Share of remaining goal races per distance category (the scenario finals use the trainee's best goal distance).</summary>
    public IReadOnlyDictionary<DistanceCategory, double> DistanceProfile { get; }

    /// <summary>Longest goal distance still ahead, in meters.</summary>
    public int LongestRemainingDistance { get; }

    public CareerContext(CareerSnapshot snapshot, IGameData data)
    {
        Snapshot = snapshot;
        Character = snapshot.Character ?? throw new ArgumentException("Snapshot has no character.", nameof(snapshot));
        Turn = snapshot.Turn ?? throw new ArgumentException("Snapshot has no turn.", nameof(snapshot));
        Data = data;
        Date = data.Turn(Turn);
        Goals = data.Goals(Character.RouteRaceIds).GroupBy(g => g.SortId).Select(g => g.First()).OrderBy(g => g.Turn).ToList();
        RemainingGoals = Goals.Where(g => g.Turn >= Turn).ToList();

        var withRace = RemainingGoals.Where(g => g.Race is not null).ToList();
        var counts = withRace.GroupBy(g => g.Race!.Category).ToDictionary(g => g.Key, g => (double)g.Count());
        int finals = RemainingGoals.Count - withRace.Count;
        if (finals > 0)
        {
            var best = counts.Count > 0 ? counts.MaxBy(c => c.Value).Key : DistanceCategory.Medium;
            counts[best] = counts.GetValueOrDefault(best) + finals;
        }
        double total = counts.Values.Sum();
        DistanceProfile = total > 0 ? counts.ToDictionary(c => c.Key, c => c.Value / total) : new() { [DistanceCategory.Medium] = 1 };
        LongestRemainingDistance = withRace.Count > 0 ? withRace.Max(g => g.Race!.Distance) : 2000;
    }

    /// <summary>First remaining goal whose fan requirement is not met yet, with the shortfall.</summary>
    public (GoalRace Goal, int Shortfall)? FanShortfall()
    {
        foreach (var goal in RemainingGoals)
        {
            int need = goal.Race?.NeedFans ?? 0;
            if (need > Character.Fans) return (goal, need - Character.Fans);
        }
        return null;
    }

    public int Aptitude(DistanceCategory category) => category switch
    {
        DistanceCategory.Sprint => Character.Aptitudes.Sprint,
        DistanceCategory.Mile => Character.Aptitudes.Mile,
        DistanceCategory.Medium => Character.Aptitudes.Medium,
        _ => Character.Aptitudes.Long,
    };

    public int Aptitude(Ground ground) => ground == Ground.Turf ? Character.Aptitudes.Turf : Character.Aptitudes.Dirt;

    public static string Grade(int aptitude) => aptitude switch
    {
        8 => "S", 7 => "A", 6 => "B", 5 => "C", 4 => "D", 3 => "E", 2 => "F", _ => "G",
    };
}
