using BlazeUma.Data;
using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor;

/// <summary>End-of-career stat targets and how much one more point of each stat is worth right now.</summary>
public sealed record StatTargets(Stats Target, IReadOnlyDictionary<int, double> Weights);

/// <summary>
/// Converts everything a turn can change (stats, skill points, energy, mood, bonds, conditions) into one
/// comparable score, in units of "one Speed point early in the career". All constants are tunable heuristics,
/// not game formulas; they live here so they can be adjusted in one place.
/// </summary>
public sealed class ValueModel
{
    // Relative importance of each stat before deficits are applied.
    private static readonly IReadOnlyDictionary<int, double> Priority = new Dictionary<int, double>
    {
        [ParamIds.Speed] = 1.25, [ParamIds.Stamina] = 1.0, [ParamIds.Power] = 1.0, [ParamIds.Guts] = 0.6, [ParamIds.Wit] = 0.85,
    };

    public const double SkillPointValue = 0.35;
    /// <summary>Gaining a general bad condition (Night Owl, Slow Metabolism …); the same 15 events use for "become X".</summary>
    public const double BadConditionValue = 15;
    public const double BondValuePerPartner = 3;
    public const double HintValue = 4;
    public const int FriendshipBond = 80;

    private readonly CareerContext _ctx;

    public StatTargets Targets { get; }

    public ValueModel(CareerContext ctx)
    {
        _ctx = ctx;
        Targets = PlanTargets(ctx);
    }

    /// <summary>Stamina stat for a full last spurt in a race of this distance with these Speed and Guts (<see cref="RaceStamina"/>).</summary>
    public static int StaminaFor(CareerContext ctx, int distance, int speed, int guts) =>
        RaceStamina.Required(distance, ctx.Style, speed, guts, ctx.Aptitude(Distances.Categorize(distance)));

    /// <summary>A stat at a future turn, assuming it moves linearly from now toward its end-of-career target.</summary>
    private static int Projected(CareerContext ctx, int now, int end, int turn) => now >= end ? now
        : now + (int)((end - now) * Math.Clamp((turn - ctx.Turn) / (double)Math.Max(1, CareerContext.FinalTurn - ctx.Turn), 0, 1));

    /// <summary>
    /// Stamina for every remaining goal race at the Speed and Guts the trainee is projected to have by then; the
    /// scenario finals (no race data) are taken at the route's longest goal distance near the end of the career.
    /// </summary>
    private static int StaminaTarget(CareerContext ctx, int speedTarget, int gutsTarget)
    {
        var c = ctx.Character;
        var races = ctx.RemainingGoals.Where(g => g.Race is not null).Select(g => (g.Race!.Distance, g.Turn)).ToList();
        if (ctx.RemainingGoals.Any(g => g.Race is null))
            races.Add((ctx.Goals.Where(g => g.Race is not null).Select(g => g.Race!.Distance).DefaultIfEmpty(2000).Max(), CareerContext.FinalTurn));
        return races.Select(r => StaminaFor(ctx, r.Distance,
                Projected(ctx, c.Stats.Speed, speedTarget, r.Turn), Projected(ctx, c.Stats.Guts, gutsTarget, r.Turn)))
            .DefaultIfEmpty(0).Max();
    }

    /// <summary>
    /// Stat total from the two best trainings on offer when the snapshot shows none (race day, event screen).
    /// The logged careers' best two trainings average 22–29 from turn 16 to turn 72 (measure_logs.py data).
    /// </summary>
    public const int TypicalTrainingStatGain = 25;
    /// <summary>
    /// Stats per turn from events, races and outings on top of trainings. The logged careers grew 24–27 stats a
    /// turn in all (turns 16–27 and 27–71) against ~19 from 0.7 × their best trainings.
    /// </summary>
    public const double OtherStatsPerTurn = 5;
    /// <summary>Camp trainings are level 5; URA base gains at level 3 total ~0.8 of level 5 (<see cref="BaseTraining"/>).</summary>
    private const double CampToNormalGain = 0.8;

