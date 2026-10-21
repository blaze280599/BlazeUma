using BlazeUma.Memory;
using BlazeUma.Memory.Game;
using BlazeUma.Memory.Il2Cpp;

namespace BlazeUma.Cli;

/// <summary>What the runner waits for after a tap before moving on.</summary>
internal enum WaitFor { Nothing, TrainingMenu, TileSelected, Dialog, TurnAdvance }

/// <summary>The button a step taps right now, or why it is skipped / cannot run.</summary>
internal sealed record StepTarget(ulong Button, string Where, string? SkipReason = null, string? Error = null)
{
    public static StepTarget Skip(string reason) => new(0, "", SkipReason: reason);
    public static StepTarget Fail(string error) => new(0, "", Error: error);
}

/// <summary>One tap in a plan. <see cref="Resolve"/> runs just before the tap, against the live screen.</summary>
internal sealed record PlanStep(string Description, Func<ManagedReader, StepTarget> Resolve, WaitFor Wait, int CommandId = 0);

/// <summary>An advisor pick turned into taps. Steps not covered yet leave <see cref="Unsupported"/> set.</summary>
internal sealed record ActionPlan(string Action, IReadOnlyList<PlanStep> Steps, string? Unsupported = null);

/// <summary>
/// Maps advisor actions (option labels) to tap sequences on the home screen. Each step finds its button on
/// the live screen when it runs, so addresses are never stale. Dialogs that follow a step (confirmations,
/// the outing picker) are handled by the runner's dialog follow-up, where the user picks the button.
/// </summary>
internal static class ActionPlanner
{
    public static readonly IReadOnlyDictionary<string, int> Trainings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["Speed"] = TrainingIds.Speed, ["Stamina"] = TrainingIds.Stamina, ["Power"] = TrainingIds.Power,
        ["Guts"] = TrainingIds.Guts, ["Wit"] = TrainingIds.Wit,
    };

    public static ActionPlan For(string action)
    {
        if (action.StartsWith("Train ", StringComparison.OrdinalIgnoreCase) && Trainings.TryGetValue(action[6..].Trim(), out int id))
            return Training(action, id);
        if (action.Equals("Rest", StringComparison.OrdinalIgnoreCase))
            return new ActionPlan(action, [Home("Tap Rest", "_nextMonthButton", WaitFor.Dialog)]);
        if (action.StartsWith("Recreation", StringComparison.OrdinalIgnoreCase))
            return new ActionPlan(action, [Home("Tap Recreation (opens the outing picker)", "_outingButton", WaitFor.Dialog)]);
        if (action.Equals("Infirmary", StringComparison.OrdinalIgnoreCase))
            return new ActionPlan(action, [Home("Tap Infirmary", "_hospitalButton", WaitFor.Dialog)]);
        return new ActionPlan(action, [], Unsupported: "not automated yet; do this one by hand");
    }

    private static ActionPlan Training(string action, int commandId)
    {
        string name = Trainings.First(t => t.Value == commandId).Key;
        return new ActionPlan(action,
        [
            Home("Open the training menu", "_trainingButton", WaitFor.TrainingMenu),
            new PlanStep($"Select the {name} tile", r =>
            {
                var tile = TrainingMenu.ReadTiles(r).FirstOrDefault(t => t.CommandId == commandId);
                if (tile is null) return StepTarget.Fail($"no {name} tile on screen");
                return tile.IsSelected
                    ? StepTarget.Skip($"{name} is already selected")
                    : new StepTarget(tile.Button, $"TrainingFooter._items[{tile.Index}] ({name})");
            }, WaitFor.TileSelected, commandId),
            new PlanStep($"Do {name} training (tap the selected tile)", r =>
            {
                var tile = TrainingMenu.ReadTiles(r).FirstOrDefault(t => t.CommandId == commandId);
                if (tile is null) return StepTarget.Fail($"no {name} tile on screen");
                // Tapping a tile that is not selected only selects it; refuse rather than mis-train.
                if (!tile.IsSelected) return StepTarget.Fail($"{name} is not the selected tile");
                return new StepTarget(tile.Button, $"TrainingFooter._items[{tile.Index}] ({name}, selected)");
            }, WaitFor.TurnAdvance, commandId),
        ]);
    }

    /// <summary>A step that taps a tappable button whose path contains <paramref name="pathPart"/>.</summary>
    private static PlanStep Home(string description, string pathPart, WaitFor wait) => new(description, r =>
    {
        var matches = new ButtonFinder(r).FromCurrentScene()
            .Where(b => b.Path.Contains(pathPart, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return StepTarget.Fail($"button {pathPart} not on screen");
        var tappable = matches.FirstOrDefault(b => b.CanTap);
        return tappable is null
            ? StepTarget.Fail($"button {pathPart} is disabled")
            : new StepTarget(tappable.Address, tappable.Path);
    }, wait);
}
