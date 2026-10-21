using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BlazeUma.Memory.Native;

/// <summary>
/// The running game, opened with PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION only.
/// Module bases are discovered on every open; never cache them across launches.
/// </summary>
public sealed class GameProcess : IMemory, IDisposable
{
    public const string ExecutableName = "UmamusumePrettyDerby";
    public const string GameAssemblyName = "GameAssembly.dll";

    private readonly SafeProcessHandle _handle;

    public int ProcessId { get; }
    public ulong GameAssemblyBase { get; }
    public string GameAssemblyPath { get; }

    private GameProcess(int pid, ulong moduleBase, string modulePath, SafeProcessHandle handle)
    {
        ProcessId = pid;
        GameAssemblyBase = moduleBase;
        GameAssemblyPath = modulePath;
        _handle = handle;
    }

    /// <summary>Attaches to the single running game, or to <paramref name="pid"/> if given.</summary>
    public static GameProcess Open(int? pid = null)
    {
        if (!Environment.Is64BitProcess)
            throw new InvalidOperationException("BlazeUma must run as a 64-bit process.");

        var matches = Process.GetProcessesByName(ExecutableName).Select(p => p.Id).ToList();
        int target = pid switch
        {
            null when matches.Count == 1 => matches[0],
            null => throw new InvalidOperationException(
                matches.Count == 0 ? "The game is not running." : "More than one game process; pass a pid."),
            _ when matches.Contains(pid.Value) => pid.Value,
            _ => throw new InvalidOperationException($"PID {pid} is not {ExecutableName}.exe."),
        };

        var (moduleBase, modulePath) = FindModule(target, GameAssemblyName)
            ?? throw new InvalidOperationException($"{GameAssemblyName} is not loaded yet.");

        var handle = NativeMethods.OpenProcess(
            NativeMethods.ProcessVmRead | NativeMethods.ProcessQueryLimitedInformation, false, target);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess failed");
        return new GameProcess(target, moduleBase, modulePath, handle);
    }

    private static (ulong Base, string Path)? FindModule(int pid, string name)
    {
        using var snapshot = NativeMethods.CreateToolhelp32Snapshot(
            NativeMethods.Th32csSnapModule | NativeMethods.Th32csSnapModule32, pid);
        if (snapshot.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateToolhelp32Snapshot failed");
        var entry = new NativeMethods.ModuleEntry32 { Size = (uint)Marshal.SizeOf<NativeMethods.ModuleEntry32>() };
        for (bool ok = NativeMethods.Module32First(snapshot, ref entry); ok; ok = NativeMethods.Module32Next(snapshot, ref entry))
        {
            if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                return ((ulong)entry.BaseAddress, entry.Path);
        }
        return null;
    }

    public void Read(ulong address, Span<byte> buffer)
    {
        if (buffer.Length is <= 0 or > MemoryExtensions.MaxReadSize
            || address < MemoryExtensions.MinAddress || address >= MemoryExtensions.MaxAddress)
            throw new MemoryReadException($"Invalid read of {buffer.Length} bytes at 0x{address:X}");
        if (!NativeMethods.ReadProcessMemory(_handle, (nint)address, ref MemoryMarshal.GetReference(buffer), buffer.Length, out var read))
            throw new MemoryReadException($"ReadProcessMemory failed at 0x{address:X} (error {Marshal.GetLastWin32Error()})");
        if (read != buffer.Length)
            throw new MemoryReadException($"Partial read at 0x{address:X}: {read} of {buffer.Length} bytes");
    }

    /// <summary>global-metadata.dat next to this GameAssembly.dll (works for any install location).</summary>
    public string DefaultMetadataPath =>
        Path.Combine(Path.GetDirectoryName(GameAssemblyPath)!, "UmamusumePrettyDerby_Data", "il2cpp_data", "Metadata", "global-metadata.dat");

    public void Dispose() => _handle.Dispose();
}
