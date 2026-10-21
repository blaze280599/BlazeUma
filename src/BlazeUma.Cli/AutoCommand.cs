using System.Diagnostics;
using BlazeUma.Advisor;
using BlazeUma.Automation;
using BlazeUma.Data;
using BlazeUma.Memory;
using BlazeUma.Memory.Game;
using BlazeUma.Memory.Il2Cpp;

namespace BlazeUma.Cli;

/// <summary>
/// BlazeUma.Cli auto plan|run [--action "Train Wit"] [--pid N] [--metadata PATH] [--master PATH]
/// plan: show the taps for the advisor's pick (or --action) without doing anything.
/// run:  supervised: confirm each tap with Enter (s = skip step, q = stop, a = do the rest of this action
///       without asking). Races run on through entry, the race and results to the next turn. Other dialogs
///       that open afterwards (confirmations) are listed so you choose the button.
///       --yes taps without asking (no console needed); a dialog that needs a choice stops the run.
/// </summary>
internal static class AutoCommand
{
    public const string Usage = "usage: BlazeUma.Cli auto plan|run [--action \"Train Wit\"] [--yes] [--pid N] [--metadata PATH] [--master PATH]";

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);
    /// <summary>Events answered in the same run after the action (e.g. a post-race event).</summary>
    private const int MaxFollowUpEvents = 3;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("plan" or "run")) { Console.Error.WriteLine(Usage); return 2; }
        bool execute = args[0] == "run";
        int? pid = null;
        string? metadata = null, master = null, action = null;
        bool resolveAll = false;
        bool yes = false;          // run: tap without asking; dialogs that need a choice stop the run   // diagnostic: resolve every step against the current screen
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pid": pid = int.Parse(args[++i]); break;
                case "--metadata": metadata = args[++i]; break;
                case "--master": master = args[++i]; break;
                case "--action": action = args[++i]; break;
                case "--resolve-all": resolveAll = true; break;
                case "--yes": yes = true; break;
                default: Console.Error.WriteLine(Usage); return 2;
            }
        }
        if (execute && !yes && Console.IsInputRedirected)
        {
            Console.Error.WriteLine("auto run is supervised and needs an interactive console (or --yes to tap without asking).");
            return 2;
        }

        using var reader = GameReader.Open(pid, metadata);
        using var data = MasterDatabase.Open(master ?? MasterDatabase.PathInGameDirectory(reader.GameDirectory));
        for (int round = 0; ; round++)
        {
            var snapshot = reader.Snapshot();
            var advice = new CareerAdvisor(data).Advise(snapshot);
            Console.WriteLine(advice.Header);

            if (action is null)
            {
                if (RaceRunner.InProgress(reader.ManagedReader))
                {
                    // Entry confirmation or race screens left open by a stopped run: finish that race.
                    action = ActionPlanner.GoalRace;
                    Console.WriteLine("A race is under way (entry confirmation, race screens or after-race popups).");
                }
                else if (advice.Situation == Situation.GoalRaceDay)
                {
                    action = ActionPlanner.GoalRace;
                    Console.WriteLine($"Advisor: {advice.Summary}");
                }
                else if (advice.Situation == Situation.Event && advice.Best is not null)
                {
                    action = advice.Best.Label;
                    Console.WriteLine($"Advisor: {advice.Summary}");
                }
                else if (advice.Situation != Situation.TrainingTurn || advice.Best is null)
                {
                    Console.WriteLine($"Advisor: {advice.Summary}. Only home-screen actions are automated so far; do this one by hand.");
                    return 0;
                }
                else if (round > 0)
                {
                    // Follow-ups only answer events; the next turn's action is a new run.
                    Console.WriteLine($"Advisor: {advice.Summary}. Stopping here; run auto run again for this turn's action.");
                    return 0;
                }
                else
                {
                    action = advice.Best.Label;
                    Console.WriteLine($"Advisor pick: {action}");
                }
            }
            else
            {
                Console.WriteLine($"Requested: {action} (advisor would pick {advice.Best?.Label ?? advice.Summary})");
            }

            var bonds = snapshot.Character?.Bonds ?? [];
            var plan = ActionPlanner.For(action,
                slot => bonds.FirstOrDefault(b => b.TargetId == slot) is { } bond ? data.CharacterName(bond.CharaId) : null,
                name => data.ProgramsForTurn(snapshot.Turn ?? 0)
                    .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.ProgramId);
            if (plan.Unsupported is not null) { Console.WriteLine($"{action}: {plan.Unsupported}."); return 0; }

            var r = reader.ManagedReader;
            for (int i = 0; i < plan.Steps.Count && !(plan.RunsRace && RaceRunner.InProgress(r)); i++)
            {
                string target = i == 0 || resolveAll ? Describe(plan.Steps[i].Resolve(reader)) : "(found on screen when it runs)";
                Console.WriteLine($"  {i + 1}. {plan.Steps[i].Description}  {target}");
            }
            Console.WriteLine(plan.RunsRace
                ? "  then: confirm entry, View Results, skip the result scenes and popups, until home on the next turn"
                : plan.IsEvent ? "  then: wait for the event to finish (the turn's action is a separate run)"
                : "  then: pick from any dialog that opens, until the turn advances");
            if (!execute) return 0;

            if (round == 0) Console.WriteLine(ButtonTapper.EnsureAgent(reader.ProcessId));
            int result = Execute(reader, plan, snapshot.Turn ?? 0, yes);
            // An event right after an action (after a race, on the next turn) is part of finishing it.
            if (result == 0 && round < MaxFollowUpEvents && EventShowing(reader))
            {
                Console.WriteLine();
                Console.WriteLine("An event followed; answering it.");
                action = null;
                continue;
            }
            return result;
        }
    }

    private static string Describe(StepTarget t) =>
        t.Error is not null ? $"[cannot: {t.Error}]" : t.SkipReason is not null ? $"[skip: {t.SkipReason}]" : $"→ {t.Where}";

    private static int Execute(GameReader reader, ActionPlan plan, int startTurn, bool yes)
    {
        var r = reader.ManagedReader;
        var tapper = new ButtonTapper(reader);
        bool auto = yes;
        char Confirm(string question)
        {
            if (auto) { Console.WriteLine($"{question}  (auto)"); return '\n'; }
            char c = Ask(question);
            if (c == 'a') auto = true;
            return c;
        }

        // A race left mid-way (stopped run, crash) resumes from the screen it is on.
        bool resume = plan.RunsRace && RaceRunner.InProgress(r);
        if (resume) Console.WriteLine("A race is already under way; resuming from the current screen.");

        for (int i = 0; i < plan.Steps.Count && !resume; i++)
        {
            var step = plan.Steps[i];
            var target = step.Resolve(reader);
            if (target.SkipReason is not null) { Console.WriteLine($"Step {i + 1}: skipped, {target.SkipReason}."); continue; }
            if (target.Error is not null) { Console.WriteLine($"Step {i + 1}: stopping, {target.Error}."); return 1; }

            switch (Confirm($"Step {i + 1}/{plan.Steps.Count}: {step.Description}  → {target.Where}"))
            {
                case 'q': Console.WriteLine("Stopped. Nothing else was tapped."); return 0;
                case 's': Console.WriteLine("Skipped."); continue;
            }
            string reply = target.Method is { } m
                ? tapper.Invoke(target.Button, m[..m.LastIndexOf('.')], m[(m.LastIndexOf('.') + 1)..])
                : tapper.Tap(target.Button);
            if (!reply.StartsWith("ok")) { Console.WriteLine($"Tap failed: {reply}"); return 1; }

            string result = WaitAfter(reader, step, startTurn);
            Console.WriteLine($"   {result}");
            if (TurnNow(reader) > startTurn || (!plan.IsEvent && EventShowing(reader))) return Finish(reader, startTurn);
        }

        if (plan.RunsRace && (resume || TurnNow(reader) <= startTurn))
        {
            Console.WriteLine("Running the race:");
            RaceRunner.Run(reader, tapper, startTurn, Confirm, resume);
        }

        // Follow-up: confirmations and pickers. The user chooses; nothing is guessed.
        while (TurnNow(reader) <= startTurn && !EventShowing(reader))
        {
            var dialog = DialogButtons(r);
            if (dialog.Count == 0) break;
            Console.WriteLine("A dialog is open:");
            for (int j = 0; j < dialog.Count; j++) Console.WriteLine($"  {j + 1}. {dialog[j].Path}");
            if (yes) { Console.WriteLine("Not choosing a dialog button unasked (--yes); pick it by hand."); break; }
            Console.Write($"Tap which (1-{dialog.Count}), Enter = leave it, q = stop: ");
            string? answer = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(answer) || answer == "q") break;
            if (!int.TryParse(answer, out int pick) || pick < 1 || pick > dialog.Count) { Console.WriteLine("Not a choice."); continue; }
            string reply = tapper.Tap(dialog[pick - 1].Address);
            if (!reply.StartsWith("ok")) { Console.WriteLine($"Tap failed: {reply}"); return 1; }
            Console.WriteLine($"   {WaitUntil(() => TurnNow(reader) > startTurn || EventShowing(reader) || DialogButtons(r).All(b => b.Address != dialog[pick - 1].Address), TimeSpan.FromSeconds(30), "screen changed", "no change yet")}");
        }
        return Finish(reader, startTurn);
    }

    private static int Finish(GameReader reader, int startTurn)
    {
        int now = TurnNow(reader);
        if (EventShowing(reader)) Console.WriteLine("An event is on screen; run `auto plan` or `--advise` for the choice.");
        Console.WriteLine(now > startTurn ? $"Done: turn {startTurn} → {now}." : $"Turn is still {startTurn}.");
        try
        {
            if (now <= startTurn && RaceEntryScreen.Read(reader.ManagedReader) is null && DialogButtons(reader.ManagedReader).Count == 0
                && reader.Snapshot().Screen?.ViewController != "Gallop.SingleModeMainViewController")
                Console.WriteLine("This screen is not automated yet; continue by hand. " +
                                  "`buttons --watch` records each screen so it can be added.");
        }
        catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException or InconsistentSnapshotException) { }
        return 0;
    }

    private static string WaitAfter(GameReader reader, PlanStep step, int startTurn)
    {
        var r = reader.ManagedReader;
        return step.Wait switch
        {
            WaitFor.TrainingMenu => WaitUntil(() => InTraining(r) ?? false, TimeSpan.FromSeconds(5), "training menu open", "training menu did not report open; continuing"),
            WaitFor.TileSelected => WaitUntil(() => TrainingMenu.ReadTiles(r).Any(t => t.CommandId == step.CommandId && t.IsSelected),
                TimeSpan.FromSeconds(5), "tile selected", "tile did not become selected"),
            WaitFor.Dialog => WaitUntil(() => DialogButtons(r).Count > 0 || TurnNow(reader) > startTurn, TimeSpan.FromSeconds(5), "dialog open", "no dialog appeared"),
            WaitFor.TurnAdvance => WaitUntil(() => TurnNow(reader) > startTurn || EventShowing(reader) || DialogButtons(r).Count > 0,
                TimeSpan.FromSeconds(90), "screen moved on", "turn did not advance within 90 s"),
            WaitFor.RaceList => WaitUntil(() => RaceEntryScreen.Read(r) is not null, TimeSpan.FromSeconds(8), "race list open", "race list did not open"),
            WaitFor.RaceSelected => WaitUntil(() => RaceEntryScreen.Read(r) is { } list && list.SelectedProgramId ==
                    (step.CommandId != 0 ? step.CommandId : list.Rows.FirstOrDefault(x => x.IsGoal && x.EntryState == 0)?.ProgramId),
                TimeSpan.FromSeconds(5), "race selected", "race did not become selected"),
            WaitFor.DialogClosed => WaitUntil(() => DialogButtons(r).Count == 0, TimeSpan.FromSeconds(5), "popup closed", "popup still open"),
            WaitFor.EventDone => WaitUntil(() => !EventShowing(reader), TimeSpan.FromSeconds(60), "event answered", "event still showing after 60 s"),
            WaitFor.LeftRaceList => WaitUntil(() => RaceEntryScreen.Read(r) is null || DialogButtons(r).Count > 0,
                TimeSpan.FromSeconds(10), "left the race list", "still on the race list"),
            _ => "done",
        };
    }

    private static string WaitUntil(Func<bool> condition, TimeSpan timeout, string success, string failure)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            try { if (condition()) return $"{success} ({clock.Elapsed.TotalSeconds:0.0} s)"; }
            catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException or InconsistentSnapshotException) { }
            Thread.Sleep(Poll);
        }
        return failure;
    }

    private static char Ask(string question)
    {
        Console.Write($"{question}\n   Enter = tap, s = skip, q = stop, a = all the rest: ");
        string answer = Console.ReadLine()?.Trim().ToLowerInvariant() ?? "q";
        return answer.Length == 0 ? '\n' : answer[0];
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
