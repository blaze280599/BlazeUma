using BlazeUma.Data;
using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor;

/// <summary>
/// Scores learnable skills by how well they fit this trainee's remaining goals and running style, then plans
/// purchases. Skill points are kept until a goal race is close, because later hints make skills cheaper.
/// </summary>
public sealed class SkillAdvisor(CareerContext ctx)
{
    /// <summary>Hint discount per hint level (URA). Level 1 = 10% is confirmed in game; higher levels are community values.</summary>
    private static readonly int[] HintDiscount = [0, 10, 20, 30, 35, 40];

    /// <param name="Row">Skill screen row (-1 when predicted); one candidate per row is bought.</param>
    /// <param name="Presses">Skill ids selected with the row's +, in order: ○ then ◎ when both are still to learn.</param>
    public sealed record Candidate(SkillInfo Skill, int Price, double Fit, double Value, int Row = -1, IReadOnlyList<int>? Presses = null)
    {
        public double PerPoint => Price > 0 ? Value / Price : 0;
        public IReadOnlyList<int> Steps => Presses ?? [Skill.Id];
    }

    /// <summary>
    /// Candidates from the open skill screen: the next skill in each row, and each upgrade after it priced with
    /// the steps before it, since ◎ can only be learned after the ○ before it (its listed price is for ◎ alone).
    /// </summary>
    public IReadOnlyList<Candidate> FromShop(SkillShop shop)
    {
        var list = new List<Candidate>();
        foreach (var row in shop.Skills.Where(s => !s.Obtained).GroupBy(s => s.Row))
        {
            var steps = row.OrderBy(s => s.Step).ToList();
            if (steps[0].Locked) continue;
            for (int i = 0; i < steps.Count; i++)
                if (ctx.Data.Skill(steps[i].SkillId) is { } info)
                    list.Add(Score(info, steps.Take(i + 1).Sum(s => s.Price)) with
                    {
                        Row = row.Key, Presses = [.. steps.Take(i + 1).Select(s => s.SkillId)],
                    });
        }
        return list;
    }

    /// <summary>Candidates predicted from game data when the skill screen is closed.</summary>
    public IReadOnlyList<Candidate> Predicted()
    {
        var owned = ctx.Character.Skills.Select(s => s.SkillId).ToHashSet();
        var list = ctx.Data.BaseSkillIds(ctx.Character.CardId, ctx.Character.TalentLevel)
            .Select(ctx.Data.Skill).OfType<SkillInfo>()
            .Where(s => !owned.Contains(s.Id))
            .Select(s => Score(s, s.BaseCost))
            .ToList();
        int Discounted(SkillInfo s, int groupId, int rarity)
        {
            var hint = ctx.Character.Hints.FirstOrDefault(h => h.GroupId == groupId && h.Rarity == rarity);
            int discount = hint is null ? 0 : HintDiscount[Math.Clamp(hint.Level, 0, HintDiscount.Length - 1)];
            return s.BaseCost * (100 - discount) / 100;
        }

        foreach (var hint in ctx.Character.Hints)
        {
            if (ctx.Data.SkillForHint(hint.GroupId, hint.Rarity) is not { } info || owned.Contains(info.Id)) continue;
            int price = Discounted(info, hint.GroupId, hint.Rarity);
            // A gold skill also buys its white version unless owned (live shop: Sturm und Drang 342 = 162 + 180).
            if (info.IsGold && ctx.Data.SkillForHint(hint.GroupId, 1) is { } white && !owned.Contains(white.Id))
                price += Discounted(white, hint.GroupId, 1);
            list.RemoveAll(c => c.Skill.Id == info.Id);
            list.Add(Score(info, price));
        }
        return list;
    }

    public Candidate Score(SkillInfo skill, int price)
    {
        double fit = Fit(skill.Condition);
        double ability = skill.AbilityType switch
        {
            27 or 31 or 21 or 22 => 1.0,   // target speed, acceleration, current speed
            9 => ctx.LongestRemainingDistance >= 2000 ? 1.0 : 0.7,   // stamina recovery
            >= 1 and <= 5 => 0.5,          // passive stat bonus
            _ => 0.5,
        };
        return new Candidate(skill, price, fit, skill.GradeValue * fit * ability);
    }

    /// <summary>Share of a hint's discount counted for a skill that doesn't fit (wrong distance or style).</summary>
    public const double OffFitHintShare = 0.1;

