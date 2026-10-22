using BlazeUma.Data;
using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor;

/// <summary>Scores entering an optional race this turn instead of training.</summary>
public sealed class RaceEvaluator(CareerContext ctx, ValueModel values)
{
    /// <summary>Below B aptitude the trainee is unlikely to place well.</summary>
    public const int MinAptitude = 6;
    private const double RaceEnergyCost = -15;

    /// <summary>
    /// Chance of the post-race penalty event for the Nth race in a row (index = N; below 3 there is none).
    /// From the 3rd race in a row the trainee may get an event that lowers mood and stats or adds Skin Outbreak
    /// (uma.guide career mechanics, "Skin Outbreak: random chance from consecutive races (3 or more)";
    /// GameWith Global, "4 or more consecutive races will also cause the same disadvantages"). No source gives
    /// the rate; these are estimates that rise with the streak, to be replaced once logs measure them.
    /// </summary>
    private static readonly double[] StreakEventChance = [0, 0, 0, 0.3, 0.5, 0.7];

    /// <summary>
    /// The penalty event takes 10 from three random stats (GameWith Global) and lowers mood or adds Skin Outbreak
    /// (a chance of −1 mood each turn until cured); counted here as half a chance of each.
    /// </summary>
    private const int StreakStatLoss = 3 * 10;

    public ScoredOption Evaluate(RaceProgram race)
    {
        var reasons = new List<string>();
        int distanceApt = ctx.Aptitude(race.Category), groundApt = ctx.Aptitude(race.Ground);
        reasons.Add($"{race.GradeName} {race.Distance}m {race.Ground} ({race.Category} {CareerContext.Grade(distanceApt)}, {race.Ground} {CareerContext.Grade(groundApt)})");
        if (distanceApt < MinAptitude || groundApt < MinAptitude)
        {
            reasons.Add("aptitude too low to place");
            return new ScoredOption(race.Name, -50, reasons);
        }

        var placing = PlacingFor(ctx, race);
        reasons.Add(placing.ToString());
        if (ctx.Aptitude(ctx.BestStyle) > ctx.Aptitude(ctx.Style))
            reasons.Add($"set to {CareerContext.StyleName(ctx.Style)} ({CareerContext.Grade(ctx.Aptitude(ctx.Style))}); " +
                $"{CareerContext.StyleName(ctx.BestStyle)} ({CareerContext.Grade(ctx.Aptitude(ctx.BestStyle))}) would race better");
        if (placing.StaminaNeeded > ctx.Character.Stats.Stamina)
            reasons.Add($"stamina {ctx.Character.Stats.Stamina} of ~{placing.StaminaNeeded} for a full spurt at {race.Distance}m");
        if (ctx.Character.Mood < Mood.Normal)
            reasons.Add($"mood {ctx.Character.Mood} lowers race stats (x{MoodRate(ctx.Character.Mood):0.00})");
        double expectedFans = race.FirstPlaceFans * placing.FanShare;
        double score = 0;

        var fanNeed = ctx.FanShortfall();
        int fromGoals = fanNeed is var (needGoal, _) ? ctx.ExpectedGoalFansBefore(needGoal.Turn) : 0;
        if (fanNeed is var (covered, need) && fromGoals >= need)
        {
            score += expectedFans / 1000;
            reasons.Add($"{need} fans for {covered.Name} should come from earlier goal races (~+{fromGoals}) (+{expectedFans / 1000:0.0})");
        }
        else if (fanNeed is var (goal, shortfall))
        {
            // Missing a goal's fan requirement fails the goal and ends the career, so the fans must be won
            // before the deadline. With few race turns left, race early enough to have a retry.
            int chances = SuitableTurnsBefore(goal.Turn);
            double urgency = chances switch
            {
                <= 2 => 1.5,   // this turn plus at most two retries
                <= 5 => 0.8,
                _ => Math.Clamp(2.0 / (chances + 1), 0.25, 1),
            };
            double covers = Math.Min(1, expectedFans / shortfall);
            double fanScore = 30 * covers * urgency;
            score += fanScore;
            reasons.Add($"needs {shortfall} more fans for {goal.Name}; expected +{expectedFans:0}, " +
                $"{chances} other suitable race turn(s) before it{(chances <= 2 ? " — deadline close" : "")} (+{fanScore:0.0})");
        }
        else
        {
            score += expectedFans / 1000;
            reasons.Add($"fans not needed for goals (+{expectedFans / 1000:0.0})");
        }

        (double sp, double stats) = placing.ExpectedRewards(race.Grade);
        double rewards = sp * ValueModel.SkillPointValue + stats * values.AverageStatWeight;
        score += rewards;
        reasons.Add($"skill points and stats: ~{sp:0} SP, ~{stats:0} stats expected (+{rewards:0.0})");

        double energy = values.Energy(RaceEnergyCost);
        score += energy;
        reasons.Add($"energy {RaceEnergyCost} ({energy:0.0})");

        if (StreakPenalty(out string streakReason) is var streak and > 0)
        {
            score -= streak;
            reasons.Add(streakReason);
        }

        if (ctx.NextGoal is { } next && next.Turn - ctx.Turn is > 0 and <= 2)
        {
            score -= 8;
            reasons.Add($"{next.Name} is in {next.Turn - ctx.Turn} turn(s); avoid racing back to back (-8)");
        }
        return new ScoredOption(race.Name, score, reasons);
    }

