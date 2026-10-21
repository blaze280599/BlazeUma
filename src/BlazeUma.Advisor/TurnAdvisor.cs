using BlazeUma.Data;
using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor;

/// <summary>Ranks the home-screen actions: the five trainings, rest, recreation, infirmary and an optional race.</summary>
public sealed class TurnAdvisor(CareerContext ctx, ValueModel values)
{
    private static readonly Dictionary<int, string> TrainingNames = new()
    {
        [TrainingIds.Speed] = "Speed", [TrainingIds.Stamina] = "Stamina", [TrainingIds.Power] = "Power",
        [TrainingIds.Guts] = "Guts", [TrainingIds.Wit] = "Wit",
    };

    public IReadOnlyList<ScoredOption> Rank()
    {
        var options = new List<ScoredOption>();
        foreach (var command in ctx.Snapshot.Commands ?? [])
        {
            if (!command.Enabled) continue;
            var option = command.Type switch
            {
                CommandType.Training => Training(command),
                CommandType.Rest => Rest(),
                CommandType.Recreation => Recreation(command),
                CommandType.Infirmary => Infirmary(),
                CommandType.Race => new RaceEvaluator(ctx, values).BestPredicted() is { } race
                    ? race with { Label = $"Race: {race.Label}" }
                    : null,
                _ => null,
            };
            if (option is not null) options.Add(option);
        }
        // Recreation variants (normal / friend outing) can both be listed; keep the better one.
        return options.GroupBy(o => o.Label).Select(g => g.MaxBy(o => o.Score)!).OrderByDescending(o => o.Score).ToList();
    }

    private ScoredOption Training(Command command)
    {
        var reasons = new List<string>();
        double gains = 0;
        foreach (var (param, gain) in command.Gains)
        {
            int id = ParamNameToId(param);
            if (id == ParamIds.Energy) continue;
            double v = values.Param(id, gain.Value + gain.Bonus);
            gains += v;
            reasons.Add($"{param} {gain.Value + gain.Bonus:+0;-0} ({v:+0.0;-0.0})");
        }

        double bonds = 0;
        foreach (var partner in command.Partners)
        {
            int bond = ctx.Character.Bonds.FirstOrDefault(b => b.TargetId == partner.Position)?.Value ?? 0;
            if (bond < ValueModel.FriendshipBond)
                bonds += ValueModel.BondValuePerPartner * Math.Clamp(1 - ctx.Turn / 48.0, 0.2, 1);
            if (partner.HasHint) bonds += ValueModel.HintValue;
        }
        if (bonds > 0) reasons.Add($"{command.Partners.Count} partner(s): bond/hints (+{bonds:0.0})");

        double energy = values.Energy(command.Gains.TryGetValue("energy", out var e) ? e.Value + e.Bonus : 0);
        reasons.Add($"energy ({energy:+0.0;-0.0})");

        double p = command.FailureRate / 100.0;
        double risk = p * (gains + bonds + values.FailureLoss);
        if (command.FailureRate > 0) reasons.Add($"{command.FailureRate}% failure (-{risk:0.0})");

        double score = (1 - p) * (gains + bonds) - p * values.FailureLoss + energy;
        string name = TrainingNames.GetValueOrDefault(command.Id, $"Training {command.Id}");
        return new ScoredOption($"Train {name}", score, reasons);
    }

    private ScoredOption Rest()
    {
        double v = values.Energy(ValueModel.ExpectedRestEnergy);
        return new ScoredOption("Rest", v, [$"energy {ctx.Character.Energy} +~{ValueModel.ExpectedRestEnergy} ({v:+0.0})"]);
    }

    private ScoredOption Recreation(Command command)
    {
        double mood = values.Mood(1), energy = values.Energy(10);
        var reasons = new List<string> { $"mood {ctx.Character.Mood} +1 ({mood:+0.0})", $"energy +~10 ({energy:+0.0})" };
        double score = mood + energy;
        if (ctx.NextGoal is { } goal && goal.Turn - ctx.Turn <= 3 && ctx.Character.Mood < Mood.Normal)
        {
            score += 10;
            reasons.Add($"{goal.Name} in {goal.Turn - ctx.Turn} turn(s) with low mood (+10)");
        }
        return new ScoredOption("Recreation", score, reasons);
    }

    private ScoredOption Infirmary()
    {
        var bad = ctx.Character.Conditions.Select(ctx.Data.Condition).Where(c => c is { IsBad: true }).ToList();
        double score = 25 * bad.Count + values.Energy(20);
        return new ScoredOption("Infirmary", score, [$"cures {string.Join(", ", bad.Select(c => c!.Name))}"]);
    }

    public static int ParamNameToId(string name) => name switch
    {
        "speed" => ParamIds.Speed, "stamina" => ParamIds.Stamina, "power" => ParamIds.Power, "guts" => ParamIds.Guts,
        "wit" => ParamIds.Wit, "energy" => ParamIds.Energy, "mood" => ParamIds.Mood, "skillPoints" => ParamIds.SkillPoints,
        _ when name.StartsWith("param") && int.TryParse(name[5..], out int id) => id,
        _ => 0,
    };
}
