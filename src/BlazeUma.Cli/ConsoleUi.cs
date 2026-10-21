using System.Runtime.InteropServices;

namespace BlazeUma.Cli;

/// <summary>The screen the app shows next.</summary>
internal enum Mode { Menu, Advisor, AutoBot, Exit }

/// <summary>A menu entry: what it is called and one line on what it does.</summary>
internal sealed record MenuItem(string Label, string? Description = null);

/// <summary>The banner, arrow-key menus and small console helpers of the interactive app.</summary>
internal static class ConsoleUi
{
    private static readonly string[] Logo =
    [
        "██████╗ ██╗      █████╗ ███████╗███████╗██╗   ██╗███╗   ███╗ █████╗ ",
        "██╔══██╗██║     ██╔══██╗╚══███╔╝██╔════╝██║   ██║████╗ ████║██╔══██╗",
        "██████╔╝██║     ███████║  ███╔╝ █████╗  ██║   ██║██╔████╔██║███████║",
        "██╔══██╗██║     ██╔══██║ ███╔╝  ██╔══╝  ██║   ██║██║╚██╔╝██║██╔══██║",
        "██████╔╝███████╗██║  ██║███████╗███████╗╚██████╔╝██║ ╚═╝ ██║██║  ██║",
        "╚═════╝ ╚══════╝╚═╝  ╚═╝╚══════╝╚══════╝ ╚═════╝ ╚═╝     ╚═╝╚═╝  ╚═╝",
    ];
    /// <summary>256-colour codes from yellow to red, one per logo row.</summary>
    private static readonly int[] LogoColors = [226, 220, 214, 208, 202, 196];

    public static string Paint(string text, string code) => $"\u001b[{code}m{text}\u001b[0m";
    public static string Bold(string t) => Paint(t, "1");
    public static string Dim(string t) => Paint(t, "2");
    public static string Accent(string t) => Paint(t, "1;38;5;208");
    public static string Warn(string t) => Paint(t, "1;33");

    public static void Setup()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.Title = "BlazeUma";
        EnableVirtualTerminal();
    }

    /// <summary>Clears the screen and draws the logo with <paramref name="subtitle"/> under it.</summary>
    public static void Banner(string subtitle)
    {
        Console.Clear();
        Console.WriteLine();
        for (int i = 0; i < Logo.Length; i++) Console.WriteLine("  " + Paint(Logo[i], $"1;38;5;{LogoColors[i]}"));
        Console.WriteLine();
        Console.WriteLine("  " + Dim("Umamusume: Pretty Derby career helper") + Dim("  ·  ") + Accent(subtitle));
        Console.WriteLine();
    }

    /// <summary>
    /// Shows <paramref name="items"/> under <paramref name="title"/> and returns the chosen index. Arrow keys
    /// move, Enter picks, a number picks directly; Esc picks <paramref name="escape"/> when there is one.
    /// </summary>
    public static int Choose(string title, IReadOnlyList<MenuItem> items, string? detail = null, int? escape = null)
    {
        FlushInput();
        Console.WriteLine("  " + Bold(title));
        if (detail is not null)
            foreach (string line in detail.Split('\n')) Console.WriteLine("  " + line);
        Console.WriteLine();

        int? top = null;   // taken after the first draw, so a scroll at the bottom of the window does not shift it
        int selected = 0;
        bool cursor = Console.CursorVisible;
        Console.CursorVisible = false;
        try
        {
            while (true)
            {
                if (top is int row) Console.SetCursorPosition(0, row);
                int labelWidth = items.Max(x => x.Label.Length) + 3;
                for (int i = 0; i < items.Count; i++)
                {
                    string label = $"{i + 1}. {items[i].Label}".PadRight(labelWidth + 3);
                    string line = i == selected ? "  " + Accent("❯ " + label) : "    " + label;
                    if (items[i].Description is { } d) line += Dim("  " + d);
                    Console.WriteLine(line + "\u001b[K");
                }
                Console.WriteLine();
                Console.WriteLine("  " + Dim($"↑/↓ move · Enter select · 1-{items.Count} shortcut" + (escape is int e ? $" · Esc {items[e].Label.ToLowerInvariant()}" : "")) + "\u001b[K");
                top ??= Console.CursorTop - items.Count - 2;

                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow: selected = (selected + items.Count - 1) % items.Count; break;
                    case ConsoleKey.DownArrow: selected = (selected + 1) % items.Count; break;
                    case ConsoleKey.Enter: return Done(selected);
                    case ConsoleKey.Escape when escape is int back: return Done(back);
                    default:
                        if (key.KeyChar is >= '1' and <= '9' && key.KeyChar - '1' < items.Count) return Done(key.KeyChar - '1');
                        break;
                }
            }
        }
        finally { Console.CursorVisible = cursor; }

        int Done(int choice)
        {
            Console.WriteLine();
            return choice;
        }
    }

    /// <summary>Drops keys typed before a prompt, e.g. the hotkey that opened it.</summary>
    public static void FlushInput()
    {
        while (Console.KeyAvailable) Console.ReadKey(intercept: true);
    }

    public static void WaitForKey(string prompt = "Press any key to continue")
    {
        FlushInput();
        Console.WriteLine();
        Console.WriteLine("  " + Dim(prompt));
        Console.ReadKey(intercept: true);
    }

    /// <summary>Best effort: bring the console in front of the game when the bot needs the user.</summary>
    public static void BringToFront()
    {
        nint window = GetConsoleWindow();
        if (window != 0) SetForegroundWindow(window);
    }

    private static void EnableVirtualTerminal()
    {
        const int StdOutput = -11;
        const uint VirtualTerminalProcessing = 0x0004;
        nint handle = GetStdHandle(StdOutput);
        if (GetConsoleMode(handle, out uint mode)) SetConsoleMode(handle, mode | VirtualTerminalProcessing);
    }

    [DllImport("kernel32.dll")] private static extern nint GetConsoleWindow();
    [DllImport("kernel32.dll")] private static extern nint GetStdHandle(int handle);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(nint handle, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(nint handle, uint mode);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
}