    /// <summary>
    /// Races run in a row right before this turn. Goal turns are always raced; other turns count when
    /// <see cref="BlazeUma.Memory.Game.CareerSnapshot.RaceTurns"/> lists them (without a turn log only goal races
    /// are known).
    /// </summary>
    public static int StreakBefore(CareerContext ctx)
    {
        var raced = ctx.Snapshot.RaceTurns;
        int streak = 0;
        for (int t = ctx.Turn - 1; t >= 1 && (ctx.IsGoalTurn(t) || raced?.Contains(t) == true); t--) streak++;
        return streak;
    }

    /// <summary>
    /// Expected loss from the consecutive-race event if this race is the 3rd or later in a row. A goal race
    /// as the 3rd or later race does not trigger it (GameWith Global: "if the 3rd race or later one is a Goal
    /// Race, no disadvantages will occur"), so a goal right after this race adds no cost of its own; it still
    /// lengthens the streak for any race after it, which that turn's evaluation counts.
    /// </summary>
    private double StreakPenalty(out string reason)
    {
        int nth = StreakBefore(ctx) + 1;
        reason = "";
        if (nth < 3) return 0;
        double chance = StreakEventChance[Math.Min(nth, StreakEventChance.Length - 1)];
        double loss = StreakStatLoss * values.AverageStatWeight
            + 0.5 * Math.Abs(values.Mood(-1)) + 0.5 * -values.ConditionValue(ConditionIds.SkinOutbreak);
        double penalty = chance * loss;
        reason = $"race {nth} in a row: {chance:P0} chance of an event taking -10 from 3 stats and mood or Skin Outbreak (-{penalty:0.0})";
        return penalty;
    }

    private int? _suitableTurns;

    /// <summary>
    /// Later turns before <paramref name="goalTurn"/> with an enterable race at B+ aptitude. Goal turns are
    /// excluded: the goal race takes the turn.
    /// </summary>
    private int SuitableTurnsBefore(int goalTurn) => _suitableTurns ??= Enumerable.Range(ctx.Turn + 1, Math.Max(0, goalTurn - ctx.Turn - 1))
        .Where(t => !ctx.IsGoalTurn(t))
        .Count(t => ctx.Data.ProgramsForTurn(t).Any(r =>
            r.Grade != (int)RaceGrade.Maiden && r.NeedFans <= ctx.Character.Fans
            && ctx.Aptitude(r.Category) >= MinAptitude && ctx.Aptitude(r.Ground) >= MinAptitude));

    /// <summary>race_motivation_rate: Awful 0.96 … Great 1.04, applied to every stat in the race.</summary>
    public static double MoodRate(Mood mood) => 1 + 0.02 * ((int)Math.Clamp((int)mood, 1, 5) - 3);

    /// <summary>race_proper_distance_rate.proper_rate_speed by grade (8 = S … 1 = G).</summary>
    private static double DistanceSpeedRate(int grade) => grade switch
    {
        >= 8 => 1.05, 7 => 1.0, 6 => 0.9, 5 => 0.8, 4 => 0.6, 3 => 0.4, 2 => 0.2, _ => 0.1,
    };

