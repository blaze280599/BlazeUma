using System.Text;

namespace BlazeUma.Memory.Il2Cpp;

/// <summary>
/// Field and collection access on live managed objects by field name.
/// Offsets always come from the runtime field tables; nothing here hardcodes a game class layout.
/// </summary>
public sealed class ManagedReader(IMemory memory, Il2CppRuntime runtime, ObscuredKeys keys)
{
    private const int MaxElements = 100_000;
    private readonly Dictionary<(ulong, string), Il2CppField> _fields = [];

    public IMemory Memory => memory;
    public Il2CppRuntime Runtime => runtime;

    public ulong ClassOf(ulong obj) => memory.ReadPointer(obj);
    public string? ClassName(ulong obj) => obj == 0 ? null : runtime.ObjectClassName(obj);

    public Il2CppField Field(ulong obj, string name)
    {
        ulong klass = ClassOf(obj);
        if (!_fields.TryGetValue((klass, name), out var field))
            _fields[(klass, name)] = field = runtime.RequireField(klass, name).Field;
        if (field.IsStatic) throw new UnsupportedLayoutException($"{name} is static.");
        return field;
    }

    private ulong Address(ulong obj, string name, params Il2CppTypeKind[] kinds)
    {
        var field = Field(obj, name);
        if (kinds.Length > 0 && !kinds.Contains(field.Kind))
            throw new UnsupportedLayoutException($"{name} has kind {field.Kind}, expected {string.Join("/", kinds)}.");
        return obj + (ulong)field.Offset;
    }

    public ulong Ptr(ulong obj, string name) => memory.ReadPointer(Address(obj, name,
        Il2CppTypeKind.Class, Il2CppTypeKind.GenericInstance, Il2CppTypeKind.SzArray, Il2CppTypeKind.String));
    public int Int(ulong obj, string name) => memory.ReadInt32(Address(obj, name, Il2CppTypeKind.Int32, Il2CppTypeKind.ValueType));
    public bool Bool(ulong obj, string name) => memory.ReadByte(Address(obj, name, Il2CppTypeKind.Boolean)) != 0;

    public int ObscuredInt(ulong obj, string name) =>
        ObscuredDecoder.DecodeInt(memory.ReadBytes(Address(obj, name, Il2CppTypeKind.ValueType), ObscuredDecoder.IntSize), keys.IntKey);
    public bool ObscuredBool(ulong obj, string name) =>
        ObscuredDecoder.DecodeBool(memory.ReadBytes(Address(obj, name, Il2CppTypeKind.ValueType), ObscuredDecoder.BoolSize), keys.BoolKey);
    public long ObscuredLong(ulong obj, string name) =>
        ObscuredDecoder.DecodeLong(memory.ReadBytes(Address(obj, name, Il2CppTypeKind.ValueType), ObscuredDecoder.LongSize), keys.LongKey);

    public int DecodeObscuredInt(ReadOnlySpan<byte> inline) => ObscuredDecoder.DecodeInt(inline, keys.IntKey);

    private static int CheckedCount(long count) => count is >= 0 and <= MaxElements
        ? (int)count : throw new UnsupportedLayoutException($"Implausible element count {count}.");

    /// <summary>Elements of a System.Collections.Generic.List&lt;T&gt; of reference types (_items +0x10, _size +0x18).</summary>
    public IReadOnlyList<ulong> ListItems(ulong list)
    {
        if (list == 0) return [];
        int size = CheckedCount(memory.ReadInt32(list + 0x18));
        return size == 0 ? [] : ArrayItems(memory.ReadPointer(list + 0x10), size);
    }

    /// <summary>Elements of a reference-type T[] (max_length +0x18, data +0x20).</summary>
    public IReadOnlyList<ulong> ArrayItems(ulong array, int? take = null)
    {
        if (array == 0) return [];
        int length = CheckedCount((long)memory.ReadUInt64(array + 0x18));
        int count = take ?? length;
        if (count > length) throw new UnsupportedLayoutException("List size exceeds its backing array.");
        if (count == 0) return [];
        var raw = memory.ReadBytes(array + 0x20, count * 8);
        var items = new ulong[count];
        for (int i = 0; i < count; i++) items[i] = BitConverter.ToUInt64(raw, i * 8);
        return items;
    }

