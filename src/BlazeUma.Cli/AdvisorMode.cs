using System.Text.Json;
using System.Text.Json.Serialization;
using BlazeUma.Advisor;
using BlazeUma.Data;
using BlazeUma.Memory;
using BlazeUma.Memory.Game;
using BlazeUma.Memory.Il2Cpp;

namespace BlazeUma.Cli;

/// <summary>
/// Advisor: reads the game once a second and shows the recommended action, redrawn when it changes. Waits
/// for the game and reconnects after restarts. Each turn (advice + measured outcome) is logged to
/// logs/career-*.jsonl next to the exe. Esc goes back to the menu, V shows or hides the reasons for every option.
/// </summary>
internal static class AdvisorMode
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan KeyPoll = TimeSpan.FromMilliseconds(50);

    public static Mode Run()
    {
        var json = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
        };
        var log = new CareerLog(Path.Combine(AppContext.BaseDirectory, "logs"), json);
        bool verbose = false;
        var formatter = AdviceFormatter.ForConsole(verbose);
        string Hint() => $"Esc main menu · V {(verbose ? "hide" : "show")} details";

        GameReader? reader = null;
        MasterDatabase? data = null;
        CareerAdvisor? advisor = null;
        string? shownKey = null;
        int failures = 0;

        // Redraws only when `key` changes (keys leave out clocks).
        void Show(string key, Func<string> render)
        {
            if (key == shownKey) return;
            Console.Clear();
            Console.WriteLine(render());
            shownKey = key;
        }

        void Status(string title, string detail, bool problem = false) =>
            Show($"{title}\n{detail}\n{problem}", () => formatter.Status(title, detail, problem, Hint()));

        Console.Clear();
        try
        {
            while (true)
            {
                try
                {
                    if (reader is null)
                    {
                        reader = GameReader.Open(null);
                        if (data is null)
                        {
                            data = MasterDatabase.Open(MasterDatabase.PathInGameDirectory(reader.GameDirectory));
                            advisor = new CareerAdvisor(data);
                        }
                    }
                    var snapshot = reader.Snapshot();
                    failures = 0;
                    var advice = advisor!.Advise(snapshot);
                    log.Observe(snapshot, advice);
                    Show(formatter.Format(advice, DateTimeOffset.MinValue), () => formatter.Format(advice, snapshot.ReadAt)
                        + Environment.NewLine + Environment.NewLine + formatter.Footer(Hint())
                        + (log.CurrentPath is { } path ? Environment.NewLine + formatter.Footer($"logging to {path}") : ""));
                }
                catch (InvalidOperationException e)
                {
                    // Not running yet, or still starting up (GameAssembly.dll not loaded).
                    reader?.Dispose();
                    reader = null;
                    Status("Waiting for Umamusume", e.Message.Contains("not running")
                        ? "The game is not running. Start it and BlazeUma will connect automatically."
                        : e.Message.Contains("More than one")
                            ? "More than one game window is open; close one."
                            : "The game is still starting up…");
                }
                catch (Exception e) when (e is MemoryReadException or InconsistentSnapshotException or UnsupportedLayoutException)
                {
                    failures++;
                    if (reader is not null && reader.GameExited)
                    {
                        reader.Dispose();
                        reader = null;
                        Status("Umamusume was closed", "Waiting for the game to start again; BlazeUma will reconnect automatically.");
                    }
                    else if (reader is null)
                    {
                        // Attaching failed: right after launch the IL2CPP class cache is not ready yet. Keep retrying;
                        // after ~30 tries show the reason in case the game was updated and a layout really changed.
                        Status("Connecting to Umamusume", failures < 30 ? "The game is still loading…" : e.Message, problem: failures >= 30);
                    }
                    else if (failures >= 5)
                    {
                        // A few failures are normal while screens change; only report when it persists.
                        Status("Can't read the game right now", e.Message, problem: true);
                    }
                }

                switch (WaitForKey(Interval))
                {
                    case ConsoleKey.Escape:
                        return Mode.Menu;
                    case ConsoleKey.V:
                        verbose = !verbose;
                        formatter = AdviceFormatter.ForConsole(verbose);
                        shownKey = null;
                        break;
                }
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            Console.WriteLine(formatter.Status("BlazeUma could not read the game", e.Message, problem: true));
            ConsoleUi.WaitForKey("Press any key to return to the menu");
            return Mode.Menu;
        }
        finally
        {
            reader?.Dispose();
            data?.Dispose();
        }
    }

    /// <summary>Waits up to <paramref name="timeout"/> for a key; null when none was pressed.</summary>
    private static ConsoleKey? WaitForKey(TimeSpan timeout)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (Console.KeyAvailable) return Console.ReadKey(intercept: true).Key;
            Thread.Sleep(KeyPoll);
        }
        return null;
    }
}