    /// <summary>race_proper_distance_rate.proper_rate_power: D and better accelerate fully.</summary>
    private static double DistancePowerRate(int grade) => grade switch { >= 4 => 1.0, 3 => 0.6, 2 => 0.5, _ => 0.4 };

    /// <summary>race_proper_ground_rate, on acceleration (Power).</summary>
    private static double GroundRate(int grade) => grade switch
    {
        >= 8 => 1.05, 7 => 1.0, 6 => 0.9, 5 => 0.8, 4 => 0.7, 3 => 0.5, 2 => 0.3, _ => 0.1,
    };

    /// <summary>race_proper_runningstyle_rate, on Wit.</summary>
    private static double StyleRate(int grade) => grade switch
    {
        >= 8 => 1.1, 7 => 1.0, 6 => 0.85, 5 => 0.75, 4 => 0.6, 3 => 0.4, 2 => 0.2, _ => 0.1,
    };

    /// <summary>
    /// The trainee's chance to finish 1st, top 3 and top 5 in a race, from a power rating against a typical field.
    /// <para>
    /// Rating: (Speed × distance speed rate + 0.6 × Power × distance power rate × ground rate + 0.15 × Guts
    /// + 0.2 × Wit × style rate) / 2, every stat times the mood rate and counted half above 1200 (coefficients from
    /// master.mdb race_proper_*_rate and race_motivation_rate, as on uma.guide's race mechanics). It reads like a
    /// stat value. Stamina enters only as a shortfall against <see cref="RaceStamina.Required"/> (owned recovery
    /// skills included): without HP for a full last spurt the trainee fades, each 10% short costing 4% of rating.
    /// Junior fields cannot full-spurt a mile either, so the fade is relative: the rating is divided by the fade of a
    /// field rival with Stamina at <see cref="Placing.FieldStamina"/> × the field rating (pace chaser, A distance).
    /// </para>
    /// <para>
    /// Field: career NPC groups (race_instance.npc_group_id 101–303) are not in master.mdb and no community source
    /// lists their stats, so the field is <b>calibrated, not measured</b>: a rating curve over the career (turn 12: 150,
    /// 24: 235, 48: 360, 72: 468, 78: 500) times a grade factor (Pre-OP and below 0.8, OP 0.9, G3 1.0, G2 1.03,
    /// G1 1.06). It is fitted to the two races the turn logs show the trainee winning: Wakagoma Stakes (Classic OP
    /// 2000 m, turn 26) with Speed 301 / Stamina 152 / Power 174 and Arima Kinen (Senior G1 2500 m, turn 72) with
    /// Speed 642 / Stamina 369 / Power 404, both at Great mood; each comes out a ~70–80% win. URA fields are weak:
    /// a trainee with Senior-level stats (800+ Speed) wins nearly every race. Two wins don't pin the curve down, so
    /// refit it as logs add races (losses especially).
    /// </para>
    /// <para>
    /// The relative difference d = rating / field − 1 maps through logistic curves with scale 0.08:
    /// win σ(d/0.08 − 0.5), top 3 σ(d/0.08 + 1), top 5 σ(d/0.08 + 1.8). An even match wins ~38% and places top 3
    /// ~73%; 10% stronger wins ~68%; 10% weaker wins ~15%.
    /// </para>
    /// </summary>
    public sealed record Placing(double Rating, double Field, double Win, double Top3, double Top5, int StaminaNeeded)
    {
        public const double Scale = 0.08;

        /// <summary>The field's Stamina as a share of its rating (calibrated): rivals fade too, early on most of all.</summary>
        public const double FieldStamina = 0.6;

        /// <summary>Career rating curve (turn, field rating) the grade factor scales; see <see cref="Placing"/>.</summary>
        private static readonly (int Turn, double Rating)[] FieldCurve = [(12, 150), (24, 235), (48, 360), (72, 468), (78, 500)];