    /// <summary>
    /// Worth of <paramref name="levels"/> more hint levels on a skill: the skill points the discount saves (on its
    /// base cost, at <see cref="ValueModel.SkillPointValue"/>), times its fit if it is worth buying (as in
    /// <see cref="Plan"/>), else <see cref="OffFitHintShare"/>. Nothing when owned or already at the top level.
    /// </summary>
    public double HintWorth(SkillInfo skill, int levels = 1)
    {
        if (ctx.Character.Skills.Any(s => s.SkillId == skill.Id)) return 0;
        int level = ctx.Character.Hints.FirstOrDefault(h => h.GroupId == skill.GroupId && h.Rarity == skill.Rarity)?.Level ?? 0;
        int top = HintDiscount.Length - 1;
        if (level >= top) return 0;
        int gained = HintDiscount[Math.Min(top, level + levels)] - HintDiscount[Math.Max(0, level)];
        var c = Score(skill, skill.BaseCost);
        double share = c.Value > 0 && c.Fit >= 0.5 ? c.Fit : OffFitHintShare;
        return gained / 100.0 * skill.BaseCost * ValueModel.SkillPointValue * share;
    }

    /// <summary>A recovery skill's expected HP in one race, as a share of max HP.</summary>
    public sealed record Recovery(SkillInfo Skill, double Fraction, bool Owned);

    /// <summary>Share of a hinted recovery skill's HP counted before it is bought: the trainee may never buy it.</summary>
    public const double HintedRecoveryCredit = 0.5;

    /// <summary>
    /// Recovery skills that should fire in <paramref name="race"/>: owned ones in full and hinted ones (useful for
    /// the remaining goals, fit ≥ 0.5) at <see cref="HintedRecoveryCredit"/>, each times its fit for this race's
    /// distance, ground and style. A gold skill replaces its white version, so a group counts once: a hinted gold
    /// over an owned white adds only the difference.
    /// </summary>
    public IReadOnlyList<Recovery> RecoveryFor(RaceProgram race)
    {
        var owned = ctx.Character.Skills.Select(s => ctx.Data.Skill(s.SkillId)).OfType<SkillInfo>()
            .Where(s => s.RecoveryFraction > 0)
            .GroupBy(s => s.GroupId).Select(g => g.MaxBy(s => s.RecoveryFraction)!).ToList();
        var ownedIds = ctx.Character.Skills.Select(s => s.SkillId).ToHashSet();
        double Owned(int groupId) => owned.FirstOrDefault(s => s.GroupId == groupId)?.RecoveryFraction ?? 0;
        var hinted = ctx.Character.Hints.Select(h => ctx.Data.SkillForHint(h.GroupId, h.Rarity)).OfType<SkillInfo>()
            .Where(s => s.RecoveryFraction > 0 && !ownedIds.Contains(s.Id) && Fit(s.Condition) >= 0.5)
            .Select(s => new Recovery(s,
                HintedRecoveryCredit * Math.Max(0, s.RecoveryFraction - Owned(s.GroupId)) * Fit(s.Condition, race), false))
            .GroupBy(r => r.Skill.GroupId).Select(g => g.MaxBy(r => r.Fraction)!);
        return owned.Select(s => new Recovery(s, s.RecoveryFraction * Fit(s.Condition, race), true))
            .Concat(hinted).Where(r => r.Fraction > 0).ToList();
    }

    /// <summary>
    /// 0..1 fit from the skill's activation condition. Alternatives separated by '@' take the best branch;
    /// clauses separated by '&amp;' multiply. A condition on the race itself (<see cref="RaceClauses"/>) is the
    /// mean of its fit over the remaining goal races, so "Kyoto Racecourse" counts by the goals run at Kyoto.
    /// </summary>
    public double Fit(string condition) =>
        ctx.RemainingGoals.Count > 0 && condition.Split('@', '&').Any(c => RaceClauses.Contains(ClauseName(c.Trim())))
            ? ctx.RemainingGoals.Average(g => Fit(condition, g.Race))
            : Fit(condition, race: null);

    /// <summary>
    /// Clauses on the race's course, date or weather. Corner, straight and phase clauses aren't among them: every
    /// race has those parts, so they stay at 1.
    /// </summary>
    private static readonly HashSet<string> RaceClauses =
        ["track_id", "rotation", "season", "weather", "ground_condition", "is_basis_distance", "time", "grade", "is_dirtgrade"];

    /// <summary>Race clauses whose values exclude each other, with fits that are odds (see <see cref="Fit(string, RaceProgram?)"/>).</summary>
    private static readonly HashSet<string> Exclusive = ["season", "weather", "ground_condition", "track_id", "rotation"];

    /// <summary>Fit of a track_id clause in a race whose course is unknown (the scenario finals): about one track in ten.</summary>
    public const double UnknownTrackFit = 0.1;

    private static string ClauseName(string clause) => new([.. clause.TakeWhile(c => char.IsLetterOrDigit(c) || c == '_')]);

    /// <summary>Fit for one specific race: distance and surface clauses must match that race exactly.</summary>
    public double Fit(string condition, RaceProgram? race)
    {
        if (string.IsNullOrWhiteSpace(condition)) return 1;
        // "ground_condition==2@ground_condition==3@ground_condition==4" (Wet Conditions): one race can't be both, so
        // the branches' odds add up rather than the best one counting.
        var branches = condition.Split('@').Select(b => b.Trim()).ToList();
        if (branches.Count > 1 && !branches.Any(b => b.Contains('&')) && branches.Select(ClauseName).Distinct().Count() == 1
            && Exclusive.Contains(ClauseName(branches[0])))
            return Math.Min(1, branches.Sum(b => ClauseFit(b, race)));
        return condition.Split('@').Max(branch => branch.Split('&').Aggregate(1.0, (fit, clause) => fit * ClauseFit(clause.Trim(), race)));
    }

