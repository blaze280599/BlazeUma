using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor;

/// <summary>What changed between the start of one turn and the start of the next.</summary>
public sealed record TurnOutcome(
    int Turn, int NextTurn, string InferredAction,
    IReadOnlyDictionary<string, int> StatChanges,
    int EnergyBefore, int EnergyAfter, int MaxEnergyChange,
    Mood MoodBefore, Mood MoodAfter,
    int SkillPointChange, int FanChange,
    IReadOnlyList<string> BondChanges,
    IReadOnlyList<int> ConditionsAdded, IReadOnlyList<int> ConditionsRemoved,
    IReadOnlyList<int> SkillsLearned);

/// <summary>One line of the career log.</summary>
/// <param name="Kind">"turn" (advice at the start of a turn and what followed) or "event".</param>
public sealed record LogRecord(
    string Kind, int Turn, DateTimeOffset At, string Summary,
    IReadOnlyList<ScoredOption> Options, CareerSnapshot Snapshot, TurnOutcome? Outcome, int? StoryId);

/// <summary>
/// Turns a stream of snapshots into log records: one per completed turn (start-of-turn advice plus the
/// measured outcome) and one per event shown. Pure logic; the caller decides where records go.
/// </summary>
public sealed class TurnRecorder
{
    private CareerSnapshot? _start;
    private Advice? _startAdvice;
    private int? _lastEventStory;

    /// <summary>Raised state: the snapshot belongs to a different career than the previous ones.</summary>
    public bool NewCareer { get; private set; }

    public IReadOnlyList<LogRecord> Observe(CareerSnapshot snapshot, Advice advice)
    {
        NewCareer = false;
        var records = new List<LogRecord>();
        if (snapshot.Turn is not int turn || snapshot.Character is null) return records;

        if (advice.Situation == Situation.Event && snapshot.Story is { } story && story.StoryId != _lastEventStory)
        {
            _lastEventStory = story.StoryId;
            records.Add(new LogRecord("event", turn, snapshot.ReadAt, advice.Summary, advice.Options, snapshot, null, story.StoryId));
        }

        // A turn starts on the home screen (or race day); events and sub-screens in between are not starts.
        if (advice.Situation is not (Situation.TrainingTurn or Situation.GoalRaceDay)) return records;
        if (_start is { Turn: int startTurn, Character: { } before })
        {
            if (turn == startTurn) return records;
            if (turn < startTurn || before.CardId != snapshot.Character.CardId)
            {
                NewCareer = true;
            }
            else
            {
                var outcome = Compare(_start, snapshot);
                records.Add(new LogRecord("turn", startTurn, _start.ReadAt, _startAdvice!.Summary, _startAdvice.Options, _start, outcome, null));
            }
        }
        _start = snapshot;
        _startAdvice = advice;
        return records;
    }

    public static TurnOutcome Compare(CareerSnapshot from, CareerSnapshot to)
    {
        var a = from.Character!;
        var b = to.Character!;
        var stats = new Dictionary<string, int>();
        void Stat(string name, int x, int y) { if (y != x) stats[name] = y - x; }
        Stat("speed", a.Stats.Speed, b.Stats.Speed);
        Stat("stamina", a.Stats.Stamina, b.Stats.Stamina);
        Stat("power", a.Stats.Power, b.Stats.Power);
        Stat("guts", a.Stats.Guts, b.Stats.Guts);
        Stat("wit", a.Stats.Wit, b.Stats.Wit);

        var bonds = new List<string>();
        foreach (var after in b.Bonds)
        {
            var prior = a.Bonds.FirstOrDefault(x => x.TargetId == after.TargetId);
            int change = after.Value - (prior?.Value ?? 0);
            if (change != 0) bonds.Add($"{after.CharaId}:{change:+0;-0}");
            if (prior is not null && after.OutingStep != prior.OutingStep) bonds.Add($"{after.CharaId}:outing {prior.OutingStep}->{after.OutingStep}");
        }
        var skillsBefore = a.Skills.Select(s => s.SkillId).ToHashSet();

        return new TurnOutcome(
            from.Turn!.Value, to.Turn!.Value, InferAction(from, to),
            stats, a.Energy, b.Energy, b.MaxEnergy - a.MaxEnergy, a.Mood, b.Mood,
            b.SkillPoints - a.SkillPoints, b.Fans - a.Fans, bonds,
            b.Conditions.Except(a.Conditions).ToList(), a.Conditions.Except(b.Conditions).ToList(),
            b.Skills.Select(s => s.SkillId).Where(id => !skillsBefore.Contains(id)).ToList());
    }

    /// <summary>
    /// Best guess at the action taken, from its fingerprints: fans → race; friend outing step → outing;
    /// selected training changed or stats up → training; cured condition → infirmary;
    /// mood up → recreation; energy up → rest. Events in between can blur this, so it is labelled a guess.
    /// </summary>
    public static string InferAction(CareerSnapshot from, CareerSnapshot to)
    {
        var a = from.Character!;
        var b = to.Character!;
        if (b.Fans > a.Fans) return "race";
        var outing = b.Bonds.FirstOrDefault(x => a.Bonds.FirstOrDefault(y => y.TargetId == x.TargetId) is { } y && x.OutingStep > y.OutingStep);
        if (outing is not null) return $"outing:{outing.CharaId}";
        int statGain = b.Stats.Speed + b.Stats.Stamina + b.Stats.Power + b.Stats.Guts + b.Stats.Wit
                     - (a.Stats.Speed + a.Stats.Stamina + a.Stats.Power + a.Stats.Guts + a.Stats.Wit);
        // Wit training restores energy, so a stat gain alone (not energy spent) marks a repeated training.
        if (to.LastTrainingId != from.LastTrainingId || statGain >= 5)
            return $"training:{to.LastTrainingId}";
        if (a.Conditions.Except(b.Conditions).Any()) return "infirmary";
        if (b.Mood > a.Mood) return "recreation";
        if (b.Energy - a.Energy >= 20) return "rest";
        return "unknown";
    }
}
