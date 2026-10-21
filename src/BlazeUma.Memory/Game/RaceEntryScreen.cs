using BlazeUma.Memory.Il2Cpp;

namespace BlazeUma.Memory.Game;

/// <summary>One row of the race list. Tapping <see cref="Button"/> selects the row.</summary>
/// <param name="EntryState">0 = enterable; 3 = fan requirement not met; 5 = blocked because a goal race is due today.</param>
public sealed record RaceRow(int Index, int ProgramId, int EntryState, bool IsGoal, ulong Button);

/// <summary>The open race list: its rows, the selected program, and the green Race (entry) button.</summary>
public sealed record RaceEntryState(IReadOnlyList<RaceRow> Rows, int SelectedProgramId, ulong EntryButton);

/// <summary>
/// Reads <c>Gallop.SingleModeRaceEntryViewController</c> (rows in <c>_itemList</c>, each with <c>Program</c>,
/// <c>_programEntryState</c>, <c>IsRouteRace</c> and <c>_entryButton</c>; selection in
/// <c>_currentSelectedProgram</c>) and its view's <c>EntryButton</c>. Null when the race list is not open.
/// </summary>
public static class RaceEntryScreen
{
    public const string ControllerClass = "Gallop.SingleModeRaceEntryViewController";

    public static RaceEntryState? Read(ManagedReader r)
    {
        ulong scene = r.SingletonInstance("Gallop.SceneManager");
        if (scene == 0) return null;
        ulong controller = r.Ptr(scene, "_currentViewController");
        if (r.ClassName(controller) != ControllerClass) return null;
        ulong view = r.Ptr(scene, "_currentViewBase");

        var rows = new List<RaceRow>();
        var items = r.ListItems(r.Ptr(controller, "_itemList"));
        for (int i = 0; i < items.Count; i++)
        {
            ulong program = r.Ptr(items[i], "Program");
            rows.Add(new RaceRow(i, program == 0 ? 0 : r.Int(program, "Id"), r.Int(items[i], "_programEntryState"),
                r.Bool(items[i], "IsRouteRace"), r.Ptr(items[i], "_entryButton")));
        }
        ulong selected = r.Ptr(controller, "_currentSelectedProgram");
        return new RaceEntryState(rows, selected == 0 ? 0 : r.Int(selected, "Id"), view == 0 ? 0 : r.Ptr(view, "EntryButton"));
    }
}
