using BlazeUma.Data;
using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor;

/// <summary>End-of-career stat targets and how much one more point of each stat is worth right now.</summary>
/// <remarks>StaminaNote says how recovery skills lowered the Stamina requirement, or is null.</remarks>
public sealed record StatTargets(Stats Target, IReadOnlyDictionary<int, double> Weights, string? StaminaNote = null);

/// <summary>
/// Converts everything a turn can change (stats, skill points, energy, mood, bonds, conditions) into one
/// comparable score, in units of "one Speed point early in the career". All constants are tunable heuristics,
/// not game formulas; they live here so they can be adjusted in one place.
/// </summary>
public sealed class ValueModel
{
    // Relative importance of each stat before deficits are applied. Wit's covers only its own race effects
    // (positioning, rushing, mid-race speed): what it does for skills is SkillWitValue, added on top. It was 0.85
    // before that was counted; 0.6–0.4 were replayed, and 0.5 keeps the flips to Junior/early Classic Wit trainings.
    private static readonly IReadOnlyDictionary<int, double> Priority = new Dictionary<int, double>
    {
        [ParamIds.Speed] = 1.25, [ParamIds.Stamina] = 1.0, [ParamIds.Power] = 1.0, [ParamIds.Guts] = 0.6, [ParamIds.Wit] = 0.5,
    };

