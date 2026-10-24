using BlazeUma.Data;
using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor;

/// <summary>
/// One race of a career as its log saw it: the last run's placing, and how many runs it took (Try Again after a lost
/// goal race runs it again on the same turn). <paramref name="AlarmClocks"/> is the count left after the last run.
/// </summary>
public sealed record LoggedRace(int Turn, int RaceInstanceId, int Place, int Runners, int? FansGained, int Runs, int? AlarmClocks);

/// <summary>A career's races from its log's "race-result" records, for judging how the career went.</summary>
public sealed record RaceSummary(IReadOnlyList<LoggedRace> Races, Character? Character)
{
    public static readonly RaceSummary Empty = new([], null);

    public int Wins => Races.Count(r => r.Place == 1);
    public int Retries => Races.Sum(r => r.Runs - 1);

    /// <summary>
    /// Groups the records by turn and race; a run logged twice (a resumed run seeing the same results list) counts
    /// once, by its seed.
    /// </summary>
    public static RaceSummary From(IEnumerable<LogRecord> records)
    {
        var results = records.Where(r => r is { Kind: "race-result", Race: not null }).ToList();
        var races = results
            .GroupBy(r => (r.Turn, r.Race!.RaceInstanceId))
            .Select(g =>
            {
                var runs = g.DistinctBy(r => r.Race!.Seed).OrderBy(r => r.At).ToList();
                var last = runs[^1].Race!;
                return new LoggedRace(g.Key.Turn, last.RaceInstanceId, last.Place, last.Runners, last.FansGained, runs.Count, last.AlarmClocks);
            })
            .OrderBy(r => r.Turn)
            .ToList();
        return new RaceSummary(races, results.LastOrDefault()?.Snapshot.Character);
    }

    /// <summary>"7 wins from 9 races, 2 retries", and the goal races missed when <paramref name="data"/> is given.</summary>
    public string Headline(IGameData? data = null) =>
        Races.Count == 0 ? "no race results logged"
            : $"{Wins} {(Wins == 1 ? "win" : "wins")} from {Races.Count} {(Races.Count == 1 ? "race" : "races")}, {Retries} {(Retries == 1 ? "retry" : "retries")}"
              + (GoalsMissed(data) is int missed and > 0 ? $", {missed} goal {(missed == 1 ? "race" : "races")} missed" : "");

    /// <summary>One line per race: date, name, placing, runs and the alarm clocks left; a goal race shows its required place.</summary>
    public IReadOnlyList<string> Lines(IGameData data)
    {
        var goals = Goals(data);
        return Races.Select(r =>
        {
            var goal = goals.FirstOrDefault(g => g.Turn == r.Turn && g.Race?.RaceInstanceId == r.RaceInstanceId);
            var program = goal?.Race ?? data.ProgramsForTurn(r.Turn).FirstOrDefault(p => p.RaceInstanceId == r.RaceInstanceId);
            string name = program is null ? $"race {r.RaceInstanceId}" : $"{program.Name} ({program.GradeName})";
            string goalNote = goal is null ? "" : r.Place <= goal.RequiredPlace ? "  goal met" : $"  goal MISSED (needed top {goal.RequiredPlace})";
            string runs = r.Runs > 1 ? $"  {r.Runs} runs" : "";
            string clocks = r.AlarmClocks is int c ? $"  clocks left {c}" : "";
            return $"{data.Turn(r.Turn),-20} {Ordinal(r.Place),4}/{r.Runners,-2}  {name}{goalNote}{runs}{clocks}";
        }).ToList();
    }

    /// <summary>Goal races whose last run placed below the goal's required place; null when the goals are unknown.</summary>
    public int? GoalsMissed(IGameData? data)
    {
        if (data is null || Character is null) return null;
        var goals = Goals(data);
        return Races.Count(r => goals.FirstOrDefault(g => g.Turn == r.Turn && g.Race?.RaceInstanceId == r.RaceInstanceId) is { } g && r.Place > g.RequiredPlace);
    }

    private IReadOnlyList<GoalRace> Goals(IGameData data) =>
        Character is { RouteRaceIds: { } ids } ? data.Goals(ids) : [];

    public static string Ordinal(int n) =>
        n + ((n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
}
