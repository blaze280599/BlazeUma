namespace BlazeUma.Advisor;

public enum Situation { NoCareer, TrainingTurn, GoalRaceDay, Event, SkillShop, RaceList }

/// <summary>One candidate action with its score and the reasons behind it. Higher is better.</summary>
public sealed record ScoredOption(string Label, double Score, IReadOnlyList<string> Reasons)
{
    public override string ToString() => $"{Label} ({Score:0.0}): {string.Join("; ", Reasons)}";
}

/// <summary>What the advisor recommends for the screen the game is on.</summary>
public sealed record Advice(Situation Situation, string Summary, IReadOnlyList<ScoredOption> Options, IReadOnlyList<string> Notes)
{
    public ScoredOption? Best => Options.Count > 0 ? Options[0] : null;
}
