using BlazeUma.Memory;
using BlazeUma.Memory.Game;

namespace BlazeUma.Automation;

/// <summary>
/// Taps game buttons through the in-game agent: reads the button's <c>m_OnClick</c> UnityEvent and has the
/// agent call <c>UnityEvent.Invoke</c> on Unity's main thread, the same handlers a real tap runs.
/// </summary>
public sealed class ButtonTapper(GameReader reader)
{
    /// <summary>The agent version this build talks to; an older loaded agent is swapped out.</summary>
    public const string AgentVersion = "0.3";

    private (int OnClickOffset, ulong InvokeMethod, ulong InvokeFunction)? _resolved;
    private ((ulong Mi, ulong Fn) GameObject, (ulong Mi, ulong Fn) ActiveInHierarchy)? _activeMethods;

    /// <summary>Loads the agent if needed (replacing an older version) and checks it answers. Returns the ping reply.</summary>
    public static string EnsureAgent(int pid)
    {
        if (Injector.IsLoaded(pid))
        {
            string reply;
            using (var agent = AgentClient.Connect(pid)) reply = agent.Send("ping");
            if (reply.EndsWith($" v{AgentVersion}")) return reply;
            using (var agent = AgentClient.Connect(pid)) agent.Send("quit");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (Injector.IsLoaded(pid))
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException($"Old agent ({reply}) did not unload.");
                Thread.Sleep(100);
            }
        }
        string dll = AgentLocator.Find()
            ?? throw new FileNotFoundException("Agent DLL not found; build it with cmake (see agent/).");
        Injector.Inject(pid, dll);
        using var fresh = AgentClient.Connect(pid);
        return fresh.Send("ping");
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

    /// <summary>
    /// True if the button's GameObject is active in the hierarchy, i.e. actually on screen. Enabled and
    /// interactable are not enough: hidden layouts keep their buttons enabled. Asks the agent (Unity API,
    /// main thread). Null if the agent call failed.
    /// </summary>
    public bool? IsActive(ulong button)
    {
        if (_activeMethods is null)
        {
            var rt = reader.ManagedReader.Runtime;
            ulong component = rt.GetClass("UnityEngine.Component");
            ulong gameObject = rt.GetClass("UnityEngine.GameObject");
            if (component == 0 || gameObject == 0) throw new InvalidOperationException("UnityEngine.Component/GameObject not initialised yet.");
            _activeMethods = (rt.RequireMethod(component, "get_gameObject"), rt.RequireMethod(gameObject, "get_activeInHierarchy"));
        }
        var (go, active) = _activeMethods.Value;
        using var agent = AgentClient.Connect(reader.ProcessId);
        ulong? obj = Call(agent, button, go);
        if (obj is not ulong goPtr || goPtr == 0) return null;
        return Call(agent, goPtr, active) is ulong flag ? (flag & 0xFF) != 0 : null;
    }

    private static ulong? Call(AgentClient agent, ulong obj, (ulong Mi, ulong Fn) method)
    {
        string reply = agent.Send($"call {obj:x} {method.Fn:x} {method.Mi:x}");
        return reply.StartsWith("ok ") ? Convert.ToUInt64(reply[3..], 16) : null;
    }
}
