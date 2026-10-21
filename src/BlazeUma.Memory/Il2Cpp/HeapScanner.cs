using System.Buffers.Binary;
using System.Diagnostics;
using BlazeUma.Memory.Native;

namespace BlazeUma.Memory.Il2Cpp;

/// <summary>
/// Bounded, read-only search of the managed heap for live objects of one class: an aligned qword equal to
/// the class pointer followed by a zero monitor. A match is only a candidate (class metadata and stale copies
/// look the same), so callers must validate what they find.
/// </summary>
public static class HeapScanner
{
    private const int Chunk = 1 << 20;

    public static List<ulong> FindObjects(GameProcess process, ulong klass, int max = 64, TimeSpan? budget = null)
    {
        var found = new List<ulong>();
        var clock = Stopwatch.StartNew();
        var limit = budget ?? TimeSpan.FromSeconds(15);
        byte[] buffer = new byte[Chunk + 16];
        Span<byte> needle = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(needle, klass);

        foreach (var (start, size) in process.PrivateRegions())
        {
            for (ulong offset = 0; offset < size; offset += Chunk)
            {
                if (clock.Elapsed > limit || found.Count >= max) return found;
                int length = (int)Math.Min(Chunk + 16, size - offset);
                var span = buffer.AsSpan(0, length);
                try { process.Read(start + offset, span); }
                catch (MemoryReadException) { continue; }

                for (int at = span.IndexOf(needle); at >= 0;)
                {
                    // Chunks overlap by 16 bytes so a header is never split; only count matches that start
                    // inside this chunk so the overlap is not reported twice.
                    if (at % 8 == 0 && at < Chunk && at + 16 <= length && BinaryPrimitives.ReadUInt64LittleEndian(span[(at + 8)..]) == 0)
                    {
                        found.Add(start + offset + (ulong)at);
                        if (found.Count >= max) return found;
                    }
                    int next = span[(at + 8)..].IndexOf(needle);
                    at = next < 0 ? -1 : at + 8 + next;
                }
            }
        }
        return found;
    }
}
