using BlazeUma.Automation;
using BlazeUma.Memory;
using BlazeUma.Memory.Game;
using BlazeUma.Memory.Il2Cpp;
using BlazeUma.Memory.Native;

namespace BlazeUma.Cli;

/// <summary>
/// BlazeUma.Cli agent inject|ping|unload|tap [--pid N] [--dll PATH]
/// inject: load the agent into the game (no effect if already loaded).
/// ping: check it answers. unload: ask the agent to remove itself.
/// tap &lt;button_addr&gt;: resolve the button's m_OnClick UnityEvent and ask the agent to invoke it on
/// Unity's main thread (same effect as the user tapping that button).
/// </summary>
internal static class AgentCommand
{
    public const string Usage = "usage: BlazeUma.Cli agent inject|ping|unload|tap <addr>|tap-path <query> [--pid N] [--dll PATH]";

    /// <summary>Short names for buttons the advisor cares about; matched against the button's field path.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Rest"] = "nextMonth",
        ["Training"] = "trainingButton",
        ["Recreation"] = "outingButton",
        ["Outing"] = "outingButton",
        ["Races"] = "raceRegistButton",
        ["Race"] = "raceRegistButton",
        ["Skills"] = "skillGetButton",
        ["Infirmary"] = "hospitalButton",
        ["Hospital"] = "hospitalButton",
        ["Hint"] = "AdviceButton",
        ["Details"] = "DetailButton",
        ["Back"] = "footerBackButton",
        // Dialog buttons: a single-button popup uses _centerButton; two-button popups use _left and _right.
        ["Close"] = "centerButton",
        ["OK"] = "centerButton",
        ["Confirm"] = "rightButton",
        ["Cancel"] = "leftButton",
    };

    public static int Run(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine(Usage); return 2; }
        int? pid = null;
        string? dll = null;
        string? tapArg = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pid": pid = int.Parse(args[++i]); break;
                case "--dll": dll = args[++i]; break;
                default:
                    if (args[0] is "tap" or "tap-path" && tapArg is null) tapArg = args[i];
                    else { Console.Error.WriteLine(Usage); return 2; }
                    break;
            }
        }

        int target;
        using (var game = GameProcess.Open(pid)) target = game.ProcessId;

        switch (args[0])
        {
            case "inject":
                string path = dll ?? AgentLocator.Find()
                    ?? throw new FileNotFoundException("Agent DLL not found; build it with cmake (see agent/) or pass --dll.");
                Console.WriteLine(Injector.Inject(target, path)
                    ? $"Agent loaded into PID {target} from {path}"
                    : $"Agent already loaded in PID {target}");
                Console.WriteLine(Ask(target, "ping"));
                return 0;
            case "ping":
                Console.WriteLine(Ask(target, "ping"));
                return 0;
            case "unload":
                Console.WriteLine(Ask(target, "quit"));
                return 0;
            case "tap":
                if (tapArg is null) { Console.Error.WriteLine("tap needs a button address (hex); run `BlazeUma.Cli buttons` to see them."); return 2; }
                return Tap(pid, tapArg);
            case "tap-path":
                if (tapArg is null) { Console.Error.WriteLine("tap-path needs a path substring (e.g. 'Rest', 'Details', 'Close')."); return 2; }
                return TapByPath(pid, tapArg);
            default:
                Console.Error.WriteLine(Usage);
                return 2;
        }
    }

    private static int Tap(int? pid, string addrText)
    {
        ulong buttonAddr = Convert.ToUInt64(addrText.StartsWith("0x") ? addrText[2..] : addrText, 16);
        using var reader = GameReader.Open(pid);
        string reply = new ButtonTapper(reader).Tap(buttonAddr);
        Console.WriteLine(reply);
        return reply.StartsWith("ok") ? 0 : 1;
    }

    /// <summary>Finds a tappable button whose path contains <paramref name="query"/> (case-insensitive) and taps it.</summary>
    private static int TapByPath(int? pid, string query)
    {
        string match = Aliases.TryGetValue(query, out var mapped) ? mapped : query;
        ButtonFinder finder;
        IReadOnlyList<FoundButton> buttons;
        using (var reader = GameReader.Open(pid))
        {
            finder = new ButtonFinder(reader.ManagedReader);
            buttons = finder.FromCurrentScene();
        }
        var candidates = buttons.Where(b => b.CanTap && b.Path.Contains(match, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0)
        {
            Console.Error.WriteLine($"No tappable button matches '{query}' (looked for '{match}'). Run `BlazeUma.Cli buttons --all` to see what's there.");
            return 1;
        }
        if (candidates.Count > 1)
        {
            Console.Error.WriteLine($"'{query}' is ambiguous:");
            foreach (var c in candidates) Console.Error.WriteLine($"  0x{c.Address:X}  {c.Path}");
            return 1;
        }
        var button = candidates[0];
        Console.WriteLine($"Tapping {button.Path} at 0x{button.Address:X}");
        return Tap(pid, $"0x{button.Address:X}");
    }

    private static string Ask(int pid, string command)
    {
        try
        {
            using var agent = AgentClient.Connect(pid);
            return agent.Send(command);
        }
        catch (TimeoutException)
        {
            return $"No agent answering in PID {pid} (not injected, or still starting).";
        }
    }
}
