using System.Buffers.Binary;
using BlazeUma.Memory.Il2Cpp;

namespace BlazeUma.Memory.Tests;

public class ObscuredDecoderTests
{
    private static byte[] Int(uint key, uint hidden, byte inited, int fake = 0, byte fakeActive = 0)
    {
        var b = new byte[ObscuredDecoder.IntSize];
        BinaryPrimitives.WriteUInt32LittleEndian(b, key);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), hidden);
        b[8] = inited;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12), fake);
        b[16] = fakeActive;
        return b;
    }

    private static byte[] Bool(byte key, int hidden, byte inited = 1, byte fake = 0, byte fakeActive = 0)
    {
        var b = new byte[ObscuredDecoder.BoolSize];
        b[0] = key;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), hidden);
        (b[8], b[9], b[10]) = (inited, fake, fakeActive);
        return b;
    }

    [Fact]
    public void Int_RealCareerBytes_DecodeSpeed()
    {
        // POC live capture: speed 124.
        var data = Convert.FromHexString("268502005a850200010000000000000000000000");
        Assert.Equal(124, ObscuredDecoder.DecodeInt(data, 0));
    }

    [Fact]
    public void Int_RealDictionaryKey_DecodesChoiceIndex()
    {
        // EventChoiceRewardDict entry key observed on turn 5.
        var data = Convert.FromHexString("2685020027850200010000000000000000000000");
        Assert.Equal(1, ObscuredDecoder.DecodeInt(data, 0x28526));
    }

    [Fact]
    public void Int_ZeroKey_UsesStaticKey_AndIsSigned() =>
        Assert.Equal(-1, ObscuredDecoder.DecodeInt(Int(0, 0xFFFFFFFF ^ 123u, 1, -1, 1), 123));

    [Fact]
    public void Int_Uninitialized_IsZero() => Assert.Equal(0, ObscuredDecoder.DecodeInt(new byte[20], 123));

    [Fact]
    public void Int_FakeMismatch_Throws() =>
        Assert.Throws<InconsistentSnapshotException>(() => ObscuredDecoder.DecodeInt(Int(123, 42 ^ 123u, 1, 43, 1), 0));

    [Fact]
    public void Int_InvalidFlag_Throws() =>
        Assert.Throws<InconsistentSnapshotException>(() => ObscuredDecoder.DecodeInt(Int(1, 1, 2), 0));

    [Theory]
    [InlineData(10, false)]   // observed false flags: hidden 10, key 191
    [InlineData(106, true)]   // observed true flags: hidden 106, key 191
    public void Bool_RealValues(int hidden, bool expected) =>
        Assert.Equal(expected, ObscuredDecoder.DecodeBool(Bool(191, hidden), 0));

    [Fact]
    public void Bool_ZeroKey_UsesStaticKey() => Assert.True(ObscuredDecoder.DecodeBool(Bool(0, 213 ^ 191), 191));

    [Fact]
    public void Bool_UnknownEncoding_Throws() =>
        Assert.Throws<InconsistentSnapshotException>(() => ObscuredDecoder.DecodeBool(Bool(191, 11), 0));

    [Fact]
    public void Bool_FakeMismatch_Throws() =>
        Assert.Throws<InconsistentSnapshotException>(() => ObscuredDecoder.DecodeBool(Bool(191, 106, fake: 0, fakeActive: 1), 0));

    [Fact]
    public void Long_DecodesWithStaticKey()
    {
        var b = new byte[ObscuredDecoder.LongSize];
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(8), 1234567890123L ^ 0xDDC35);
        b[16] = 1;
        Assert.Equal(1234567890123L, ObscuredDecoder.DecodeLong(b, 0xDDC35));
    }
}