    /// <summary>Stat points the picked training is expected to give: the two best trainings on offer, averaged.</summary>
    public static double TrainingStatGain(CareerContext ctx)
    {
        var best = (ctx.Snapshot.Commands ?? []).Where(c => c.Type == CommandType.Training)
            .Select(c => c.Gains.Where(g => TurnAdvisor.ParamNameToId(g.Key) is >= ParamIds.Speed and <= ParamIds.Wit)
                .Sum(g => g.Value.Value + g.Value.Bonus))
            .OrderDescending().Take(2).ToList();
        double gain = best.Count > 0 && best.Average() > 0 ? best.Average() : TypicalTrainingStatGain;
        return ctx.InSummerCamp ? gain * CampToNormalGain : gain;
    }

    /// <summary>
    /// End-of-career stat total if the rest of the career goes like today: every remaining turn gives
    /// <see cref="TrainingTurnShare"/> of a training's gain plus <see cref="OtherStatsPerTurn"/>.
    /// </summary>
    public static int ProjectedStatTotal(CareerContext ctx) => Total(ctx.Character.Stats)
        + (int)(ctx.TurnsLeft * (TrainingStatGain(ctx) * TrainingTurnShare + OtherStatsPerTurn));

    private static int Total(Stats s) => s.Speed + s.Stamina + s.Power + s.Guts + s.Wit;

    /// <summary>
    /// Shares of the projected total besides Stamina, per goal distance (uma.guide stats page: Stamina ≈ Speed to
    /// the cap, then Power ~800–1000 ≈ Wit, Guts last). Power is acceleration and position fighting, which decide
    /// short races with short spurts, so sprint and mile take more of it; Guts lowers late-race HP use and wins
    /// long duels, so long routes take a little more. Wit covers the rest. Stamina gets Speed's share.
    /// </summary>
    private static (double Speed, double Power, double Guts, double Wit) DistanceShares(DistanceCategory d) => d switch
    {
        DistanceCategory.Sprint => (0.42, 0.30, 0.08, 0.20),
        DistanceCategory.Mile => (0.42, 0.28, 0.10, 0.20),
        DistanceCategory.Long => (0.42, 0.22, 0.15, 0.21),
        _ => (0.42, 0.25, 0.11, 0.22),
    };

    /// <summary>
    /// <see cref="DistanceShares"/> weighted by the route's remaining goal distances. Late surgers and end closers
    /// run from the back and need acceleration to pass (uma.guide: Power for closers), so they move 0.03 from Wit
    /// to Power.
    /// </summary>
    private static Dictionary<int, double> Shares(CareerContext ctx)
    {
        var s = new Dictionary<int, double> { [ParamIds.Speed] = 0, [ParamIds.Power] = 0, [ParamIds.Guts] = 0, [ParamIds.Wit] = 0 };
        foreach (var (category, share) in ctx.DistanceProfile)
        {
            var d = DistanceShares(category);
            s[ParamIds.Speed] += d.Speed * share; s[ParamIds.Power] += d.Power * share;
            s[ParamIds.Guts] += d.Guts * share; s[ParamIds.Wit] += d.Wit * share;
        }
        if (ctx.Style is RunningStyle.LateSurger or RunningStyle.EndCloser)
        {
            s[ParamIds.Power] += 0.03;
            s[ParamIds.Wit] -= 0.03;
        }
        s[ParamIds.Stamina] = s[ParamIds.Speed];
        return s;
    }

