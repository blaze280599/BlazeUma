using BlazeUma.Data;
using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor;

/// <summary>
/// Entry point: picks the right advisor for the screen the game is showing. <paramref name="odds"/>: event odds
/// learned from career logs (<see cref="CareerLogs.LearnedOdds"/>); the shipped file when null.
/// </summary>
public sealed class CareerAdvisor(IGameData data, EventOdds? odds = null)
{
    private const string SkillScreen = "Gallop.SingleModeSkillLearningViewController";
    private const string RaceScreen = "Gallop.SingleModeRaceEntryViewController";
    private const string CompleteScreen = "Gallop.SingleModeConfirmCompleteViewController";

    /// <summary>
    /// The turn the career's last screen was seen on. The turn stays put after it, so the skill screen opened
    /// from there (same turn) is known to be the career's last chance to spend skill points.
    /// </summary>
    private int? _endTurn;

    public Advice Advise(CareerSnapshot snapshot)
    {
        var warnings = snapshot.Errors.Select(e => new Note("Read error", e)).ToList();
        if (!snapshot.CareerActive || snapshot.Character is null || snapshot.Turn is null)
            return new Advice(Situation.NoCareer, "Umamusume is open, not in a career",
                "Start or resume a career to get advice", [], warnings, []);

        if (snapshot.Screen?.ViewController == CompleteScreen) _endTurn = snapshot.Turn;
        var ctx = new CareerContext(snapshot, data, careerOver: _endTurn == snapshot.Turn);
        var values = new ValueModel(ctx);
        var c = ctx.Character;
        string header = $"Turn {ctx.Turn} · {ctx.Date} · Mood {c.Mood} · Energy {c.Energy}/{c.MaxEnergy} · SP {c.SkillPoints} · Fans {c.Fans}";
        var t = values.Targets.Target;
        var w = values.Targets.Weights;
        var notes = new List<Note>(warnings)
        {
            new("Targets", $"Spd {t.Speed}  Sta {t.Stamina}  Pow {t.Power}  Gut {t.Guts}  Wit {t.Wit}"),
        };
        if (values.Targets.StaminaNote is { } staminaNote) notes.Add(new Note("Recovery", staminaNote));
        if (values.EnergyOutlook.Reason is { } energyNote) notes.Add(new Note("Energy", energyNote));
        if (!ctx.CareerOver && ctx.NextGoal is { } next)
            notes.Add(new Note("Next goal", $"{next.Name} on turn {next.Turn}" +
                (next.Turn > ctx.Turn ? $" (in {next.Turn - ctx.Turn})" : " (today)") +
                (next.RequiredPlace > 0 ? $", need top {next.RequiredPlace}" : "")));
        if (!ctx.CareerOver && ctx.FanShortfall() is var (fanGoal, shortfall))
        {
            int fromGoals = ctx.ExpectedGoalFansBefore(fanGoal.Turn);
            notes.Add(fromGoals >= shortfall
                ? new Note("Fans", $"{shortfall} more needed for {fanGoal.Name} (turn {fanGoal.Turn}); earlier goal races should cover it (~+{fromGoals})")
                : new Note(fanGoal.Turn - ctx.Turn <= 4 ? "Warning" : "Fans",
                    fanGoal.Kind == GoalKind.Fans
                        ? $"{shortfall} more fans needed by turn {fanGoal.Turn} ({fanGoal.Name}); missing it fails the goal"
                        : $"{shortfall} more fans needed to enter {fanGoal.Name} (turn {fanGoal.Turn}); missing it fails the goal"));
        }
        var details = new List<string>
        {
            $"stat weights Spd {w[ParamIds.Speed]:0.00}  Sta {w[ParamIds.Stamina]:0.00}  Pow {w[ParamIds.Power]:0.00}  Gut {w[ParamIds.Guts]:0.00}  Wit {w[ParamIds.Wit]:0.00}",
        };

        // An event plays over whatever screen is open (a card event popped up over the race list on turn 23), so
        // it comes before the screen checks.
        if (snapshot.Story is { LooksActive: true } story)
        {
            string title = data.StoryTitle(story.StoryId) ?? $"story {story.StoryId}";
            if (snapshot.EventChoices is not { Count: > 0 } choices)
                return new Advice(Situation.Event, header, $"Event \"{title}\": no choice to make, continue", [], notes, details);
            // The previews should belong to this event; seen to match once so far, so only warn on a mismatch.
            if (snapshot.EventChoicesEventId is int choicesFor && choicesFor != story.EventId)
                notes.Insert(0, new Note("Warning", $"choices were built for event {choicesFor}, but event {story.EventId} is showing; they may be stale"));
            var ranked = new EventAdvisor(ctx, values, odds).Rank(choices, story);
            return new Advice(Situation.Event, header, $"Event \"{title}\": {ranked[0].Label}", ranked, notes, details);
        }
        if (snapshot.Screen?.ViewController == SkillScreen && snapshot.SkillShop is { } shop)
            return SkillShopAdvice(ctx, header, shop, notes, details);
        if (snapshot.Screen?.ViewController == RaceScreen && snapshot.RaceList is { } races)
            return RaceListAdvice(ctx, values, header, races, notes, details);

        var skills = new SkillAdvisor(ctx);
        var (buy, why) = skills.Plan(skills.Predicted(), c.SkillPoints);
        notes.Add(buy.Count > 0
            ? new Note("Skills", $"buy {string.Join(", ", buy.Select(b => $"{b.Skill.Name} ({b.Price})"))} {(ctx.CareerOver ? "before completing the career" : "before racing")} ({why})")
            : new Note("Skills", why));
        var toBuy = buy.Select(b => b.Skill.Id).ToList();
        // Skills are bought before the turn's action (the auto bot does the same), so lead with them.
        string First(string then) => buy.Count > 0 ? $"Buy {string.Join(", ", buy.Select(b => b.Skill.Name))} first, then {char.ToLowerInvariant(then[0])}{then[1..]}" : then;

        if (snapshot.Screen?.ViewController == CompleteScreen)
            return new Advice(Situation.CareerEnd, header, First("Complete the career"), [], notes, details, toBuy);

        if (ctx.GoalToday is { } goal)
            return new Advice(Situation.GoalRaceDay, header,
                First($"Race day: run {goal.Name}{(goal.RequiredPlace > 0 ? $" (need top {goal.RequiredPlace})" : "")}"), [], notes, details, toBuy);

        var options = new TurnAdvisor(ctx, values).Rank();
        return new Advice(Situation.TrainingTurn, header, First(options.Count > 0 ? options[0].Label : "No enabled actions found"), options, notes, details, toBuy);
    }

