using System.Text.Json;
using System.Text.Json.Serialization;
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
/// <param name="Kind">"turn" (advice at the start of a turn and what followed), "event" (an event shown), or
/// "event-result" (what that event actually did, for measuring outcome odds), or "race-result" (a race's placing).</param>
public sealed record LogRecord(
    string Kind, int Turn, DateTimeOffset At, string Summary,
    IReadOnlyList<ScoredOption> Options, CareerSnapshot Snapshot, TurnOutcome? Outcome, int? StoryId,
    EventResult? Result = null, RaceRecord? Race = null)
{
    /// <summary>How log lines are written and read back: nulls left out, enums by name.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>
/// What an event with choices did: the change from its choice screen to the next screen after it, and the
/// preview outcome that explains it best. <paramref name="Choice"/> and <paramref name="Outcome"/> are null when
/// two outcomes explain it equally well; <paramref name="MatchError"/> is how far off the best one is (0 = exact).
/// </summary>
public sealed record EventResult(int StoryId, int? Choice, int? Outcome, double MatchError, TurnOutcome Change);

/// <summary>
/// One run of a race, read from its results list: the trainee's 1-based <paramref name="Place"/>, and which run of
/// this race on this turn it was (<paramref name="Attempt"/> 2+ after Try Again). <paramref name="AlarmClocks"/> is
/// the count left as the results showed.
/// </summary>
public sealed record RaceRecord(int RaceInstanceId, int Seed, int Place, int Runners, int? FansGained, int Attempt, int? AlarmClocks);

/// <summary>
/// Turns a stream of snapshots into log records: one per completed turn (start-of-turn advice plus the
/// measured outcome) and one per event shown. Pure logic; the caller decides where records go.
/// </summary>
public sealed class TurnRecorder
{
    private CareerSnapshot? _start;
    private Advice? _startAdvice;
    private int? _lastEventStory;
    private CareerSnapshot? _pendingEvent;
    private (int Turn, int RaceInstanceId, int Seed, int Attempt)? _lastRace;

    /// <summary>
    /// The change across an event, and the preview outcome nearest to it: stat, energy, mood and skill-point
    /// differences (energy and mood clamped to their range) plus 5 per condition that does not match.
    /// Random-stat effects (display 14 / 34) cannot be placed and are left out.
    /// </summary>
    public static EventResult Resolve(CareerSnapshot atEvent, CareerSnapshot after)
    {
        var change = Compare(atEvent, after);
        var c = atEvent.Character!;
        int lastStat = atEvent.LastTrainingId is int id ? TurnAdvisor.TrainingStat(id) : 0;
        var actual = new Dictionary<int, int>
        {
            [ParamIds.Speed] = change.StatChanges.GetValueOrDefault("speed"), [ParamIds.Stamina] = change.StatChanges.GetValueOrDefault("stamina"),
            [ParamIds.Power] = change.StatChanges.GetValueOrDefault("power"), [ParamIds.Guts] = change.StatChanges.GetValueOrDefault("guts"),
            [ParamIds.Wit] = change.StatChanges.GetValueOrDefault("wit"), [ParamIds.Energy] = change.EnergyAfter - change.EnergyBefore,
            [ParamIds.Mood] = change.MoodAfter - change.MoodBefore, [ParamIds.SkillPoints] = change.SkillPointChange,
        };

        double Error(IReadOnlyList<EventEffect> outcome)
        {
            var predicted = actual.Keys.ToDictionary(k => k, _ => 0);
            var gained = new HashSet<int>();
            var cured = new HashSet<int>();
            foreach (var e in outcome)
            {
                void Add(int param, int amount) { if (predicted.ContainsKey(param)) predicted[param] += amount; }
                switch (e.DisplayId)
                {
                    case 1 or 39: Add(e.Value0, e.Value1); break;
                    case 2 or 38: Add(e.Value0, -e.Value1); break;
                    case 13: foreach (int s in new[] { ParamIds.Speed, ParamIds.Stamina, ParamIds.Power, ParamIds.Guts, ParamIds.Wit }) Add(s, e.Value0); break;
                    case 19: Add(lastStat, e.Value0); break;
                    case 20: Add(lastStat, -e.Value0); break;
                    case 9 or 37 or 40: gained.Add(e.Value0); break;
                    case 10: cured.Add(e.Value0); break;
                }
            }
            predicted[ParamIds.Energy] = Math.Clamp(predicted[ParamIds.Energy], -c.Energy, c.MaxEnergy - c.Energy);
            predicted[ParamIds.Mood] = Math.Clamp(predicted[ParamIds.Mood], 1 - (int)c.Mood, 5 - (int)c.Mood);
            double error = actual.Sum(a => Math.Abs(a.Value - predicted[a.Key]));
            error += 5 * gained.Except(change.ConditionsAdded).Count() + 5 * change.ConditionsAdded.Except(gained).Count();
            error += 5 * cured.Except(change.ConditionsRemoved).Count();
            return error;
        }

        var ranked = (atEvent.EventChoices ?? [])
            .SelectMany(choice => choice.Outcomes.Select((o, i) => (choice.Choice, Outcome: i + 1, Error: Error(o))))
            .OrderBy(x => x.Error)
            .ToList();
        if (ranked.Count == 0) return new EventResult(atEvent.Story!.StoryId, null, null, double.NaN, change);
        var best = ranked[0];
        bool clear = ranked.Count == 1 || ranked[1].Error > best.Error;
        return new EventResult(atEvent.Story!.StoryId, clear ? best.Choice : null, clear ? best.Outcome : null, best.Error, change);
    }

    /// <summary>Raised state: the snapshot belongs to a different career than the previous ones.</summary>
    public bool NewCareer { get; private set; }

    public IReadOnlyList<LogRecord> Observe(CareerSnapshot snapshot, Advice advice)
    {
        NewCareer = false;
        var records = new List<LogRecord>();
        if (snapshot.Turn is not int turn || snapshot.Character is null) return records;
        records.AddRange(ObserveRace(snapshot));

        bool newEvent = advice.Situation == Situation.Event && snapshot.Story is { } shown && shown.StoryId != _lastEventStory;
        // An event with choices is resolved by the next screen that is not that event (home, or the next event).
        if (_pendingEvent is { Story: { } pending } && (advice.Situation != Situation.Event || newEvent))
        {
            records.Add(new LogRecord("event-result", turn, snapshot.ReadAt, "", [], snapshot, null, pending.StoryId,
                Resolve(_pendingEvent, snapshot)));
            _pendingEvent = null;
        }
        if (newEvent)
        {
            var story = snapshot.Story!;
            _lastEventStory = story.StoryId;
            records.Add(new LogRecord("event", turn, snapshot.ReadAt, advice.Summary, advice.Options, snapshot, null, story.StoryId));
            if (snapshot.EventChoices is { Count: > 0 }) _pendingEvent = snapshot;
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

    /// <summary>
    /// A "race-result" record the first time a results list shows a race run (told apart by its seed); none
    /// otherwise. The auto bot calls this on its own from the race screens, where it takes no advice.
    /// </summary>
    public IReadOnlyList<LogRecord> ObserveRace(CareerSnapshot snapshot)
    {
        if (snapshot is not { Turn: int turn, Character: not null, RaceResult: { } result } || _lastRace?.Seed == result.Seed) return [];
        int attempt = _lastRace is { } last && last.Turn == turn && last.RaceInstanceId == result.RaceInstanceId ? last.Attempt + 1 : 1;
        _lastRace = (turn, result.RaceInstanceId, result.Seed, attempt);
        var race = new RaceRecord(result.RaceInstanceId, result.Seed, result.Place, result.Runners, result.FansGained, attempt, snapshot.AlarmClocks);
        return [new LogRecord("race-result", turn, snapshot.ReadAt, $"{RaceSummary.Ordinal(result.Place)} of {result.Runners}", [], snapshot,
            null, null, Race: race)];
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
