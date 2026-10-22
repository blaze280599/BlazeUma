using BlazeUma.Memory.Game;
using BlazeUma.Memory.Il2Cpp;

namespace BlazeUma.Cli;

/// <summary>
/// <c>BlazeUma fields Gallop.Singleton [field...]</c>: prints the instance fields (with inherited ones) of a
/// singleton, or of the object reached through the given pointer fields: bools, ints and pointers with their class.
/// For finding the field that tells a screen state apart. Reads memory only; nothing is called in the game.
/// </summary>
internal static class FieldsCommand
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: BlazeUma fields Gallop.Singleton [pointerField...]");
            return 2;
        }
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var reader = GameReader.Open(null);
        var r = reader.ManagedReader;
        ulong obj = r.SingletonInstance(args[0]);
        foreach (string name in args.Skip(1))
            if (obj != 0) obj = r.Ptr(obj, name);
        if (obj == 0)
        {
            Console.WriteLine("null");
            return 1;
        }
        Console.WriteLine($"{r.ClassName(obj)} @ 0x{obj:X}");
        for (ulong klass = r.ClassOf(obj); klass != 0; klass = reader.Runtime.Parent(klass))
            foreach (var f in reader.Runtime.DeclaredFields(klass).Where(f => !f.IsStatic && !f.IsLiteral))
            {
                string value = f.Kind switch
                {
                    Il2CppTypeKind.Boolean => r.Bool(obj, f.Name).ToString(),
                    Il2CppTypeKind.Int32 => r.Int(obj, f.Name).ToString(),
                    Il2CppTypeKind.Class or Il2CppTypeKind.GenericInstance or Il2CppTypeKind.SzArray or Il2CppTypeKind.String =>
                        r.Ptr(obj, f.Name) is var p && p != 0 ? $"0x{p:X} {r.ClassName(p)}" : "null",
                    _ => null!,
                };
                if (value is not null) Console.WriteLine($"  {f.Name} = {value}");
            }
        return 0;
    }
}