    /// <summary>Clauses that only hold in some race situations (overtaking, being passed, boxed in).</summary>
    private static readonly HashSet<string> Situational =
        ["is_overtake", "overtake_target_time", "overtake_target_no_order_up_time", "is_surrounded", "blocked_side_continuetime", "is_behind_in"];

    private double ClauseFit(string clause, RaceProgram? race)
    {
        string name = ClauseName(clause);
        if (name.StartsWith("activate_count"))
        {
            // Triggers only after N other skills activate; a trainee with few skills rarely gets there.
            int needed = int.TryParse(new string([.. clause.Reverse().TakeWhile(char.IsDigit).Reverse()]), out int n) ? n : 1;
            return ctx.Character.Skills.Count >= needed + 2 ? 1 : 0.25;
        }
        if (Situational.Contains(name)) return 0.6;

        var parts = clause.Split("==");
        if (parts.Length != 2 || !int.TryParse(parts[1], out int value)) return 1;
        // Without a race (or its course), these are the odds for a race nobody knows yet.
        var course = race?.Course;
        static double Is(bool match) => match ? 1 : 0;
        return parts[0] switch
        {
            "track_id" => course is null ? UnknownTrackFit : Is(course.TrackId == value),
            "rotation" => course is null ? 0.5 : Is(course.Rotation == value),
            "time" => course is null ? Is(value == 2) : Is(course.Time == value),   // nearly all races are day races
            "is_dirtgrade" => course is null ? Is(value == 0) : Is(course.DirtGrade == (value == 1)),
            "is_basis_distance" => race is null ? 0.5 : Is((race.Distance % 400 == 0) == (value == 1)),
            "grade" => race is null ? 0.5 : Is(race.Grade == value),
            "season" => RaceConditions.SeasonShare(course?.Date, value),
            "weather" => RaceConditions.WeatherShare(course?.Date, value),
            "ground_condition" => RaceConditions.GroundShare(course?.Date, value),
            "distance_type" when race is not null => (int)race.Category == value ? 1 : 0,
            "distance_type" => ctx.DistanceProfile.GetValueOrDefault((DistanceCategory)value),
            "running_style" => value == (int)ctx.Style ? 1 : 0,
            "ground_type" when race is not null => (int)race.Ground == value ? 1 : 0,
            "ground_type" => ctx.Aptitude((Ground)value) >= RaceEvaluator.MinAptitude ? 1 : 0,
            _ => 1,
        };
    }

    /// <summary>
    /// Purchases to make now: only when a goal race is today or next turn, or the career is ending.
    /// Before a goal race, only skills that work in that race (its distance and surface) are bought; the
    /// rest wait for a race they help. Greedy by value per point; a row's upgrade (○ → ◎) replaces its lower
    /// step when the extra points fit, and a lower step never replaces a bought upgrade.
    /// </summary>
    public (IReadOnlyList<Candidate> Buy, string Reason) Plan(IReadOnlyList<Candidate> candidates, int skillPoints)
    {
        bool goalSoon = !ctx.CareerOver && ctx.NextGoal is { } g && g.Turn - ctx.Turn <= 1;
        bool ending = ctx.TurnsLeft <= 1;
        if (!goalSoon && !ending)
            return ([], "save points until the turn before a goal race (hints make skills cheaper)");

        var nextRace = goalSoon ? ctx.NextGoal!.Race : null;
        bool Useful(Candidate c) => c.Value > 0 && c.Fit >= 0.5 && (nextRace is null || Fit(c.Skill.Condition, nextRace) >= 0.5);

        var buy = new List<Candidate>();
        int left = skillPoints;
        foreach (var c in candidates.Where(Useful).OrderByDescending(c => c.PerPoint))
        {
            if (c.Row >= 0 && buy.FindIndex(b => b.Row == c.Row) is var same and >= 0)
            {
                int extra = c.Price - buy[same].Price;
                if (c.Steps.Count <= buy[same].Steps.Count || extra > left) continue;
                buy[same] = c;
                left -= extra;
                continue;
            }
            if (c.Price > left) continue;
            buy.Add(c);
            left -= c.Price;
        }
        string reason = goalSoon
            ? $"{ctx.NextGoal!.Name} is {(ctx.GoalToday is not null ? "today" : "next turn")}" +
              (nextRace is not null ? $"; only skills that work at {nextRace.Distance}m {nextRace.Ground}" : "")
            : "career is ending";
        if (goalSoon && buy.Count == 0) reason += "; nothing useful for this race, keep saving";
        return (buy, reason);
    }
}