    private static Advice SkillShopAdvice(CareerContext ctx, string header, SkillShop shop, List<Note> notes, List<string> details)
    {
        var advisor = new SkillAdvisor(ctx);
        var candidates = advisor.FromShop(shop);
        // RemainingPoint already excludes skills selected with + but not learned yet; count them back in, so a
        // half-finished purchase (turn 53: 542 SP showing 58) is still planned rather than dropped.
        int points = shop.RemainingPoints + shop.Skills.Where(s => s.Selected && !s.Obtained).Sum(s => s.Price);
        var (buy, why) = advisor.Plan(candidates, points);
        // A ◎ still needing its ○ takes two presses of the row's +; say so, with what each press costs.
        string Price(SkillAdvisor.Candidate c) => c.Steps.Count == 1 ? $"{c.Price} SP"
            : $"{string.Join(" + ", c.Steps.Select(id => shop.Skills.First(s => s.SkillId == id).Price))} = {c.Price} SP";
        string Presses(SkillAdvisor.Candidate c) => c.Steps.Count switch { 1 => "press + once", 2 => "press + twice", var n => $"press + {n} times" };
        string Status(SkillAdvisor.Candidate c) =>
            buy.Contains(c) ? $"BUY: {Presses(c)}"
            : buy.FirstOrDefault(b => b.Row == c.Row && b.Steps.Contains(c.Skill.Id)) is { } upgrade ? $"bought as part of {upgrade.Skill.Name}"
            : "skip for now";
        var options = candidates.OrderByDescending(c => c.PerPoint)
            .Select(c => new ScoredOption($"{c.Skill.Name} ({Price(c)})", c.PerPoint * 100,
                [Status(c), $"fit {c.Fit:P0}", $"rating {c.Skill.GradeValue}"]))
            .ToList();
        string summary = buy.Count > 0
            ? $"Buy {string.Join(", ", buy.Select(b => b.Steps.Count > 1 ? $"{b.Skill.Name} ({Presses(b)})" : b.Skill.Name))}"
            : "Buy nothing yet";
        // One line per skill to buy, ticked once all its presses are selected, so the whole plan shows at a glance.
        var steps = new List<Note>();
        foreach (var b in buy)
        {
            int left = b.Steps.Count(id => shop.Skills.First(s => s.SkillId == id) is { Selected: false });
            string step = left == 0 ? $"✓ {b.Skill.Name} (selected)"
                : $"{b.Skill.Name}: {(left == b.Steps.Count ? Presses(b) : $"press + {(left == 1 ? "once" : $"{left} times")} more")}";
            steps.Add(new Note(steps.Count == 0 ? "Press +" : "", step));
        }
        if (buy.Count > 0)
            steps.Add(new Note("", $"then Confirm and Learn ({points - buy.Sum(b => b.Price)} SP left)"));
        notes.InsertRange(0, steps);
        notes.Add(new Note("Why", why));
        return new Advice(Situation.SkillShop, header, summary, options, notes, details, buy.SelectMany(b => b.Steps).ToList());
    }

    private static Advice RaceListAdvice(CareerContext ctx, ValueModel values, string header, RaceList list, List<Note> notes, List<string> details)
    {
        var evaluator = new RaceEvaluator(ctx, values);
        var options = new List<ScoredOption>();
        foreach (var entry in list.Races)
        {
            var program = ctx.Data.Program(entry.ProgramId);
            if (program is null) continue;
            if (entry.IsGoal) { options.Add(new ScoredOption(program.Name, 1000, ["career goal"])); continue; }
            if (!entry.CanEnter) { options.Add(new ScoredOption(program.Name, -100, [$"locked (needs {entry.NeedFans} fans)"])); continue; }
            options.Add(evaluator.Evaluate(program));
        }
        // Racing uses the turn, so compare with the best thing to do instead (only meaningful for the current turn).
        ScoredOption? instead = null;
        if (list.DisplayTurn == ctx.Turn)
        {
            instead = new TurnAdvisor(ctx, values).Rank().FirstOrDefault(o => !o.Label.StartsWith("Race:"));
            if (instead is not null) options.Add(instead with { Label = $"Don't race: {instead.Label}" });
        }
        else
        {
            notes.Insert(0, new Note("Showing", $"turn {list.DisplayTurn} ({ctx.Data.Turn(list.DisplayTurn)}), not the current turn"));
        }
        options = options.OrderByDescending(o => o.Score).ToList();
        var best = options.FirstOrDefault();
        string summary = best is null ? "No races listed"
            : best.Label.StartsWith("Don't race:") ? $"Go back: {instead!.Label}"
            : best.Score > 0 ? $"Enter {best.Label}" : "No race worth entering; go back";
        return new Advice(Situation.RaceList, header, summary, options, notes, details);
    }
}
