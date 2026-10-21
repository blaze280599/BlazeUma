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

        // Recurring scenario events with a known best answer override the preview estimate.
        if (story is not null && story.StoryId / 1_000_000 == 501 && KnownAnswer(story.StoryId % 1000, choices) is var (pick, why))
        {
            int i = choices.ToList().FindIndex(c => c.Choice == pick);
            if (i >= 0)
            {
                double top = options.Max(o => o.Score);
                options[i] = options[i] with { Score = Math.Max(options[i].Score, top + 1), Reasons = [$"uma.guide: {why}", .. options[i].Reasons] };
            }
        }
        return options.OrderByDescending(o => o.Score).ToList();
    }

    private const int NewYearResolutions = 101, NewYearShrine = 102, SummerCampYear2 = 104, Acupuncturist = 720;

    /// <summary>
    /// uma.guide's recommendation for a recurring trainee event (keyed like <see cref="TraineeEventOdds"/>), or
    /// null to keep the preview estimate. Choices are found by their preview effects where possible, so the
    /// rule holds whichever order the game lists them in.
    /// </summary>
    private (int Choice, string Why)? KnownAnswer(int eventKey, IReadOnlyList<EventChoice> choices)
    {
        var c = ctx.Character;
        int? Giving(int param) => choices.FirstOrDefault(ch => ch.Outcomes.Count > 0
            && ch.Outcomes[0].Any(e => e.DisplayId is 1 or 39 && e.Value0 == param))?.Choice;

        switch (eventKey)
        {
            case NewYearResolutions or NewYearShrine when Giving(ParamIds.Energy) is int energy && Giving(ParamIds.SkillPoints) is int sp:
            {
                // Classic: energy unless it would overflow. Senior: the Raffle next turn restores energy, so
                // skill points unless energy is too low to train next turn.
                int gain = choices.First(ch => ch.Choice == energy).Outcomes[0]
                    .First(e => e.DisplayId is 1 or 39 && e.Value0 == ParamIds.Energy).Value1;
                bool takeEnergy = ctx.Date.Year == 2 ? c.Energy + gain <= c.MaxEnergy : c.Energy < 50;
                return takeEnergy
                    ? (energy, ctx.Date.Year == 2 ? "New Year: take energy while it doesn't overflow" : "New Year: energy too low to train next turn")
                    : (sp, ctx.Date.Year == 2 ? "New Year: energy would overflow, take skill points" : "New Year: Raffle restores energy next turn, take skill points");
            }
            case SummerCampYear2 when Giving(ParamIds.Power) is int power:
                return (power, "Classic summer camp: Power over Guts");
            case Acupuncturist when choices.Count == 5:
                // Strength, Winning, Health, Charm, Quit. Strength (~30%) is never worth the risk; Charm only for
                // its Charming condition at the very start; Winning (two recovery skills, ~50%) is usually best.
                return ctx.Turn <= 2 ? (4, "Acupuncturist: Charm Chakra this early, for Charming")
                    : (2, "Acupuncturist: Winning Chakra (recovery skills) is usually best");
        }
        return null;
    }

    /// <summary>Text and value of one preview line; display ids and slots per single_mode_event_choice_reward / text 394.</summary>
    public (string Text, double Value) Describe(EventEffect e)
    {
        int lastStat = TurnAdvisor.TrainingStat(ctx.Snapshot.LastTrainingId ?? 0);
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
