using BlazeUma.Advisor;
using BlazeUma.Memory.Game;
using BlazeUma.Memory.Il2Cpp;

namespace BlazeUma.Cli;

/// <summary>What the runner waits for after a tap before moving on.</summary>
internal enum WaitFor { Nothing, TrainingMenu, TileSelected, Dialog, TurnAdvance, RaceList, RaceSelected, LeftRaceList, DialogClosed, EventDone,
    SkillScreen, SkillSelected, LeftSkillScreen, SkillsLearned }

/// <summary>
/// The button a step taps right now, or why it is skipped / cannot run. <see cref="Method"/> ("Class.Method"):
/// call that no-argument method on <see cref="Button"/> instead of its click event; <see cref="AsMouse"/>: call
/// it as during a real mouse press (<see cref="BlazeUma.Automation.ButtonTapper.InvokeAsMouse"/>).
/// </summary>
internal sealed record StepTarget(ulong Button, string Where, string? SkipReason = null, string? Error = null, string? Method = null,
    bool AsMouse = false)
{
    public static StepTarget Skip(string reason) => new(0, "", SkipReason: reason);
    public static StepTarget Fail(string error) => new(0, "", Error: error);
}

/// <summary>One tap in a plan. <see cref="Resolve"/> runs just before the tap, against the live screen.</summary>
internal sealed record PlanStep(string Description, Func<GameReader, StepTarget> Resolve, WaitFor Wait, int CommandId = 0);

