using BlazeUma.Memory;
using BlazeUma.Memory.Game;
using BlazeUma.Memory.Il2Cpp;

namespace BlazeUma.Cli;

/// <summary>
/// BlazeUma.Cli buttons [--pid N] [--metadata PATH] [--all]
/// Lists UI buttons reachable from the current scene's view and controller.
/// Only tappable (enabled + interactable) buttons by default; --all shows every match.
/// </summary>
internal static class ButtonsCommand
{
    public const string Usage = "usage: BlazeUma.Cli buttons [--pid N] [--metadata PATH] [--all]";

    public static int Run(string[] args)
    {
        int? pid = null;
        string? metadata = null;
        bool all = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pid": pid = int.Parse(args[++i]); break;
                case "--metadata": metadata = args[++i]; break;
                case "--all": all = true; break;
                default: Console.Error.WriteLine(Usage); return 2;
            }
        }

        try
        {
            using var reader = GameReader.Open(pid, metadata);
            var buttons = new ButtonFinder(reader.ManagedReader).FromCurrentScene();
            var screen = reader.Snapshot().Screen;
            Console.WriteLine($"Screen: {screen?.ViewController ?? "?"}");
            int shown = 0;
            foreach (var b in buttons.OrderByDescending(b => b.CanTap).ThenBy(b => b.Path))
            {
                if (!all && !b.CanTap) continue;
                char mark = b.CanTap ? '✓' : (b.IsEnable ? 'E' : ' ');
                Console.WriteLine($" {mark} 0x{b.Address:X}  {b.Path,-70}  {b.ClassName}");
                shown++;
            }
            Console.WriteLine($"{shown} of {buttons.Count} buttons shown{(all ? "" : " (pass --all to include non-tappable)")}");
            return 0;
        }
        catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception or IOException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }
}
