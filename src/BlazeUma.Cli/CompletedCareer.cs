using BlazeUma.Advisor;
using BlazeUma.Data;
using BlazeUma.Memory.Game;

namespace BlazeUma.Cli;

/// <summary>A career the auto bot finished (Complete Career → Finish), as read just before Finish.</summary>
internal sealed record CompletedCareer(string Name, int Turn, string Date, Stats Stats, int Fans, int Skills, int SkillPointsLeft,
    DateTime FinishedAt)
{
    public static CompletedCareer From(CareerSnapshot snapshot, IGameData data)
    {
        var c = snapshot.Character!;
        int turn = snapshot.Turn ?? 0;
        return new CompletedCareer(data.CharacterName(c.CardId / 100) ?? $"card {c.CardId}", turn, data.Turn(turn).ToString(),
            c.Stats, c.Fans, c.Skills.Count, c.SkillPoints, DateTime.Now);
    }

    /// <summary>Lines for the main menu.</summary>
    public string Describe()
    {
        var s = Stats;
        string ending = Turn >= CareerContext.FinalTurn ? $"finished the finale ({Date})" : $"ended early on turn {Turn} of {CareerContext.FinalTurn} ({Date})";
        return string.Join('\n',
            ConsoleUi.Bold(Name) + ConsoleUi.Dim($"  ·  {ending}  ·  completed {FinishedAt:HH:mm}"),
            $"Spd {s.Speed}  Sta {s.Stamina}  Pow {s.Power}  Gut {s.Guts}  Wit {s.Wit}" + ConsoleUi.Dim($"  (total {s.Speed + s.Stamina + s.Power + s.Guts + s.Wit})"),
            $"Fans {Fans:N0}  ·  {Skills} skills" + ConsoleUi.Dim($"  ·  {SkillPointsLeft} SP unused"));
    }
}
