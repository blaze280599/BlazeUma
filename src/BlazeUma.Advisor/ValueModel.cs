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
    public const double ExpectedRestEnergy = 50;
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
    /// Targets follow uma.guide's stat priority for the 1400-cap URA (July patch): Speed capped, Stamina for a
    /// full last spurt in every remaining goal race, Power ~800–1000 (closers need more acceleration), Wit as high
    /// as possible, Guts optional.
    /// </summary>
    private static StatTargets PlanTargets(CareerContext ctx)
    {
        var c = ctx.Character;
        bool front = ctx.Style is RunningStyle.FrontRunner or RunningStyle.PaceChaser;
        int guts = Math.Min(350, c.MaxStats.Guts);
        var target = new Stats(
            Speed: c.MaxStats.Speed,
            Stamina: Math.Min(StaminaTarget(ctx, c.MaxStats.Speed, guts), c.MaxStats.Stamina),
            Power: Math.Min(front ? 900 : 1000, c.MaxStats.Power),
            Guts: guts,
            Wit: Math.Min(1000, c.MaxStats.Wit));

        var weights = new Dictionary<int, double>();
        foreach (int stat in new[] { ParamIds.Speed, ParamIds.Stamina, ParamIds.Power, ParamIds.Guts, ParamIds.Wit })
        {
            int current = Get(c.Stats, stat), goal = Get(target, stat), cap = Get(c.MaxStats, stat);
            double weight = current >= cap ? 0
                : current >= goal ? Priority[stat] * 0.15
                : Priority[stat] * (0.25 + 0.75 * (goal - current) / (double)goal);
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
