using BlazeUma.Memory;
using BlazeUma.Memory.Game;

namespace BlazeUma.Automation;

/// <summary>
/// Taps game buttons through the in-game agent: reads the button's <c>m_OnClick</c> UnityEvent and has the
/// agent call <c>UnityEvent.Invoke</c> on Unity's main thread, the same handlers a real tap runs.
/// </summary>
public sealed class ButtonTapper(GameReader reader)
{
    private (int OnClickOffset, ulong InvokeMethod, ulong InvokeFunction)? _resolved;

    /// <summary>Loads the agent if needed and checks it answers. Returns a short status line.</summary>
    public static string EnsureAgent(int pid)
    {
        if (!Injector.IsLoaded(pid))
        {
            string dll = AgentLocator.Find()
                ?? throw new FileNotFoundException("Agent DLL not found; build it with cmake (see agent/).");
            Injector.Inject(pid, dll);
        }
        using var agent = AgentClient.Connect(pid);
        return agent.Send("ping");
    }

    private (int, ulong, ulong) Resolve()
    {
        if (_resolved is { } cached) return cached;
        var r = reader.ManagedReader;
        ulong buttonClass = r.Runtime.GetClass(ButtonFinder.ButtonClass);
        if (buttonClass == 0) throw new InvalidOperationException("Gallop.ButtonCommon is not initialised yet.");
        var onClick = r.Runtime.FindField(buttonClass, "m_OnClick")
            ?? throw new InvalidOperationException("m_OnClick not found on ButtonCommon's class chain.");
        ulong unityEvent = r.Runtime.GetClass("UnityEngine.Events.UnityEvent");
        if (unityEvent == 0) throw new InvalidOperationException("UnityEngine.Events.UnityEvent is not initialised yet.");
        var (mi, fn) = r.Runtime.RequireMethod(unityEvent, "Invoke");
        _resolved = (onClick.Field.Offset, mi, fn);
        return _resolved.Value;
    }

    /// <summary>Taps the button at <paramref name="button"/>. Returns the agent's reply ("ok" on success).</summary>
    public string Tap(ulong button)
    {
        var (offset, mi, fn) = Resolve();
        ulong eventObj = reader.ManagedReader.Memory.ReadPointer(button + (ulong)offset);
        if (eventObj == 0) return "error button has no m_OnClick handlers";
        using var agent = AgentClient.Connect(reader.ProcessId);
        return agent.Send($"tap {eventObj:x} {fn:x} {mi:x}");
    }
}
