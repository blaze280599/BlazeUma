using System.Text.Json;
using System.Text.Json.Serialization;
using BlazeUma.Advisor;
using BlazeUma.Data;
using BlazeUma.Memory;
using BlazeUma.Memory.Game;
using BlazeUma.Memory.Il2Cpp;

// BlazeUma.Cli [--pid N] [--metadata PATH] [--master PATH] [--watch SECONDS] [--advise]
// Prints a read-only career snapshot as JSON, or with --advise the recommended action.
// With --watch, repeats until Ctrl+C (snapshots one JSON per line; advice only when it changes).

int? pid = null;
string? metadata = null, master = null;
double? watch = null;
bool advise = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--pid": pid = int.Parse(args[++i]); break;
        case "--metadata": metadata = args[++i]; break;
        case "--master": master = args[++i]; break;
        case "--watch": watch = double.Parse(args[++i]); break;
        case "--advise": advise = true; break;
        default:
            Console.Error.WriteLine("usage: BlazeUma.Cli [--pid N] [--metadata PATH] [--master PATH] [--watch SECONDS] [--advise]");
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
    using var data = advise ? MasterDatabase.Open(master ?? MasterDatabase.PathInGameDirectory(reader.GameDirectory)) : null;
    var advisor = data is null ? null : new CareerAdvisor(data);
    string? lastAdvice = null;
    do
    {
        try
        {
            var snapshot = reader.Snapshot();
            if (advisor is null)
            {
                Console.WriteLine(JsonSerializer.Serialize(snapshot, json));
            }
            else
            {
                string text = Format(advisor.Advise(snapshot));
                if (text != lastAdvice) Console.WriteLine(text);
                lastAdvice = text;
            }
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

static string Format(Advice advice)
{
    var lines = new List<string> { $"== {advice.Summary}" };
    lines.AddRange(advice.Options.Take(8).Select((o, i) => $"  {(i == 0 ? ">" : " ")} {o.Label,-32} {o.Score,7:0.0}   {string.Join("; ", o.Reasons)}"));
    lines.AddRange(advice.Notes.Select(n => $"  - {n}"));
    return string.Join(Environment.NewLine, lines);
}
