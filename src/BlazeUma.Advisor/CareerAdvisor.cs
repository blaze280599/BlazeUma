using BlazeUma.Data;
using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor;

/// <summary>Entry point: picks the right advisor for the screen the game is showing.</summary>
public sealed class CareerAdvisor(IGameData data)
{
    private const string SkillScreen = "Gallop.SingleModeSkillLearningViewController";
    private const string RaceScreen = "Gallop.SingleModeRaceEntryViewController";

    public Advice Advise(CareerSnapshot snapshot)
    {
        var warnings = snapshot.Errors.Select(e => new Note("Read error", e)).ToList();
        if (!snapshot.CareerActive || snapshot.Character is null || snapshot.Turn is null)
            return new Advice(Situation.NoCareer, "Umamusume is open, not in a career",
                "Start or resume a career to get advice", [], warnings, []);

        var ctx = new CareerContext(snapshot, data);
        var values = new ValueModel(ctx);
        var c = ctx.Character;
        string header = $"Turn {ctx.Turn} · {ctx.Date} · Mood {c.Mood} · Energy {c.Energy}/{c.MaxEnergy} · SP {c.SkillPoints} · Fans {c.Fans}";
        var t = values.Targets.Target;
        var w = values.Targets.Weights;
        var notes = new List<Note>(warnings)
        {
            new("Targets", $"Spd {t.Speed}  Sta {t.Stamina}  Pow {t.Power}  Gut {t.Guts}  Wit {t.Wit}"),
        };
        if (ctx.NextGoal is { } next)
            notes.Add(new Note("Next goal", $"{next.Name} on turn {next.Turn}" +
                (next.Turn > ctx.Turn ? $" (in {next.Turn - ctx.Turn})" : " (today)") +
                (next.RequiredPlace > 0 ? $", need top {next.RequiredPlace}" : "")));
        if (ctx.FanShortfall() is var (fanGoal, shortfall))
        {
            int fromGoals = ctx.ExpectedGoalFansBefore(fanGoal.Turn);
            notes.Add(fromGoals >= shortfall
                ? new Note("Fans", $"{shortfall} more needed for {fanGoal.Name} (turn {fanGoal.Turn}); earlier goal races should cover it (~+{fromGoals})")
                : new Note(fanGoal.Turn - ctx.Turn <= 4 ? "Warning" : "Fans",
                    $"{shortfall} more fans needed to enter {fanGoal.Name} (turn {fanGoal.Turn}); missing it fails the goal"));
        }
        var details = new List<string>
        {
            $"stat weights Spd {w[ParamIds.Speed]:0.00}  Sta {w[ParamIds.Stamina]:0.00}  Pow {w[ParamIds.Power]:0.00}  Gut {w[ParamIds.Guts]:0.00}  Wit {w[ParamIds.Wit]:0.00}",
        };

        if (snapshot.Screen?.ViewController == SkillScreen && snapshot.SkillShop is { } shop)
            return SkillShopAdvice(ctx, header, shop, notes, details);
        if (snapshot.Screen?.ViewController == RaceScreen && snapshot.RaceList is { } races)
            return RaceListAdvice(ctx, values, header, races, notes, details);
        if (snapshot.Story is { LooksActive: true } story)
        {
            string title = data.StoryTitle(story.StoryId) ?? $"story {story.StoryId}";
            if (snapshot.EventChoices is not { Count: > 0 } choices)
                return new Advice(Situation.Event, header, $"Event \"{title}\": no choice to make, continue", [], notes, details);
            // The previews should belong to this event; seen to match once so far, so only warn on a mismatch.
            if (snapshot.EventChoicesEventId is int choicesFor && choicesFor != story.EventId)
                notes.Insert(0, new Note("Warning", $"choices were built for event {choicesFor}, but event {story.EventId} is showing; they may be stale"));
            var ranked = new EventAdvisor(ctx, values).Rank(choices, story);
            return new Advice(Situation.Event, header, $"Event \"{title}\": {ranked[0].Label}", ranked, notes, details);
        }

        var skills = new SkillAdvisor(ctx);
        var (buy, why) = skills.Plan(skills.Predicted(), c.SkillPoints);
        notes.Add(buy.Count > 0
            ? new Note("Skills", $"buy {string.Join(", ", buy.Select(b => $"{b.Skill.Name} ({b.Price})"))} before racing ({why})")
            : new Note("Skills", why));

        if (ctx.GoalToday is { } goal)
            return new Advice(Situation.GoalRaceDay, header,
                $"Race day: run {goal.Name}{(goal.RequiredPlace > 0 ? $" (need top {goal.RequiredPlace})" : "")}", [], notes, details);

        var options = new TurnAdvisor(ctx, values).Rank();
        return new Advice(Situation.TrainingTurn, header, options.Count > 0 ? options[0].Label : "No enabled actions found", options, notes, details);
    }

    private static Advice SkillShopAdvice(CareerContext ctx, string header, SkillShop shop, List<Note> notes, List<string> details)
    {
        var advisor = new SkillAdvisor(ctx);
        var candidates = advisor.FromShop(shop);
        var (buy, why) = advisor.Plan(candidates, shop.RemainingPoints);
        var options = candidates.OrderByDescending(c => c.PerPoint)
            .Select(c => new ScoredOption($"{c.Skill.Name} ({c.Price} SP)", c.PerPoint * 100,
                [buy.Contains(c) ? "BUY" : "skip for now", $"fit {c.Fit:P0}", $"rating {c.Skill.GradeValue}"]))
            .ToList();
        string summary = buy.Count > 0 ? $"Buy {string.Join(", ", buy.Select(b => b.Skill.Name))}" : "Buy nothing yet";
        notes.Add(new Note("Why", why));
        return new Advice(Situation.SkillShop, header, summary, options, notes, details);
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
