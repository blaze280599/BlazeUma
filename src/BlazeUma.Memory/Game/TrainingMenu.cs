using BlazeUma.Memory.Il2Cpp;

namespace BlazeUma.Memory.Game;

/// <summary>One training tile in the home screen's training footer.</summary>
/// <param name="CommandId">Training command id from the tile's TurnInfo (101 Speed, 105 Stamina, 102 Power, 103 Guts, 106 Wit).</param>
/// <param name="IsSelected">The highlighted tile. Tapping a selected tile performs that training.</param>
public sealed record TrainingTile(int Index, int CommandId, bool IsSelected, ulong Button);

/// <summary>
/// Reads the training tiles: <c>SceneManager._currentViewBase.TrainingFooter._items</c>, a
/// List&lt;SingleModeMainViewTrainingFooterItem&gt; where each item has <c>_button</c>, <c>_trainingMenu</c>
/// (the turn's TurnInfo) and <c>_isSelect</c>.
/// </summary>
public static class TrainingMenu
{
    public static IReadOnlyList<TrainingTile> ReadTiles(ManagedReader r)
    {
        ulong scene = r.SingletonInstance("Gallop.SceneManager");
        if (scene == 0) return [];
        ulong view = r.Ptr(scene, "_currentViewBase");
        if (view == 0 || r.ClassName(view) != "Gallop.SingleModeMainView") return [];
        ulong footer = r.Ptr(view, "TrainingFooter");
        if (footer == 0) return [];

        var tiles = new List<TrainingTile>();
        var items = r.ListItems(r.Ptr(footer, "_items"));
        for (int i = 0; i < items.Count; i++)
        {
            ulong turnInfo = r.Ptr(items[i], "_trainingMenu");
            if (turnInfo == 0) continue;
            tiles.Add(new TrainingTile(i, r.ObscuredInt(turnInfo, "_commandId"), r.Bool(items[i], "_isSelect"), r.Ptr(items[i], "_button")));
        }
        return tiles;
    }
}
