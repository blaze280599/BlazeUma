using System.Buffers.Binary;

namespace BlazeUma.Memory.Il2Cpp;

/// <summary>Raised when an Obscured wrapper's flags or fake value disagree with the decoded value.</summary>
public sealed class InconsistentSnapshotException(string message) : Exception(message);

/// <summary>
/// Local decoders for CodeStage ACTk Obscured* value types as stored INLINE (no object header).
/// Never calls the game's own Decrypt; the layouts are verified by <see cref="ObscuredKeys"/>.
/// </summary>
public static class ObscuredDecoder
{
    public const int IntSize = 20;   // key u32, hidden u32, inited u8 (+3), fake i32, fakeActive u8 (+3)
    public const int BoolSize = 12;  // key u8 (+3), hidden i32, inited u8, fake u8, fakeActive u8 (+1)
    public const int LongSize = 40;  // key i64, hidden i64, inited u8 (+7), fake i64, fakeActive u8 (+7)

    // ACTk ObscuredBool stores (value ? 213 : 181) ^ key.
    private const int BoolFalse = 181, BoolTrue = 213;

    private static bool Flag(byte value, string name) => value switch
    {
        0 => false,
        1 => true,
        _ => throw new InconsistentSnapshotException($"Invalid Obscured {name} flag {value}"),
    };

    public static int DecodeInt(ReadOnlySpan<byte> data, uint staticKey)
    {
        uint key = BinaryPrimitives.ReadUInt32LittleEndian(data);
        uint hidden = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        bool inited = Flag(data[8], "inited");
        int fake = BinaryPrimitives.ReadInt32LittleEndian(data[12..]);
        bool fakeActive = Flag(data[16], "fakeValueActive");
        if (!inited) return 0;  // the game also treats an uninitialized wrapper as 0
        int value = unchecked((int)(hidden ^ (key != 0 ? key : staticKey)));
        if (fakeActive && value != fake)
            throw new InconsistentSnapshotException("ObscuredInt fake value mismatch; retry the read.");
        return value;
    }

    public static bool DecodeBool(ReadOnlySpan<byte> data, byte staticKey)
    {
        byte key = data[0];
        int hidden = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        bool inited = Flag(data[8], "inited");
        bool fake = Flag(data[9], "fakeValue");
        bool fakeActive = Flag(data[10], "fakeValueActive");
        if (!inited) return false;
        int decoded = hidden ^ (key != 0 ? key : staticKey);
        bool value = decoded switch
        {
            BoolTrue => true,
            BoolFalse => false,
            _ => throw new InconsistentSnapshotException($"ObscuredBool decoded to {decoded}, expected 181 or 213."),
        };
        if (fakeActive && value != fake)
            throw new InconsistentSnapshotException("ObscuredBool fake value mismatch; retry the read.");
        return value;
    }

    public static long DecodeLong(ReadOnlySpan<byte> data, long staticKey)
    {
        long key = BinaryPrimitives.ReadInt64LittleEndian(data);
        long hidden = BinaryPrimitives.ReadInt64LittleEndian(data[8..]);
        bool inited = Flag(data[16], "inited");
        long fake = BinaryPrimitives.ReadInt64LittleEndian(data[24..]);
        bool fakeActive = Flag(data[32], "fakeValueActive");
        if (!inited) return 0;
        long value = hidden ^ (key != 0 ? key : staticKey);
        if (fakeActive && value != fake)
            throw new InconsistentSnapshotException("ObscuredLong fake value mismatch; retry the read.");
        return value;
    }
}

/// <summary>Verifies the live Obscured* layouts match <see cref="ObscuredDecoder"/> and reads their static fallback keys.</summary>
public sealed class ObscuredKeys
{
    private const string Ns = "CodeStage.AntiCheat.ObscuredTypes.";

    public uint IntKey { get; }
    public byte BoolKey { get; }
    public long LongKey { get; }

    internal ObscuredKeys(uint intKey, byte boolKey, long longKey) => (IntKey, BoolKey, LongKey) = (intKey, boolKey, longKey);

    public ObscuredKeys(IMemory memory, Il2CppRuntime runtime)
    {
        IntKey = memory.ReadUInt32(Verify(runtime, "ObscuredInt",
            new() { ["currentCryptoKey"] = 0x10, ["hiddenValue"] = 0x14, ["inited"] = 0x18, ["fakeValue"] = 0x1C, ["fakeValueActive"] = 0x20 }));
        BoolKey = memory.ReadByte(Verify(runtime, "ObscuredBool",
            new() { ["currentCryptoKey"] = 0x10, ["hiddenValue"] = 0x14, ["inited"] = 0x18, ["fakeValue"] = 0x19, ["fakeValueActive"] = 0x1A }));
        LongKey = (long)memory.ReadUInt64(Verify(runtime, "ObscuredLong",
            new() { ["currentCryptoKey"] = 0x10, ["hiddenValue"] = 0x18, ["inited"] = 0x20, ["fakeValue"] = 0x28, ["fakeValueActive"] = 0x30 }));

        // The observed two-argument ObscuredInt.Decrypt contains `test edi,edi; je; xor ebx,edi` at +0x2F.
        ulong intClass = runtime.GetClass(Ns + "ObscuredInt");
        byte[] signature = [0x85, 0xFF, 0x74, 0x0F, 0x33, 0xDF];
        if (!runtime.MethodPointers(intClass, "Decrypt").Any(p => memory.ReadBytes(p + 0x2F, 6).AsSpan().SequenceEqual(signature)))
            throw new UnsupportedLayoutException("ObscuredInt decoder signature changed.");
    }

    /// <summary>Checks boxed instance offsets and returns the address of the static cryptoKey.</summary>
    private static ulong Verify(Il2CppRuntime runtime, string name, Dictionary<string, int> expected)
    {
        ulong klass = runtime.GetClass(Ns + name);
        if (klass == 0) throw new UnsupportedLayoutException($"{name} class is not initialized.");
        var fields = runtime.DeclaredFields(klass);
        var instance = fields.Where(f => !f.IsStatic && !f.IsLiteral).ToDictionary(f => f.Name, f => f.Offset);
        if (instance.Count != expected.Count || expected.Any(e => instance.GetValueOrDefault(e.Key, -1) != e.Value))
            throw new UnsupportedLayoutException($"Unsupported {name} layout.");
        var key = fields.SingleOrDefault(f => f.IsStatic && !f.IsLiteral && f.Name == "cryptoKey")
            ?? throw new UnsupportedLayoutException($"{name}.cryptoKey not found.");
        return runtime.StaticFieldData(klass) + (ulong)key.Offset;
    }
}
