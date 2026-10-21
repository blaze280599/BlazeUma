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

    /// <summary>Stamina a race distance needs by the end of the career (≈ 480 at 1600 m, 780 at 2500 m).</summary>
    public static int StaminaFor(int distance) => (int)Math.Clamp(350 + (distance - 1200) * 0.33, 300, 1100);

    private static StatTargets PlanTargets(CareerContext ctx)
    {
        var c = ctx.Character;
        bool front = ctx.Style is RunningStyle.FrontRunner or RunningStyle.PaceChaser;
        var target = new Stats(
            Speed: Math.Min(1100, c.MaxStats.Speed),
            Stamina: Math.Min(StaminaFor(ctx.LongestRemainingDistance), c.MaxStats.Stamina),
            Power: Math.Min(front ? 750 : 800, c.MaxStats.Power),
            Guts: Math.Min(350, c.MaxStats.Guts),
            Wit: Math.Min(front ? 650 : 600, c.MaxStats.Wit));

        var weights = new Dictionary<int, double>();
        foreach (int stat in new[] { ParamIds.Speed, ParamIds.Stamina, ParamIds.Power, ParamIds.Guts, ParamIds.Wit })
        {
            int current = Get(c.Stats, stat), goal = Get(target, stat), cap = Get(c.MaxStats, stat);
            double weight = current >= cap ? 0
                : current >= goal ? Priority[stat] * 0.15
                : Priority[stat] * (0.25 + 0.75 * (goal - current) / (double)goal);
            weights[stat] = weight;
        }

        // A goal race soon that the trainee lacks stamina for: stamina becomes urgent (once, however many goals).
        double yearShare = ctx.Date.Year switch { 1 => 0.5, 2 => 0.75, _ => 1.0 };
        if (ctx.RemainingGoals.Any(g => g.Race is not null && g.Turn - ctx.Turn <= 12
                && c.Stats.Stamina < StaminaFor(g.Race.Distance) * yearShare))
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
    /// lost turns), and gains above max energy are wasted.
    /// </summary>
    public double Energy(double delta, double? from = null)
    {
        double start = from ?? _ctx.Character.Energy;
        double end = Math.Clamp(start + delta, 0, _ctx.Character.MaxEnergy);
        double lo = Math.Min(start, end), hi = Math.Max(start, end), value = 0;
        (double Upto, double Marginal)[] bands = [(30, 0.9), (50, 0.6), (70, 0.35), (double.MaxValue, 0.15)];
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

    /// <summary>What a failed training costs on top of losing its gains: stat −5, mood −1, chance of Practice Poor.</summary>
    public double FailureLoss => 5 * AverageStatWeight + Math.Abs(Mood(-1)) + 4;
}
