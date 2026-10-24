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
    /// <summary>Taps of a select (training tile, race row) before giving up when the game does not take it.</summary>
    private const int MaxSelectTaps = 3;
    /// <summary>The training tiles slide in after the menu reports open; a tap before that can be dropped.</summary>
    private static readonly TimeSpan MenuAnimation = TimeSpan.FromMilliseconds(600);
    /// <summary>
    /// How long the game must stay calm (no event scene, screen change or input lock) before every tap, so menus and
    /// popups finish animating (tuned by eye on the live game).
    /// </summary>
    private static readonly TimeSpan StepGap = TimeSpan.FromSeconds(1);
    /// <summary>
    /// The same before a plan's first tap. A turn's opening event starts a moment after the home screen shows: on
    /// turn 56 Races was tapped ~1 s before "A Trainer's Knowledge" began, the list opened over its dialogue, and a
    /// later time the scene froze for good under it.
    /// </summary>
    private static readonly TimeSpan FirstTapCalm = TimeSpan.FromSeconds(3);
    /// <summary>An event scene whose frame has not moved for this long has frozen (the game is softlocked).</summary>
    private static readonly TimeSpan FrozenSceneLimit = TimeSpan.FromSeconds(10);
    /// <summary>How long the game may stay busy (screen change, input lock, an event scene) before the bot asks for help.</summary>
    private static readonly TimeSpan BusyLimit = TimeSpan.FromSeconds(90);
    /// <summary>How long a step waits for a screen change or input lock to end before leaving the plan to the next round.</summary>
    private static readonly TimeSpan StepReadyLimit = TimeSpan.FromSeconds(10);
    /// <summary>Plans per turn that may stop on an unexpected screen and be planned again from the live screen.</summary>
    private const int MaxRecoveries = 3;

    /// <summary>
    /// <see cref="Deferred"/>: the plan stopped before a tap because the game got busy; the next round reads it afresh.
    /// <see cref="Recover"/>: a step found a screen it did not expect before the action's own tap (a dropped tap left
    /// the race list closed); nothing irreversible was done, so the next round plans again from the screen it finds.
    /// </summary>
    private enum Outcome { Done, Interrupted, NeedsYou, CareerCompleted, Deferred, Recover }

    private Stopwatch? _idle;
    /// <summary>Since when the game has been busy (<see cref="Busy"/>); null while it takes taps.</summary>
    private Stopwatch? _busy;
    /// <summary>The event scene's last frame, and since when it has stayed there.</summary>
    private (int Frame, Stopwatch Since)? _sceneFrame;
    private int _turn, _actionsThisTurn, _recoveries;
    /// <summary>
    /// A turn whose action already went through (it ended in an event). After the event the home screen can
    /// show the same turn again for a moment; only events are answered until the turn number moves on.
    /// </summary>
    private int _spentTurn = -1;
    /// <summary>The turn the skill screen was last opened for, so it is opened at most once a turn.</summary>
    private int _skillsTurn = -1;
    /// <summary>The skill screen was opened from the career's last screen; next time there, complete the career.</summary>
    private bool _finalSkillsOpened;
    /// <summary>The same career log as Advisor mode (turns, events and what each event did), next to the exe.</summary>
    private CareerLog? _log;

    /// <summary>The last career this bot completed, for the main menu; null until one is.</summary>
    public CompletedCareer? LastCareer { get; private set; }
    private bool _completed;

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
                if (_completed)
                {
                    Console.WriteLine();
                    Console.WriteLine("  " + ConsoleUi.Accent("Career completed!") + ConsoleUi.Dim("  Back to the main menu…"));
                    Thread.Sleep(TimeSpan.FromSeconds(2));
                    return Mode.Menu;
                }
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
        var (odds, oddsSource) = CareerLogs.LearnedOdds(CareerLogs.DefaultDirectory);
        var advisor = new CareerAdvisor(data, odds);
        EnsureFastCutscenes(reader);
        _idle = null;
        _busy = null;
        _turn = -1;
        _spentTurn = -1;
        _skillsTurn = -1;
        _finalSkillsOpened = false;
        _completed = false;
        _actionsThisTurn = 0;
        _recoveries = 0;
        int failures = 0;
        _log = CareerLog.Create();

        Console.WriteLine();
        Console.WriteLine("  " + ConsoleUi.Dim($"Logging to {CareerLogs.DefaultDirectory}; {oddsSource}."));
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

                if (outcome == Outcome.CareerCompleted) { _completed = true; return null; }
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
        _busy = null;
        _sceneFrame = null;
        _actionsThisTurn = 0;
        _recoveries = 0;
        _spentTurn = -1;   // the user has looked at the game; judge the turn afresh
        control.Arm();
        return true;
    }

    /// <summary>Reads the game and carries out one advisor pick.</summary>
    private (Outcome, string?) Round(GameReader reader, CareerAdvisor advisor, MasterDatabase data)
    {
        var r = reader.ManagedReader;
        // Nothing is tapped while the game loads, changes screens or locks input.
        if (UiState.Busy(r) is { } busy) return Busy(busy);
        bool storyPlaying = UiState.StoryPlaying(r);
        // A goal met outside a race (a fan goal reached by a training) opens "Goal Achieved" over the home screen.
        if (!storyPlaying && RaceRunner.CloseGoalAchieved(reader, new ButtonTapper(reader))) { Thread.Sleep(Settle); return (Outcome.Done, null); }
        var snapshot = _log!.Stamp(reader.Snapshot());
        if (snapshot.Screen?.ViewController == InspirationScreen) return (Inspiration(reader), null);
        var advice = advisor.Advise(snapshot);
        _log.Observe(snapshot, advice);
        int turn = snapshot.Turn ?? 0;
        // An event scene is on screen: only its choices are the bot's to tap. The screen under it (the skill
        // screen, the home screen) still reads as itself, and taps there freeze the game (turn 41).
        if (storyPlaying && advice.Situation != Situation.Event)
        {
            int? frame = UiState.StoryFrame(r);
            if (frame is null) _sceneFrame = null;
            else if (_sceneFrame?.Frame != frame) _sceneFrame = (frame.Value, Stopwatch.StartNew());
            if (_sceneFrame?.Since.Elapsed >= FrozenSceneLimit)
            {
                _sceneFrame = null;
                _busy = null;
                return (Outcome.NeedsYou, $"The event scene has been stuck on frame {frame} for {FrozenSceneLimit.TotalSeconds:0} s: the game looks "
                    + "softlocked under it. Tapping won't free it; restart the game (the career resumes on the same turn), then resume.");
            }
            return Busy("an event scene is playing");
        }
        _sceneFrame = null;
        // Just stopped being busy: give the screen a moment to settle, then read it afresh.
        if (_busy is not null) { _busy = null; Thread.Sleep(Settle); return (Outcome.Done, null); }

        string? action =
            RaceRunner.InProgress(reader.ManagedReader) ? ActionPlanner.GoalRace   // left mid-race (pause, by hand)
            : advice.Situation == Situation.Event ? advice.Best?.Label
            // Back on the race list after an event interrupted entering a race: carry on with that race, or (after
            // a restart, when the bot doesn't know what it was doing) the race list's own pick.
            : advice.Situation == Situation.RaceList ? RaceListAction(advice, turn)
            : advice.Situation == Situation.SkillShop ? ActionPlanner.LearnSkills
            // The last screen: spend the skill points once (the skill screen's Back returns here), then finish.
            : advice.Situation == Situation.CareerEnd
                ? (_finalSkillsOpened || ActionPlanner.DialogTitle(reader.ManagedReader) == ActionPlanner.CompleteCareer ? ActionPlanner.CompleteCareer : ActionPlanner.FinalSkills)
            : turn == _spentTurn ? null                                             // the game is still finishing this turn
            // The advisor buys skills on the turn before a goal race (and on race day): learn them before acting.
            : advice.Situation is Situation.TrainingTurn or Situation.GoalRaceDay && turn != _skillsTurn
              && advice.SkillsToBuy is { Count: > 0 } ? ActionPlanner.OpenSkills
            : advice.Situation == Situation.GoalRaceDay ? ActionPlanner.GoalRace
            : advice.Situation == Situation.TrainingTurn ? advice.Best?.Label
            : null;
        if (action is null)
        {
            // Loading or a transition, or a screen the bot does not handle yet.
            _idle ??= Stopwatch.StartNew();
            if (_idle.Elapsed < IdleLimit) { Thread.Sleep(Poll); return (Outcome.Done, null); }
            _idle = null;
            return (Outcome.NeedsYou, turn == _spentTurn
                ? $"Turn {turn}'s action went through, but the game has not moved on to the next turn; check the game, then resume."
                : $"{advice.Summary}. The auto bot doesn't handle this screen yet; do it by hand in the game, then resume.");
        }
        _idle = null;

        if (turn != _turn)
        {
            _turn = turn;
            _actionsThisTurn = 0;
            _recoveries = 0;
            Console.WriteLine();
            Console.WriteLine("  " + ConsoleUi.Dim(new string('─', 70)));
            Console.WriteLine("  " + ConsoleUi.Paint(advice.Header, "36"));
        }
        if (++_actionsThisTurn > MaxActionsPerTurn)
            return (Outcome.NeedsYou, $"Turn {turn} has not moved on after {MaxActionsPerTurn} actions; check the game.");
        Console.WriteLine("  " + ConsoleUi.Accent("▶ " + action) + ConsoleUi.Dim(action == advice.Summary ? "" : $"  ({advice.Summary})"));

        var bonds = snapshot.Character?.Bonds ?? [];
        if (action == ActionPlanner.OpenSkills) _skillsTurn = turn;
        // The last turn's race already spent some of the per-turn actions; the end screens get their own.
        if (action == ActionPlanner.FinalSkills) { _finalSkillsOpened = true; _actionsThisTurn = 1; }
        var plan = action == ActionPlanner.OpenSkills ? ActionPlanner.SkillScreenOpen(advice.Situation == Situation.GoalRaceDay)
            : action is ActionPlanner.FinalSkills or ActionPlanner.CompleteCareer ? ActionPlanner.CompleteScreenPlan(action)
            : action == ActionPlanner.LearnSkills ? ActionPlanner.SkillShop(advice.SkillsToBuy ?? [], id => data.Skill(id)?.Name ?? $"skill {id}")
            : ActionPlanner.For(action,
                slot => bonds.FirstOrDefault(b => b.TargetId == slot) is { } bond ? data.CharacterName(bond.CharaId) : null,
                name => data.ProgramsForTurn(turn)
                    .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.ProgramId);
        if (plan.Unsupported is not null) return (Outcome.NeedsYou, $"{action}: {plan.Unsupported}.");

        // After the previous turn's training, IsInTraining stays true while the next turn starts, and through any
        // event that opens it, until ~1.5 s after the home screen settles (monitor log, turns 38-41). Acting earlier
        // skips opening the menu and taps the previous menu's hidden tiles (turns 36 and 41). The home screen
        // cannot be told from a menu opened by hand, so a flag that stays set past the limit is trusted.
        if (!plan.IsEvent && !plan.RunsRace)
            WaitUntil(() => TrainingMenu.IsOpen(reader.ManagedReader) != true, TimeSpan.FromSeconds(8));
        var result = Execute(reader, plan, turn, out string? problem, out bool actionTapped);
        if (result == Outcome.Recover)
        {
            if (++_recoveries > MaxRecoveries)
                return (Outcome.NeedsYou, $"{problem} Planned again from the screen {MaxRecoveries} times this turn without getting through.");
            Console.WriteLine(ConsoleUi.Dim($"   {problem} Reading the screen again to recover (try {_recoveries} of {MaxRecoveries})."));
            // A race half-entered (the list open, or not yet): carry on with the same race from wherever it is.
            if (plan.RunsRace) _interruptedRace = (turn, action);
            Thread.Sleep(Settle);
        }
        if (result is Outcome.Deferred or Outcome.Recover)
        {
            // The skill screen wasn't opened after all; let the next round open it this turn.
            if (action == ActionPlanner.OpenSkills) _skillsTurn = -1;
            if (action == ActionPlanner.FinalSkills) _finalSkillsOpened = false;
            _actionsThisTurn--;
            return (Outcome.Done, null);
        }
        // A turn action whose own tap went through and then led to an event (Happy Meek's challenge, a card
        // event…) has used the turn. An event that pops up before that tap (turn 40's camp event) has not.
        // Opening or using the skill screen doesn't use the turn: an event after Tap Skills (turn 41's Dance Lesson)
        // left the bot waiting for a turn that never came.
        bool usesTurn = action is not (ActionPlanner.OpenSkills or ActionPlanner.LearnSkills or ActionPlanner.FinalSkills);
        if (result == Outcome.Done && actionTapped && usesTurn && !plan.IsEvent && TurnNow(reader) == turn && EventShowing(reader)) _spentTurn = turn;
        // An event over the race list before the race was entered: the turn is still free, finish entering it after.
        if (result == Outcome.Done && plan.RunsRace && !actionTapped && TurnNow(reader) == turn && EventShowing(reader))
            _interruptedRace = (turn, action);
        // Finish tapped on "Complete Career": the career is over; the stats were read just before.
        if (result == Outcome.Done && actionTapped && action == ActionPlanner.CompleteCareer)
        {
            LastCareer = CompletedCareer.From(snapshot, data, _log!.Races());
            return (Outcome.CareerCompleted, null);
        }
        if (result == Outcome.Done) Thread.Sleep(Settle);
        return (result, problem);
    }

    /// <summary>Waits a round out while the game is busy; asks for help once it has been busy for <see cref="BusyLimit"/>.</summary>
    private (Outcome, string?) Busy(string reason)
    {
        _idle = null;
        _busy ??= Stopwatch.StartNew();
        if (_busy.Elapsed < BusyLimit) { Thread.Sleep(Poll); return (Outcome.Done, null); }
        _busy = null;
        return (Outcome.NeedsYou, $"The game has been busy for {BusyLimit.TotalSeconds:0} s ({reason}), so the bot hasn't tapped anything. Check the game, then resume.");
    }

    /// <summary>
    /// Waits for the game to take <paramref name="plan"/>'s next tap: calm for <paramref name="calm"/> in a row, with
    /// no screen change or input lock (up to <see cref="StepReadyLimit"/>), and no event scene unless the plan
    /// answers that event. Null when it can, else why not.
    /// </summary>
    private static string? WaitStepReady(GameReader reader, ActionPlan plan, TimeSpan calm)
    {
        var r = reader.ManagedReader;
        var clock = Stopwatch.StartNew();
        var calmFor = Stopwatch.StartNew();
        while (true)
        {
            string? busy;
            try
            {
                busy = !plan.IsEvent && UiState.StoryPlaying(r) ? "an event scene started" : UiState.Busy(r);
                // An event scene won't end within a step's wait: leave it to the next round's event handling.
                if (busy is "an event scene started") return busy;
            }
            catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException or InconsistentSnapshotException) { busy = "the game can't be read"; }
            if (busy is not null) calmFor.Restart();
            else if (calmFor.Elapsed >= calm) return null;
            if (busy is not null && clock.Elapsed >= StepReadyLimit) return busy;
            Thread.Sleep(Poll);
        }
    }

    /// <summary>The race plan an event interrupted on the race list, and its turn.</summary>
    private (int Turn, string Action)? _interruptedRace;

    /// <summary>
    /// What to do on the race list: the race an event interrupted this turn, else the list's best race (a goal row
    /// is the goal race); null when its pick is not to race, so the screen is left to the user.
    /// </summary>
    private string? RaceListAction(Advice advice, int turn)
    {
        if (_interruptedRace is { } interrupted && interrupted.Turn == turn) return interrupted.Action;
        if (advice.Best is not { Score: > 0 } best || best.Label.StartsWith("Don't race:")) return null;
        return best.Reasons.Contains("career goal") ? ActionPlanner.GoalRace : $"Race: {best.Label}";
    }

    /// <summary>The inspiration (succession) cutscene: one GO! button (Senior Early Mar, turn 55).</summary>
    internal const string InspirationScreen = "Gallop.SingleModeSuccessionEventViewController";
    private const string InspirationButton = "View.SuccessionButton";

    /// <summary>Taps GO! once and waits for the cutscene to move on; taps again only if GO! is still showing (3 tries).</summary>
    private Outcome Inspiration(GameReader reader)
    {
        var r = reader.ManagedReader;
        var tapper = new ButtonTapper(reader);
        Console.WriteLine("  " + ConsoleUi.Accent("▶ Inspiration") + ConsoleUi.Dim("  (tap GO!)"));
        bool OnScreen() => reader.Snapshot().Screen?.ViewController == InspirationScreen;
        for (int attempt = 1; attempt <= 3 && !control.Halting; attempt++)
        {
            try
            {
                if (!OnScreen()) break;
                var go = new ButtonFinder(r).FromCurrentScene().FirstOrDefault(b => b.Path == InspirationButton && b.CanTap);
                if (go is null || tapper.IsActive(go.Address) != true) { Thread.Sleep(Poll); continue; }   // not shown yet
                Thread.Sleep(StepGap);
                Console.WriteLine(ConsoleUi.Dim("   • Tap GO!" + (attempt > 1 ? $" (again, try {attempt})" : "")));
                tapper.Tap(go.Address);
            }
            catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException or InconsistentSnapshotException) { }
            if (WaitUntil(() => !OnScreen(), TimeSpan.FromSeconds(5))) break;
        }
        if (control.Halting) return Outcome.Interrupted;
        Thread.Sleep(Settle);
        return Outcome.Done;
    }

    /// <param name="actionTapped">The plan's last step (the action itself: train, rest, outing, race entry) was tapped.</param>
    private Outcome Execute(GameReader reader, ActionPlan plan, int startTurn, out string? problem, out bool actionTapped)
    {
        problem = null;
        actionTapped = false;
        var r = reader.ManagedReader;
        var tapper = new ButtonTapper(reader);

        // A race left mid-way (paused, stopped, crash) resumes from the screen it is on.
        bool resume = plan.RunsRace && RaceRunner.InProgress(r);
        if (resume) Console.WriteLine("   A race is already under way; resuming from the current screen.");

        for (int i = 0; i < plan.Steps.Count && !resume; i++)
        {
            var step = plan.Steps[i];
            // A select tapped while the menu is still animating in can be dropped by the game; its effect can be
            // checked, so tap again. Other taps are not repeated (a second Rest or training tap could act twice).
            int attempts = step.Wait is WaitFor.TileSelected or WaitFor.RaceSelected ? MaxSelectTaps : 1;
            bool took = true;
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                if (WaitStepReady(reader, plan, i == 0 && attempt == 1 ? FirstTapCalm : StepGap) is { } busy)
                {
                    Console.WriteLine(ConsoleUi.Dim($"   • Not tapping \"{step.Description}\": {busy}. Picking up when the game is ready."));
                    return actionTapped ? Outcome.Done : Outcome.Deferred;
                }
                var target = step.Resolve(reader);
                if (target.SkipReason is not null) break;
                if (target.Error is not null)
                {
                    problem = $"{plan.Action}, step {i + 1} ({step.Description}): {target.Error}.";
                    return actionTapped ? Outcome.NeedsYou : Outcome.Recover;
                }

                Console.WriteLine(ConsoleUi.Dim($"   • {step.Description}" + (attempt > 1 ? $" (again, try {attempt})" : "")));
                string reply = target.Method is not { } m ? tapper.Tap(target.Button)
                    : target.AsMouse ? tapper.InvokeAsMouse(target.Button, m[..m.LastIndexOf('.')], m[(m.LastIndexOf('.') + 1)..])
                    : tapper.Invoke(target.Button, m[..m.LastIndexOf('.')], m[(m.LastIndexOf('.') + 1)..]);
                if (!reply.StartsWith("ok")) { problem = $"Tap failed ({step.Description}): {reply}"; return Outcome.NeedsYou; }
                if (i == plan.Steps.Count - 1) actionTapped = true;
                if (took = WaitAfter(reader, step, startTurn)) break;
            }
            if (TurnNow(reader) > startTurn || (!plan.IsEvent && EventShowing(reader))) return Outcome.Done;
            // The action's own tap changed nothing: tapping again would only wait again.
            if (!took && step.Wait == WaitFor.TurnAdvance)
            {
                problem = $"{plan.Action}: \"{step.Description}\" was tapped but nothing happened within 90 s.";
                return Outcome.NeedsYou;
            }
        }

        if (plan.RunsRace && (resume || TurnNow(reader) <= startTurn))
        {
            Console.WriteLine(ConsoleUi.Dim("   Running the race:"));
            if (RaceRunner.Run(reader, tapper, startTurn, RaceStep, resume, () => LogRace(reader)) != 0)
            {
                if (control.Halting) return Outcome.Interrupted;
                problem = "The race screens need you (see above). Finish them by hand, or resume to try again.";
                return Outcome.NeedsYou;
            }
        }

        // Finish ends the career; the results screens after it are not the bot's to answer.
        if (plan.Action == ActionPlanner.CompleteCareer && actionTapped) return Outcome.Done;
        // Popups that follow an action (the Rest confirmation, …) have a real choice: the user picks.
        while (TurnNow(reader) <= startTurn && !EventShowing(reader) && !StoryPlaying(reader))
        {
            var dialog = DialogButtons(r);
            if (dialog.Count == 0) break;
            // Only offer what is showing: a two-button popup still reports its hidden Center button.
            dialog = dialog.Where(b => tapper.IsActive(b.Address) == true).ToList();
            if (dialog.Count == 0) dialog = DialogButtons(r);
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

    /// <summary>Waits for what <paramref name="step"/>'s tap should lead to; false if it did not happen in time.</summary>
    private static bool WaitAfter(GameReader reader, PlanStep step, int startTurn)
    {
        var r = reader.ManagedReader;
        switch (step.Wait)
        {
            case WaitFor.TrainingMenu:
                bool open = WaitUntil(() => TrainingMenu.IsOpen(r) ?? false, TimeSpan.FromSeconds(5));
                Thread.Sleep(MenuAnimation);   // the tiles only take taps once they have slid in
                return open;
            case WaitFor.TileSelected:
                return WaitUntil(() => TrainingMenu.ReadTiles(r).Any(t => TrainingIds.Base(t.CommandId) == step.CommandId && t.IsSelected), TimeSpan.FromSeconds(3));
            case WaitFor.Dialog: return WaitUntil(() => DialogButtons(r).Count > 0 || TurnNow(reader) > startTurn, TimeSpan.FromSeconds(5));
            case WaitFor.TurnAdvance:
                return WaitUntil(() => TurnNow(reader) > startTurn || EventShowing(reader) || DialogButtons(r).Count > 0, TimeSpan.FromSeconds(90));
            case WaitFor.RaceList: return WaitUntil(() => RaceEntryScreen.Read(r) is not null, TimeSpan.FromSeconds(8));
            case WaitFor.RaceSelected:
                return WaitUntil(() => RaceEntryScreen.Read(r) is { } list && list.SelectedProgramId ==
                    (step.CommandId != 0 ? step.CommandId : list.Rows.FirstOrDefault(x => x.IsGoal && x.EntryState == 0)?.ProgramId), TimeSpan.FromSeconds(3));
            case WaitFor.DialogClosed: return WaitUntil(() => DialogButtons(r).Count == 0, TimeSpan.FromSeconds(5));
            case WaitFor.EventDone: return WaitUntil(() => !EventShowing(reader), TimeSpan.FromSeconds(60));
            case WaitFor.LeftRaceList: return WaitUntil(() => RaceEntryScreen.Read(r) is null || DialogButtons(r).Count > 0, TimeSpan.FromSeconds(10));
            case WaitFor.SkillScreen:
                bool shown = WaitUntil(() => reader.Snapshot().Screen?.ViewController == ActionPlanner.SkillScreen, TimeSpan.FromSeconds(8));
                Thread.Sleep(MenuAnimation);   // the list slides in
                return shown;
            case WaitFor.SkillSelected:
                return WaitUntil(() => ActionPlanner.SkillRow(r, step.CommandId) is { Selected: true }, TimeSpan.FromSeconds(3));
            case WaitFor.SkillsLearned:
                return WaitUntil(() => ActionPlanner.DialogIndex(r, ActionPlanner.SkillsLearnedTitle) is not null, TimeSpan.FromSeconds(8));
            case WaitFor.LeftSkillScreen:
                return WaitUntil(() => reader.Snapshot().Screen?.ViewController != ActionPlanner.SkillScreen, TimeSpan.FromSeconds(8));
            default: return true;
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

    /// <summary>Logs the placing on the open results list; a failed read only loses this race's line.</summary>
    private void LogRace(GameReader reader)
    {
        try
        {
            if (_log!.RecordRace(reader.Snapshot()) is { } race)
                Console.WriteLine(ConsoleUi.Dim($"   Placed {RaceSummary.Ordinal(race.Place)} of {race.Runners}"
                    + (race.Attempt > 1 ? $" (run {race.Attempt})" : "") + (race.AlarmClocks is int c ? $"; {c} alarm clocks left." : ".")));
        }
        catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException or InconsistentSnapshotException) { }
    }

    private static int TurnNow(GameReader reader)
    {
        try { return reader.Snapshot().Turn ?? 0; }
        catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException or InconsistentSnapshotException) { return 0; }
    }

    private static bool StoryPlaying(GameReader reader)
    {
        try { return UiState.StoryPlaying(reader.ManagedReader); }
        catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException) { return false; }
    }

    private static bool EventShowing(GameReader reader)
    {
        try { return reader.Snapshot().Story is { LooksActive: true }; }
        catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException or InconsistentSnapshotException) { return false; }
    }
}
