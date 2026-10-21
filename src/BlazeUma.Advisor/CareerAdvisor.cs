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
        if (!snapshot.CareerActive || snapshot.Character is null || snapshot.Turn is null)
            return new Advice(Situation.NoCareer, "No career in progress.", [], snapshot.Errors);

        var ctx = new CareerContext(snapshot, data);
        var values = new ValueModel(ctx);
        var notes = new List<string>(snapshot.Errors.Select(e => $"read error: {e}"));
        notes.Add(TargetsNote(ctx, values));

        if (snapshot.Screen?.ViewController == SkillScreen && snapshot.SkillShop is { } shop)
            return SkillShopAdvice(ctx, shop, notes);
        if (snapshot.Screen?.ViewController == RaceScreen && snapshot.RaceList is { } races)
            return RaceListAdvice(ctx, values, races, notes);
        if (snapshot.Story is { LooksActive: true } story && snapshot.EventChoices is { Count: > 0 } choices)
        {
            var ranked = new EventAdvisor(ctx, values).Rank(choices, story);
            string title = data.StoryTitle(story.StoryId) ?? $"story {story.StoryId}";
            return new Advice(Situation.Event, $"{title}: pick {ranked[0].Label.ToLowerInvariant()}", ranked, notes);
        }

        var skills = new SkillAdvisor(ctx);
        var (buy, why) = skills.Plan(skills.Predicted(), ctx.Character.SkillPoints);
        notes.Add(buy.Count > 0
            ? $"before racing, buy: {string.Join(", ", buy.Select(b => $"{b.Skill.Name} ({b.Price})"))} ({why})"
            : $"skills: {why}");

        if (ctx.GoalToday is { } goal)
            return new Advice(Situation.GoalRaceDay, $"Race day: run {goal.Name} (goal{(goal.RequiredPlace > 0 ? $", need top {goal.RequiredPlace}" : "")}).", [], notes);

        var options = new TurnAdvisor(ctx, values).Rank();
        string summary = options.Count > 0 ? $"Turn {ctx.Turn} ({ctx.Date}): {options[0].Label}" : "No enabled actions found.";
        return new Advice(Situation.TrainingTurn, summary, options, notes);
    }

    private static string TargetsNote(CareerContext ctx, ValueModel values)
    {
        var t = values.Targets.Target;
        var w = values.Targets.Weights;
        string next = ctx.NextGoal is { } g ? $"; next goal {g.Name} on turn {g.Turn}" : "";
        return $"targets Spd {t.Speed} Sta {t.Stamina} Pow {t.Power} Gut {t.Guts} Wit {t.Wit} " +
               $"(weights {w[ParamIds.Speed]:0.00}/{w[ParamIds.Stamina]:0.00}/{w[ParamIds.Power]:0.00}/{w[ParamIds.Guts]:0.00}/{w[ParamIds.Wit]:0.00}){next}";
    }

    private static Advice SkillShopAdvice(CareerContext ctx, SkillShop shop, List<string> notes)
    {
        var advisor = new SkillAdvisor(ctx);
        var candidates = advisor.FromShop(shop);
        var (buy, why) = advisor.Plan(candidates, shop.RemainingPoints);
        var options = candidates.OrderByDescending(c => c.PerPoint)
            .Select(c => new ScoredOption($"{c.Skill.Name} ({c.Price} SP)", c.PerPoint * 100,
                [$"fit {c.Fit:P0}", $"rating {c.Skill.GradeValue}", buy.Contains(c) ? "BUY" : "skip for now"]))
            .ToList();
        string summary = buy.Count > 0
            ? $"Buy {string.Join(", ", buy.Select(b => b.Skill.Name))} ({why})."
            : $"Buy nothing yet: {why}.";
        return new Advice(Situation.SkillShop, summary, options, notes);
    }

    private static Advice RaceListAdvice(CareerContext ctx, ValueModel values, RaceList list, List<string> notes)
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
        options = options.OrderByDescending(o => o.Score).ToList();
        if (list.DisplayTurn != ctx.Turn)
            notes.Add($"list shows turn {list.DisplayTurn} ({ctx.Data.Turn(list.DisplayTurn)}), not the current turn {ctx.Turn}");
        var best = options.FirstOrDefault();
        string summary = best is null ? "No races listed."
            : best.Score > 0 ? $"Best race: {best.Label}." : "No race worth entering; go back and train.";
        return new Advice(Situation.RaceList, summary, options, notes);
    }
}
