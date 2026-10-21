using BlazeUma.Automation;
using BlazeUma.Memory.Native;

namespace BlazeUma.Cli;

/// <summary>
/// BlazeUma.Cli agent inject|ping|unload [--pid N] [--dll PATH]
/// inject: load the agent into the game (no effect if already loaded). ping: check it answers.
/// unload: ask the agent to remove itself.
/// </summary>
internal static class AgentCommand
{
    public const string Usage = "usage: BlazeUma.Cli agent inject|ping|unload [--pid N] [--dll PATH]";

    public static int Run(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine(Usage); return 2; }
        int? pid = null;
        string? dll = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pid": pid = int.Parse(args[++i]); break;
                case "--dll": dll = args[++i]; break;
                default: Console.Error.WriteLine(Usage); return 2;
            }
        }

        int target;
        using (var game = GameProcess.Open(pid)) target = game.ProcessId;

        switch (args[0])
        {
            case "inject":
                string path = dll ?? FindAgentDll()
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
            default:
                Console.Error.WriteLine(Usage);
                return 2;
        }
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

    /// <summary>agent/build/Release/BlazeUma.Agent.dll, searched from the working directory and the exe upwards.</summary>
    private static string? FindAgentDll()
    {
        string relative = Path.Combine("agent", "build", "Release", Injector.AgentFileName);
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
