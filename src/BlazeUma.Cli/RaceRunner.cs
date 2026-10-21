using System.Diagnostics;
using BlazeUma.Automation;
using BlazeUma.Memory;
using BlazeUma.Memory.Game;
using BlazeUma.Memory.Il2Cpp;

namespace BlazeUma.Cli;

/// <summary>
/// Drives the screens after race entry (confirm, paddock, race, results, goal popups, new month) until the
/// home screen is back on a later turn. Screens and buttons come from a hand-played race traced with
/// <c>buttons --watch</c>. Each poll picks the first rule whose button is on screen: enabled, interactable
/// and active in the hierarchy (hidden layouts keep enabled buttons, so the agent checks activeness).
/// </summary>
internal static class RaceRunner
{
    private sealed record Rule(string Screen, string Path, string Description);

    private const string Home = "Gallop.SingleModeMainViewController";
    private const string EntryConfirmTitle = "Race Details";

    /// <summary>Ordered: earlier rules win when several buttons are on screen.</summary>
    private static readonly Rule[] Rules =
    [
        new("Gallop.SingleModePaddockViewController", "View.PaddockContentsHolder._raceSkipButton", "View Results (skip watching the race)"),
        new("Gallop.RaceMainViewController", "Controller._resultList._singleModeNextButton", "Next (leave the results)"),
        new("Gallop.RaceMainViewController", "Controller._resultList._nextButton", "Next (leave the results)"),
        new("Gallop.RaceMainViewController", "Controller._raceResultSceneUI._skipButton", "Skip the finish scene"),
        new("Gallop.RaceMainViewController", "Controller._raceUI._skipButtonCommon", "Skip the race"),
        new("Gallop.SingleModeMonthStartViewController", "View._screenTapButton", "Tap through the new month"),
    ];

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(30);

    /// <param name="ask">Asks before a tap; returns 'q' to stop, 's' to skip, anything else to tap.</param>
    /// <returns>0 when home on a later turn, 1 when stopped (by the user, a failed tap or an unmapped screen).</returns>
    public static int Run(GameReader reader, ButtonTapper tapper, int startTurn, Func<string, char> ask)
    {
        var r = reader.ManagedReader;
        var stall = Stopwatch.StartNew();
        string? lastTap = null;
        int repeats = 0;
        string lastScreen = "";

        while (true)
        {
            Thread.Sleep(Poll);
            (string Description, FoundButton Button)? next;
            string screen;
            try
            {
                screen = ScreenName(r);
                var buttons = new ButtonFinder(r).FromCurrentScene();
                bool dialogOpen = buttons.Any(b => b.Path.StartsWith("Dialog[") && b.CanTap);
                if (screen == Home && !dialogOpen && Turn(reader) > startTurn) return 0;
                if (dialogOpen)
                {
                    var pick = DialogPick(r, tapper, buttons);
                    if (pick.Error is not null)
                    {
                        Console.WriteLine($"   {pick.Error}; choose it below.");
                        return 1;
                    }
                    next = pick.Choice;
                }
                else next = ScreenPick(tapper, screen, buttons);
            }
            catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException or InconsistentSnapshotException)
            {
                continue;   // mid-transition; read again
            }

            if (screen != lastScreen) { lastScreen = screen; stall.Restart(); }
            if (next is null)
            {
                if (stall.Elapsed < Stall) continue;
                Console.WriteLine($"   Nothing to tap on {screen} for {Stall.TotalSeconds:0} s; this screen is not mapped yet. Continue by hand " +
                                  "(`buttons --watch` records it so it can be added).");
                return 1;
            }

            var (description, button) = next.Value;
            string key = $"{screen}|{button.Path}|{button.Address:X}";
            repeats = key == lastTap ? repeats + 1 : 0;
            if (repeats >= 3) { Console.WriteLine($"   {description} did not take after 3 taps; stopping."); return 1; }

            switch (ask($"{description}  → {button.Path}"))
            {
                case 'q': Console.WriteLine("Stopped. Nothing else was tapped."); return 1;
                case 's': Console.WriteLine("Skipped; continue by hand."); return 1;
            }
            string reply = tapper.Tap(button.Address);
            if (!reply.StartsWith("ok")) { Console.WriteLine($"   Tap failed: {reply}"); return 1; }
            lastTap = key;
            stall.Restart();
            WaitGone(r, tapper, button.Address);
        }
    }

    /// <summary>The first rule for this screen whose button is really on screen.</summary>
    private static (string, FoundButton)? ScreenPick(ButtonTapper tapper, string screen, IReadOnlyList<FoundButton> buttons)
    {
        foreach (var rule in Rules.Where(x => x.Screen == screen))
        {
            var b = buttons.FirstOrDefault(x => x.Path == rule.Path && x.CanTap);
            if (b is not null && tapper.IsActive(b.Address) == true) return (rule.Description, b);
        }
        return null;
    }

    /// <summary>
    /// Race entry confirmation: the right button (Race). Other popups (Goal Achieved, rewards): their only
    /// visible button. Anything with a real choice goes back to the user.
    /// </summary>
    private static ((string, FoundButton)? Choice, string? Error) DialogPick(ManagedReader r, ButtonTapper tapper, IReadOnlyList<FoundButton> buttons)
    {
        // Act on the topmost dialog only.
        int top = buttons.Where(b => b.Path.StartsWith("Dialog[")).Select(DialogIndex).Max();
        string title = DialogTitle(r, top) ?? "(untitled)";
        var shown = buttons
            .Where(b => b.CanTap && DialogIndex(b) == top && b.Path.Contains("._currentDialogObj.") && tapper.IsActive(b.Address) == true)
            .ToList();
        if (title == EntryConfirmTitle)
        {
            var right = shown.FirstOrDefault(b => b.Path.EndsWith("._rightButton"));
            return right is null ? (null, $"\"{title}\" has no Race button showing") : (($"Confirm entry (\"{title}\")", right), null);
        }
        return shown.Count switch
        {
            0 => (null, null),   // still animating in
            1 => (($"Close \"{title}\"", shown[0]), null),
            _ => (null, $"\"{title}\" offers {shown.Count} buttons"),
        };
    }

    /// <summary>After a tap, give the button a moment to disappear so it is not tapped twice.</summary>
    private static void WaitGone(ManagedReader r, ButtonTapper tapper, ulong button)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            Thread.Sleep(Poll);
            try
            {
                var b = new ButtonFinder(r).FromCurrentScene().FirstOrDefault(x => x.Address == button);
                if (b is null || !b.CanTap || tapper.IsActive(button) != true) return;
            }
            catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException) { return; }
        }
    }

    private static int DialogIndex(FoundButton b) => int.Parse(b.Path[7..b.Path.IndexOf(']')]);

    private static string? DialogTitle(ManagedReader r, int index)
    {
        ulong manager = r.SingletonInstance("Gallop.DialogManager");
        if (manager == 0) return null;
        var dialogs = r.ListItems(r.Ptr(manager, "_dialogList"));
        if (index >= dialogs.Count) return null;
        ulong data = r.Ptr(dialogs[index], "_data");
        return data == 0 ? null : r.String(r.Ptr(data, "Title"));
    }

    private static string ScreenName(ManagedReader r)
    {
        ulong scene = r.SingletonInstance("Gallop.SceneManager");
        return scene == 0 ? "?" : (r.ClassName(r.Ptr(scene, "_currentViewController")) ?? "?");
    }

    private static int Turn(GameReader reader)
    {
        try { return reader.Snapshot().Turn ?? 0; }
        catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException or InconsistentSnapshotException) { return 0; }
    }
}
