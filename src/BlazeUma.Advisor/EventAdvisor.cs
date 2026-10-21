using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor;

/// <summary>
/// Scores event choices from the game's own choice previews. The game does not store outcome odds, so known
/// trainee events use community-measured odds (umareference.com/guide/percentages-of-events) and the rest
/// assume every outcome is equally likely.
/// </summary>
public sealed class EventAdvisor(CareerContext ctx, ValueModel values)
{
    /// <summary>Odds per choice, keyed by the last three digits of a trainee story id (501 + chara + event).</summary>
    private static readonly Dictionary<int, double[][]> TraineeEventOdds = new()
    {
        [713] = [[0.92, 0.08], [0.30, 0.55, 0.15]],   // Get Well Soon!
        [714] = [[0.50, 0.50], [0.97, 0.03]],         // Don't Overdo It!
    };

    public IReadOnlyList<ScoredOption> Rank(IReadOnlyList<EventChoice> choices, StoryEvent? story)
    {
        double[][]? odds = story is { StoryId: var id } && id / 1_000_000 == 501
            ? TraineeEventOdds.GetValueOrDefault(id % 1000)
            : null;

        var options = new List<ScoredOption>();
        foreach (var choice in choices)
        {
            int i = choice.Choice - 1;
            double[]? p = odds is not null && i < odds.Length && odds[i].Length == choice.Outcomes.Count ? odds[i] : null;
            var reasons = new List<string>();
            double expected = 0;
            for (int o = 0; o < choice.Outcomes.Count; o++)
            {
                double weight = p?[o] ?? 1.0 / choice.Outcomes.Count;
                var parts = choice.Outcomes[o].Select(Describe).ToList();
                double value = parts.Sum(x => x.Value);
                expected += weight * value;
                reasons.Add($"{weight:P0}: {string.Join(", ", parts.Select(x => x.Text))} ({value:+0.0;-0.0})");
            }
            if (p is null && choice.Outcomes.Count > 1) reasons.Add("odds unknown; outcomes weighted equally");
            options.Add(new ScoredOption(choice.Choice == 1 ? "Top choice" : choice.Choice == 2 ? "Bottom choice" : $"Choice {choice.Choice}", expected, reasons));
        }
        return options.OrderByDescending(o => o.Score).ToList();
    }

    /// <summary>Text and value of one preview line; display ids and slots per single_mode_event_choice_reward / text 394.</summary>
    public (string Text, double Value) Describe(EventEffect e)
    {
        int lastStat = ctx.Snapshot.LastTrainingId switch
        {
            TrainingIds.Speed => ParamIds.Speed, TrainingIds.Stamina => ParamIds.Stamina, TrainingIds.Power => ParamIds.Power,
            TrainingIds.Guts => ParamIds.Guts, TrainingIds.Wit => ParamIds.Wit, _ => 0,
        };
        double lastWeight = lastStat != 0 ? values.Weight(lastStat) : values.AverageStatWeight;
        string Cond(int id) => ctx.Data.Condition(id)?.Name ?? $"condition {id}";
        string Card(int id) => ctx.Data.CharacterName(id) ?? $"#{id}";

        return e.DisplayId switch
        {
            1 or 39 => ($"{ParamIds.Name(e.Value0)} +{e.Value1}", values.Param(e.Value0, e.Value1)),
            2 or 38 => ($"{ParamIds.Name(e.Value0)} -{e.Value1}", values.Param(e.Value0, -e.Value1)),
            3 => ($"fans +{e.Value0}", e.Value0 / 500.0),
            4 or 5 => ($"bond {Card(e.Value0)} +{e.Value1}", e.Value1 * 0.5),
            35 or 36 => ($"bond {Card(e.Value0)} -{e.Value1}", -e.Value1 * 0.5),
            6 => ($"hint {ctx.Data.Skill(e.Value0)?.Name ?? e.Value0.ToString()} +{e.Value1}", e.Value1 * ValueModel.HintValue),
            7 => ($"gain {ctx.Data.Skill(e.Value0)?.Name ?? e.Value0.ToString()}", 12),
            8 => ("chance of a random skill", 6),
            9 or 37 or 40 => ($"become {Cond(e.Value0)}", ctx.Data.Condition(e.Value0) is { IsBad: true } ? -15 : 15),
            10 => ($"cures {Cond(e.Value0)}", 15),
            11 => ($"unlock recreation with {Card(e.Value0)}", 6),
            13 => ($"all stats +{e.Value0}", e.Value0 * 5 * values.AverageStatWeight),
            14 => ($"{e.Value0} random stat(s) +{e.Value1}", e.Value0 * e.Value1 * values.AverageStatWeight),
            34 => ($"{e.Value0} random stat(s) -{e.Value1}", -e.Value0 * e.Value1 * values.AverageStatWeight),
            15 => ("cures all bad conditions", 15),
            16 => ($"cures {e.Value0} bad condition(s)", 10 * e.Value0),
            17 => ("restricts a training", -15),
            18 => ("restricts race entry", -10),
            19 => ($"last trained stat +{e.Value0}", e.Value0 * lastWeight),
            20 => ($"last trained stat -{e.Value0}", -e.Value0 * lastWeight),
            27 or 28 or 29 or 30 => ("bond with lowest-bond cards", 3),
            31 or 32 => ("skill hints in chain event", 6),
            _ => ($"effect {e.DisplayId} ({e.Value0}, {e.Value1}, {e.Value2})", 0),
        };
    }
}
