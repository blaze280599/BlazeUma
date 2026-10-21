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
                CommandType.Recreation => null,
                CommandType.Infirmary => Infirmary(),
                CommandType.Race => new RaceEvaluator(ctx, values).BestPredicted() is { } race
                    ? race with { Label = $"Race: {race.Label}" }
                    : null,
                _ => null,
            };
            if (option is not null) options.Add(option);
        }
        // The Recreation button opens one dialog listing the trainee and any friend outings (commands 301 / 390).
        if (ctx.Snapshot.Commands?.Any(c => c.Type == CommandType.Recreation && c.Enabled) == true)
            options.AddRange(Recreation());
        return options.OrderByDescending(o => o.Score).ToList();
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

    /// <summary>Friend outings: chain events (5 steps) usually add stats or hints; estimated, not measured.</summary>
    private const double OutingChainStepValue = 8, OutingEnergy = 20, OutingBond = 5;

    private IEnumerable<ScoredOption> Recreation()
    {
        double mood = values.Mood(1);
        double raceBonus = ctx.NextGoal is { } goal && goal.Turn - ctx.Turn <= 3 && ctx.Character.Mood < Mood.Normal ? 10 : 0;

        double soloEnergy = values.Energy(10);
        var solo = new List<string> { $"mood {ctx.Character.Mood} +1 ({mood:+0.0})", $"energy +~10 ({soloEnergy:+0.0})" };
        if (raceBonus > 0) solo.Add($"{ctx.NextGoal!.Name} soon with low mood (+10)");
        yield return new ScoredOption("Recreation: trainee", mood + soloEnergy + raceBonus, solo);

        foreach (var friend in ctx.Character.Bonds.Where(b => b.OutingUnlocked))
        {
            string name = ctx.Data.CharacterName(friend.CharaId) ?? $"card {friend.TargetId}";
            double energy = values.Energy(OutingEnergy);
            double chain = friend.OutingStep < 5 ? OutingChainStepValue : 0;
            double bond = friend.Value < 100 ? OutingBond * 0.5 : 0;
            var reasons = new List<string>
            {
                $"mood {ctx.Character.Mood} +1 ({mood:+0.0})",
                $"energy +~{OutingEnergy} ({energy:+0.0})",
                $"bond +~{OutingBond} ({bond:+0.0})",
                $"chain event {friend.OutingStep + 1}/5 ({chain:+0.0}, estimate)",
            };
            if (raceBonus > 0) reasons.Add($"{ctx.NextGoal!.Name} soon with low mood (+10)");
            yield return new ScoredOption($"Recreation: {name}", mood + energy + chain + bond + raceBonus, reasons);
        }
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
