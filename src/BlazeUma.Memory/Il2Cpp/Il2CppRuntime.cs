namespace BlazeUma.Memory.Il2Cpp;

public enum Il2CppTypeKind : byte
{
    Boolean = 2, Byte = 5, Int32 = 8, UInt32 = 9, Int64 = 10, Single = 12, String = 14,
    ValueType = 17, Class = 18, GenericInstance = 21, SzArray = 29,
}

public sealed record Il2CppField(string Name, int Offset, Il2CppTypeKind Kind, bool IsStatic, bool IsLiteral, uint Token);

/// <summary>
/// Reads IL2CPP runtime structures of GameAssembly.dll without calling game code.
/// Every layout used here is checked against the accessor instructions that prove it (see AGENTS.md in the POC).
/// </summary>
public sealed class Il2CppRuntime
{
    // Il2CppClass offsets verified from exported accessors in this build.
    public const int ClassName = 0x10, ClassNamespace = 0x18, ClassParent = 0x58, ClassFields = 0x80,
        ClassMethods = 0x98, ClassStaticFields = 0xB8, ClassInstanceSize = 0xF8, ClassMethodCount = 0x120, ClassFieldCount = 0x124;
    private const int FieldInfoSize = 0x20;

    private readonly IMemory _memory;
    private readonly GlobalMetadata _metadata;
    private readonly Dictionary<string, ulong> _exports = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, IReadOnlyList<Il2CppField>> _fieldCache = [];
    private readonly Dictionary<string, ulong> _classCache = new(StringComparer.Ordinal);
    private readonly ulong _classCacheTable;

    public ulong ModuleBase { get; }
    public GlobalMetadata Metadata => _metadata;

    public Il2CppRuntime(IMemory memory, ulong moduleBase, GlobalMetadata metadata)
    {
        _memory = memory;
        _metadata = metadata;
        ModuleBase = moduleBase;
        ReadExports();
        VerifyAccessors();
        _classCacheTable = ResolveClassCacheTable();
    }

    private void ReadExports()
    {
        var m = _memory;
        if (m.ReadUInt16(ModuleBase) != 0x5A4D) throw new UnsupportedLayoutException("Missing DOS header.");
        ulong pe = ModuleBase + m.ReadUInt32(ModuleBase + 0x3C);
        if (m.ReadUInt32(pe) != 0x4550 || m.ReadUInt16(pe + 0x18) != 0x20B)
            throw new UnsupportedLayoutException("GameAssembly.dll is not PE32+.");
        uint sizeOfImage = m.ReadUInt32(pe + 0x50);
        uint dirRva = m.ReadUInt32(pe + 0x88);
        if (dirRva == 0 || dirRva >= sizeOfImage) throw new UnsupportedLayoutException("Invalid export directory.");
        ulong dir = ModuleBase + dirRva;
        uint count = m.ReadUInt32(dir + 0x18), functions = m.ReadUInt32(dir + 0x1C),
             names = m.ReadUInt32(dir + 0x20), ordinals = m.ReadUInt32(dir + 0x24);
        if (count is 0 or > 10000 || functions >= sizeOfImage || names >= sizeOfImage || ordinals >= sizeOfImage)
            throw new UnsupportedLayoutException("Invalid export table.");
        for (uint i = 0; i < count; i++)
        {
            uint nameRva = m.ReadUInt32(ModuleBase + names + i * 4);
            ushort ordinal = m.ReadUInt16(ModuleBase + ordinals + i * 2);
            uint fnRva = m.ReadUInt32(ModuleBase + functions + ordinal * 4u);
            if (nameRva >= sizeOfImage || fnRva >= sizeOfImage) continue;
            _exports[m.ReadCString(ModuleBase + nameRva)] = ModuleBase + fnRva;
        }
    }

    private ulong Export(string name) =>
        _exports.TryGetValue(name, out var address) ? address : throw new UnsupportedLayoutException($"Missing export {name}.");

