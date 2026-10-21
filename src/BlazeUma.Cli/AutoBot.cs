using System.Diagnostics;
using BlazeUma.Advisor;
using BlazeUma.Automation;
using BlazeUma.Data;
using BlazeUma.Memory;
using BlazeUma.Memory.Game;
using BlazeUma.Memory.Il2Cpp;

namespace BlazeUma.Cli;

/// <summary>
/// Plays the career on its own, one advisor pick after another: trainings, rest, recreation, infirmary,
/// races (goal and optional, through to the next turn) and event choices. Insert pauses and Page Down stops
/// (<see cref="BotControl"/>). A screen it cannot handle, or a popup with a real choice, pauses it and asks.
/// </summary>
internal sealed class AutoBot(BotControl control)
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);
    /// <summary>Wait after an action so the next screen (an event, the new turn) is up before it is read.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);
    /// <summary>How long a screen with nothing to do may last (loading, transitions) before the bot asks for help.</summary>
    private static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(15);
    /// <summary>Actions on one turn before the bot assumes it is stuck (a few events and the turn's action fit well under it).</summary>
    private const int MaxActionsPerTurn = 8;
    /// <summary>Unreadable snapshots in a row (normal for a moment while screens change) before the bot asks for help.</summary>
    private const int MaxReadFailures = 20;

    private enum Outcome { Done, Interrupted, NeedsYou }

    private Stopwatch? _idle;
    private int _turn, _actionsThisTurn;

    public Mode Run()
    {
        ConsoleUi.Banner("Auto bot");
        while (true)
        {
            GameReader? reader = null;
            MasterDatabase? data = null;
            try
            {
                Console.WriteLine("  Connecting to Umamusume…");
                reader = GameReader.Open(null);
                data = MasterDatabase.Open(MasterDatabase.PathInGameDirectory(reader.GameDirectory));
                Console.WriteLine("  " + ConsoleUi.Dim(ButtonTapper.EnsureAgent(reader.ProcessId)));
                string? reason = Play(reader, data);
                return AfterStop(reason);
            }
            catch (Exception e) when (IsConnectionProblem(e))
            {
                Console.WriteLine();
                int choice = ConsoleUi.Choose("Can't start the auto bot", [new("Try again"), new("Main menu")],
                    detail: ConsoleUi.Warn(e.Message), escape: 1);
                if (choice == 1) return Mode.Menu;
                ConsoleUi.Banner("Auto bot");
            }
            finally
            {
                reader?.Dispose();
                data?.Dispose();
            }
        }
    }

    private static bool IsConnectionProblem(Exception e) =>
        e is InvalidOperationException or FileNotFoundException or System.ComponentModel.Win32Exception or TimeoutException
            or IOException or MemoryReadException or UnsupportedLayoutException or InconsistentSnapshotException;

    /// <summary>What next, once the bot has stopped. <paramref name="reason"/>: why, when it was not the user.</summary>
    private static Mode AfterStop(string? reason)
    {
        Console.WriteLine();
        int choice = ConsoleUi.Choose("Auto bot stopped",
        [
            new("Start the auto bot again"),
            new("Switch to Advisor", "you play, BlazeUma recommends"),
            new("Main menu"),
            new("Exit"),
        ], detail: reason is null ? ConsoleUi.Dim("Nothing else was tapped. The game is as the bot left it.") : ConsoleUi.Warn(reason), escape: 2);
        return choice switch { 0 => Mode.AutoBot, 1 => Mode.Advisor, 2 => Mode.Menu, _ => Mode.Exit };
    }

    /// <summary>Plays until the user stops it or the game closes. Returns why it stopped, or null when the user stopped it.</summary>
    private string? Play(GameReader reader, MasterDatabase data)
    {
        var advisor = new CareerAdvisor(data);
        EnsureFastCutscenes(reader);
        _idle = null;
        _turn = -1;
        _actionsThisTurn = 0;
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("  " + ConsoleUi.Bold("Playing.") + ConsoleUi.Dim("  Insert = pause · Page Down = stop (they work while the game has focus)"));
        control.Arm();
        try
        {
            while (true)
            {
                if (control.StopRequested) return null;
                if (control.PauseRequested)
                {
                    if (!Pause("Paused", null)) return null;
                    continue;
                }

                Outcome outcome;
                string? problem;
                try
                {
                    (outcome, problem) = Round(reader, advisor, data);
                    failures = 0;
                }
                catch (Exception e) when (e is MemoryReadException or InconsistentSnapshotException or UnsupportedLayoutException)
                {
                    if (reader.GameExited) return "Umamusume was closed.";
                    if (++failures < MaxReadFailures) { Thread.Sleep(Poll); continue; }
                    failures = 0;
                    (outcome, problem) = (Outcome.NeedsYou, $"Can't read the game right now: {e.Message}");
                }
                catch (Exception e) when (e is TimeoutException or IOException or InvalidOperationException)
                {
                    // The agent's pipe, or the game going away.
                    if (reader.GameExited) return "Umamusume was closed.";
                    (outcome, problem) = (Outcome.NeedsYou, e.Message);
                }

                if (outcome == Outcome.NeedsYou && !Pause("The auto bot needs you", problem)) return null;
            }
        }
        finally { control.Disarm(); }
    }

    /// <summary>Shows the pause menu. True to resume, false to stop.</summary>
    private bool Pause(string title, string? reason)
    {
        control.Disarm();
        ConsoleUi.BringToFront();
        Console.WriteLine();
        string detail = (reason is null ? "" : ConsoleUi.Warn(reason) + "\n")
                        + ConsoleUi.Dim("You can play by hand in the game meanwhile; the bot picks up from whatever screen it finds.");
        int choice = ConsoleUi.Choose(title, [new("Resume"), new("Stop")], detail);
        if (choice == 1) return false;
        Console.WriteLine("  " + ConsoleUi.Bold("Resuming.") + ConsoleUi.Dim("  Insert = pause · Page Down = stop"));
        _idle = null;
        _actionsThisTurn = 0;
        control.Arm();
        return true;
    }

    /// <summary>Reads the game and carries out one advisor pick.</summary>
    private (Outcome, string?) Round(GameReader reader, CareerAdvisor advisor, MasterDatabase data)
    {
        var snapshot = reader.Snapshot();
        var advice = advisor.Advise(snapshot);
        int turn = snapshot.Turn ?? 0;

        string? action =
            RaceRunner.InProgress(reader.ManagedReader) ? ActionPlanner.GoalRace   // left mid-race (pause, by hand)
            : advice.Situation == Situation.GoalRaceDay ? ActionPlanner.GoalRace
            : advice.Situation is Situation.Event or Situation.TrainingTurn ? advice.Best?.Label
            : null;
        if (action is null)
        {
            // Loading or a transition, or a screen the bot does not handle yet.
            _idle ??= Stopwatch.StartNew();
            if (_idle.Elapsed < IdleLimit) { Thread.Sleep(Poll); return (Outcome.Done, null); }
            _idle = null;
            return (Outcome.NeedsYou, $"{advice.Summary}. The auto bot doesn't handle this screen yet; do it by hand in the game, then resume.");
        }
        _idle = null;

        if (turn != _turn)
        {
            _turn = turn;
            _actionsThisTurn = 0;
            Console.WriteLine();
            Console.WriteLine("  " + ConsoleUi.Dim(new string('─', 70)));
            Console.WriteLine("  " + ConsoleUi.Paint(advice.Header, "36"));
        }
        if (++_actionsThisTurn > MaxActionsPerTurn)
            return (Outcome.NeedsYou, $"Turn {turn} has not moved on after {MaxActionsPerTurn} actions; check the game.");
        Console.WriteLine("  " + ConsoleUi.Accent("▶ " + action) + ConsoleUi.Dim(action == advice.Summary ? "" : $"  ({advice.Summary})"));

        var bonds = snapshot.Character?.Bonds ?? [];
        var plan = ActionPlanner.For(action,
            slot => bonds.FirstOrDefault(b => b.TargetId == slot) is { } bond ? data.CharacterName(bond.CharaId) : null,
            name => data.ProgramsForTurn(turn)
                .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.ProgramId);
        if (plan.Unsupported is not null) return (Outcome.NeedsYou, $"{action}: {plan.Unsupported}.");

        var result = Execute(reader, plan, turn, out string? problem);
        if (result == Outcome.Done) Thread.Sleep(Settle);
        return (result, problem);
    }

    private Outcome Execute(GameReader reader, ActionPlan plan, int startTurn, out string? problem)
    {
        problem = null;
        var r = reader.ManagedReader;
        var tapper = new ButtonTapper(reader);

        // A race left mid-way (paused, stopped, crash) resumes from the screen it is on.
        bool resume = plan.RunsRace && RaceRunner.InProgress(r);
        if (resume) Console.WriteLine("   A race is already under way; resuming from the current screen.");

        for (int i = 0; i < plan.Steps.Count && !resume; i++)
        {
            var step = plan.Steps[i];
            var target = step.Resolve(reader);
            if (target.SkipReason is not null) continue;
            if (target.Error is not null) { problem = $"{plan.Action}, step {i + 1} ({step.Description}): {target.Error}."; return Outcome.NeedsYou; }

            Console.WriteLine(ConsoleUi.Dim($"   • {step.Description}"));
            string reply = target.Method is { } m
                ? tapper.Invoke(target.Button, m[..m.LastIndexOf('.')], m[(m.LastIndexOf('.') + 1)..])
                : tapper.Tap(target.Button);
            if (!reply.StartsWith("ok")) { problem = $"Tap failed ({step.Description}): {reply}"; return Outcome.NeedsYou; }

            WaitAfter(reader, step, startTurn);
            if (TurnNow(reader) > startTurn || (!plan.IsEvent && EventShowing(reader))) return Outcome.Done;
        }

        if (plan.RunsRace && (resume || TurnNow(reader) <= startTurn))
        {
            Console.WriteLine(ConsoleUi.Dim("   Running the race:"));
            if (RaceRunner.Run(reader, tapper, startTurn, RaceStep, resume) != 0)
            {
                if (control.Halting) return Outcome.Interrupted;
                problem = "The race screens need you (see above). Finish them by hand, or resume to try again.";
                return Outcome.NeedsYou;
            }
        }

        // Popups that follow an action (the Rest confirmation, …) have a real choice: the user picks.
        while (TurnNow(reader) <= startTurn && !EventShowing(reader))
        {
            var dialog = DialogButtons(r);
            if (dialog.Count == 0) break;
            var pick = AskDialog(r, dialog);
            if (pick is null) { problem = "A popup is open in the game; answer it by hand, then resume."; return Outcome.NeedsYou; }
            string reply = tapper.Tap(pick.Address);
            if (!reply.StartsWith("ok")) { problem = $"Tap failed: {reply}"; return Outcome.NeedsYou; }
            WaitUntil(() => TurnNow(reader) > startTurn || EventShowing(reader) || DialogButtons(r).All(b => b.Address != pick.Address), TimeSpan.FromSeconds(30));
        }
        return Outcome.Done;
    }

    /// <summary>The race runner asks before each tap: a pending pause or stop ends the race run there ('q').</summary>
    private char RaceStep(string description)
    {
        if (control.Halting) return 'q';
        Console.WriteLine(ConsoleUi.Dim($"   • {description}"));
        return '\n';
    }

    /// <summary>Asks the user which button of an open popup to tap; null to leave it and pause.</summary>
    private FoundButton? AskDialog(ManagedReader r, IReadOnlyList<FoundButton> dialog)
    {
        control.Disarm();
        ConsoleUi.BringToFront();
        string title = ActionPlanner.DialogTitle(r) ?? "(untitled)";
        Console.WriteLine();
        var items = dialog.Select(b => new MenuItem(ButtonName(b.Path))).Append(new MenuItem("Leave it", "pause the bot")).ToList();
        int choice = ConsoleUi.Choose($"The game is asking: \"{title}\"", items, escape: items.Count - 1);
        control.Arm(clear: false);
        return choice < dialog.Count ? dialog[choice] : null;
    }

    private static string ButtonName(string path) => path[(path.LastIndexOf('.') + 1)..] switch
    {
        "_leftButton" => "Left button (usually Cancel)",
        "_rightButton" => "Right button (usually OK)",
        "_centerButton" => "Center button (OK / Close)",
        var name => name.TrimStart('_'),
    };

    private static void WaitAfter(GameReader reader, PlanStep step, int startTurn)
    {
        var r = reader.ManagedReader;
        switch (step.Wait)
        {
            case WaitFor.TrainingMenu: WaitUntil(() => InTraining(r) ?? false, TimeSpan.FromSeconds(5)); break;
            case WaitFor.TileSelected:
                WaitUntil(() => TrainingMenu.ReadTiles(r).Any(t => t.CommandId == step.CommandId && t.IsSelected), TimeSpan.FromSeconds(5));
                break;
            case WaitFor.Dialog: WaitUntil(() => DialogButtons(r).Count > 0 || TurnNow(reader) > startTurn, TimeSpan.FromSeconds(5)); break;
            case WaitFor.TurnAdvance:
                WaitUntil(() => TurnNow(reader) > startTurn || EventShowing(reader) || DialogButtons(r).Count > 0, TimeSpan.FromSeconds(90));
                break;
            case WaitFor.RaceList: WaitUntil(() => RaceEntryScreen.Read(r) is not null, TimeSpan.FromSeconds(8)); break;
            case WaitFor.RaceSelected:
                WaitUntil(() => RaceEntryScreen.Read(r) is { } list && list.SelectedProgramId ==
                    (step.CommandId != 0 ? step.CommandId : list.Rows.FirstOrDefault(x => x.IsGoal && x.EntryState == 0)?.ProgramId), TimeSpan.FromSeconds(5));
                break;
            case WaitFor.DialogClosed: WaitUntil(() => DialogButtons(r).Count == 0, TimeSpan.FromSeconds(5)); break;
            case WaitFor.EventDone: WaitUntil(() => !EventShowing(reader), TimeSpan.FromSeconds(60)); break;
            case WaitFor.LeftRaceList: WaitUntil(() => RaceEntryScreen.Read(r) is null || DialogButtons(r).Count > 0, TimeSpan.FromSeconds(10)); break;
        }
    }

    /// <summary>Polls <paramref name="condition"/> until it holds or <paramref name="timeout"/> passes; false on timeout.</summary>
    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            try { if (condition()) return true; }
            catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException or InconsistentSnapshotException) { }
            Thread.Sleep(Poll);
        }
        return false;
    }

    /// <summary>
    /// Career cutscene speed (training, events), shown on the footer Skip button: 0 = off, 1 = ▶, 2 = ▶▶ (the
    /// career maximum). Each Skip tap cycles 0 → 1 → 2 → 0 (<c>PartsSingleModeCommonFooter.OnClickSkip</c>).
    /// </summary>
    private const int FastestCutscenes = 2;
    private const string FooterSkip = "UI._singleModeFooter._skipButton";

    private static int? CutsceneSpeed(ManagedReader r)
    {
        try
        {
            ulong saves = r.SingletonInstance("Gallop.SaveDataManager");
            ulong settings = saves == 0 ? 0 : r.Ptr(r.Ptr(saves, "_applicationSettingSaveLoder"), "_container");
            return settings == 0 ? null : r.ObscuredInt(settings, "TrainingHighSpeedType");
        }
        catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException) { return null; }
    }

    /// <summary>Taps the footer Skip until cutscenes play at ▶▶.</summary>
    private static void EnsureFastCutscenes(GameReader reader)
    {
        var r = reader.ManagedReader;
        var tapper = new ButtonTapper(reader);
        for (int tap = 0; tap < 3; tap++)
        {
            int? speed = CutsceneSpeed(r);
            if (speed is null or FastestCutscenes) return;
            var skip = new ButtonFinder(r).FromCurrentScene().FirstOrDefault(b => b.Path == FooterSkip && b.CanTap);
            if (skip is null || tapper.IsActive(skip.Address) != true) { Console.WriteLine(ConsoleUi.Dim($"  Cutscene speed is {speed}; Skip is not on screen to raise it.")); return; }
            string reply = tapper.Tap(skip.Address);
            Console.WriteLine(ConsoleUi.Dim($"  Cutscene speed {speed} → tap Skip ({reply})"));
            if (!reply.StartsWith("ok")) return;
            Thread.Sleep(Poll);
        }
    }

    private static IReadOnlyList<FoundButton> DialogButtons(ManagedReader r) =>
        // _inputBlock is an invisible full-screen blocker; tapping it can count as tapping outside (cancel).
        new ButtonFinder(r).FromCurrentScene()
            .Where(b => b.CanTap && b.Path.StartsWith("Dialog[") && !b.Path.EndsWith("_inputBlock")).ToList();

    private static bool? InTraining(ManagedReader r)
    {
        ulong scene = r.SingletonInstance("Gallop.SceneManager");
        ulong controller = scene == 0 ? 0 : r.Ptr(scene, "_currentViewController");
        ulong training = controller == 0 ? 0 : r.Ptr(controller, "TrainingController");
        return training == 0 ? null : r.Bool(training, "IsInTraining");
    }

    private static int TurnNow(GameReader reader)
    {
        try { return reader.Snapshot().Turn ?? 0; }
        catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException or InconsistentSnapshotException) { return 0; }
    }

    private static bool EventShowing(GameReader reader)
    {
        try { return reader.Snapshot().Story is { LooksActive: true }; }
        catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException or InconsistentSnapshotException) { return false; }
    }
}
