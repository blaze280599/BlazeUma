using System.Buffers.Binary;
using System.Text;

namespace BlazeUma.Memory;

/// <summary>Raised when a remote read fails or returns fewer bytes than requested.</summary>
public sealed class MemoryReadException(string message) : Exception(message);

/// <summary>Raised when the game's code or data no longer matches a verified layout.</summary>
public sealed class UnsupportedLayoutException(string message) : Exception(message);

/// <summary>Read-only view of another process's address space.</summary>
public interface IMemory
{
    /// <summary>Fills <paramref name="buffer"/> exactly, or throws <see cref="MemoryReadException"/>.</summary>
    void Read(ulong address, Span<byte> buffer);
}

public static class MemoryExtensions
{
    public const ulong MinAddress = 0x10000;
    public const ulong MaxAddress = 0x7FFF_FFFF_FFFF;
    public const int MaxReadSize = 16 * 1024 * 1024;

    public static byte[] ReadBytes(this IMemory memory, ulong address, int size)
    {
        var buffer = new byte[size];
        memory.Read(address, buffer);
        return buffer;
    }

    public static byte ReadByte(this IMemory memory, ulong address)
    {
        Span<byte> b = stackalloc byte[1];
        memory.Read(address, b);
        return b[0];
    }

    public static ushort ReadUInt16(this IMemory memory, ulong address)
    {
        Span<byte> b = stackalloc byte[2];
        memory.Read(address, b);
        return BinaryPrimitives.ReadUInt16LittleEndian(b);
    }

    public static int ReadInt32(this IMemory memory, ulong address)
    {
        Span<byte> b = stackalloc byte[4];
        memory.Read(address, b);
        return BinaryPrimitives.ReadInt32LittleEndian(b);
    }

    public static uint ReadUInt32(this IMemory memory, ulong address) => unchecked((uint)memory.ReadInt32(address));

    public static ulong ReadUInt64(this IMemory memory, ulong address)
    {
        Span<byte> b = stackalloc byte[8];
        memory.Read(address, b);
        return BinaryPrimitives.ReadUInt64LittleEndian(b);
    }

    public static ulong ReadPointer(this IMemory memory, ulong address) => memory.ReadUInt64(address);

    /// <summary>Reads a NUL-terminated UTF-8 string without crossing into the next page unless needed.</summary>
    public static string ReadCString(this IMemory memory, ulong address, int limit = 256)
    {
        var bytes = new List<byte>(32);
        while (bytes.Count < limit)
        {
            ulong at = address + (ulong)bytes.Count;
            int toPageEnd = 0x1000 - (int)(at & 0xFFF);
            var chunk = memory.ReadBytes(at, Math.Min(toPageEnd, limit - bytes.Count));
            int nul = Array.IndexOf(chunk, (byte)0);
            if (nul >= 0)
            {
                bytes.AddRange(chunk.AsSpan(0, nul));
                return Encoding.UTF8.GetString([.. bytes]);
            }
            bytes.AddRange(chunk);
        }
        throw new MemoryReadException($"Unterminated native string at 0x{address:X}");
    }

    public static bool IsPlausiblePointer(ulong value) => value is >= MinAddress and < MaxAddress && (value & 7) == 0;
}
