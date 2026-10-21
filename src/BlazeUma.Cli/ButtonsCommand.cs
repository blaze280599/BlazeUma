using BlazeUma.Automation;
using BlazeUma.Memory;
using BlazeUma.Memory.Game;
using BlazeUma.Memory.Il2Cpp;

namespace BlazeUma.Cli;

/// <summary>
/// BlazeUma.Cli buttons [--pid N] [--metadata PATH] [--all] [--active] [--watch SECONDS]
/// Lists UI buttons reachable from the current scene's view, controller, footer and open dialogs.
/// Only tappable (enabled + interactable) buttons by default; --all shows every match.
/// --active asks the in-game agent which tappable buttons are really on screen (active GameObject) and
/// shows only those; hidden layouts keep their buttons enabled, so without it some listed buttons are not visible.
/// --watch traces: prints (and appends to logs/trace-*.txt) every time the screen or its buttons change,
/// so a flow played by hand can be mapped for automation.
/// </summary>
internal static class ButtonsCommand
{
    public const string Usage = "usage: BlazeUma.Cli buttons [--pid N] [--metadata PATH] [--all] [--active] [--watch SECONDS]";

    public static int Run(string[] args)
    {
        int? pid = null;
        string? metadata = null;
        bool all = false, active = false;
        double? watch = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pid": pid = int.Parse(args[++i]); break;
                case "--metadata": metadata = args[++i]; break;
                case "--all": all = true; break;
                case "--active": active = true; break;
                case "--watch": watch = double.Parse(args[++i]); break;
                default: Console.Error.WriteLine(Usage); return 2;
            }
        }

        try
        {
            using var reader = GameReader.Open(pid, metadata);
            ButtonTapper? tapper = null;
            if (active)
            {
                Console.WriteLine(ButtonTapper.EnsureAgent(reader.ProcessId));
                tapper = new ButtonTapper(reader);
            }
            if (watch is null)
            {
                Console.WriteLine(Describe(reader.ManagedReader, all, tapper));
                return 0;
            }
            return Trace(reader.ManagedReader, all, tapper, TimeSpan.FromSeconds(Math.Max(0.25, watch.Value)));
        }
        catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception or IOException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    /// <summary>Screen name, open dialog titles and the buttons, as printable text.</summary>
    private static string Describe(ManagedReader r, bool all, ButtonTapper? tapper)
    {
        // With the active check, hidden dialog layout templates are filtered out, so walk them too.
        var buttons = new ButtonFinder(r, includeDialogTemplates: tapper is not null).FromCurrentScene();
        var lines = new List<string> { $"Screen: {ViewName(r)}{DialogTitles(r)}" };
        int shown = 0;
        foreach (var b in buttons.OrderByDescending(b => b.CanTap).ThenBy(b => b.Path))
        {
            if (!all && !b.CanTap) continue;
            // h = tappable but hidden (inactive GameObject); only known with --active.
            bool hidden = tapper is not null && b.CanTap && tapper.IsActive(b.Address) != true;
            if (hidden && !all) continue;
            char mark = hidden ? 'h' : b.CanTap ? '✓' : (b.IsEnable ? 'E' : ' ');
            lines.Add($" {mark} 0x{b.Address:X}  {b.Path,-70}  {b.ClassName}");
            shown++;
        }
        lines.Add($"{shown} of {buttons.Count} buttons shown{(all ? "" : " (pass --all to include non-tappable)")}");
        return string.Join(Environment.NewLine, lines);
    }

    private static int Trace(ManagedReader r, bool all, ButtonTapper? tapper, TimeSpan interval)
    {
        Directory.CreateDirectory("logs");
        string path = Path.Combine("logs", $"trace-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        Console.WriteLine($"Tracing screen changes to {path}; Ctrl+C to stop.");
        string? last = null;
        var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                // Compare without addresses: the same screen re-created at new addresses is not a change.
                string text = Describe(r, all, tapper);
                string key = string.Join("\n", text.Split(Environment.NewLine).Select(l => l.Length > 22 && l.StartsWith(' ') ? l[22..] : l));
                if (key != last)
                {
                    string entry = $"--- {DateTime.Now:HH:mm:ss.fff}{Environment.NewLine}{text}{Environment.NewLine}";
                    Console.WriteLine(entry);
                    File.AppendAllText(path, entry);
                    last = key;
                }
            }
            catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException or InconsistentSnapshotException
                                          or TimeoutException or IOException) { }
            try { Thread.Sleep(interval); } catch (ThreadInterruptedException) { break; }
        }
        return 0;
    }

    private static string ViewName(ManagedReader r)
    {
        ulong scene = r.SingletonInstance("Gallop.SceneManager");
        return scene == 0 ? "?" : (r.ClassName(r.Ptr(scene, "_currentViewController")) ?? "?");
    }

    private static string DialogTitles(ManagedReader r)
    {
        try
        {
            ulong manager = r.SingletonInstance("Gallop.DialogManager");
            if (manager == 0) return "";
            var titles = r.ListItems(r.Ptr(manager, "_dialogList"))
                .Select(d => r.Ptr(d, "_data")).Where(d => d != 0)
                .Select(d => r.String(r.Ptr(d, "Title")) ?? "(untitled)").ToList();
            return titles.Count == 0 ? "" : $"  dialogs: {string.Join(" | ", titles)}";
        }
        catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException) { return ""; }
    }
}
