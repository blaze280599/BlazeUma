using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BlazeUma.Memory.Native;

/// <summary>Read-only Win32 surface. No write, allocation, thread or injection APIs are imported.</summary>
internal static class NativeMethods
{
    public const uint ProcessVmRead = 0x0010;
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint Th32csSnapModule = 0x08;
    public const uint Th32csSnapModule32 = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ModuleEntry32
    {
        public uint Size;
        public uint ModuleId;
        public uint ProcessId;
        public uint GlobalUsage;
        public uint ProcessUsage;
        public nint BaseAddress;
        public uint BaseSize;
        public nint Module;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Path;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, ref byte buffer, nint size, out nint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Module32FirstW")]
    public static extern bool Module32First(SafeFileHandle snapshot, ref ModuleEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Module32NextW")]
    public static extern bool Module32Next(SafeFileHandle snapshot, ref ModuleEntry32 entry);
}
