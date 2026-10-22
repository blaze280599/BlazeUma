using BlazeUma.Memory.Game;

namespace BlazeUma.Cli;

/// <summary>
/// <c>BlazeUma buttons [--depth N]</c>: prints the current screen and every button on it (path, class, tappable),
/// for mapping a screen the auto bot doesn't know yet. Reads memory only; nothing is called in the game.
/// </summary>
internal static class ButtonsCommand
{
    public static int Run(string[] args)
    {
        int depth = args is ["--depth", var d, ..] && int.TryParse(d, out int n) ? n : ButtonFinder.DefaultMaxDepth;
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var reader = GameReader.Open(null);
        var r = reader.ManagedReader;
        ulong scene = r.SingletonInstance("Gallop.SceneManager");
        Console.WriteLine($"screen: {(scene == 0 ? "?" : r.ClassName(r.Ptr(scene, "_currentViewController")) ?? "?")}");
        if (ActionPlanner.DialogTitle(r) is { } title) Console.WriteLine($"top dialog: \"{title}\"");
        foreach (var b in new ButtonFinder(r, includeDialogTemplates: true).FromCurrentScene(depth))
            Console.WriteLine($"  {(b.CanTap ? "tap " : "off ")} {b.Path}  ({b.ClassName}, 0x{b.Address:X})");
        return 0;
    }
}