    private ulong Require(string export, string hexBytes, int offset = 0)
    {
        ulong address = Export(export);
        var expected = Convert.FromHexString(hexBytes.Replace(" ", ""));
        if (!_memory.ReadBytes(address + (ulong)offset, expected.Length).AsSpan().SequenceEqual(expected))
            throw new UnsupportedLayoutException($"Unsupported game layout: {export}");
        return address;
    }

    private void VerifyAccessors()
    {
        Require("il2cpp_class_get_name", "48 8b 41 10 c3");
        Require("il2cpp_class_get_namespace", "48 8b 41 18 c3");
        Require("il2cpp_field_get_offset", "48 63 41 18 c3");
        Require("il2cpp_class_instance_size", "8b 81 f8 00 00 00 c3");
        Require("il2cpp_class_get_static_field_data", "48 8b 81 b8 00 00 00 c3");
        Require("il2cpp_class_get_fields", "66 83 bb 24 01 00 00 00", 0x20);
        Require("il2cpp_class_get_fields", "48 8b 83 80 00 00 00", 0x2A);
    }

    private ulong Rip(ulong instruction, int displacementOffset = 3, int length = 7) =>
        (ulong)((long)instruction + length + _memory.ReadInt32(instruction + (ulong)displacementOffset));

    /// <summary>image_get_class -> type-definition resolver -> class-cache resolver -> global cache pointer.</summary>
    private ulong ResolveClassCacheTable()
    {
        ulong getClass = Require("il2cpp_image_get_class", "48 8b 41 28 44 8b 00 44 03 c2");
        if (_memory.ReadByte(getClass + 0x36) != 0xE9) throw new UnsupportedLayoutException("Unexpected type-definition resolver branch.");
        ulong resolver = Rip(getClass + 0x36, 1, 5);
        if (_memory.ReadByte(resolver + 0x2A) != 0xE9) throw new UnsupportedLayoutException("Unexpected class-cache resolver branch.");
        ulong cacheFn = Rip(resolver + 0x2A, 1, 5);
        var load = _memory.ReadBytes(cacheFn + 0x20, 3);
        if (load is not [0x48, 0x8B, 0x05]) throw new UnsupportedLayoutException("Unexpected class-cache load.");
        ulong table = _memory.ReadPointer(Rip(cacheFn + 0x20));
        if (!MemoryExtensions.IsPlausiblePointer(table)) throw new UnsupportedLayoutException("Class cache is not initialized.");
        return table;
    }

    /// <summary>
    /// Already-initialized Il2CppClass* for a qualified type name, or 0 if the game has not created it yet.
    /// Never asks the game to initialize a class.
    /// </summary>
    public ulong GetClass(string qualifiedName)
    {
        if (_classCache.TryGetValue(qualifiedName, out var cached)) return cached;
        int index = _metadata.FindType(qualifiedName);
        if (index < 0) throw new UnsupportedLayoutException($"Type not in metadata: {qualifiedName}");
        ulong klass = _memory.ReadPointer(_classCacheTable + (ulong)index * 8);
        if (klass == 0) return 0;
        if (QualifiedClassName(klass) != qualifiedName)
            throw new UnsupportedLayoutException($"Runtime class does not match metadata for {qualifiedName}.");
        return _classCache[qualifiedName] = klass;
    }

    public string QualifiedClassName(ulong klass)
    {
        string name = _memory.ReadCString(_memory.ReadPointer(klass + ClassName));
        string ns = _memory.ReadCString(_memory.ReadPointer(klass + ClassNamespace));
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }

    /// <summary>Class name of a managed object, or null if the header does not look like an object.</summary>
    public string? ObjectClassName(ulong obj)
    {
        if (!MemoryExtensions.IsPlausiblePointer(obj)) return null;
        ulong klass = _memory.ReadPointer(obj);
        return MemoryExtensions.IsPlausiblePointer(klass) ? QualifiedClassName(klass) : null;
    }

