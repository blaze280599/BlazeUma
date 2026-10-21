using BlazeUma.Memory;
using BlazeUma.Memory.Game;

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
internal sealed record PlanStep(string Description, Func<GameReader, StepTarget> Resolve, WaitFor Wait, int CommandId = 0);

/// <summary>An advisor pick turned into taps. Steps not covered yet leave <see cref="Unsupported"/> set.</summary>
internal sealed record ActionPlan(string Action, IReadOnlyList<PlanStep> Steps, string? Unsupported = null);

/// <summary>
/// Maps advisor actions (option labels) to tap sequences on the home screen. Each step finds its button on
/// the live screen when it runs, so addresses are never stale. Dialogs that follow a step (e.g. the Rest
/// confirmation) are handled by the runner's dialog follow-up, where the user picks the button.
/// </summary>
internal static class ActionPlanner
{
    public const int TraineeOuting = 301;

    public static readonly IReadOnlyDictionary<string, int> Trainings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["Speed"] = TrainingIds.Speed, ["Stamina"] = TrainingIds.Stamina, ["Power"] = TrainingIds.Power,
        ["Guts"] = TrainingIds.Guts, ["Wit"] = TrainingIds.Wit,
    };

    /// <param name="friendName">Name of the character in a deck slot (1–6), to match "Recreation: &lt;name&gt;".</param>
    public static ActionPlan For(string action, Func<int, string?> friendName)
    {
        if (action.StartsWith("Train ", StringComparison.OrdinalIgnoreCase) && Trainings.TryGetValue(action[6..].Trim(), out int id))
            return Training(action, id);
        if (action.Equals("Rest", StringComparison.OrdinalIgnoreCase))
            return new ActionPlan(action, [Home("Tap Rest", "_nextMonthButton", WaitFor.Dialog)]);
        if (action.StartsWith("Recreation", StringComparison.OrdinalIgnoreCase))
            return Recreation(action, friendName);
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
            new PlanStep($"Select the {name} tile", g =>
            {
                var tile = TrainingMenu.ReadTiles(g.ManagedReader).FirstOrDefault(t => t.CommandId == commandId);
                if (tile is null) return StepTarget.Fail($"no {name} tile on screen");
                return tile.IsSelected
                    ? StepTarget.Skip($"{name} is already selected")
                    : new StepTarget(tile.Button, $"TrainingFooter._items[{tile.Index}] ({name})");
            }, WaitFor.TileSelected, commandId),
            new PlanStep($"Do {name} training (tap the selected tile)", g =>
            {
                var tile = TrainingMenu.ReadTiles(g.ManagedReader).FirstOrDefault(t => t.CommandId == commandId);
                if (tile is null) return StepTarget.Fail($"no {name} tile on screen");
                // Tapping a tile that is not selected only selects it; refuse rather than mis-train.
                if (!tile.IsSelected) return StepTarget.Fail($"{name} is not the selected tile");
                return new StepTarget(tile.Button, $"TrainingFooter._items[{tile.Index}] ({name}, selected)");
            }, WaitFor.TurnAdvance, commandId),
        ]);
    }

    /// <summary>"Recreation: trainee" or "Recreation: &lt;friend name&gt;": open the picker, then tap that row.</summary>
    private static ActionPlan Recreation(string action, Func<int, string?> friendName)
    {
        string who = action.Contains(':') ? action[(action.IndexOf(':') + 1)..].Trim() : "trainee";
        bool trainee = who.Equals("trainee", StringComparison.OrdinalIgnoreCase);
        var open = Home("Tap Recreation (opens the outing picker)", "_outingButton", WaitFor.Dialog);
        return new ActionPlan(action,
        [
            // The home buttons stay readable behind the dialog, so skip the tap if the picker is already up.
            open with { Resolve = g => OutingPicker.Read(g).Count > 0 ? StepTarget.Skip("the outing picker is already open") : open.Resolve(g) },
            new PlanStep($"Choose the {(trainee ? "trainee's own" : who)} outing", g =>
            {
                var rows = OutingPicker.Read(g);
                if (rows.Count == 0) return StepTarget.Fail("the outing picker is not open");
                var row = trainee
                    ? rows.FirstOrDefault(x => x.CommandGroupId == TraineeOuting)
                    : rows.FirstOrDefault(x => x.DeckPosition > 0
                        && string.Equals(friendName(x.DeckPosition), who, StringComparison.OrdinalIgnoreCase));
                return row is null
                    ? StepTarget.Fail($"no outing row for {who} (rows: {string.Join(", ", rows.Select(x => x.DeckPosition == 0 ? "trainee" : friendName(x.DeckPosition) ?? $"slot {x.DeckPosition}"))})")
                    : new StepTarget(row.Button, $"outing row {row.Index} ({(row.DeckPosition == 0 ? "trainee" : who)}, command {row.CommandGroupId})");
            }, WaitFor.TurnAdvance),
        ]);
    }

    /// <summary>A step that taps a tappable button whose path contains <paramref name="pathPart"/>.</summary>
    private static PlanStep Home(string description, string pathPart, WaitFor wait) => new(description, g =>
    {
        var matches = new ButtonFinder(g.ManagedReader).FromCurrentScene()
            .Where(b => b.Path.Contains(pathPart, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return StepTarget.Fail($"button {pathPart} not on screen");
        var tappable = matches.FirstOrDefault(b => b.CanTap);
        return tappable is null
            ? StepTarget.Fail($"button {pathPart} is disabled")
            : new StepTarget(tappable.Address, tappable.Path);
    }, wait);
}