    /// <summary>Elements of an inline ObscuredInt[] (20-byte elements from +0x20).</summary>
    public IReadOnlyList<int> ObscuredIntArray(ulong array)
    {
        if (array == 0) return [];
        int length = CheckedCount((long)memory.ReadUInt64(array + 0x18));
        if (length == 0) return [];
        var raw = memory.ReadBytes(array + 0x20, length * ObscuredDecoder.IntSize);
        return Enumerable.Range(0, length)
            .Select(i => ObscuredDecoder.DecodeInt(raw.AsSpan(i * ObscuredDecoder.IntSize, ObscuredDecoder.IntSize), keys.IntKey))
            .ToArray();
    }

    /// <summary>
    /// Live entries of a Dictionary&lt;TKey, TRef&gt; (entries +0x18, count +0x20). Each entry starts with
    /// hashCode and next; removed entries have a negative hashCode. <paramref name="stride"/> and
    /// <paramref name="valueOffset"/> depend on TKey: 24/16 for int keys, 40/32 for inline ObscuredInt keys.
    /// </summary>
    private IEnumerable<(byte[] Entry, ulong Value)> DictionaryEntries(ulong dict, int stride, int valueOffset)
    {
        if (dict == 0) yield break;
        ulong entries = memory.ReadPointer(dict + 0x18);
        int count = CheckedCount(memory.ReadInt32(dict + 0x20));
        if (count == 0) yield break;
        if ((long)memory.ReadUInt64(entries + 0x18) < count) throw new UnsupportedLayoutException("Dictionary count exceeds entries.");
        var raw = memory.ReadBytes(entries + 0x20, count * stride);
        for (int i = 0; i < count; i++)
        {
            var entry = raw.AsSpan(i * stride, stride);
            if (BitConverter.ToInt32(entry) < 0) continue;
            yield return (entry.ToArray(), BitConverter.ToUInt64(entry[valueOffset..]));
        }
    }

    public IEnumerable<(int Key, ulong Value)> IntKeyDictionary(ulong dict) =>
        DictionaryEntries(dict, 24, 16).Select(e => (BitConverter.ToInt32(e.Entry, 8), e.Value));

    public IEnumerable<(int Key, ulong Value)> ObscuredIntKeyDictionary(ulong dict) =>
        DictionaryEntries(dict, 40, 32).Select(e => (DecodeObscuredInt(e.Entry.AsSpan(8, ObscuredDecoder.IntSize)), e.Value));

    /// <summary>System.String contents (length +0x10, UTF-16 chars +0x14), or null.</summary>
    public string? String(ulong str, int maxLength = 4096)
    {
        if (str == 0) return null;
        int length = memory.ReadInt32(str + 0x10);
        if (length < 0 || length > maxLength) throw new UnsupportedLayoutException($"Implausible string length {length}.");
        return length == 0 ? "" : Encoding.Unicode.GetString(memory.ReadBytes(str + 0x14, length * 2));
    }

    /// <summary>
    /// Instance of a Gallop.Singleton&lt;T&gt; / MonoSingleton&lt;T&gt;: walks from the concrete class to the
    /// generic parent declaring the static <c>_instance</c>. Returns 0 if the class or instance does not exist yet.
    /// </summary>
    public ulong SingletonInstance(string qualifiedName)
    {
        ulong klass = runtime.GetClass(qualifiedName);
        if (klass == 0) return 0;
        for (ulong k = runtime.Parent(klass); k != 0; k = runtime.Parent(k))
        {
            var field = runtime.DeclaredFields(k).FirstOrDefault(f => f.IsStatic && !f.IsLiteral && f.Name == "_instance");
            if (field is null) continue;
            ulong statics = runtime.StaticFieldData(k);
            if (statics == 0) return 0;
            ulong instance = memory.ReadPointer(statics + (ulong)field.Offset);
            if (instance != 0 && ClassOf(instance) != klass)
                throw new UnsupportedLayoutException($"{qualifiedName} singleton points at {ClassName(instance)}.");
            return instance;
        }
        throw new UnsupportedLayoutException($"{qualifiedName} has no singleton _instance.");
    }
}
