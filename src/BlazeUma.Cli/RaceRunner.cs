using System.Diagnostics;
using BlazeUma.Automation;
using BlazeUma.Memory;
using BlazeUma.Memory.Game;
using BlazeUma.Memory.Il2Cpp;

namespace BlazeUma.Cli;

/// <summary>
/// Drives the screens after race entry (confirm, paddock, race, results, goal popups, new month) until the
/// home screen is back on a later turn. Screens and buttons come from a hand-played race, traced screen by
/// screen. Each poll picks the first rule whose button is on screen: enabled, interactable and active in the
/// hierarchy (hidden layouts keep enabled buttons, so the agent checks activeness).
/// </summary>
internal static class RaceRunner
{
    /// <param name="Once">Tap at most once per run (a toggle such as fast-forward; it stays on screen after).</param>
    /// <param name="Repeat">Tap again while it stays on screen (in-race Skip: each tap skips one of 3–4 cutscenes).</param>
    private sealed record Rule(string Screen, string Path, string Description, bool Once = false, bool Repeat = false);

    private const int MaxTaps = 3, MaxRepeatTaps = 12;

    private const string Home = "Gallop.SingleModeMainViewController";
    private const string EntryConfirmTitle = "Race Details";
    /// <summary>Shown after losing a goal race (DialogSingleModeRouteContinue): Cancel / Try Again.</summary>
    private const string TryAgainTitle = "Try Again", RetryLabel = "Try Again";
    /// <summary>Shown when View Results is tapped for a race never watched before (padlock on the button).</summary>
    private const string LockedTitle = "Unlock Requirements";
    /// <summary>Popups that follow a race; open on the home screen they still mean the race is being finished.</summary>
    private static readonly string[] AfterRaceTitles = [EntryConfirmTitle, "Goal Achieved"];
    /// <summary>
    /// The turn counter moves on before the after-race popups and the new-month screen, so home only counts
    /// as done once it has stayed home with no dialog for this long (trace gaps were 2–3 s).
    /// </summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    /// <summary>Ordered: earlier rules win when several buttons are on screen.</summary>
    private static readonly Rule[] Rules =
    [
        // View Results plays the placing cut-in ("4th", TAP) over the paddock, whose buttons stay active under it.
        new("Gallop.SingleModePaddockViewController", "Controller._cutinParent._tapButton", "Tap through the placing", Repeat: true),
        new("Gallop.SingleModePaddockViewController", "View.PaddockContentsHolder._raceSkipButton", "View Results (skip watching the race)"),
        // View Results is locked until a race has been watched once; then the race is run and skipped in-race.
        new("Gallop.SingleModePaddockViewController", "View.PaddockContentsHolder._raceStartButtonRight", "Race (View Results is locked)"),
        new("Gallop.SingleModePaddockViewController", "View.PaddockContentsHolder._raceStartButtonCenter", "Race (View Results is locked)"),
        new("Gallop.RaceMainViewController", "Controller._resultList._singleModeNextButton", "Next (leave the results)"),
        new("Gallop.RaceMainViewController", "Controller._resultList._nextButton", "Next (leave the results)"),
        new("Gallop.RaceMainViewController", "Controller._raceResultSceneUI._skipButton", "Skip the finish scene"),
        // Runner list shown before a watched race (View Results locked): its Race! button starts it. Before the
        // in-race buttons: fast-forward is also visible on the runner list and would be spent before the start.
        new("Gallop.RaceMainViewController", "Controller._raceMemberListUI._startButton", "Race! (start from the runner list)"),
        new("Gallop.RaceMainViewController", "Controller._raceUI._skipButtonCommon", "Skip the race", Repeat: true),
        // Fast-forward only when Skip is not showing.
        new("Gallop.RaceMainViewController", "Controller._raceUI._fastForwardUI._fastForwardButtonCommon", "Fast-forward the race", Once: true),
        new("Gallop.SingleModeMonthStartViewController", "View._screenTapButton", "Tap through the new month"),
    ];

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(30);
    /// <summary>A watched race plays with nothing to tap for a minute or more.</summary>
    private static readonly TimeSpan RaceStall = TimeSpan.FromSeconds(240);

