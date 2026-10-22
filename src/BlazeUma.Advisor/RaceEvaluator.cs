using BlazeUma.Data;

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

        // Placing chance is unknown without a race simulation; assume a fair chance at good aptitude.
        // Mood scales race performance (Bad/Awful run noticeably worse), so it scales the chance too.
        int mood = (int)ctx.Character.Mood;
        double placeChance = (distanceApt >= 7 && groundApt >= 7 ? 0.6 : 0.4) * (1 + 0.08 * (mood - 3));
        double expectedFans = race.FirstPlaceFans * placeChance;
        double score = 0;
        if (mood < 3)
            reasons.Add($"mood {ctx.Character.Mood} lowers the chance to place ({placeChance:P0})");

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

        (double sp, double stats) = race.Grade switch
        {
            100 => (45.0, 10.0), 200 => (35.0, 8.0), 300 => (30.0, 6.0), 400 => (25.0, 5.0), _ => (20.0, 3.0),
        };
        double rewards = (sp * ValueModel.SkillPointValue + stats * values.AverageStatWeight) * placeChance;
        score += rewards;
        reasons.Add($"skill points and stats (+{rewards:0.0}, rough grade estimate)");

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
            + 0.5 * Math.Abs(values.Mood(-1)) + 0.5 * ValueModel.BadConditionValue;
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
