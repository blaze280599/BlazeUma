using BlazeUma.Memory;
using BlazeUma.Memory.Game;
using BlazeUma.Memory.Il2Cpp;

namespace BlazeUma.Cli;

/// <summary>
/// <c>BlazeUma uistate</c>: prints the signals the auto bot waits on before tapping (screen change, input lock,
/// event scene) whenever they change, until Ctrl+C. For checking them against what the game shows. Reads memory only.
/// </summary>
internal static class UiStateCommand
{
    public static int Run(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var reader = GameReader.Open(null);
        string? last = null;
        while (!reader.GameExited)
        {
            string line;
            try
            {
                var r = reader.ManagedReader;
                string ready = UiState.Busy(r) ?? (UiState.StoryPlaying(r) ? "event scene playing" : "ready");
                line = $"{ready,-30} {UiState.Describe(r)}";
            }
            catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException) { line = $"unreadable: {e.Message}"; }
            // The frame count moves every frame while a scene plays; compare without it so only real changes print.
            string key = System.Text.RegularExpressions.Regex.Replace(line, @"frame \d+", "");
            if (key != last) Console.WriteLine($"{DateTime.Now:HH:mm:ss.f}  {line}");
            last = key;
            Thread.Sleep(100);
        }
        return 0;
    }
}
