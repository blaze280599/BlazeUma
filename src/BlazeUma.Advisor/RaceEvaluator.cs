using BlazeUma.Data;

namespace BlazeUma.Advisor;

/// <summary>Scores entering an optional race this turn instead of training.</summary>
public sealed class RaceEvaluator(CareerContext ctx, ValueModel values)
{
    /// <summary>Below B aptitude the trainee is unlikely to place well.</summary>
    public const int MinAptitude = 6;
    private const double RaceEnergyCost = -15;

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

        if (ctx.FanShortfall() is var (goal, shortfall))
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

        if (ctx.NextGoal is { } next && next.Turn - ctx.Turn is > 0 and <= 2)
        {
            score -= 8;
            reasons.Add($"{next.Name} is in {next.Turn - ctx.Turn} turn(s); avoid racing back to back (-8)");
        }
        return new ScoredOption(race.Name, score, reasons);
    }

    private int? _suitableTurns;

    /// <summary>Later turns before <paramref name="goalTurn"/> with an enterable race at B+ aptitude.</summary>
    private int SuitableTurnsBefore(int goalTurn) => _suitableTurns ??= Enumerable.Range(ctx.Turn + 1, Math.Max(0, goalTurn - ctx.Turn - 1))
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