    public ulong Parent(ulong klass) => _memory.ReadPointer(klass + ClassParent);
    public ulong StaticFieldData(ulong klass) => _memory.ReadPointer(klass + ClassStaticFields);
    public uint InstanceSize(ulong klass) => _memory.ReadUInt32(klass + ClassInstanceSize);

    /// <summary>Declared fields of exactly this class (not inherited).</summary>
    public IReadOnlyList<Il2CppField> DeclaredFields(ulong klass)
    {
        if (_fieldCache.TryGetValue(klass, out var cached)) return cached;
        ushort count = _memory.ReadUInt16(klass + ClassFieldCount);
        if (count > 4096) throw new UnsupportedLayoutException("Implausible field count.");
        ulong table = _memory.ReadPointer(klass + ClassFields);
        var fields = new List<Il2CppField>(count);
        for (int i = 0; table != 0 && i < count; i++)
        {
            ulong field = table + (ulong)(i * FieldInfoSize);
            ulong type = _memory.ReadPointer(field + 8);
            uint bits = _memory.ReadUInt32(type + 8);
            fields.Add(new Il2CppField(
                _memory.ReadCString(_memory.ReadPointer(field)),
                _memory.ReadInt32(field + 0x18),
                (Il2CppTypeKind)((bits >> 16) & 0xFF),
                (bits & 0x10) != 0,
                (bits & 0x40) != 0,
                _memory.ReadUInt32(field + 0x1C)));
        }
        return _fieldCache[klass] = fields;
    }

    /// <summary>
    /// Finds a field by name on the class or its parents; also matches auto-property backing fields
    /// ("Name" finds "&lt;Name&gt;k__BackingField"). Returns the class that declares it.
    /// </summary>
    public (ulong DeclaringClass, Il2CppField Field)? FindField(ulong klass, string name)
    {
        string backing = $"<{name}>k__BackingField";
        for (ulong k = klass; k != 0; k = Parent(k))
        {
            foreach (var f in DeclaredFields(k))
                if (!f.IsLiteral && (f.Name == name || f.Name == backing))
                    return (k, f);
        }
        return null;
    }

    public (ulong DeclaringClass, Il2CppField Field) RequireField(ulong klass, string name) =>
        FindField(klass, name) ?? throw new UnsupportedLayoutException($"Field {name} not found on {QualifiedClassName(klass)}.");

    /// <summary>Native code addresses of every declared overload with this name.</summary>
    public IEnumerable<ulong> MethodPointers(ulong klass, string methodName) =>
        MethodInfos(klass, methodName).Select(mi => _memory.ReadPointer(mi));

    /// <summary>MethodInfo pointers for every declared overload with this name (own class and inherited).</summary>
    public IEnumerable<ulong> MethodInfos(ulong klass, string methodName)
    {
        for (ulong k = klass; k != 0; k = Parent(k))
        {
            ulong methods = _memory.ReadPointer(k + ClassMethods);
            ushort count = _memory.ReadUInt16(k + ClassMethodCount);
            if (methods == 0 || count is 0 or > 4096) continue;
            for (int i = 0; i < count; i++)
            {
                ulong method = _memory.ReadPointer(methods + (ulong)(i * 8));
                if (_memory.ReadCString(_memory.ReadPointer(method + 0x18)) == methodName)
                    yield return method;
            }
        }
    }

    /// <summary>Single MethodInfo and function pointer for a method known to be present.</summary>
    public (ulong MethodInfo, ulong Function) RequireMethod(ulong klass, string methodName)
    {
        ulong mi = MethodInfos(klass, methodName).FirstOrDefault();
        if (mi == 0) throw new UnsupportedLayoutException($"Method {methodName} not found on {QualifiedClassName(klass)}.");
        return (mi, _memory.ReadPointer(mi));
    }
}
