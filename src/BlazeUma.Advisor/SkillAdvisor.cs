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

    public sealed record Candidate(SkillInfo Skill, int Price, double Fit, double Value)
    {
        public double PerPoint => Price > 0 ? Value / Price : 0;
    }

    /// <summary>Candidates from the open skill screen.</summary>
    public IReadOnlyList<Candidate> FromShop(SkillShop shop) =>
        shop.Skills.Where(s => !s.Obtained && !s.Locked)
            .Select(s => ctx.Data.Skill(s.SkillId) is { } info ? Score(info, s.Price) : null)
            .OfType<Candidate>()
            .ToList();

    /// <summary>Candidates predicted from game data when the skill screen is closed.</summary>
    public IReadOnlyList<Candidate> Predicted()
    {
        var owned = ctx.Character.Skills.Select(s => s.SkillId).ToHashSet();
        var list = ctx.Data.BaseSkillIds(ctx.Character.CardId, ctx.Character.TalentLevel)
            .Select(ctx.Data.Skill).OfType<SkillInfo>()
            .Where(s => !owned.Contains(s.Id))
            .Select(s => Score(s, s.BaseCost))
            .ToList();
        foreach (var hint in ctx.Character.Hints)
        {
            if (ctx.Data.SkillForHint(hint.GroupId, hint.Rarity) is not { } info || owned.Contains(info.Id)) continue;
            int discount = HintDiscount[Math.Clamp(hint.Level, 0, HintDiscount.Length - 1)];
            list.RemoveAll(c => c.Skill.Id == info.Id);
            list.Add(Score(info, info.BaseCost * (100 - discount) / 100));
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

    /// <summary>
    /// 0..1 fit from the skill's activation condition. Alternatives separated by '@' take the best branch;
    /// clauses separated by '&amp;' multiply.
    /// </summary>
    public double Fit(string condition)
    {
        if (string.IsNullOrWhiteSpace(condition)) return 1;
        return condition.Split('@').Max(branch => branch.Split('&').Aggregate(1.0, (fit, clause) => fit * ClauseFit(clause.Trim())));
    }

    private double ClauseFit(string clause)
    {
        var parts = clause.Split("==");
        if (parts.Length != 2 || !int.TryParse(parts[1], out int value)) return 1;
        return parts[0] switch
        {
            "distance_type" => ctx.DistanceProfile.GetValueOrDefault((DistanceCategory)value),
            "running_style" => value == (int)ctx.Style ? 1 : 0,
            "ground_type" => ctx.Aptitude((Ground)value) >= RaceEvaluator.MinAptitude ? 1 : 0,
            _ => 1,
        };
    }

    /// <summary>
    /// Purchases to make now: only when a goal race is today or next turn, or the career is ending.
    /// Greedy by value per point among skills that fit at least half the remaining goals.
    /// </summary>
    public (IReadOnlyList<Candidate> Buy, string Reason) Plan(IReadOnlyList<Candidate> candidates, int skillPoints)
    {
        bool goalSoon = ctx.NextGoal is { } g && g.Turn - ctx.Turn <= 1;
        bool ending = ctx.TurnsLeft <= 1;
        if (!goalSoon && !ending)
            return ([], ctx.NextGoal is { } next
                ? $"save skill points: next goal {next.Name} is in {next.Turn - ctx.Turn} turns and hints will make skills cheaper"
                : "save skill points");

        var buy = new List<Candidate>();
        int left = skillPoints;
        foreach (var c in candidates.Where(c => c.Fit >= 0.5 && c.Value > 0).OrderByDescending(c => c.PerPoint))
        {
            if (c.Price > left) continue;
            buy.Add(c);
            left -= c.Price;
        }
        string reason = goalSoon ? $"{ctx.NextGoal!.Name} is {(ctx.GoalToday is not null ? "today" : "next turn")}" : "career is ending";
        return (buy, reason);
    }
}
