using BlazeUma.Memory.Il2Cpp;

namespace BlazeUma.Memory.Game;

/// <summary>One row of the Recreation dialog.</summary>
/// <param name="CommandGroupId">301 = the trainee's own outing, 390 = a friend support card's outing.</param>
/// <param name="DeckPosition">Deck slot (1–6) of the friend card, or 0 for the trainee's outing.</param>
public sealed record OutingRow(int Index, ulong Button, int CommandGroupId, int DeckPosition, int SupportCardId);

/// <summary>
/// Reads the open Recreation dialog (<c>Gallop.DialogSingleModeOutingPartnerSelect</c>). Its rows
/// (<c>_itemList</c>) each have a <c>_decideButton</c> and an <c>OutingInfo</c> with the master outing
/// (<c>CommandGroupId</c>) and, for friend outings, the <c>EquipSupportCard</c>. Nothing managed points at the
/// picker, so it is found by heap scan and accepted only if its <c>_dialog</c> is a dialog that is open now.
/// </summary>
public static class OutingPicker
{
    public const string PickerClass = "Gallop.DialogSingleModeOutingPartnerSelect";

    public static IReadOnlyList<OutingRow> Read(GameReader game)
    {
        var r = game.ManagedReader;
        ulong dialogManager = r.SingletonInstance("Gallop.DialogManager");
        if (dialogManager == 0) return [];
        var open = r.ListItems(r.Ptr(dialogManager, "_dialogList")).ToHashSet();
        if (open.Count == 0) return [];

        foreach (ulong candidate in game.FindInstances(PickerClass, TimeSpan.FromSeconds(20)))
        {
            try
            {
                if (!open.Contains(r.Ptr(candidate, "_dialog"))) continue;
                var rows = new List<OutingRow>();
                var items = r.ListItems(r.Ptr(candidate, "_itemList"));
                for (int i = 0; i < items.Count; i++)
                {
                    ulong info = r.Ptr(items[i], "_info");
                    ulong master = r.Ptr(info, "_masterOuting");
                    ulong card = r.Ptr(info, "_equipSupportCard");
                    rows.Add(new OutingRow(i, r.Ptr(items[i], "_decideButton"), r.Int(master, "CommandGroupId"),
                        card == 0 ? 0 : r.ObscuredInt(card, "Position"), card == 0 ? 0 : r.ObscuredInt(card, "SupportCardId")));
                }
                return rows;
            }
            catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException or InconsistentSnapshotException) { }
        }
        return [];
    }
}
