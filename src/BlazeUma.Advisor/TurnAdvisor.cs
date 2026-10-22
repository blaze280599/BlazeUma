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
        // In summer camp Rest is disabled and the Recreation command (304) is Rest & Recreation instead.
        if (ctx.Snapshot.Commands?.Any(c => c.Type == CommandType.Recreation && c.Enabled) == true)
        {
            if (ctx.InSummerCamp) options.Add(RestAndRecreation());
            else options.AddRange(Recreation());
        }
        return options.OrderByDescending(o => o.Score).ToList();
    }

    private ScoredOption Training(Command command)
    {
        int trainingId = TrainingIds.Base(command.Id);   // summer camp's 60x ids → the usual ones
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
        var names = new List<string>();
        foreach (var partner in command.Partners)
        {
            var (value, label) = PartnerValue(partner, trainingId);
            bonds += value;
            names.Add(label);
        }
        if (names.Count > 0) reasons.Add($"with {string.Join(", ", names)} (+{bonds:0.0})");

        // A failed training is taken not to count toward the facility's next level.
        var (facility, facilityReason) = values.FacilityProgress(trainingId);
        if (facilityReason is not null) reasons.Add(facilityReason);

        double energy = values.Energy(command.Gains.TryGetValue("energy", out var e) ? e.Value + e.Bonus : 0);
        reasons.Add($"energy ({energy:+0.0;-0.0})");

        double p = command.FailureRate / 100.0;
        double risk = p * (gains + bonds + facility + values.FailureLoss);
        if (command.FailureRate > 0) reasons.Add($"{command.FailureRate}% failure (-{risk:0.0})");

        double score = (1 - p) * (gains + bonds + facility) - p * values.FailureLoss + energy;
        string name = TrainingNames.GetValueOrDefault(trainingId, $"Training {command.Id}");
        return new ScoredOption($"Train {name}", score, reasons);
    }

    /// <summary>
    /// Bond worth of training next to a partner. Stat cards (positions 1–6) build toward friendship trainings
    /// at 80 bond (<see cref="ValueModel.BondTrainingValue"/>); friend cards and scenario NPCs (ids above 100,
    /// e.g. Director Akikawa) never give friendship trainings, so they keep a flat estimate, NPCs at half. The
    /// stat gains already include any partner bonus.
    /// </summary>
    private (double Value, string Label) PartnerValue(TrainingPartner partner, int trainingId)
    {
        if (partner.Position == HappyMeekPosition) return HappyMeek(trainingId);
        var bond = ctx.Character.Bonds.FirstOrDefault(b => b.TargetId == partner.Position);
        int level = bond?.Value ?? 0;
        string name = bond is not null ? ctx.Data.CharacterName(bond.CharaId) ?? $"#{partner.Position}" : $"#{partner.Position}";
        bool isCard = partner.Position is >= 1 and <= 6;
        var card = isCard ? ctx.Character.Deck.FirstOrDefault(d => d.Position == partner.Position) : null;
        var info = card is not null ? ctx.Data.SupportCard(card.SupportCardId) : null;
        bool friendship = info is not null && level >= ValueModel.FriendshipBond && info.TrainingCommandId == trainingId;

        // Stat cards: bond is worth the friendship trainings it brings forward, from the card's own Friendship
        // Bonus and Specialty Priority. Friend/group cards and NPCs give no friendship trainings: flat estimate.
        double value = 0;
        if (level < ValueModel.FriendshipBond)
            value += info is { TrainingCommandId: > 0 } && ctx.Data.SupportCardEffects(card!.SupportCardId, card.LimitBreak, card.Exp) is { } effects
                ? values.BondTrainingValue(level, info.TrainingCommandId, effects)
                : ValueModel.BondValuePerPartner * Math.Clamp(1 - ctx.Turn / 48.0, 0.2, 1) * (isCard ? 1 : 0.5);
        if (partner.HasHint) value += ValueModel.HintValue;

        string label = $"{name} {level}" + (friendship ? " friendship!" : "") + (partner.HasHint ? " hint" : "");
        return (value, label);
    }

    /// <summary>Happy Meek's partner position (chara 2001) on URA trainings since the July patch.</summary>
    public const int HappyMeekPosition = 2001;

    /// <summary>
    /// Happy Meek on a training may challenge the trainee to a contest after it succeeds (never in summer camp).
    /// Win: +10–20 to the stat, +4 to its cap, +30 SP, and a step toward the 5 wins that double the URA Finale
    /// reward; loss: +5 stat, +15 SP (uma.guide). Neither the duel chance nor the win chance is read from the
    /// game, so both are assumed even; the contest stat is taken to be the training's own (the top choice).
    /// </summary>
    private (double Value, string Label) HappyMeek(int trainingId)
    {
        if (ctx.InSummerCamp) return (0, "Happy Meek (no duels in camp)");
        const double DuelChance = 0.5, WinChance = 0.5, FinaleStep = 5;
        double weight = values.Weight(TrainingStat(trainingId));
        double win = 15 * weight + 30 * ValueModel.SkillPointValue + FinaleStep;
        double loss = 5 * weight + 15 * ValueModel.SkillPointValue;
        return (DuelChance * (WinChance * win + (1 - WinChance) * loss), "Happy Meek duel?");
    }

    public static int TrainingStat(int trainingId) => TrainingIds.Base(trainingId) switch
    {
        TrainingIds.Speed => ParamIds.Speed, TrainingIds.Stamina => ParamIds.Stamina, TrainingIds.Power => ParamIds.Power,
        TrainingIds.Guts => ParamIds.Guts, TrainingIds.Wit => ParamIds.Wit, _ => 0,
    };

    private ScoredOption Rest()
    {
        double v = RestOutcomes.Sum(o => o.Chance * values.Energy(o.Energy));
        double nightOwl = ctx.Character.Conditions.Contains(ConditionIds.NightOwl) ? 0
            : RestOutcomes.Where(o => o.NightOwl).Sum(o => o.Chance) * -values.ConditionValue(ConditionIds.NightOwl);
        var reasons = new List<string>
        {
            $"energy {ctx.Character.Energy} +70 / +50 / +30 (25% / 62.5% / 12.5%) ({v:+0.0})",
            $"2.5% Night Owl (-{nightOwl:0.0})",
        };
        v -= nightOwl;

        // uma.guide: enter summer camp with high energy. Low energy now means a rest during camp later, which
        // costs a level-5 training instead of an ordinary one; how low decides how likely that rest is.
        if (ctx.TurnsUntilSummerCamp is 1 or 2)
        {
            int max = ctx.Character.MaxEnergy;
            double likely = Math.Clamp((0.7 * max - ctx.Character.Energy) / (0.4 * max), 0, 1);
            double premium = likely * values.CampTrainingPremium();
            if (premium > 0)
            {
                v += premium;
                reasons.Add($"summer camp in {ctx.TurnsUntilSummerCamp}: rest now, not on a level-5 camp turn (+{premium:0.0})");
            }
        }
        return new ScoredOption("Rest", v, reasons);
    }

    public const string RestAndRecreationLabel = "Rest & Recreation";

    /// <summary>
    /// Summer camp's Rest & Recreation (the Recreation command, 304, while Rest is disabled): +40 energy, +1 mood
    /// and cures all general bad conditions (uma.guide).
    /// </summary>
    private ScoredOption RestAndRecreation()
    {
        double energy = values.Energy(40), mood = values.Mood(1);
        var bad = values.BadConditions;
        double cure = bad.Sum(values.CureValue);
        var reasons = new List<string>
        {
            $"summer camp Rest & Recreation: energy {ctx.Character.Energy} +40 ({energy:+0.0})",
            $"mood {ctx.Character.Mood} +1 ({mood:+0.0})",
        };
        if (bad.Count > 0) reasons.Add($"cures {ConditionNames(bad)} (+{cure:0.0})");
        return new ScoredOption(RestAndRecreationLabel, energy + mood + cure, reasons);
    }

    private string ConditionNames(IEnumerable<int> conditions) =>
        string.Join(", ", conditions.Select(c => ctx.Data.Condition(c)?.Name ?? $"condition {c}"));

    /// <summary>Rest outcomes measured by umareference.com (percentages-of-events): energy, and the Night Owl chance.</summary>
    private static readonly (double Chance, int Energy, bool NightOwl)[] RestOutcomes =
        [(0.25, 70, false), (0.625, 50, false), (0.10, 30, false), (0.025, 30, true)];

    /// <summary>The trainee's own outing, same source: karaoke, stroll, and three shrine visits.</summary>
    private static readonly (double Chance, int Mood, int Energy)[] SoloOutings =
        [(0.35, 2, 0), (0.30, 1, 10), (0.20, 1, 10), (0.10, 1, 20), (0.05, 1, 30)];

    /// <summary>Friend outings: chain events (5 steps) usually add stats or hints; estimated, not measured.</summary>
    private const double OutingChainStepValue = 8, OutingEnergy = 20, OutingBond = 5;

    private IEnumerable<ScoredOption> Recreation()
    {
        double mood = values.Mood(1);
        double raceBonus = ctx.NextGoal is { } goal && goal.Turn - ctx.Turn <= 3 && ctx.Character.Mood < Mood.Normal ? 10 : 0;

        double soloValue = SoloOutings.Sum(o => o.Chance * (values.Mood(o.Mood) + values.Energy(o.Energy)));
        var solo = new List<string> { $"karaoke +2 mood 35%, else +1 mood and +10–30 energy ({soloValue:+0.0})" };
        if (raceBonus > 0) solo.Add($"{ctx.NextGoal!.Name} soon with low mood (+10)");
        yield return new ScoredOption("Recreation: trainee", soloValue + raceBonus, solo);

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

    /// <summary>
    /// Cures each bad condition <see cref="ValueModel.InfirmaryCureChance"/> of the time; a cure is worth the turns
    /// the condition would otherwise last (<see cref="ValueModel.ConditionValue"/>), so little near the career's
    /// end or just before a summer camp Rest &amp; Recreation.
    /// </summary>
    private ScoredOption Infirmary()
    {
        var bad = values.BadConditions;
        double cure = ValueModel.InfirmaryCureChance * bad.Sum(values.CureValue);
        double score = cure + values.Energy(20);
        return new ScoredOption("Infirmary", score,
            [$"cures {ConditionNames(bad)} ({ValueModel.InfirmaryCureChance:P0}, +{cure:0.0})"]);
    }

    public static int ParamNameToId(string name) => name switch
    {
        "speed" => ParamIds.Speed, "stamina" => ParamIds.Stamina, "power" => ParamIds.Power, "guts" => ParamIds.Guts,
        "wit" => ParamIds.Wit, "energy" => ParamIds.Energy, "mood" => ParamIds.Mood, "skillPoints" => ParamIds.SkillPoints,
        _ when name.StartsWith("param") && int.TryParse(name[5..], out int id) => id,
        _ => 0,
    };
}