    /// <summary>
    /// Splits <paramref name="total"/> by <see cref="Shares"/>, each stat at most its cap; Stamina's cap is
    /// <paramref name="staminaNeed"/>, the race requirement, so stamina a short route doesn't need goes to the
    /// other stats. A stat already past its share keeps its current value; what capped and ahead stats free or
    /// take is shared out among the others.
    /// </summary>
    private static Stats Split(CareerContext ctx, int total, int staminaNeed)
    {
        var c = ctx.Character;
        var shares = Shares(ctx);
        var fixedAt = new Dictionary<int, int>();
        var target = new Dictionary<int, int>();
        for (bool changed = true; changed;)
        {
            changed = false;
            var free = shares.Keys.Where(k => !fixedAt.ContainsKey(k)).ToList();
            double left = total - fixedAt.Values.Sum(), shareSum = free.Sum(k => shares[k]);
            foreach (int stat in free)
            {
                int t = (int)(left * shares[stat] / shareSum), now = Get(c.Stats, stat);
                int cap = stat == ParamIds.Stamina ? Math.Max(now, staminaNeed) : Get(c.MaxStats, stat);
                target[stat] = t;
                if (t <= now || t >= cap)
                {
                    fixedAt[stat] = target[stat] = Math.Clamp(t, now, cap);
                    changed = true;
                }
            }
        }
        // Stamina past the race requirement stays the requirement, so its weight drops to the "goal met" level.
        int stamina = c.Stats.Stamina >= staminaNeed ? staminaNeed : target[ParamIds.Stamina];
        return new Stats(target[ParamIds.Speed], stamina, target[ParamIds.Power], target[ParamIds.Guts], target[ParamIds.Wit]);
    }

    /// <summary>
    /// Targets follow uma.guide's stat priority for the 1400-cap URA (July patch), scaled to what this career can
    /// reach (<see cref="ProjectedStatTotal"/>) and split by <see cref="Shares"/>, each stat at most its cap:
    /// Stamina as much as Speed up to a full last spurt in every remaining goal race, Power ~ Wit, Guts last. The
    /// stamina requirement depends on the split's Speed and Guts, so the split is run three times.
    /// </summary>
    private static StatTargets PlanTargets(CareerContext ctx)
    {
        var c = ctx.Character;
        int total = ProjectedStatTotal(ctx);
        var target = Split(ctx, total, c.MaxStats.Stamina);
        for (int pass = 0; pass < 2; pass++)
            target = Split(ctx, total, Math.Min(StaminaTarget(ctx, target.Speed, target.Guts), c.MaxStats.Stamina));

        // Reachable targets close in on every stat as the career runs out, so a deficit is measured against the
        // share of the end total still to gain: 1 = on plan (0.75 × priority), 1.5+ = well behind (full priority).
        double toGain = Math.Max(0.02, (total - Total(c.Stats)) / (double)total);
        var weights = new Dictionary<int, double>();
        foreach (int stat in new[] { ParamIds.Speed, ParamIds.Stamina, ParamIds.Power, ParamIds.Guts, ParamIds.Wit })
        {
            int current = Get(c.Stats, stat), goal = Get(target, stat), cap = Get(c.MaxStats, stat);
            double weight = current >= cap ? 0
                : current >= goal ? Priority[stat] * 0.15
                : Priority[stat] * (0.25 + 0.5 * Math.Min((goal - current) / (goal * toGain), 1.5));
            weights[stat] = weight;
        }

        // A goal race soon that the trainee lacks stamina for at today's Speed and Guts: stamina becomes urgent
        // (once, however many goals). Earlier fields are weak and don't need a full spurt to beat (a Junior debut
        // at Speed 250 / Guts 150 would "need" ~800), so only part of the requirement counts before Senior year.
        double yearShare = ctx.Date.Year switch { 1 => 0.5, 2 => 0.75, _ => 1.0 };
        if (ctx.RemainingGoals.Any(g => g.Race is not null && g.Turn - ctx.Turn <= 12
                && c.Stats.Stamina < StaminaFor(ctx, g.Race.Distance, c.Stats.Speed, c.Stats.Guts) * yearShare))
            weights[ParamIds.Stamina] *= 1.5;
        return new StatTargets(target, weights);
    }

    public static int Get(Stats s, int stat) => stat switch
    {
        ParamIds.Speed => s.Speed, ParamIds.Stamina => s.Stamina, ParamIds.Power => s.Power,
        ParamIds.Guts => s.Guts, ParamIds.Wit => s.Wit, _ => 0,
    };

    public double Weight(int stat) => Targets.Weights.GetValueOrDefault(stat);

