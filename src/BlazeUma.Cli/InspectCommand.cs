using BlazeUma.Memory;
using BlazeUma.Memory.Game;
using BlazeUma.Memory.Il2Cpp;

namespace BlazeUma.Cli;

/// <summary>
/// BlazeUma.Cli inspect dialog|view|controller|story|0x<addr> [--pid N] [--metadata PATH]
/// Prints the instance fields of the topmost dialog (and its <c>_data</c>), of the current view / view
/// controller, or of the StoryManager and its story view controller: name, type kind and value (strings, numbers, bools; class name for object references).
/// For mapping screens whose buttons are not plain fields.
/// </summary>
internal static class InspectCommand
{
    public const string Usage = "usage: BlazeUma.Cli inspect dialog|view|controller|story [--pid N] [--metadata PATH]";

    public static int Run(string[] args)
    {
        bool isAddress = args.Length > 0 && args[0].StartsWith("0x");
        if (args.Length == 0 || !isAddress && args[0] is not ("dialog" or "view" or "controller" or "story")) { Console.Error.WriteLine(Usage); return 2; }
        int? pid = null;
        string? metadata = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pid": pid = int.Parse(args[++i]); break;
                case "--metadata": metadata = args[++i]; break;
                default: Console.Error.WriteLine(Usage); return 2;
            }
        }

        using var reader = GameReader.Open(pid, metadata);
        var r = reader.ManagedReader;
        ulong target;
        if (args[0] == "dialog")
        {
            ulong manager = r.SingletonInstance("Gallop.DialogManager");
            var dialogs = manager == 0 ? [] : r.ListItems(r.Ptr(manager, "_dialogList"));
            if (dialogs.Count == 0) { Console.WriteLine("No dialog is open."); return 0; }
            target = dialogs[^1];
            Dump(r, target, "dialog");
            Dump(r, r.Ptr(target, "_data"), "dialog._data");
            Dump(r, r.Ptr(target, "_currentDialogObj"), "dialog._currentDialogObj");
            return 0;
        }
        if (isAddress)
        {
            // Only for addresses this tool printed from live roots; reading is harmless either way.
            Dump(r, Convert.ToUInt64(args[0][2..], 16), args[0]);
            return 0;
        }
        if (args[0] == "story")
        {
            ulong story = r.SingletonInstance("Gallop.StoryManager");
            Dump(r, story, "StoryManager");
            Dump(r, story == 0 ? 0 : r.Ptr(story, "_viewController"), "StoryManager._viewController");
            return 0;
        }
        ulong scene = r.SingletonInstance("Gallop.SceneManager");
        target = scene == 0 ? 0 : r.Ptr(scene, args[0] == "view" ? "_currentViewBase" : "_currentViewController");
        Dump(r, target, args[0]);
        return 0;
    }

    private static void Dump(ManagedReader r, ulong obj, string label)
    {
        Console.WriteLine($"{label} = {(obj == 0 ? "null" : $"0x{obj:X} {r.ClassName(obj)}")}");
        if (obj == 0) return;
        var rt = r.Runtime;
        for (ulong k = r.ClassOf(obj); k != 0; k = rt.Parent(k))
        {
            string cls = rt.QualifiedClassName(k);
            if (cls is "UnityEngine.Object" or "UnityEngine.Component" or "UnityEngine.Behaviour" or "System.Object") break;
            foreach (var f in rt.DeclaredFields(k).Where(f => !f.IsStatic && !f.IsLiteral))
                Console.WriteLine($"  {cls.Split('.')[^1],-28} {f.Name,-36} {Value(r, obj, f)}");
        }
    }

    private static string Value(ManagedReader r, ulong obj, Il2CppField f)
    {
        try
        {
            ulong at = obj + (ulong)f.Offset;
            return f.Kind switch
            {
                Il2CppTypeKind.Boolean => (r.Memory.ReadByte(at) != 0).ToString(),
                Il2CppTypeKind.Int32 => r.Memory.ReadInt32(at).ToString(),
                Il2CppTypeKind.String => $"\"{r.String(r.Memory.ReadPointer(at), 200)}\"",
                Il2CppTypeKind.Class or Il2CppTypeKind.GenericInstance or Il2CppTypeKind.SzArray
                    => r.Memory.ReadPointer(at) is var p && p != 0 ? $"→ 0x{p:X} {r.ClassName(p)}" : "null",
                _ => $"({f.Kind})",
            };
        }
        catch (MemoryReadException) { return "(unreadable)"; }
    }
}
