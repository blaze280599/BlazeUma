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
/// run:  supervised: confirm each tap with Enter (s = skip step, q = stop). Dialogs that open afterwards
///       (confirmations, the outing picker) are listed so you choose the button.
/// </summary>
internal static class AutoCommand
{
    public const string Usage = "usage: BlazeUma.Cli auto plan|run [--action \"Train Wit\"] [--pid N] [--metadata PATH] [--master PATH]";

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("plan" or "run")) { Console.Error.WriteLine(Usage); return 2; }
        bool execute = args[0] == "run";
        int? pid = null;
        string? metadata = null, master = null, action = null;
        bool resolveAll = false;   // diagnostic: resolve every step against the current screen
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pid": pid = int.Parse(args[++i]); break;
                case "--metadata": metadata = args[++i]; break;
                case "--master": master = args[++i]; break;
                case "--action": action = args[++i]; break;
                case "--resolve-all": resolveAll = true; break;
                default: Console.Error.WriteLine(Usage); return 2;
            }
        }
        if (execute && Console.IsInputRedirected)
        {
            Console.Error.WriteLine("auto run is supervised and needs an interactive console.");
            return 2;
        }

        using var reader = GameReader.Open(pid, metadata);
        using var data = MasterDatabase.Open(master ?? MasterDatabase.PathInGameDirectory(reader.GameDirectory));
        var snapshot = reader.Snapshot();
        var advice = new CareerAdvisor(data).Advise(snapshot);
        Console.WriteLine(advice.Header);

        if (action is null)
        {
            if (advice.Situation != Situation.TrainingTurn || advice.Best is null)
            {
                Console.WriteLine($"Advisor: {advice.Summary}. Only home-screen actions are automated so far; do this one by hand.");
                return 0;
            }
            action = advice.Best.Label;
            Console.WriteLine($"Advisor pick: {action}");
        }
        else
        {
            Console.WriteLine($"Requested: {action} (advisor would pick {advice.Best?.Label ?? advice.Summary})");
        }

        var bonds = snapshot.Character?.Bonds ?? [];
        var plan = ActionPlanner.For(action, slot =>
            bonds.FirstOrDefault(b => b.TargetId == slot) is { } bond ? data.CharacterName(bond.CharaId) : null);
        if (plan.Unsupported is not null) { Console.WriteLine($"{action}: {plan.Unsupported}."); return 0; }

        var r = reader.ManagedReader;
        for (int i = 0; i < plan.Steps.Count; i++)
        {
            string target = i == 0 || resolveAll ? Describe(plan.Steps[i].Resolve(reader)) : "(found on screen when it runs)";
            Console.WriteLine($"  {i + 1}. {plan.Steps[i].Description}  {target}");
        }
        Console.WriteLine("  then: pick from any dialog that opens, until the turn advances");
        if (!execute) return 0;

        Console.WriteLine(ButtonTapper.EnsureAgent(reader.ProcessId));
        return Execute(reader, plan, snapshot.Turn ?? 0);
    }

    private static string Describe(StepTarget t) =>
        t.Error is not null ? $"[cannot: {t.Error}]" : t.SkipReason is not null ? $"[skip: {t.SkipReason}]" : $"→ {t.Where}";

    private static int Execute(GameReader reader, ActionPlan plan, int startTurn)
    {
        var r = reader.ManagedReader;
        var tapper = new ButtonTapper(reader);

        for (int i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            var target = step.Resolve(reader);
            if (target.SkipReason is not null) { Console.WriteLine($"Step {i + 1}: skipped, {target.SkipReason}."); continue; }
            if (target.Error is not null) { Console.WriteLine($"Step {i + 1}: stopping, {target.Error}."); return 1; }

            switch (Ask($"Step {i + 1}/{plan.Steps.Count}: {step.Description}  → {target.Where}"))
            {
                case 'q': Console.WriteLine("Stopped. Nothing else was tapped."); return 0;
                case 's': Console.WriteLine("Skipped."); continue;
            }
            string reply = tapper.Tap(target.Button);
            if (!reply.StartsWith("ok")) { Console.WriteLine($"Tap failed: {reply}"); return 1; }

            string result = WaitAfter(reader, step, startTurn);
            Console.WriteLine($"   {result}");
            if (TurnNow(reader) > startTurn || EventShowing(reader)) return Finish(reader, startTurn);
        }

        // Follow-up: confirmations and pickers. The user chooses; nothing is guessed.
        while (TurnNow(reader) <= startTurn && !EventShowing(reader))
        {
            var dialog = DialogButtons(r);
            if (dialog.Count == 0) break;
            Console.WriteLine("A dialog is open:");
            for (int j = 0; j < dialog.Count; j++) Console.WriteLine($"  {j + 1}. {dialog[j].Path}");
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
        Console.Write($"{question}\n   Enter = tap, s = skip, q = stop: ");
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
