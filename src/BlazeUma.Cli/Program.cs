using System.Text.Json;
using System.Text.Json.Serialization;
using BlazeUma.Advisor;
using BlazeUma.Cli;
using BlazeUma.Data;
using BlazeUma.Memory;
using BlazeUma.Memory.Game;
using BlazeUma.Memory.Il2Cpp;

// BlazeUma.Cli [--pid N] [--metadata PATH] [--master PATH] [--watch SECONDS] [--advise [--verbose]] [--log DIR | --no-log]
// Prints a read-only career snapshot as JSON, or with --advise the recommended action.
// With --watch, repeats until Ctrl+C (snapshots one JSON per line; advice redrawn when it changes).
// --advise --watch also logs each turn (advice + measured outcome) to DIR/career-*.jsonl (default: logs).

const string Usage = "usage: BlazeUma.Cli [--pid N] [--metadata PATH] [--master PATH] [--watch SECONDS] [--advise [--verbose]] [--log DIR | --no-log]\n"
    + "       " + AgentCommand.Usage;

if (args.Length > 0 && args[0] == "agent")
{
    try { return AgentCommand.Run(args[1..]); }
    catch (Exception e) when (e is InvalidOperationException or FileNotFoundException or System.ComponentModel.Win32Exception or TimeoutException or IOException)
    {
        Console.Error.WriteLine(e.Message);
        return 1;
    }
}
if (args.Length > 0 && args[0] == "buttons")
    return ButtonsCommand.Run(args[1..]);
int? pid = null;
string? metadata = null, master = null, logDir = "logs";
double? watch = null;
bool advise = false, verbose = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--pid": pid = int.Parse(args[++i]); break;
        case "--metadata": metadata = args[++i]; break;
        case "--master": master = args[++i]; break;
        case "--watch": watch = double.Parse(args[++i]); break;
        case "--advise": advise = true; break;
        case "--verbose": verbose = true; break;
        case "--log": logDir = args[++i]; break;
        case "--no-log": logDir = null; break;
        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
if (watch is < 0.5) { Console.Error.WriteLine("--watch must be at least 0.5 seconds"); return 2; }

var json = new JsonSerializerOptions
{
    WriteIndented = watch is null,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter() },
};

var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

var formatter = AdviceFormatter.ForConsole(verbose);
bool redraw = watch is not null && !Console.IsOutputRedirected;
Console.OutputEncoding = System.Text.Encoding.UTF8;
var log = advise && watch is not null && logDir is not null
    ? new CareerLog(logDir, new JsonSerializerOptions(json) { WriteIndented = false })
    : null;

// In watch mode the reader survives the game closing and reopening: it waits, then reattaches.
GameReader? reader = null;
MasterDatabase? data = null;
CareerAdvisor? advisor = null;
string? shownKey = null;
int failures = 0;

// Prints only when `key` changes (keys leave out clocks); in an interactive watch it redraws in place.
void Show(string key, Func<string> render)
{
    if (key == shownKey) return;
    if (redraw) Console.Clear();
    Console.WriteLine(render());
    shownKey = key;
}

// Status without advice: a coloured panel for people, a JSON line for scripts (snapshot mode).
void Status(string title, string detail, bool problem = false) =>
    Show($"{title}\n{detail}\n{problem}", () => advise
        ? formatter.Status(title, detail, problem)
        : JsonSerializer.Serialize(new { status = title, detail }, json));

try
{
    do
    {
        try
        {
            if (reader is null)
            {
                reader = GameReader.Open(pid, metadata);
                if (advise && data is null)
                {
                    data = MasterDatabase.Open(master ?? MasterDatabase.PathInGameDirectory(reader.GameDirectory));
                    advisor = new CareerAdvisor(data);
                }
            }
            var snapshot = reader.Snapshot();
            failures = 0;
            if (advisor is null)
            {
                string snapshotJson = JsonSerializer.Serialize(snapshot, json);
                Show(snapshotJson, () => snapshotJson);
            }
            else
            {
                var advice = advisor.Advise(snapshot);
                log?.Observe(snapshot, advice);
                Show(formatter.Format(advice, DateTimeOffset.MinValue), () => formatter.Format(advice, snapshot.ReadAt)
                    + (log?.CurrentPath is { } path ? Environment.NewLine + formatter.Footer($"logging to {path}") : ""));
            }
        }
        catch (InvalidOperationException e) when (watch is not null)
        {
            // Not running yet, or still starting up (GameAssembly.dll not loaded).
            reader?.Dispose();
            reader = null;
            Status("Waiting for Umamusume", e.Message.Contains("not running")
                ? "The game is not running. Start it and BlazeUma will connect automatically."
                : e.Message.Contains("More than one")
                    ? "More than one game window is open; close one, or pass --pid."
                    : e.Message.StartsWith("PID")
                        ? e.Message
                        : "The game is still starting up…");
        }
        catch (Exception e) when (watch is not null && e is MemoryReadException or InconsistentSnapshotException or UnsupportedLayoutException)
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
        if (watch is null) break;
        try { await Task.Delay(TimeSpan.FromSeconds(watch.Value), cancel.Token); }
        catch (TaskCanceledException) { break; }
    } while (!cancel.IsCancellationRequested);
    return 0;
}
catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException or InconsistentSnapshotException
                              or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
{
    if (advise) Console.Error.WriteLine(formatter.Status("BlazeUma could not read the game", e.Message, problem: true, watching: false));
    else Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "error", type = e.GetType().Name, message = e.Message }));
    return 1;
}
finally
{
    reader?.Dispose();
    data?.Dispose();
}