        public static double FieldRating(int turn, int grade)
        {
            double level = FieldCurve[0].Rating;
            for (int i = 1; i < FieldCurve.Length; i++)
            {
                var (t0, r0) = FieldCurve[i - 1];
                var (t1, r1) = FieldCurve[i];
                if (turn >= t1) level = r1;
                else if (turn > t0) { level = r0 + (r1 - r0) * (turn - t0) / (t1 - t0); break; }
            }
            double factor = grade switch { 100 => 1.06, 200 => 1.03, 300 => 1.0, 400 => 0.9, _ => 0.8 };
            return level * factor;
        }

        /// <summary>
        /// Fan share by finishing place (single_mode_fan_count, the same for every fan set): 1st 100%, 2nd 40%,
        /// 3rd 25%, 4th 15%, 5th 10%, 6th–18th 9% down to 1% (3.9% on average).
        /// </summary>
        public double FanShare => Win + (Top3 - Win) * (0.40 + 0.25) / 2 + (Top5 - Top3) * (0.15 + 0.10) / 2 + (1 - Top5) * 0.039;

        /// <summary>
        /// Expected skill points and total stats. 1st place: G1 +48 SP / +15 stats (Arima Kinen) and OP +32 SP / +5
        /// stats are measured from the turn logs (A2, research/scripts/measure_logs.py); G2, G3 and Pre-OP are
        /// interpolated estimates. 2nd–5th at half of 1st, and 6th or lower at a fifth of the SP and no stats,
        /// are estimates too.
        /// </summary>
        public (double Sp, double Stats) ExpectedRewards(int grade)
        {
            (double sp, double stats) = grade switch
            {
                100 => (48.0, 15.0), 200 => (42.0, 10.0), 300 => (37.0, 8.0), 400 => (32.0, 5.0), _ => (25.0, 3.0),
            };
            double placed = Top5 - Win, rest = 1 - Top5;
            return (sp * (Win + 0.5 * placed + 0.2 * rest), stats * (Win + 0.5 * placed));
        }

        public override string ToString() => $"win ~{Win:P0}, top 3 ~{Top3:P0} (rating {Rating:0} vs field {Field:0})";
    }

    public static Placing PlacingFor(CareerContext ctx, RaceProgram race)
    {
        var s = ctx.Character.Stats;
        double mood = MoodRate(ctx.Character.Mood);
        double Stat(int value) => RaceStamina.Effective(value) * mood;
        int distanceApt = ctx.Aptitude(race.Category), groundApt = ctx.Aptitude(race.Ground);
        double rating = (Stat(s.Speed) * DistanceSpeedRate(distanceApt)
            + 0.6 * Stat(s.Power) * DistancePowerRate(distanceApt) * GroundRate(groundApt)
            + 0.15 * Stat(s.Guts)
            + 0.2 * Stat(s.Wit) * StyleRate(ctx.Aptitude(ctx.Style))) / 2;

        double recovery = new SkillAdvisor(ctx).RecoveryFor(race).Where(r => r.Owned).Sum(r => r.Fraction);
        int needed = RaceStamina.Required(race.Distance, ctx.Style, s.Speed, s.Guts, distanceApt, recovery: recovery);
        double field = Placing.FieldRating(ctx.Turn, race.Grade);
        int fieldNeeded = RaceStamina.Required(race.Distance, RunningStyle.PaceChaser, (int)field, (int)(0.6 * field), 7);
        rating *= Fade(s.Stamina, needed) / Fade(Placing.FieldStamina * field, fieldNeeded);

        double z = (rating / field - 1) / Placing.Scale;
        static double Fade(double stamina, int needed) => 1 - 0.4 * (needed <= 0 ? 0 : Math.Clamp(1 - stamina / needed, 0, 1));
        static double Sigmoid(double x) => 1 / (1 + Math.Exp(-x));
        return new Placing(rating, field, Sigmoid(z - 0.5), Sigmoid(z + 1), Sigmoid(z + 1.8), needed);
    }

    /// <summary>Best optional race the trainee can enter this turn according to game data, if any.</summary>
    public ScoredOption? BestPredicted()
    {
        var options = ctx.Data.ProgramsForTurn(ctx.Turn)
            .Where(r => r.Grade != (int)RaceGrade.Maiden && r.NeedFans <= ctx.Character.Fans)
            .Select(Evaluate)
            .OrderByDescending(o => o.Score)
            .ToList();
        return options.FirstOrDefault();
    }
}