    /// <param name="ask">Asks before a tap; returns 'q' to stop, 's' to skip, anything else to tap.</param>
    /// <returns>0 when home on a later turn, 1 when stopped (by the user, a failed tap or an unmapped screen).</returns>
    /// <param name="resumed">Started mid-race (possibly after the turn already moved on): home settling is enough.</param>
    public static int Run(GameReader reader, ButtonTapper tapper, int startTurn, Func<string, char> ask, bool resumed = false)
    {
        var settled = Stopwatch.StartNew();
        var buttonless = Stopwatch.StartNew();   // how long the open dialog has shown no buttons
        var r = reader.ManagedReader;
        var stall = Stopwatch.StartNew();
        string? lastRule = null;
        var taps = new Dictionary<string, int>();
        var locked = new HashSet<string>();   // rule paths that turned out locked (or were Once) this run
        var misses = new Dictionary<string, int>();   // taps after which the button stayed on screen
        string lastScreen = "";

        while (true)
        {
            Thread.Sleep(Poll);
            (string Description, FoundButton Button)? next;
            string screen;
            try
            {
                screen = ScreenName(r);
                var buttons = new ButtonFinder(r, includeDialogTemplates: true).FromCurrentScene();
                bool dialogOpen = buttons.Any(b => b.Path.StartsWith("Dialog[") && b.CanTap);
                // A post-race event with choices: the caller answers it (the auto bot's event plan).
                if (screen == Home && EventWithChoices(r))
                {
                    Console.WriteLine("   An event with choices is showing; handing over to answer it.");
                    return 0;
                }
                if (screen != Home || dialogOpen) settled.Restart();
                else if (settled.Elapsed >= Settle && (resumed || Turn(reader) > startTurn)) return 0;
                if (dialogOpen)
                {
                    var pick = DialogPick(r, tapper, buttons, out bool lockedNotice, out var blocker);
                    if (lockedNotice && lastRule is not null && locked.Add(lastRule))
                        Console.WriteLine($"   {lastRule.Split('.')[^1]} is locked; using the next option.");
                    if (pick.Error is not null)
                    {
                        Console.WriteLine($"   {pick.Error}; choose it below.");
                        return 1;
                    }
                    next = pick.Choice;
                    if (next is not null || blocker is null) buttonless.Restart();
                    // Tap-anywhere popups have no visible button; their blocker closes them. Waits out popups
                    // whose button appears after an animation (Goal Achieved's Next, which the blocker does not close).
                    else if (buttonless.Elapsed >= TimeSpan.FromSeconds(5)) next = ($"Tap to close \"{DialogTitle(r, DialogIndex(blocker))}\"", blocker);
                }
                else
                {
                    // The paddock knows View Results is locked (padlock) before it is tapped; skip it rather than
                    // tap it and close "Unlock Requirements".
                    if (screen == PaddockScreen && SkipLocked(r) && locked.Add(SkipButtonPath))
                        Console.WriteLine("   View Results is locked (race not watched before); racing instead.");
                    next = ScreenPick(tapper, screen, buttons, locked);
                }
            }
            catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException or InconsistentSnapshotException)
            {
                continue;   // mid-transition; read again
            }

            if (screen != lastScreen) { lastScreen = screen; stall.Restart(); }
            if (next is null)
            {
                var limit = screen == "Gallop.RaceMainViewController" ? RaceStall : Stall;
                if (stall.Elapsed < limit) continue;
                Console.WriteLine($"   Nothing to tap on {screen} for {limit.TotalSeconds:0} s; this screen is not mapped yet. Continue by hand.");
                return 1;
            }

            var (description, button) = next.Value;
            var rule = Rules.FirstOrDefault(x => x.Path == button.Path);
            int maxTaps = rule is { Repeat: true } ? MaxRepeatTaps : MaxTaps;
            string key = $"{screen}|{button.Path}|{button.Address:X}";
            taps[key] = taps.GetValueOrDefault(key) + 1;
            if (taps[key] > maxTaps && rule is { Repeat: true } && locked.Add(rule.Path))
            {
                Console.WriteLine($"   {description} is still showing after {maxTaps} taps; letting the race play.");
                continue;
            }
            if (taps[key] > maxTaps) { Console.WriteLine($"   {description} did not take after {maxTaps} taps; stopping."); return 1; }

            switch (ask($"{description}  → {button.Path}"))
            {
                case 'q': Console.WriteLine("Stopped. Nothing else was tapped."); return 1;
                case 's': Console.WriteLine("Skipped; continue by hand."); return 1;
            }
            string reply = TapButton(r, tapper, button);
            if (!reply.StartsWith("ok")) { Console.WriteLine($"   Tap failed: {reply}"); return 1; }
            if (!button.Path.StartsWith("Dialog[")) lastRule = button.Path;
            stall.Restart();
            bool gone = WaitGone(r, tapper, button.Address);
            if (description.StartsWith(RetryLabel))
            {
                // The race is run again from the paddock: start the per-run bookkeeping afresh.
                taps.Clear(); locked.Clear(); misses.Clear(); lastRule = null;
                Console.WriteLine("   Running the race again.");
            }
            if (rule is { Once: true }) locked.Add(rule.Path);
            else if (rule is { Repeat: false } && !gone && (misses[rule.Path] = misses.GetValueOrDefault(rule.Path) + 1) >= 2 && locked.Add(rule.Path))
                Console.WriteLine($"   {description} did nothing twice; trying the next option.");
        }
    }

    /// <summary>
    /// True when a race is already under way: entry confirmation open, a paddock/race/new-month screen, or an
    /// after-race popup on the home screen.
    /// </summary>
    public static bool InProgress(ManagedReader r)
    {
        try
        {
            string screen = ScreenName(r);
            if (Rules.Any(x => x.Screen == screen)) return true;
            ulong manager = r.SingletonInstance("Gallop.DialogManager");
            return manager != 0 && r.ListItems(r.Ptr(manager, "_dialogList")).Count is > 0 and int n && AfterRaceTitles.Contains(DialogTitle(r, n - 1));
        }
        catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException) { return false; }
    }

    private const string PaddockScreen = "Gallop.SingleModePaddockViewController";
    private const string SkipButtonPath = "View.PaddockContentsHolder._raceSkipButton";

    /// <summary>PaddockViewControllerBase._isLockRaceSkipButton: View Results has its padlock (race never watched).</summary>
    private static bool SkipLocked(ManagedReader r)
    {
        ulong scene = r.SingletonInstance("Gallop.SceneManager");
        ulong controller = scene == 0 ? 0 : r.Ptr(scene, "_currentViewController");
        return controller != 0 && r.ClassName(controller) == PaddockScreen && r.Bool(controller, "_isLockRaceSkipButton");
    }

    /// <summary>The first rule for this screen whose button is really on screen.</summary>
    private static (string, FoundButton)? ScreenPick(ButtonTapper tapper, string screen, IReadOnlyList<FoundButton> buttons, HashSet<string> locked)
    {
        foreach (var rule in Rules.Where(x => x.Screen == screen && !locked.Contains(x.Path)))
        {
            var b = buttons.FirstOrDefault(x => x.Path == rule.Path && x.CanTap);
            if (b is not null && tapper.IsActive(b.Address) == true) return (rule.Description, b);
        }
        return null;
    }

    /// <summary>
    /// Race entry confirmation: the right button (Race). Other popups (Goal Achieved, rewards): their only
    /// visible button. Anything with a real choice goes back to the user. <paramref name="lockedNotice"/>: the
    /// popup says the last tapped button is locked.
    /// </summary>
    private static ((string, FoundButton)? Choice, string? Error) DialogPick(ManagedReader r, ButtonTapper tapper, IReadOnlyList<FoundButton> buttons,
        out bool lockedNotice, out FoundButton? blocker)
    {
        lockedNotice = false;
        blocker = null;
        // Act on the topmost dialog only.
        int top = buttons.Where(b => b.Path.StartsWith("Dialog[")).Select(DialogIndex).Max();
        string title = DialogTitle(r, top) ?? "(untitled)";
        lockedNotice = title == LockedTitle;
        // Reward item icons in a popup's contents only open the item's details (Trophy Won: the carat icon,
        // Contents.DialogAddTrophy._itemListChild._itemIcon._button, beside Close), so they never count.
        var shown = buttons
            .Where(b => b.CanTap && DialogIndex(b) == top && (b.Path.Contains("._currentDialogObj.") || b.Path.Contains("._dialogObjArray[") || b.Path.Contains("].Contents."))
                        && !b.Path.Contains("._itemIcon.") && tapper.IsActive(b.Address) == true)
            .DistinctBy(b => b.Address)   // _currentDialogObj is also one of the templates
            .ToList();
        if (shown.Count == 0)
            blocker = buttons.FirstOrDefault(b => b.CanTap && DialogIndex(b) == top && b.Path.EndsWith("._inputBlock")
                                                  && tapper.IsActive(b.Address) == true);
        // A lost goal race: Cancel fails the goal and ends the career, so retry (a free try a day, then an alarm
        // clock). Try Again is the right button; Replay and Placing in the contents only show the result.
        if (title == TryAgainTitle)
        {
            var retry = shown.FirstOrDefault(b => b.Path.EndsWith("._rightButton"));
            return retry is null ? (null, $"\"{title}\" has no Try Again button showing") : (($"{RetryLabel} (goal race lost)", retry), null);
        }
        if (title == EntryConfirmTitle)
        {
            var right = shown.FirstOrDefault(b => b.Path.EndsWith("._rightButton"));
            return right is null ? (null, $"\"{title}\" has no Race button showing") : (($"Confirm entry (\"{title}\")", right), null);
        }
        return shown.Count switch
        {
            0 => (null, null),   // animating in, or a tap-anywhere popup (blocker below)
            1 => (($"Close \"{title}\"", shown[0]), null),
            _ => (null, $"\"{title}\" offers {shown.Count} buttons"),
        };
    }

    /// <summary>
    /// Popup contents whose button is wired with <c>AddOnClickOnce</c>, which a tap (<c>m_OnClick</c>) does not
    /// fire: the handler is called instead. Goal Achieved's Next → <c>TriggerClose</c> (checked live, turn 34).
    /// </summary>
    private static readonly Dictionary<string, string> ContentsHandlers = new() { ["Gallop.DialogSingleModeRouteClear"] = "TriggerClose" };

    private static string TapButton(ManagedReader r, ButtonTapper tapper, FoundButton button)
    {
        if (button.Path.Contains("].Contents."))
        {
            ulong manager = r.SingletonInstance("Gallop.DialogManager");
            var dialogs = manager == 0 ? [] : r.ListItems(r.Ptr(manager, "_dialogList"));
            int index = DialogIndex(button);
            if (index >= 0 && index < dialogs.Count)
                foreach (ulong owner in new ButtonFinder(r).DialogContents(dialogs[index]))
                    if (r.ClassName(owner) is { } cls && ContentsHandlers.TryGetValue(cls, out var method) && button.Path.Contains($".Contents.{cls.Split('.')[^1]}."))
                        return tapper.Invoke(owner, cls, method);
        }
        return tapper.Tap(button.Address);
    }

    /// <summary>After a tap, give the button a moment to disappear so it is not tapped twice. False if it stayed.</summary>
    private static bool WaitGone(ManagedReader r, ButtonTapper tapper, ulong button)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            Thread.Sleep(Poll);
            try
            {
                var b = new ButtonFinder(r, includeDialogTemplates: true).FromCurrentScene().FirstOrDefault(x => x.Address == button);
                if (b is null || !b.CanTap || tapper.IsActive(button) != true) return true;
            }
            catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException) { return true; }
        }
        return false;
    }

    /// <summary>The i in "Dialog[i]…", or -1 for buttons that are not in a dialog.</summary>
    private static int DialogIndex(FoundButton b) =>
        b.Path.StartsWith("Dialog[") && b.Path.IndexOf(']') is > 7 and int end && int.TryParse(b.Path[7..end], out int i) ? i : -1;

    private static string? DialogTitle(ManagedReader r, int index)
    {
        ulong manager = r.SingletonInstance("Gallop.DialogManager");
        if (manager == 0) return null;
        var dialogs = r.ListItems(r.Ptr(manager, "_dialogList"));
        if (index >= dialogs.Count) return null;
        ulong data = r.Ptr(dialogs[index], "_data");
        return data == 0 ? null : r.String(r.Ptr(data, "Title"));
    }

    private static bool EventWithChoices(ManagedReader r)
    {
        ulong story = r.SingletonInstance("Gallop.StoryManager");
        ulong controller = story == 0 ? 0 : r.Ptr(story, "_viewController");
        return controller != 0 && r.Bool(controller, "_needsSingleModeSave") && r.Bool(controller, "_needsUpdateSelectIndex");
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
