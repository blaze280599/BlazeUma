using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BlazeUma.Automation;

/// <summary>
/// Loads the agent DLL into the game with LoadLibraryW on a remote thread. This is the only code in
/// BlazeUma that opens the game with write access; the memory reader stays read-only.
/// </summary>
public static class Injector
{
    public const string AgentFileName = "BlazeUma.Agent.dll";

    private const uint CreateThread = 0x0002, VmOperation = 0x0008, VmRead = 0x0010, VmWrite = 0x0020,
        QueryLimited = 0x1000, Synchronize = 0x00100000;
    private const uint MemCommitReserve = 0x3000, MemRelease = 0x8000, PageReadWrite = 0x04;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAllocEx(SafeProcessHandle process, nint address, nint size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(SafeProcessHandle process, nint address, nint size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(SafeProcessHandle process, nint address, byte[] buffer, nint size, out nint written);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeWaitHandle CreateRemoteThread(SafeProcessHandle process, nint attributes, nint stack, nint start, nint parameter, uint flags, out uint threadId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeThread(SafeWaitHandle thread, out uint exitCode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandleW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern nint GetProcAddress(nint module, string name);

    /// <summary>True if a module with the agent's file name is loaded in the process.</summary>
    public static bool IsLoaded(int pid)
    {
        try
        {
            return Process.GetProcessById(pid).Modules.Cast<ProcessModule>()
                .Any(m => string.Equals(m.ModuleName, AgentFileName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Loads <paramref name="dllPath"/> into <paramref name="pid"/>. Returns false if it was already loaded.</summary>
    public static bool Inject(int pid, string dllPath)
    {
        dllPath = Path.GetFullPath(dllPath);
        if (!File.Exists(dllPath)) throw new FileNotFoundException("Agent DLL not found; build agent/ first.", dllPath);
        if (IsLoaded(pid)) return false;

        using var process = OpenProcess(CreateThread | VmOperation | VmRead | VmWrite | QueryLimited | Synchronize, false, pid);
        if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess for injection failed");

        byte[] path = Encoding.Unicode.GetBytes(dllPath + "\0");
        nint remote = VirtualAllocEx(process, 0, path.Length, MemCommitReserve, PageReadWrite);
        if (remote == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAllocEx failed");
        try
        {
            if (!WriteProcessMemory(process, remote, path, path.Length, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteProcessMemory failed");
            // kernel32 is mapped at the same address in every process of this boot session.
            nint loadLibrary = GetProcAddress(GetModuleHandleW("kernel32.dll"), "LoadLibraryW");
            using var thread = CreateRemoteThread(process, 0, 0, loadLibrary, remote, 0, out _);
            if (thread.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateRemoteThread failed");
            if (WaitForSingleObject(thread, 10_000) != 0) throw new TimeoutException("LoadLibraryW did not return within 10 s.");
            GetExitCodeThread(thread, out uint module);
            if (module == 0) throw new InvalidOperationException("LoadLibraryW failed inside the game (DLL rejected or not found).");
            return true;
        }
        finally
        {
            VirtualFreeEx(process, remote, 0, MemRelease);
        }
    }
}