/// <summary>
/// An advisor pick turned into taps. Steps not covered yet leave <see cref="Unsupported"/> set.
/// <see cref="RunsRace"/>: after the steps, <see cref="RaceRunner"/> drives the race screens to the next turn.
/// <see cref="IsEvent"/>: answers an event, so an event on screen is the starting point, not the end.
/// </summary>
internal sealed record ActionPlan(string Action, IReadOnlyList<PlanStep> Steps, string? Unsupported = null, bool RunsRace = false,
    bool IsEvent = false);

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

    public const string GoalRace = "Goal race";
    public const string OpenSkills = "Open the skill screen", LearnSkills = "Learn skills";
    public const string SkillScreen = "Gallop.SingleModeSkillLearningViewController";
    private const string LearnConfirmTitle = "Confirmation";
    public const string SkillsLearnedTitle = "Skills Learned";

    public const string FinalSkills = "Spend the last skill points", CompleteCareer = "Complete Career";

    /// <summary>
    /// On the last screen: Skills opens the usual skill screen (its Back returns here), where the advisor buys
    /// everything useful as the career is ending. Complete Career opens a popup of the same title ("Finish this
    /// Career playthrough?", unused skill points are lost): Cancel left, Finish right (turn 74).
    /// </summary>
    public static ActionPlan CompleteScreenPlan(string action)
    {
        if (action == FinalSkills) return new(action, [Home("Tap Skills", "View.Parameter._learnSkillButton", WaitFor.SkillScreen)]);
        var done = Home("Tap Complete Career", "View.Parameter._doneButton", WaitFor.Dialog);
        return new(action,
        [
            // The finish button stays readable behind the popup, so skip the tap if the popup is already up.
            done with { Resolve = g => DialogTitle(g.ManagedReader) == CompleteCareer ? StepTarget.Skip("the popup is already open") : done.Resolve(g) },
            new PlanStep("Confirm: Finish", g =>
                DialogTitle(g.ManagedReader) != CompleteCareer ? StepTarget.Fail($"expected the \"{CompleteCareer}\" popup, found \"{DialogTitle(g.ManagedReader)}\"")
                : DialogButton(g, "_rightButton") is { } finish ? new StepTarget(finish.Address, finish.Path)
                : StepTarget.Fail($"\"{CompleteCareer}\" has no Finish button"), WaitFor.DialogClosed),
        ]);
    }

    /// <summary>Tap the home screen's Skills button (race day shows its own copy beside Race!).</summary>
    public static ActionPlan SkillScreenOpen(bool raceDay) =>
        new(OpenSkills, [Home("Tap Skills", raceDay ? "_targetRaceSkillGetButton" : "_skillGetButton", WaitFor.SkillScreen)]);

    /// <summary>
    /// On the skill learning screen: press each skill's + (row found by its _infoList, all rows are built at
    /// once: 15 of 15 groups, turn 53), then Decide; the confirmation popup that follows goes to the user.
    /// With nothing (left) to learn: Back to the home screen.
    /// </summary>
    public static ActionPlan SkillShop(IReadOnlyList<int> skillIds, Func<int, string> name)
    {
        var back = Home("Back to the home screen", "_footerBackButton._backButton", WaitFor.LeftSkillScreen);
        if (skillIds.Count == 0) return new ActionPlan(LearnSkills, [back]);
        var steps = skillIds.Select(id => new PlanStep($"Select {name(id)}", g =>
        {
            if (SkillRow(g.ManagedReader, id) is not { } row) return StepTarget.Fail($"no row for {name(id)} on the skill screen");
            if (row.Obtained) return StepTarget.Skip($"{name(id)} is already learned");
            if (row.Selected) return StepTarget.Skip($"{name(id)} is already selected");
            return new StepTarget(row.Plus, $"skill row ({name(id)}) _plusButton");
        }, WaitFor.SkillSelected, id)).ToList();
        steps.Add(Home("Learn the selected skills", "View.DecideButton", WaitFor.Dialog));
        // "Confirmation" lists the skills (DialogSingleModeAcquisitionSkillList): Cancel left, Learn right (turn 53).
        steps.Add(new PlanStep("Confirm: Learn", g =>
            DialogTitle(g.ManagedReader) != LearnConfirmTitle ? StepTarget.Fail($"expected the \"{LearnConfirmTitle}\" popup, found \"{DialogTitle(g.ManagedReader)}\"")
            : DialogButton(g, "_rightButton") is { } learn ? new StepTarget(learn.Address, learn.Path)
            : StepTarget.Fail($"\"{LearnConfirmTitle}\" has no Learn button"), WaitFor.SkillsLearned));
        // Learn opens "Skills Learned" (Close only) while Confirmation stays listed beneath it until then (turn 53).
        steps.Add(new PlanStep("Close \"Skills Learned\"", g =>
            DialogIndex(g.ManagedReader, SkillsLearnedTitle) is not int index ? StepTarget.Fail($"no \"{SkillsLearnedTitle}\" popup")
            : new ButtonFinder(g.ManagedReader).FromCurrentScene()
                .FirstOrDefault(b => b.CanTap && b.Path == $"Dialog[{index}]._currentDialogObj._centerButton") is { } close
                ? new StepTarget(close.Address, close.Path)
                : StepTarget.Fail($"\"{SkillsLearnedTitle}\" has no Close button"), WaitFor.DialogClosed));
        return new ActionPlan(LearnSkills, steps);
    }

    /// <summary>The skill screen row holding <paramref name="skillId"/>: its + button and the skill's state.</summary>
    public static (ulong Plus, bool Selected, bool Obtained)? SkillRow(ManagedReader r, int skillId)
    {
        ulong scene = r.SingletonInstance("Gallop.SceneManager");
        ulong controller = scene == 0 ? 0 : r.Ptr(scene, "_currentViewController");
        if (controller == 0 || r.ClassName(controller) != SkillScreen) return null;
        foreach (ulong item in r.ListItems(r.Ptr(controller, "_itemList")))
            foreach (ulong info in r.ListItems(r.Ptr(item, "_infoList")))
                if (r.Int(info, "SkillId") == skillId)
                    return (r.Ptr(item, "_plusButton"), r.Bool(info, "IsSelected"), r.Bool(info, "IsGet"));
        return null;
    }

    private const string SelectTile = "Gallop.SingleModeMainViewTrainingFooterItem.SimulateOnClick";

    /// <param name="friendName">Name of the character in a deck slot (1–6), to match "Recreation: &lt;name&gt;".</param>
    /// <param name="raceProgram">Program id of a race held this turn, by name, to match "Race: &lt;name&gt;".</param>
    public static ActionPlan For(string action, Func<int, string?> friendName, Func<string, int?> raceProgram)
    {
        var plan = ForAction(action, friendName, raceProgram);
        return plan.Unsupported is null && !plan.RunsRace && !plan.IsEvent
            ? plan with { Steps = [DismissInsufficientFans(), .. plan.Steps] }
            : plan;
    }

    private static ActionPlan ForAction(string action, Func<int, string?> friendName, Func<string, int?> raceProgram)
    {
        if (action.StartsWith("Train ", StringComparison.OrdinalIgnoreCase) && Trainings.TryGetValue(action[6..].Trim(), out int id))
            return Training(action, id);
        if (action.Equals(GoalRace, StringComparison.OrdinalIgnoreCase))
            return Race(action, "_targetRaceRegistButton", "Tap Race! (race day)", programId: null);
        if (action.StartsWith("Race:", StringComparison.OrdinalIgnoreCase))
        {
            string name = action[5..].Trim();
            return raceProgram(name) is int program
                ? Race(action, "_raceRegistButton", "Tap Races (opens the race list)", program)
                : new ActionPlan(action, [], Unsupported: $"no race named {name} is held this turn");
        }
        if (EventChoiceNumber(action) is int choice)
            return EventChoice(action, choice);
        if (action.Equals("Rest", StringComparison.OrdinalIgnoreCase))
            return new ActionPlan(action, [Home("Tap Rest", "_nextMonthButton", WaitFor.Dialog)]);
        // Summer camp: Rest (_nextMonthButton) is disabled and this button takes the Recreation slot.
        if (action.Equals(TurnAdvisor.RestAndRecreationLabel, StringComparison.OrdinalIgnoreCase))
            return new ActionPlan(action, [Home("Tap Rest & Recreation", "_outingSummerButton", WaitFor.Dialog)]);
        if (action.StartsWith("Recreation", StringComparison.OrdinalIgnoreCase))
            return Recreation(action, friendName);
        if (action.Equals("Infirmary", StringComparison.OrdinalIgnoreCase))
            return new ActionPlan(action, [Home("Tap Infirmary", "_hospitalButton", WaitFor.Dialog)]);
        return new ActionPlan(action, [], Unsupported: "not automated yet; do this one by hand");
    }

    private static ActionPlan Training(string action, int commandId)
    {
        string name = Trainings.First(t => t.Value == commandId).Key;
        var open = Home("Open the training menu", "_trainingButton", WaitFor.TrainingMenu);
        return new ActionPlan(action,
        [
            // Already open when resumed part-way (a pause, or a select that did not take).
            open with { Resolve = g => TrainingMenu.IsOpen(g.ManagedReader) == true ? StepTarget.Skip("the training menu is already open") : open.Resolve(g) },
            new PlanStep($"Select the {name} tile", g =>
            {
                var tile = TrainingMenu.ReadTiles(g.ManagedReader).FirstOrDefault(t => TrainingIds.Base(t.CommandId) == commandId);
                if (tile is null) return StepTarget.Fail($"no {name} tile on screen");
                // A tile selects on press, not on click: m_OnClick does not select it (checked live, turn 35).
                // SimulateOnClick is the item's own press handler (same code as OnDown → footer OnDownItem).
                return tile.IsSelected
                    ? StepTarget.Skip($"{name} is already selected")
                    : new StepTarget(tile.Item, $"TrainingFooter._items[{tile.Index}] ({name})", Method: SelectTile, AsMouse: true);
            }, WaitFor.TileSelected, commandId),
            // The footer only trains on a click on the tile that is both selected and "previously selected"
            // (OnClickItem). A press records that, but only while the input module handles a real mouse event
            // (Select checks GallopStandaloneInputModule.isMouseReact), so press as a mouse right before the click.
            new PlanStep($"Press the selected {name} tile", g =>
            {
                var tile = TrainingMenu.ReadTiles(g.ManagedReader).FirstOrDefault(t => TrainingIds.Base(t.CommandId) == commandId);
                if (tile is null) return StepTarget.Fail($"no {name} tile on screen");
                if (!tile.IsSelected) return StepTarget.Fail($"{name} is not the selected tile");
                return new StepTarget(tile.Item, $"TrainingFooter._items[{tile.Index}] ({name}, selected)", Method: SelectTile, AsMouse: true);
            }, WaitFor.Nothing, commandId),
            new PlanStep($"Do {name} training (tap the selected tile)", g =>
            {
                var tile = TrainingMenu.ReadTiles(g.ManagedReader).FirstOrDefault(t => TrainingIds.Base(t.CommandId) == commandId);
                if (tile is null) return StepTarget.Fail($"no {name} tile on screen");
                // Tapping a tile that is not selected only selects it; refuse rather than mis-train.
                if (!tile.IsSelected) return StepTarget.Fail($"{name} is not the selected tile");
                return new StepTarget(tile.Button, $"TrainingFooter._items[{tile.Index}] ({name}, selected)");
            }, WaitFor.TurnAdvance, commandId),
        ]);
    }

    /// <summary>
    /// Open the race list, select the race (the goal row when <paramref name="programId"/> is null), tap the
    /// green Race button. <see cref="RaceRunner"/> then confirms entry and runs the race through to the next turn.
    /// </summary>
    private static ActionPlan Race(string action, string openButton, string openDescription, int? programId)
    {
        RaceRow? Target(RaceEntryState list) => programId is int p
            ? list.Rows.FirstOrDefault(x => x.ProgramId == p)
            : list.Rows.FirstOrDefault(x => x.IsGoal && x.EntryState == 0);

        return new ActionPlan(action,
        [
            OpenRaceList(openDescription, openButton),
            new PlanStep("Select the race", g =>
            {
                var list = RaceEntryScreen.Read(g.ManagedReader);
                if (list is null) return StepTarget.Fail("the race list is not open");
                var row = Target(list);
                if (row is null) return StepTarget.Fail(programId is null ? "no enterable goal race in the list" : $"program {programId} is not in the list");
                if (row.EntryState != 0) return StepTarget.Fail($"program {row.ProgramId} cannot be entered (state {row.EntryState})");
                return list.SelectedProgramId == row.ProgramId
                    ? StepTarget.Skip($"program {row.ProgramId} is already selected")
                    : new StepTarget(row.Button, $"race row {row.Index} (program {row.ProgramId})");
            }, WaitFor.RaceSelected, programId ?? 0),
            new PlanStep("Tap Race (enter the selected race)", g =>
            {
                var list = RaceEntryScreen.Read(g.ManagedReader);
                if (list is null) return StepTarget.Fail("the race list is not open");
                var row = Target(list);
                if (row is null || list.SelectedProgramId != row.ProgramId)
                    return StepTarget.Fail($"the selected race is {list.SelectedProgramId}, not the planned one");
                return list.EntryButton == 0
                    ? StepTarget.Fail("no Race button on screen")
                    : new StepTarget(list.EntryButton, $"View.EntryButton (program {row.ProgramId})");
            }, WaitFor.LeftRaceList),
        ], RunsRace: true);
    }

    /// <summary>
    /// The advisor's event option labels, as on-screen positions: "Top choice" = 1, "Bottom choice" = 2 (two-choice
    /// events only), "Choice N" = Nth from the top.
    /// </summary>
    public static int? EventChoiceNumber(string action) =>
        action.Equals("Top choice", StringComparison.OrdinalIgnoreCase) ? 1
        : action.Equals("Bottom choice", StringComparison.OrdinalIgnoreCase) ? 2
        : action.StartsWith("Choice ", StringComparison.OrdinalIgnoreCase) && int.TryParse(action[7..], out int n) && n > 0 ? n
        : null;

    /// <summary>
    /// Answer an event: close the "Choices" reward preview if it is open, then tap the choice. Choice buttons
    /// are <c>Story.ChoiceButtons[i]</c> (StoryManager's view, over the home screen), top first.
    /// </summary>
    private static ActionPlan EventChoice(string action, int choice) => new(action,
    [
        new PlanStep("Close the \"Choices\" preview", g =>
        {
            var close = new ButtonFinder(g.ManagedReader).FromCurrentScene()
                .FirstOrDefault(b => b.CanTap && b.Path.EndsWith("._currentDialogObj._centerButton"));
            if (close is null) return StepTarget.Skip("no preview open");
            return DialogTitle(g.ManagedReader) == "Choices"
                ? new StepTarget(close.Address, close.Path)
                : StepTarget.Fail($"another popup is open (\"{DialogTitle(g.ManagedReader)}\")");
        }, WaitFor.DialogClosed),
        new PlanStep($"Pick choice {choice} ({action})", g =>
        {
            var r = g.ManagedReader;
            var buttons = new ButtonFinder(r).FromCurrentScene();
            var pick = buttons.FirstOrDefault(b => b.Path == $"Story.ChoiceButtons[{choice - 1}]._button");
            if (pick is null || !pick.CanTap) return StepTarget.Fail($"choice {choice} is not on screen");
            // The choice's _button has no click listeners. StoryChoiceButton.OnClickChoice only acts while the
            // pointer is inside; OnClickAutoSelect (the game's auto-select) sends the same choice without that check,
            // via OnClickCallback or StoryChoiceController.Instance (created on demand, never null).
            ulong story = r.SingletonInstance("Gallop.StoryManager");
            var rows = r.ArrayItems(r.Ptr(r.Ptr(story, "_view"), "<ChoiceButtons>k__BackingField"));
            if (choice - 1 >= rows.Count) return StepTarget.Fail($"choice {choice} is not on screen");
            ulong row = rows[choice - 1];
            if (r.Int(row, "_index") != choice - 1) return StepTarget.Fail($"choice row {choice - 1} has index {r.Int(row, "_index")}");
            return new StepTarget(row, $"Story.ChoiceButtons[{choice - 1}]", Method: "Gallop.StoryChoiceButton.OnClickAutoSelect");
        }, WaitFor.EventDone),
    ], IsEvent: true);

    /// <summary>Title of the topmost dialog, or null.</summary>
    /// <summary>Index in DialogManager._dialogList of the open popup titled <paramref name="title"/>, if any.</summary>
    public static int? DialogIndex(ManagedReader r, string title)
    {
        ulong manager = r.SingletonInstance("Gallop.DialogManager");
        var dialogs = manager == 0 ? [] : r.ListItems(r.Ptr(manager, "_dialogList"));
        for (int i = 0; i < dialogs.Count; i++)
            if (r.Ptr(dialogs[i], "_data") is var data and not 0 && r.String(r.Ptr(data, "Title")) == title) return i;
        return null;
    }

    public static string? DialogTitle(ManagedReader r)
    {
        ulong manager = r.SingletonInstance("Gallop.DialogManager");
        var dialogs = manager == 0 ? [] : r.ListItems(r.Ptr(manager, "_dialogList"));
        ulong data = dialogs.Count == 0 ? 0 : r.Ptr(dialogs[^1], "_data");
        return data == 0 ? null : r.String(r.Ptr(data, "Title"));
    }

    public const string InsufficientFansTitle = "Insufficient Fans";

    /// <summary>
    /// Open the race list. The game may already show it, or show "Insufficient Fans" (Cancel / Race) on the
    /// home screen when a goal race needs more fans; its Race button opens the list too.
    /// </summary>
    private static PlanStep OpenRaceList(string description, string openButton)
    {
        var home = Home(description, openButton, WaitFor.RaceList);
        return home with
        {
            Resolve = g =>
            {
                if (RaceEntryScreen.Read(g.ManagedReader) is not null) return StepTarget.Skip("the race list is already open");
                if (DialogTitle(g.ManagedReader) == InsufficientFansTitle)
                    return DialogButton(g, "_rightButton") is { } race
                        ? new StepTarget(race.Address, $"{race.Path} (Race on \"{InsufficientFansTitle}\")")
                        : StepTarget.Fail($"\"{InsufficientFansTitle}\" has no Race button");
                return home.Resolve(g);
            },
        };
    }

    /// <summary>
    /// Step that taps Cancel on "Insufficient Fans" if it is open (it covers the home buttons), for plans
    /// that are not races. Skipped otherwise.
    /// </summary>
    public static PlanStep DismissInsufficientFans() => new("Cancel \"Insufficient Fans\" (not racing this turn)", g =>
        DialogTitle(g.ManagedReader) != InsufficientFansTitle ? StepTarget.Skip("no fans warning open")
        : DialogButton(g, "_leftButton") is { } cancel ? new StepTarget(cancel.Address, cancel.Path)
        : StepTarget.Fail($"\"{InsufficientFansTitle}\" has no Cancel button"), WaitFor.DialogClosed);

    private static FoundButton? DialogButton(GameReader g, string name) =>
        new ButtonFinder(g.ManagedReader).FromCurrentScene()
            .FirstOrDefault(b => b.CanTap && b.Path.StartsWith("Dialog[") && b.Path.EndsWith($"._currentDialogObj.{name}"));

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