    public const double SkillPointValue = 0.35;
    /// <summary>A condition <see cref="ConditionValue"/> has no effect for (character and scenario ones), good or bad.</summary>
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
        EnergyOutlook = Outlook(ctx);
    }

    /// <summary>
    /// How much energy is worth against the next few turns (<see cref="Energy"/> multiplies by Factor), and the
    /// one-line reason, or null when they are ordinary training turns.
    /// </summary>
    public (double Factor, string? Reason) EnergyOutlook { get; }

    /// <summary>Turns <see cref="Outlook"/> looks ahead.</summary>
    public const int EnergyLookahead = 3;
    /// <summary>A goal race day spends no energy on training, but energy carries over it to the turns after.</summary>
    public const double RaceDayEnergyUse = 0.6;
    /// <summary>A summer camp turn is a level-5 training, ~1.25× a typical one (<see cref="CampToNormalGain"/>).</summary>
    public const double CampEnergyUse = 1.2;

    /// <summary>
    /// Energy is only worth the trainings it pays for. Each of the next <see cref="EnergyLookahead"/> turns counts
    /// 1 as an ordinary training turn, <see cref="CampEnergyUse"/> in summer camp, <see cref="RaceDayEnergyUse"/> as a
    /// goal race day and 0 past the career's end; the factor is their mean. Just before camp the camp bands in
    /// <see cref="Energy"/> already carry this, so the factor stays 1 there. Energy doesn't change race results
    /// (mood does), so a goal race ahead lowers energy's worth rather than raising it.
    /// </summary>
    private static (double Factor, string? Reason) Outlook(CareerContext ctx)
    {
        if (ctx.TurnsUntilSummerCamp is int camp and (1 or 2))
            return (1, $"summer camp in {camp}: energy worth more (camp bands)");
        double use = 0;
        int races = 0, camps = 0, past = 0;
        for (int t = ctx.Turn + 1; t <= ctx.Turn + EnergyLookahead; t++)
        {
            if (t > CareerContext.FinalTurn) past++;
            else if (ctx.IsGoalTurn(t)) { races++; use += RaceDayEnergyUse; }
            else if (ctx.IsSummerCamp(t)) { camps++; use += CampEnergyUse; }
            else use += 1;
        }
        double factor = use / EnergyLookahead;
        if (races == 0 && camps == 0 && past == 0) return (1, null);
        var why = new List<string>();
        if (camps > 0) why.Add($"{camps} camp turn{(camps > 1 ? "s" : "")}");
        if (races > 0) why.Add($"{races} goal race day{(races > 1 ? "s" : "")}");
        if (past > 0) why.Add($"career ends in {CareerContext.FinalTurn - ctx.Turn}");
        string worth = factor >= 1 ? "worth more" : "worth less";
        return (factor, $"next {EnergyLookahead} turns: {string.Join(", ", why)}: energy {worth} (×{factor:0.00})");
    }

    /// <summary>Stamina stat for a full last spurt in a race of this distance with these Speed and Guts (<see cref="RaceStamina"/>).</summary>
    public static int StaminaFor(CareerContext ctx, int distance, int speed, int guts, double recovery = 0) =>
        RaceStamina.Required(distance, ctx.Style, speed, guts, ctx.Aptitude(Distances.Categorize(distance)), recovery: recovery);

    /// <summary><see cref="StaminaFor(CareerContext, int, int, int, double)"/> less what the trainee's recovery skills restore in this race.</summary>
    public static int StaminaFor(CareerContext ctx, RaceProgram race, int speed, int guts) =>
        StaminaFor(ctx, race.Distance, speed, guts, new SkillAdvisor(ctx).RecoveryFor(race).Sum(r => r.Fraction));

    /// <summary>A stat at a future turn, assuming it moves linearly from now toward its end-of-career target.</summary>
    private static int Projected(CareerContext ctx, int now, int end, int turn) => now >= end ? now
        : now + (int)((end - now) * Math.Clamp((turn - ctx.Turn) / (double)Math.Max(1, CareerContext.FinalTurn - ctx.Turn), 0, 1));

    /// <summary>The goal race that sets the Stamina target, at the Speed and Guts projected for it; Race is null without race data.</summary>
    private sealed record StaminaNeed(int Stamina, RaceProgram? Race, int Speed, int Guts);

    /// <summary>
    /// Stamina for every remaining goal race at the Speed and Guts the trainee is projected to have by then, less
    /// what recovery skills restore there; the scenario finals (no race data) are taken as the route's longest
    /// goal race near the end of the career (2000 m with no skill credit when the route has no race data).
    /// </summary>
    private static StaminaNeed StaminaTarget(CareerContext ctx, int speedTarget, int gutsTarget)
    {
        var c = ctx.Character;
        var races = ctx.RemainingGoals.Where(g => g.Race is not null).Select(g => (g.Race, g.Turn)).ToList();
        if (ctx.RemainingGoals.Any(g => g.Race is null))
            races.Add((ctx.Goals.Select(g => g.Race).OfType<RaceProgram>().MaxBy(r => r.Distance), CareerContext.FinalTurn));
        return races.Select(r =>
            {
                int speed = Projected(ctx, c.Stats.Speed, speedTarget, r.Turn), guts = Projected(ctx, c.Stats.Guts, gutsTarget, r.Turn);
                return new StaminaNeed(r.Race is null ? StaminaFor(ctx, 2000, speed, guts) : StaminaFor(ctx, r.Race, speed, guts),
                    r.Race, speed, guts);
            })
            .DefaultIfEmpty(new StaminaNeed(0, null, 0, 0)).MaxBy(r => r.Stamina)!;
    }

    /// <summary>
    /// "Stamina need 893 for Arima Kinen (−180 Swinging Maestro, owned; −60 Rosy Outlook, hinted ×50%)": how far
    /// recovery skills lowered the requirement of the race that sets the Stamina target. A skill's figure is what
    /// the requirement would rise by without it; null when no recovery skill counts there.
    /// </summary>
    private static string? RecoveryNote(CareerContext ctx, StaminaNeed need)
    {
        if (need.Race is not { } race) return null;
        var recoveries = new SkillAdvisor(ctx).RecoveryFor(race);
        if (recoveries.Count == 0) return null;
        double total = recoveries.Sum(r => r.Fraction);
        int Without(double recovery) => StaminaFor(ctx, race.Distance, need.Speed, need.Guts, recovery);
        var parts = recoveries.Select(r => $"−{Without(total - r.Fraction) - need.Stamina} {r.Skill.Name}, " +
            (r.Owned ? "owned" : $"hinted ×{SkillAdvisor.HintedRecoveryCredit * 100:0}%"));
        string all = recoveries.Count > 1 ? $"−{Without(0) - need.Stamina} in all: " : "";
        return $"Stamina need {need.Stamina} for {race.Name} ({all}{string.Join("; ", parts)})";
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
    /// Wit at a future turn: today's plus Wit's <see cref="Shares"/> of the stat gain still to come
    /// (<see cref="ProjectedStatTotal"/>), spread evenly over the remaining turns, at most the cap. It doesn't
    /// depend on the stat targets, so skill values (and the Stamina target through recovery skills) can use it.
    /// </summary>
    public static int WitAt(CareerContext ctx, int turn)
    {
        var c = ctx.Character;
        var shares = Shares(ctx);
        double gain = Math.Max(0, ProjectedStatTotal(ctx) - Total(c.Stats)) * shares[ParamIds.Wit] / shares.Values.Sum();
        double elapsed = Math.Clamp((turn - ctx.Turn) / (double)Math.Max(1, CareerContext.FinalTurn - ctx.Turn), 0, 1);
        return Math.Min(c.MaxStats.Wit, c.Stats.Wit + (int)(gain * elapsed));
    }

    /// <summary>
    /// Skill points a turn brings from trainings, races and events (what Fast Learner's discount stretches and
    /// skills still to buy are paid with): the one full logged career (Taiki Shuttle, turns 1–77) gained 20.4 a turn.
    /// </summary>
    public const double SkillPointsPerTurn = 20;
    /// <summary>Share of skill points spent on skills that roll against Wit: white and gold active skills, not greens or uniques.</summary>
    public const double WitCheckedSkillShare = 0.8;
    /// <summary>Typical fit of a skill still to be bought (<see cref="SkillAdvisor.Plan"/> buys fit ≥ 0.5).</summary>
    public const double BoughtSkillFit = 0.8;

    /// <summary>
    /// What one more Wit point is worth through skills. A skill that rolls against Wit fires with chance
    /// 1 − 90/Wit (at least 20%), so a point adds 90/Wit² of it, at the Wit expected at each remaining goal race.
    /// Skills are valued at their skill points × <see cref="SkillPointValue"/>: owned ones by base cost and fit, and
    /// the points on hand and still to come (<see cref="SkillPointsPerTurn"/>), <see cref="WitCheckedSkillShare"/>
    /// of them at <see cref="BoughtSkillFit"/>. Nothing once no goal race is left.
    /// </summary>
    public static double SkillWitValue(CareerContext ctx)
    {
        if (ctx.RemainingGoals.Count == 0) return 0;
        var c = ctx.Character;
        var skills = new SkillAdvisor(ctx);
        double owned = c.Skills.Select(s => ctx.Data.Skill(s.SkillId)).OfType<SkillInfo>().Where(s => s.WitCheck)
            .Sum(s => s.BaseCost * skills.Fit(s.Condition));
        double toBuy = (c.SkillPoints + ctx.TurnsLeft * SkillPointsPerTurn) * WitCheckedSkillShare * BoughtSkillFit;
        double perPoint = ctx.RemainingGoals.Average(g =>
        {
            int wit = WitAt(ctx, g.Turn);
            return SkillAdvisor.ActivationChance(wit) is > 0.2 and < 1 ? 90.0 / ((double)wit * wit) : 0;
        });
        return (owned + toBuy) * SkillPointValue * perPoint;
    }

    /// <summary>
    /// Shares of the projected total besides Stamina, per goal distance (uma.guide stats page: Stamina ≈ Speed to
    /// the cap, then Power ~800–1000 ≈ Wit, Guts last). Power is acceleration and position fighting, which decide
    /// short races with short spurts, so sprint and mile take more of it; Guts lowers late-race HP use and wins
    /// long duels, so long routes take a little more. Wit covers the rest: only its racing role, since what it does
    /// for skills is <see cref="SkillWitValue"/> (0.04 moved from Wit to Power when that was added; Power ended
    /// career 106802 at 524). Stamina gets <see cref="StaminaShareOfSpeed"/> of Speed's share.
    /// </summary>
    private static (double Speed, double Power, double Guts, double Wit) DistanceShares(DistanceCategory d) => d switch
    {
        DistanceCategory.Sprint => (0.42, 0.34, 0.08, 0.16),
        DistanceCategory.Mile => (0.42, 0.32, 0.10, 0.16),
        DistanceCategory.Long => (0.42, 0.26, 0.15, 0.17),
        _ => (0.42, 0.29, 0.11, 0.18),
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
        s[ParamIds.Stamina] = s[ParamIds.Speed] * StaminaShareOfSpeed;
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
    /// Stamina's share of the stat budget against Speed's. uma.guide has Stamina ≈ Speed; the logged wins at
    /// 2000–2500 m ran it at 0.6–0.7 of Speed (Japan Cup 584/820, Takarazuka Kinen 458/761, Tenno Sho (Autumn)
    /// 546/817), and Stamina at Speed's share left Power ~16% of the budget (500 at the end of career 106802).
    /// </summary>
    public const double StaminaShareOfSpeed = 0.75;

    /// <summary>
    /// Share of a full last spurt's Stamina (<see cref="RaceStamina.Required"/>) the target asks for. In the logged
    /// careers every goal or G1 race run at most 40% short of a full spurt was won (Takarazuka Kinen, Japan Cup,
    /// Arima Kinen…); 57–80% short went anywhere from 1st to 8th (Oaks, Kikuka Sho, Queen Elizabeth II Cup). Asking
    /// for a full spurt (~1,600 for 3,000 m) kept Stamina urgent all career and starved Power (career 106802).
    /// </summary>
    public const double StaminaNeedShare = 0.65;

    /// <summary>
    /// Targets follow uma.guide's stat priority for the 1400-cap URA (July patch), scaled to what this career can
    /// reach (<see cref="ProjectedStatTotal"/>) and split by <see cref="Shares"/>, each stat at most its cap:
    /// Stamina as much as Speed up to <see cref="StaminaNeedShare"/> of a full last spurt in every remaining goal
    /// race, Power ~ Wit, Guts last. The stamina requirement depends on the split's Speed and Guts, so the split is
    /// run three times.
    /// </summary>
    private static StatTargets PlanTargets(CareerContext ctx)
    {
        var c = ctx.Character;
        int total = ProjectedStatTotal(ctx);
        var target = Split(ctx, total, c.MaxStats.Stamina);
        StaminaNeed need = new(c.MaxStats.Stamina, null, 0, 0);
        for (int pass = 0; pass < 2; pass++)
        {
            need = StaminaTarget(ctx, target.Speed, target.Guts);
            target = Split(ctx, total, Math.Min((int)(need.Stamina * StaminaNeedShare), c.MaxStats.Stamina));
        }

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
        // Wit also decides how often skills fire, whatever its share of the stat target.
        if (c.Stats.Wit < c.MaxStats.Wit) weights[ParamIds.Wit] += SkillWitValue(ctx);

        // A goal race soon whose required place is at risk today (the race model, below GoalAtRisk) and that the
        // trainee is short of Stamina for: stamina becomes urgent (once, however many goals). Comparing with the full
        // requirement instead kept it urgent all career on a route with 3000 m goals that no budget reaches.
        if (ctx.RemainingGoals.Any(g => g.Race is not null && g.Turn - ctx.Turn <= 12
                && c.Stats.Stamina < StaminaFor(ctx, g.Race, c.Stats.Speed, c.Stats.Guts) * StaminaNeedShare
                && GoalChance(ctx, g) < GoalAtRisk))
            weights[ParamIds.Stamina] *= 1.5;
        return new StatTargets(target, weights, RecoveryNote(ctx, need));
    }

    /// <summary>Chance of meeting a goal race's required place below which its Stamina shortfall is urgent.</summary>
    public const double GoalAtRisk = 0.5;

    /// <summary>The race model's chance of the goal's required place at today's stats (1st, top 3, top 5, or any place).</summary>
    private static double GoalChance(CareerContext ctx, GoalRace goal)
    {
        var p = RaceEvaluator.PlacingFor(ctx, goal.Race!);
        return goal.RequiredPlace switch { 1 => p.Win, <= 3 and > 0 => p.Top3, <= 5 and > 0 => p.Top5, _ => 1 };
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
    /// The result is scaled by <see cref="EnergyOutlook"/>: race days and the career's end ahead make it worth less.
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
        value *= EnergyOutlook.Factor;
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

    /// <summary>URA base gains per training at facility levels 1–5 (uma.guide Career Mechanics), before multipliers.</summary>
    private static readonly IReadOnlyDictionary<int, (int Param, int Amount)[][]> UraTraining = new Dictionary<int, (int, int)[][]>
    {
        [TrainingIds.Speed] = Levels(ParamIds.Speed, [11, 12, 13, 14, 15], ParamIds.Power, [6, 6, 6, 7, 8], 4),
        [TrainingIds.Stamina] = Levels(ParamIds.Stamina, [10, 11, 12, 13, 14], ParamIds.Guts, [6, 6, 6, 7, 8], 4),
        [TrainingIds.Power] = Levels(ParamIds.Power, [9, 10, 11, 12, 13], ParamIds.Stamina, [6, 6, 6, 7, 8], 4),
        [TrainingIds.Guts] = [.. Levels(ParamIds.Guts, [8, 9, 10, 11, 13], ParamIds.Speed, [5, 5, 5, 6, 6], 4)
            .Select(level => level.Append((ParamIds.Power, 5)).ToArray())],
        [TrainingIds.Wit] = Levels(ParamIds.Wit, [10, 11, 12, 13, 14], ParamIds.Speed, [2, 2, 2, 3, 4], 5),
    };

    /// <summary>Energy per training at facility levels 1–5, same source.</summary>
    private static readonly IReadOnlyDictionary<int, int[]> UraTrainingEnergy = new Dictionary<int, int[]>
    {
        [TrainingIds.Speed] = [-21, -22, -23, -25, -27], [TrainingIds.Stamina] = [-19, -20, -21, -23, -25],
        [TrainingIds.Power] = [-20, -21, -22, -24, -26], [TrainingIds.Guts] = [-22, -23, -24, -26, -28],
        [TrainingIds.Wit] = [5, 5, 5, 5, 5],
    };

    private static (int, int)[][] Levels(int main, int[] mainGains, int second, int[] secondGains, int skillPoints) =>
        [.. Enumerable.Range(0, 5).Select(i => new[] { (main, mainGains[i]), (second, secondGains[i]), (ParamIds.SkillPoints, skillPoints) })];

    /// <summary>Level-3 base gains, a typical facility outside camp.</summary>
    private static readonly IReadOnlyDictionary<int, (int Param, int Amount)[]> BaseTraining = UraTraining.ToDictionary(t => t.Key, t => t.Value[2]);

    /// <summary>Level-5 base gains: every facility during summer camp.</summary>
    private static readonly IReadOnlyDictionary<int, (int Param, int Amount)[]> CampTraining = UraTraining.ToDictionary(t => t.Key, t => t.Value[4]);

    /// <summary>
    /// How much more the best camp training is worth than the same training now (level 5 vs a typical level 3).
    /// Resting just before camp instead of during it trades an ordinary turn for that difference.
    /// </summary>
    public double CampTrainingPremium() => BaseTraining.Keys.Max(id =>
        (Trained(CampTraining[id]) - Trained(BaseTraining[id])) * TypicalTrainingMultiplier);

    /// <summary>Worth of a training's base gains with the trainee's growth bonuses (training formula: gains × (1 + growth)).</summary>
    private double Trained((int Param, int Amount)[] gains) => gains.Sum(g => Param(g.Param, g.Amount * _ctx.Growth(g.Param)));

    /// <summary>Mood, other cards' training effectiveness and partner count on a typical training.</summary>
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
            ? Trained(gains) * TypicalTrainingMultiplier * friendshipBonus / 100.0
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

    /// <summary>A training facility's level (1–5) and trainings done toward the next one (0–3; 4 trainings a level).</summary>
    public sealed record Facility(int Level, int Progress);

    /// <summary>Stats a trainee starts with before training (base stats plus inheritance; ~80–130 in the logs' careers).</summary>
    public const int StartStat = 100;
    /// <summary>Least share of future trainings a facility is assumed to get: the trainings on offer change every turn.</summary>
    public const double MinFacilityShare = 0.15;

    /// <summary>Turns from <paramref name="from"/> to <paramref name="to"/> that can be ordinary trainings (no summer camp, no goal race).</summary>
    private int OrdinaryTurns(int from, int to) =>
        Enumerable.Range(from, Math.Max(0, to - from + 1)).Count(t => !_ctx.IsSummerCamp(t) && !_ctx.IsGoalTurn(t));

    /// <summary>
    /// Share of the trainings so far done on a facility, taken from its main stat's growth since the start
    /// against all five stats' growth (secondary gains blur it, so it is an estimate).
    /// </summary>
    private double FacilityShare(int trainingId)
    {
        var s = _ctx.Character.Stats;
        double Grown(int stat) => Math.Max(0, Get(s, stat) - StartStat);
        double all = new[] { ParamIds.Speed, ParamIds.Stamina, ParamIds.Power, ParamIds.Guts, ParamIds.Wit }.Sum(Grown);
        return all > 0 ? Grown(TurnAdvisor.TrainingStat(trainingId)) / all : 0.2;
    }

    /// <summary>
    /// Estimated facility level. The snapshot doesn't read it (WorkSingleModeCharaData._trainingLevelDic is
    /// unmapped), and the shown gains can't give it: a level is one base point (~9%), less than what support
    /// cards, partners and friendship change between trainings. So: trainings so far (<see cref="TrainingTurnShare"/>
    /// of the ordinary turns before this one) × the facility's <see cref="FacilityShare"/>. Camp trainings are
    /// assumed not to count toward levels.
    /// </summary>
    public Facility EstimatedFacility(int trainingId)
    {
        int done = (int)(TrainingTurnShare * OrdinaryTurns(1, _ctx.Turn - 1) * FacilityShare(trainingId));
        return done >= 16 ? new Facility(5, 0) : new Facility(1 + done / 4, done % 4);
    }

    /// <summary>
    /// What one training on a facility adds by bringing its level-ups forward. Each later level-up then comes one
    /// training sooner, so one future training there runs a level higher: worth that level's extra base gains
    /// (less its extra energy) × <see cref="TypicalTrainingMultiplier"/>, for every level-up still reached among
    /// the facility's future ordinary trainings (camp is level 5 anyway). Many trainings ahead reach several
    /// level-ups, so early concentration pays; late in the career few or none are reached.
    /// </summary>
    public (double Value, string? Reason) FacilityProgress(int trainingId)
    {
        int id = TrainingIds.Base(trainingId);
        if (_ctx.InSummerCamp || !UraTraining.TryGetValue(id, out var levels)) return (0, null);
        var facility = EstimatedFacility(id);
        double future = TrainingTurnShare * OrdinaryTurns(_ctx.Turn + 1, CareerContext.FinalTurn)
            * Math.Max(FacilityShare(id), MinFacilityShare);
        double value = 0;
        int ups = 0;
        // After this training, 3 − Progress more finish the level; the next one (0-based index 3 − Progress) is the first at the new level.
        for (int level = facility.Level, at = 3 - facility.Progress; level < 5; level++, at += 4)
        {
            double reached = Math.Clamp(future - at, 0, 1);
            if (reached <= 0) break;
            double step = Trained(levels[level]) - Trained(levels[level - 1]);
            value += reached * (step * TypicalTrainingMultiplier + Energy(UraTrainingEnergy[id][level] - UraTrainingEnergy[id][level - 1]));
            ups++;
        }
        if (ups == 0) return (0, null);
        return (value, $"facility ~Lv{facility.Level} {facility.Progress}/4 (est.): {ups} level-up{(ups > 1 ? "s" : "")} a training sooner ({value:+0.0;-0.0})");
    }

    /// <summary>What a failed training costs on top of losing its gains: stat −5, mood −1, chance of Practice Poor.</summary>
    public double FailureLoss => 5 * AverageStatWeight + Math.Abs(Mood(-1)) + 4;

    /// <summary>
    /// Turns a bad condition lasts before the trainee would next rest anyway and can take the infirmary instead
    /// (estimate; the logged careers rest about one turn in six).
    /// </summary>
    public const int CureWaitTurns = 3;
    /// <summary>Slow Metabolism also goes away by chance on a stat training (uma.guide), so it lasts less (estimate).</summary>
    public const int SlowMetabolismTurns = 3;
    /// <summary>The infirmary cures ~85% of the time (GameWith Global sample; Practice Poor and Night Owl alike).</summary>
    public const double InfirmaryCureChance = 0.85;
    /// <summary>Energy from an average rest (<see cref="TurnAdvisor"/>'s rest outcomes) and from the infirmary.</summary>
    private const int RestEnergy = 50, InfirmaryEnergy = 20;
    /// <summary>
    /// Share of trainings whose failure chance is above 0. The game shows 0% at high energy and a lower chance
    /// can't go below it, so Practice Perfect and the like only help on these; Practice Poor's +2% shows even at
    /// full energy (estimate).
    /// </summary>
    public const double RiskyTrainingShare = 0.3;
    /// <summary>Night Owl and Skin Outbreak fire "by chance" each turn; no source gives the rate (estimate).</summary>
    public const double ConditionTriggerChance = 0.1;
    /// <summary>Mood levels a turn gains from events and outings, which Migraine blocks (estimate).</summary>
    public const double MoodGainsPerTurn = 0.1;
    /// <summary>Chance Slacker skips a training; the game says only "may not show up to training" (estimate).</summary>
    public const double SlackerSkipChance = 0.1;
    /// <summary>Share of a typical training's gains that is Speed: the Speed training's main stat, Guts' and Wit's second.</summary>
    public const double SpeedShareOfTraining = 0.3;
    /// <summary>Partners on a typical training, each getting Charming's extra bond.</summary>
    public const double PartnersPerTraining = 2;

    /// <summary>A typical training's stats, skill points and bonds: what a failure or a skipped training loses.</summary>
    private double TrainingValue(int turn) => TrainingStatGain(_ctx) * AverageStatWeight + 5 * SkillPointValue
        + PartnersPerTraining * BondValuePerPartner * Math.Clamp(1 - turn / 48.0, 0.2, 1);

    /// <summary>Conditions with an effect modelled in <see cref="ConditionTurnValue"/>, and whether each is bad.</summary>
    private static readonly IReadOnlyDictionary<int, bool> ModelledConditions = new Dictionary<int, bool>
    {
        [ConditionIds.NightOwl] = true, [ConditionIds.Slacker] = true, [ConditionIds.SkinOutbreak] = true,
        [ConditionIds.SlowMetabolism] = true, [ConditionIds.Migraine] = true, [ConditionIds.PracticePoor] = true,
        [ConditionIds.UnderTheWeather] = true,
        [ConditionIds.FastLearner] = false, [ConditionIds.Charming] = false, [ConditionIds.HotTopic] = false,
        [ConditionIds.PracticePerfect] = false, [ConditionIds.PracticePerfectGreat] = false, [ConditionIds.ShiningBrightly] = false,
    };

    public bool IsBadCondition(int conditionId) =>
        ModelledConditions.TryGetValue(conditionId, out bool bad) ? bad : _ctx.Data.Condition(conditionId) is { IsBad: true };

    /// <summary>The trainee's bad conditions, the ones the infirmary and Rest &amp; Recreation cure.</summary>
    public IReadOnlyList<int> BadConditions => [.. _ctx.Character.Conditions.Where(IsBadCondition)];

    /// <summary>
    /// What curing a bad condition at the infirmary costs: a turn the trainee would rest anyway gives
    /// <see cref="InfirmaryEnergy"/> instead of <see cref="RestEnergy"/> (from 30 energy), retried until it works.
    /// </summary>
    private double InfirmaryVisitCost => Energy(RestEnergy - InfirmaryEnergy, from: 30) / InfirmaryCureChance;

    /// <summary>
    /// Value of having a condition from the next turn on: below 0 for a bad one, above 0 for a good one, summed
    /// from its effect on each turn (<see cref="ConditionTurnValue"/>). A bad one lasts until summer camp's
    /// Rest &amp; Recreation cures it or the career ends (uma.guide: general bad conditions don't wear off), unless
    /// curing it costs less: <see cref="CureWaitTurns"/> turns of it and an <see cref="InfirmaryVisitCost"/>. So
    /// it costs little late in the career or just before camp. Slow Metabolism lasts at most
    /// <see cref="SlowMetabolismTurns"/>. A good one lasts to the end (umamusu.wiki Game:Conditions: good
    /// conditions are not removed). Ids without an effect here (character and scenario conditions) are worth
    /// ±<see cref="BadConditionValue"/>.
    /// </summary>
    public double ConditionValue(int conditionId)
    {
        if (!ModelledConditions.TryGetValue(conditionId, out bool bad))
            return IsBadCondition(conditionId) ? -BadConditionValue : BadConditionValue;
        int lasts = conditionId == ConditionIds.SlowMetabolism ? SlowMetabolismTurns : CareerContext.FinalTurn;
        double value = conditionId == ConditionIds.FastLearner ? 0.1 * _ctx.Character.SkillPoints * SkillPointValue : 0;
        double waiting = 0;
        for (int t = _ctx.Turn + 1; t <= Math.Min(CareerContext.FinalTurn, _ctx.Turn + lasts); t++)
        {
            value += ConditionTurnValue(conditionId, t);
            if (t == _ctx.Turn + CureWaitTurns) waiting = value;
            if (bad && _ctx.IsSummerCamp(t)) break;
        }
        return bad && conditionId != ConditionIds.SlowMetabolism ? Math.Max(value, waiting - InfirmaryVisitCost) : value;
    }

    /// <summary>Worth of curing a condition the trainee has: what it would cost if left (0 for a good one).</summary>
    public double CureValue(int conditionId) => IsBadCondition(conditionId) ? Math.Max(0, -ConditionValue(conditionId)) : 0;

    /// <summary>
    /// One turn of a condition. Effects are the game's own lines (text 143) with uma.guide's Career Mechanics and
    /// umamusu.wiki's Game:Conditions for the numbers: Night Owl −10 energy or −1 mood by chance; Skin Outbreak
    /// −1 mood by chance; Migraine no mood gains; Slow Metabolism no Speed from training; Slacker may skip a
    /// training; Practice Poor +2% and Under the Weather +5% failure; Practice Perfect ○ −2%, ◎ −4% and Shining
    /// Brightly −5% failure (on <see cref="RiskyTrainingShare"/> of trainings); Charming +2 bond with each partner a
    /// training; Hot Topic +40% bond with Akikawa and Etsuko; Fast Learner skills 10% cheaper. A goal race turn
    /// has no training.
    /// </summary>
    private double ConditionTurnValue(int conditionId, int turn)
    {
        double trainings = _ctx.IsGoalTurn(turn) ? 0 : TrainingTurnShare;
        double Failure(double rate) => trainings * rate * (TrainingValue(turn) + FailureLoss) * (rate > 0 ? 1 : -RiskyTrainingShare);
        double bondDecay = Math.Clamp(1 - turn / 48.0, 0.2, 1);
        return conditionId switch
        {
            ConditionIds.NightOwl => -ConditionTriggerChance * (0.5 * Math.Abs(Energy(-10)) + 0.5 * Math.Abs(Mood(-1))),
            ConditionIds.SkinOutbreak => -ConditionTriggerChance * Math.Abs(Mood(-1)),
            ConditionIds.Migraine => -MoodGainsPerTurn * Mood(1),
            ConditionIds.SlowMetabolism => -trainings * TrainingStatGain(_ctx) * SpeedShareOfTraining * Weight(ParamIds.Speed),
            ConditionIds.Slacker => -trainings * SlackerSkipChance * TrainingValue(turn),
            ConditionIds.PracticePoor => -Failure(0.02),
            ConditionIds.UnderTheWeather => -Failure(0.05),
            ConditionIds.PracticePerfect => Failure(-0.02),
            ConditionIds.PracticePerfectGreat => Failure(-0.04),
            ConditionIds.ShiningBrightly => Failure(-0.05),
            ConditionIds.Charming => trainings * PartnersPerTraining * 2.0 / BondPerTraining * BondValuePerPartner * bondDecay,
            // Akikawa or Etsuko is on ~1 training in 4; their bonds count half a card's (TurnAdvisor).
            ConditionIds.HotTopic => trainings * 0.25 * 0.4 * BondValuePerPartner * 0.5 * bondDecay,
            ConditionIds.FastLearner => 0.1 * SkillPointsPerTurn * SkillPointValue,
            _ => 0,
        };
    }
}

/// <summary>Trainee conditions (single_mode_chara_effect; names are text 142, the game's effect lines text 143).</summary>
public static class ConditionIds
{
    public const int NightOwl = 1, Slacker = 2, SkinOutbreak = 3, SlowMetabolism = 4, Migraine = 5, PracticePoor = 6,
        FastLearner = 7, Charming = 8, HotTopic = 9, PracticePerfect = 10, PracticePerfectGreat = 11,
        UnderTheWeather = 12, ShiningBrightly = 13;
}
