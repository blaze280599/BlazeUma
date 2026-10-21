using System.Text.Json;
using System.Text.Json.Serialization;
using BlazeUma.Memory;
using BlazeUma.Memory.Game;
using BlazeUma.Memory.Il2Cpp;

// BlazeUma.Cli [--pid N] [--metadata PATH] [--watch SECONDS]
// Prints a read-only career snapshot as JSON. With --watch, prints one JSON document per line until Ctrl+C.

int? pid = null;
string? metadata = null;
double? watch = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--pid": pid = int.Parse(args[++i]); break;
        case "--metadata": metadata = args[++i]; break;
        case "--watch": watch = double.Parse(args[++i]); break;
        default:
            Console.Error.WriteLine("usage: BlazeUma.Cli [--pid N] [--metadata PATH] [--watch SECONDS]");
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

try
{
    using var reader = GameReader.Open(pid, metadata);
    do
    {
        try
        {
            Console.WriteLine(JsonSerializer.Serialize(reader.Snapshot(), json));
        }
        catch (Exception e) when (watch is not null && e is MemoryReadException or InconsistentSnapshotException)
        {
            // Transient in watch mode (screen changes, objects being replaced); report and keep polling.
            Console.WriteLine(JsonSerializer.Serialize(new { status = "unavailable", reason = e.Message }, json));
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
    Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "error", type = e.GetType().Name, message = e.Message }));
    return 1;
}
