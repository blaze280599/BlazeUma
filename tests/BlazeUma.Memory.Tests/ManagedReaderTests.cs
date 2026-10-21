using System.Buffers.Binary;
using BlazeUma.Memory.Il2Cpp;

namespace BlazeUma.Memory.Tests;

/// <summary>Sparse fake address space for collection-layout tests.</summary>
internal sealed class FakeMemory : IMemory
{
    private readonly SortedDictionary<ulong, byte[]> _blocks = [];

    public FakeMemory Put(ulong address, byte[] bytes) { _blocks[address] = bytes; return this; }

    public void Read(ulong address, Span<byte> buffer)
    {
        foreach (var (start, bytes) in _blocks)
        {
            if (address >= start && address + (ulong)buffer.Length <= start + (ulong)bytes.Length)
            {
                bytes.AsSpan((int)(address - start), buffer.Length).CopyTo(buffer);
                return;
            }
        }
        throw new MemoryReadException($"unmapped 0x{address:X}");
    }
}

public class ManagedReaderTests
{
    private const ulong Dict = 0x100000, Entries = 0x200000, List = 0x300000, Items = 0x400000, Arr = 0x500000;
    private static ManagedReader Reader(FakeMemory m) => new(m, null!, new ObscuredKeys(0x28526, 191, 0));

    private static byte[] Block(int size, Action<Span<byte>> fill)
    {
        var b = new byte[size];
        fill(b);
        return b;
    }

    [Fact]
    public void IntKeyDictionary_SkipsRemovedEntries()
    {
        var memory = new FakeMemory()
            .Put(Dict, Block(0x28, b => { BinaryPrimitives.WriteUInt64LittleEndian(b[0x18..], Entries); BinaryPrimitives.WriteInt32LittleEndian(b[0x20..], 3); }))
            .Put(Entries, Block(0x20 + 3 * 24, b =>
            {
                BinaryPrimitives.WriteUInt64LittleEndian(b[0x18..], 3);
                (int Hash, int Key, ulong Value)[] entries = [(1, 1, 0xAAAA0), (-1, 99, 0xDEAD0) /* removed slot */, (7, 7, 0xBBBB0)];
                for (int i = 0; i < entries.Length; i++)
                {
                    var e = b[(0x20 + i * 24)..];
                    BinaryPrimitives.WriteInt32LittleEndian(e, entries[i].Hash);
                    BinaryPrimitives.WriteInt32LittleEndian(e[8..], entries[i].Key);
                    BinaryPrimitives.WriteUInt64LittleEndian(e[16..], entries[i].Value);
                }
            }));

        var items = Reader(memory).IntKeyDictionary(Dict).ToList();

        Assert.Equal([(1, 0xAAAA0UL), (7, 0xBBBB0UL)], items);
    }

    [Fact]
    public void ObscuredIntKeyDictionary_DecodesKeys()
    {
        var key1 = Convert.FromHexString("2685020027850200010000000000000000000000");
        var memory = new FakeMemory()
            .Put(Dict, Block(0x28, b => { BinaryPrimitives.WriteUInt64LittleEndian(b[0x18..], Entries); BinaryPrimitives.WriteInt32LittleEndian(b[0x20..], 1); }))
            .Put(Entries, Block(0x20 + 40, b =>
            {
                BinaryPrimitives.WriteUInt64LittleEndian(b[0x18..], 1);
                BinaryPrimitives.WriteInt32LittleEndian(b[0x20..], 1);
                key1.CopyTo(b[0x28..]);
                BinaryPrimitives.WriteUInt64LittleEndian(b[0x40..], 0xCAFE0);
            }));

        Assert.Equal([(1, 0xCAFE0UL)], Reader(memory).ObscuredIntKeyDictionary(Dict).ToList());
    }

    [Fact]
    public void ListItems_UsesSizeNotCapacity()
    {
        var memory = new FakeMemory()
            .Put(List, Block(0x20, b => { BinaryPrimitives.WriteUInt64LittleEndian(b[0x10..], Items); BinaryPrimitives.WriteInt32LittleEndian(b[0x18..], 2); }))
            .Put(Items, Block(0x20 + 4 * 8, b =>
            {
                BinaryPrimitives.WriteUInt64LittleEndian(b[0x18..], 4);
                BinaryPrimitives.WriteUInt64LittleEndian(b[0x20..], 0x111110);
                BinaryPrimitives.WriteUInt64LittleEndian(b[0x28..], 0x222220);
            }));

        Assert.Equal([0x111110UL, 0x222220UL], Reader(memory).ListItems(List));
    }

    [Fact]
    public void ObscuredIntArray_DecodesInlineElements()
    {
        // _routeRaceIdArray start observed on turn 1: route race ids 22 and 23.
        var raw = Convert.FromHexString("26850200308502000100000000000000000000002685020031850200010000000000000000000000");
        var memory = new FakeMemory().Put(Arr, Block(0x20 + raw.Length, b =>
        {
            BinaryPrimitives.WriteUInt64LittleEndian(b[0x18..], 2);
            raw.CopyTo(b[0x20..]);
        }));

        Assert.Equal([22, 23], Reader(memory).ObscuredIntArray(Arr));
    }

    [Fact]
    public void ListItems_ImplausibleSize_Throws()
    {
        var memory = new FakeMemory().Put(List, Block(0x20, b => BinaryPrimitives.WriteInt32LittleEndian(b[0x18..], -5)));
        Assert.Throws<UnsupportedLayoutException>(() => Reader(memory).ListItems(List));
    }
}
