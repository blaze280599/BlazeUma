namespace BlazeUma.Advisor;

public enum Situation { NoCareer, TrainingTurn, GoalRaceDay, Event, SkillShop, RaceList }

/// <summary>One candidate action with its score and the reasons behind it. Higher is better.</summary>
public sealed record ScoredOption(string Label, double Score, IReadOnlyList<string> Reasons)
{
    public override string ToString() => $"{Label} ({Score:0.0}): {string.Join("; ", Reasons)}";
}

/// <summary>A labelled one-line fact shown under the options, e.g. ("Next goal", "Tulip Sho on turn 29").</summary>
public sealed record Note(string Label, string Text);

/// <summary>What the advisor recommends for the screen the game is on.</summary>
/// <param name="Header">Career status line (turn, date, mood, energy …).</param>
/// <param name="Summary">The recommendation in a few words.</param>
/// <param name="Details">Diagnostics such as stat weights; shown only in verbose output.</param>
/// <param name="SkillsToBuy">Skill ids to learn now: from the open skill screen, or predicted from game data
/// elsewhere (enough to tell whether the skill screen is worth opening).</param>
public sealed record Advice(
    Situation Situation, string Header, string Summary,
    IReadOnlyList<ScoredOption> Options, IReadOnlyList<Note> Notes, IReadOnlyList<string> Details,
    IReadOnlyList<int>? SkillsToBuy = null)
{
    public ScoredOption? Best => Options.Count > 0 ? Options[0] : null;
}