    public double AverageStatWeight => Targets.Weights.Values.Average();

    /// <summary>Value of a parameter change as used by training gains and event effects.</summary>
    public double Param(int param, double amount) => param switch
    {
        ParamIds.Speed or ParamIds.Stamina or ParamIds.Power or ParamIds.Guts or ParamIds.Wit => amount * Weight(param),
        ParamIds.SkillPoints => amount * SkillPointValue,
        ParamIds.Energy => Energy(amount),
        ParamIds.Mood => Mood(amount),
        _ => 0,
    };

    /// <summary>
    /// Value of an energy change from the current level. Energy is worth more the lower it is (failure risk and
    /// lost turns), and gains above max energy are wasted. In the two turns before summer camp, energy carried
    /// in is spent on level-5 trainings, so the upper bands are worth more (uma.guide: enter camp with high energy).
    /// </summary>
    public double Energy(double delta, double? from = null)
    {
        double start = from ?? _ctx.Character.Energy;
        double end = Math.Clamp(start + delta, 0, _ctx.Character.MaxEnergy);
        double lo = Math.Min(start, end), hi = Math.Max(start, end), value = 0;
        (double Upto, double Marginal)[] bands = _ctx.TurnsUntilSummerCamp is 1 or 2
            ? [(30, 0.9), (50, 0.7), (70, 0.55), (double.MaxValue, 0.4)]
            : [(30, 0.9), (50, 0.6), (70, 0.35), (double.MaxValue, 0.15)];
        double floor = 0;
        foreach (var (upto, marginal) in bands)
        {
            double overlap = Math.Max(0, Math.Min(hi, upto) - Math.Max(lo, floor));
            value += overlap * marginal;
            floor = upto;
        }
        return end >= start ? value : -value;
    }

    /// <summary>
    /// One mood level changes all training gains by roughly 10% (≈ 2 points of a typical 20-point training) for
    /// the turns it lasts, and race performance too. Raising Bad/Awful counts double: it drags on every turn
    /// and every race until fixed, while Good → Great alone rarely beats a full training.
    /// </summary>
    public double Mood(double levels)
    {
        int mood = (int)_ctx.Character.Mood;
        double applicable = levels > 0 ? Math.Min(levels, 5 - mood) : Math.Max(levels, 1 - mood);
        double perLevel = 0.1 * 20 * Math.Min(_ctx.TurnsLeft, 10) * 0.5;
        if (mood <= 2 && levels > 0) perLevel *= 2;
        return applicable * perLevel;
    }

    /// <summary>URA level-3 base gains per training (uma.guide Career Mechanics), before multipliers.</summary>
    private static readonly IReadOnlyDictionary<int, (int Param, int Amount)[]> BaseTraining = new Dictionary<int, (int, int)[]>
    {
        [TrainingIds.Speed] = [(ParamIds.Speed, 13), (ParamIds.Power, 6), (ParamIds.SkillPoints, 4)],
        [TrainingIds.Stamina] = [(ParamIds.Stamina, 12), (ParamIds.Guts, 6), (ParamIds.SkillPoints, 4)],
        [TrainingIds.Power] = [(ParamIds.Stamina, 6), (ParamIds.Power, 11), (ParamIds.SkillPoints, 4)],
        [TrainingIds.Guts] = [(ParamIds.Speed, 5), (ParamIds.Power, 5), (ParamIds.Guts, 10), (ParamIds.SkillPoints, 4)],
        [TrainingIds.Wit] = [(ParamIds.Speed, 2), (ParamIds.Wit, 12), (ParamIds.SkillPoints, 5)],
    };

