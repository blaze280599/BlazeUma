using BlazeUma.Memory.Il2Cpp;

namespace BlazeUma.Memory.Game;

/// <summary>
/// What the game is busy with that a tap must not land under: a screen change, the UI's input lock, an event scene.
/// A person can't tap through these, and taps sent anyway (skills bought under event dialogue) have locked the
/// game up. Reads memory only.
/// </summary>
public static class UiState
{
    /// <summary>
    /// Why the game can't take a tap now, or null when nothing is in the way: <c>SceneManager</c> is changing the
    /// view or scene (loading, transitions), or <c>UIManager._lockInput</c> is set. Event scenes are separate
    /// (<see cref="StoryPlaying"/>): event choices are tapped while one plays.
    /// </summary>
    public static string? Busy(ManagedReader r)
    {
        ulong scene = r.SingletonInstance("Gallop.SceneManager");
        if (scene != 0 && (r.Bool(scene, "_isRunChangeView") || r.Bool(scene, "_isRunChangeInHubView") || r.Bool(scene, "<IsRunChangeScene>k__BackingField")))
            return "the game is changing screens";
        ulong ui = r.SingletonInstance("Gallop.UIManager");
        if (ui != 0 && r.Bool(ui, "_lockInput")) return "the game's input is locked";
        return null;
    }

    /// <summary>
    /// An event scene (dialogue, its choices, what follows a choice) is on screen: the story timeline
    /// (<c>StoryManager._view.TimelineController</c>) is playing and not finished. Unlike
    /// <see cref="StoryEvent.LooksActive"/> it stays set after the choice, while the scene plays out (turn 41's freeze).
    /// </summary>
    public static bool StoryPlaying(ManagedReader r)
    {
        ulong timeline = Timeline(r);
        return timeline != 0 && r.Bool(timeline, "<IsPlaying>k__BackingField") && !r.Bool(timeline, "<IsFinished>k__BackingField");
    }

    /// <summary>
    /// The event scene timeline's frame count, or null while the scene waits for a tap (its frame stands still
    /// then too). A frame that stops moving otherwise means the scene froze (2026-10-24: frame 356 with the race list
    /// opened over the dialogue and closed again).
    /// </summary>
    public static int? StoryFrame(ManagedReader r)
    {
        ulong timeline = Timeline(r);
        return timeline == 0 || r.Bool(timeline, "_isWaitingUserInput") ? null : r.Int(timeline, "_frameCount");
    }

    /// <summary>One line with every signal above and the canvas lock counts, for checking them against the live game.</summary>
    public static string Describe(ManagedReader r)
    {
        ulong scene = r.SingletonInstance("Gallop.SceneManager");
        ulong ui = r.SingletonInstance("Gallop.UIManager");
        ulong timeline = Timeline(r);
        ulong story = r.SingletonInstance("Gallop.StoryManager");
        ulong storyController = story == 0 ? 0 : r.Ptr(story, "_viewController");
        string view = scene == 0 ? "-" : r.ClassName(r.Ptr(scene, "_currentViewController"))?.Replace("Gallop.", "") ?? "-";
        string F(ulong obj, string field) => obj == 0 ? "-" : r.Bool(obj, field) ? "1" : "0";
        string Locks(params string[] lockers) => ui == 0 ? "-" : string.Join("/", lockers.Select(l =>
        {
            ulong locker = r.Ptr(ui, l);
            ulong counter = locker == 0 ? 0 : r.Ptr(r.Ptr(locker, "_inputLocker"), "_counter");
            return counter == 0 ? "-" : r.Int(counter, "_num").ToString();
        }));
        return $"{view,-40} change {F(scene, "_isRunChangeView")}{F(scene, "_isRunChangeInHubView")}{F(scene, "<IsRunChangeScene>k__BackingField")}"
             + $"  lockInput {F(ui, "_lockInput")}  locks {Locks("_uiRootLocker", "_gameCanvasLocker", "_headerFooterCanvasLocker", "_noImageEffectCanvasLocker")}"
             + $"  story playing {F(timeline, "<IsPlaying>k__BackingField")} finished {F(timeline, "<IsFinished>k__BackingField")}"
             + $" waitingInput {F(timeline, "_isWaitingUserInput")} frame {(timeline == 0 ? "-" : r.Int(timeline, "_frameCount").ToString())}"
             + $"  looksActive {(storyController == 0 ? "-" : r.Bool(storyController, "_needsSingleModeSave") && r.Bool(storyController, "_needsUpdateSelectIndex") ? "1" : "0")}";
    }

    private static ulong Timeline(ManagedReader r)
    {
        ulong story = r.SingletonInstance("Gallop.StoryManager");
        ulong view = story == 0 ? 0 : r.Ptr(story, "_view");
        return view == 0 ? 0 : r.Ptr(view, "<TimelineController>k__BackingField");
    }
}
