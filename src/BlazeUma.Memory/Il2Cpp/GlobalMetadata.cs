using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BlazeUma.Memory.Il2Cpp;

/// <summary>
/// Bounded parser for the observed IL2CPP metadata v31 layout (not every v31 build).
/// Only type-definition names are needed; runtime field tables are read from the live game.
/// </summary>
public sealed class GlobalMetadata
{
    private const uint Magic = 0xFAB11BAF;
    private const int Version = 31;
    private const int TypeDefinitionSize = 88;   // 0x58
    private const int FieldDefinitionSize = 12;

    private readonly byte[] _data;
    private readonly int _stringsOffset, _stringsSize, _typesOffset, _typesSize;
    private Dictionary<string, int>? _typeIndex;

    public string Path { get; }
    public string Sha256 { get; }
    public int TypeCount => _typesSize / TypeDefinitionSize;

    public GlobalMetadata(string path)
    {
        Path = path;
        _data = File.ReadAllBytes(path);
        if (U32(0) != Magic || (int)U32(4) != Version)
            throw new UnsupportedLayoutException("Only IL2CPP metadata v31 is supported.");
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(_data));
        (_stringsOffset, _stringsSize) = Table(0x18);
        (_typesOffset, _typesSize) = Table(0xA0);
        var (_, fieldsSize) = Table(0x60);
        if (_typesSize % TypeDefinitionSize != 0 || fieldsSize % FieldDefinitionSize != 0)
            throw new UnsupportedLayoutException("Unexpected metadata record sizes.");
    }

    private uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset));

    private (int Offset, int Size) Table(int headerOffset)
    {
        int offset = (int)U32(headerOffset), size = (int)U32(headerOffset + 4);
        if (offset < 0 || size < 0 || (long)offset + size > _data.Length)
            throw new UnsupportedLayoutException($"Metadata table at header 0x{headerOffset:X} is out of bounds.");
        return (offset, size);
    }

    private string String(int index)
    {
        if (index < 0 || index >= _stringsSize)
            throw new UnsupportedLayoutException("Metadata string index out of bounds.");
        var span = _data.AsSpan(_stringsOffset + index, _stringsSize - index);
        int end = span.IndexOf((byte)0);
        if (end < 0) throw new UnsupportedLayoutException("Unterminated metadata string.");
        return Encoding.UTF8.GetString(span[..end]);
    }

    /// <summary>"Namespace.Name" of a type definition (nested types have an empty namespace).</summary>
    public string QualifiedName(int typeIndex)
    {
        if (typeIndex < 0 || typeIndex >= TypeCount) throw new ArgumentOutOfRangeException(nameof(typeIndex));
        int record = _typesOffset + typeIndex * TypeDefinitionSize;
        string name = String((int)U32(record)), ns = String((int)U32(record + 4));
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }

    /// <summary>Type-definition index for a qualified name, or -1. The first definition wins for duplicates.</summary>
    public int FindType(string qualifiedName)
    {
        if (_typeIndex is null)
        {
            var map = new Dictionary<string, int>(TypeCount, StringComparer.Ordinal);
            for (int i = 0; i < TypeCount; i++)
                map.TryAdd(QualifiedName(i), i);
            _typeIndex = map;
        }
        return _typeIndex.GetValueOrDefault(qualifiedName, -1);
    }
}