    /// <summary>URA level-5 base gains (every facility during summer camp), same source as <see cref="BaseTraining"/>.</summary>
    private static readonly IReadOnlyDictionary<int, (int Param, int Amount)[]> CampTraining = new Dictionary<int, (int, int)[]>
    {
        [TrainingIds.Speed] = [(ParamIds.Speed, 15), (ParamIds.Power, 8), (ParamIds.SkillPoints, 4)],
        [TrainingIds.Stamina] = [(ParamIds.Stamina, 14), (ParamIds.Guts, 8), (ParamIds.SkillPoints, 4)],
        [TrainingIds.Power] = [(ParamIds.Stamina, 8), (ParamIds.Power, 13), (ParamIds.SkillPoints, 4)],
        [TrainingIds.Guts] = [(ParamIds.Speed, 6), (ParamIds.Power, 5), (ParamIds.Guts, 13), (ParamIds.SkillPoints, 4)],
        [TrainingIds.Wit] = [(ParamIds.Speed, 4), (ParamIds.Wit, 14), (ParamIds.SkillPoints, 5)],
    };

    /// <summary>
    /// How much more the best camp training is worth than the same training now (level 5 vs a typical level 3).
    /// Resting just before camp instead of during it trades an ordinary turn for that difference.
    /// </summary>
    public double CampTrainingPremium() => BaseTraining.Keys.Max(id =>
        (CampTraining[id].Sum(g => Param(g.Param, g.Amount)) - BaseTraining[id].Sum(g => Param(g.Param, g.Amount))) * TypicalTrainingMultiplier);

    /// <summary>Mood, other cards' training effectiveness, partner count and growth on a typical training.</summary>
    public const double TypicalTrainingMultiplier = 1.4;
    /// <summary>Bond from one training with a card (without Charming).</summary>
    public const int BondPerTraining = 7;
    /// <summary>Turns between trainings with a given card when not chasing it: it is on the picked training ~1 in 4.</summary>
    public const double TurnsPerBondTraining = 4;
    /// <summary>Share of turns that are trainings (the rest are races, rests and recreation).</summary>
    public const double TrainingTurnShare = 0.7;
    /// <summary>Chance a friendship training on offer is the one picked (other friendship trainings, races, rests compete).</summary>
    public const double FriendshipPickRate = 0.6;

    /// <summary>
    /// Extra value of one friendship training with a card over the same training without it: the training's
    /// gains times the card's Friendship Bonus (training formula: gains × Π(1 + FB/100)).
    /// </summary>
    public double FriendshipTrainingExtra(int trainingId, int friendshipBonus) =>
        BaseTraining.TryGetValue(trainingId, out var gains)
            ? gains.Sum(g => Param(g.Param, g.Amount)) * TypicalTrainingMultiplier * friendshipBonus / 100.0
            : 0;

    /// <summary>
    /// Community approximation of how often a card stands on its specialty training: weight 100 + Specialty
    /// Priority there against 100 for each other training and 50 for not showing up.
    /// </summary>
    public static double SpecialtyChance(int specialtyPriority) => (100.0 + specialtyPriority) / (550 + specialtyPriority);

    /// <summary>
    /// Worth of one training's bond with a stat card below friendship. Each bond training brings the card's
    /// friendship trainings about <see cref="TurnsPerBondTraining"/> turns sooner, worth that many turns of
    /// friendship trainings — or nothing if friendship would not be reached before the career ends.
    /// </summary>
    public double BondTrainingValue(int bond, int specialtyTrainingId, SupportCardEffects effects)
    {
        if (bond >= FriendshipBond) return 0;
        int trainingsLeft = (int)Math.Ceiling((FriendshipBond - bond) / (double)BondPerTraining);
        double turnsWithFriendship = _ctx.TurnsLeft - trainingsLeft * TurnsPerBondTraining;
        double turnsGained = Math.Clamp(turnsWithFriendship, 0, TurnsPerBondTraining);
        return turnsGained * TrainingTurnShare * SpecialtyChance(effects.SpecialtyPriority) * FriendshipPickRate
            * FriendshipTrainingExtra(specialtyTrainingId, effects.FriendshipBonus);
    }

    /// <summary>What a failed training costs on top of losing its gains: stat −5, mood −1, chance of Practice Poor.</summary>
    public double FailureLoss => 5 * AverageStatWeight + Math.Abs(Mood(-1)) + 4;
}
