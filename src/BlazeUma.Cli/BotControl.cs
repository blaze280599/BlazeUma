using System.Runtime.InteropServices;

namespace BlazeUma.Cli;

/// <summary>
/// The auto bot's hotkeys: Insert pauses, Page Down stops. Polled with GetAsyncKeyState so they work while
/// the game has focus. Only listens while armed (the bot is playing); the bot takes a request up at its next
/// safe point: before the next action, or before the next tap in a race.
/// </summary>
internal sealed class BotControl : IDisposable
{
    private const int VkInsert = 0x2D, VkNext = 0x22;   // VK_NEXT is Page Down
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(40);

    private readonly Thread _thread;
    private volatile bool _armed, _pause, _stop, _disposed;

    public BotControl()
    {
        _thread = new Thread(Watch) { IsBackground = true, Name = "BlazeUma hotkeys" };
        _thread.Start();
    }

    public bool PauseRequested => _pause;
    public bool StopRequested => _stop;
    /// <summary>A pause or a stop is waiting to be taken up.</summary>
    public bool Halting => _pause || _stop;

    /// <summary>Start listening; <paramref name="clear"/> drops a request still pending.</summary>
    public void Arm(bool clear = true)
    {
        if (clear) _pause = _stop = false;
        _armed = true;
    }

    public void Disarm() => _armed = false;

    public void Dispose()
    {
        _disposed = true;
        _thread.Join();
    }

    private void Watch()
    {
        bool insert = false, pageDown = false;
        while (!_disposed)
        {
            Thread.Sleep(Poll);
            bool insertNow = Down(VkInsert), pageDownNow = Down(VkNext);
            if (_armed && pageDownNow && !pageDown && !_stop)
            {
                _stop = true;
                Console.WriteLine(ConsoleUi.Warn("  ■ Stop requested; stopping at the next safe point…"));
            }
            else if (_armed && insertNow && !insert && !_pause && !_stop)
            {
                _pause = true;
                Console.WriteLine(ConsoleUi.Warn("  ‖ Pause requested; pausing at the next safe point…"));
            }
            insert = insertNow;
            pageDown = pageDownNow;
        }
    }

    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
