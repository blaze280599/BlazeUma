using BlazeUma.Memory.Il2Cpp;

namespace BlazeUma.Memory.Game;

/// <summary>A UI button found on the current screen, with its field path and tappable state.</summary>
public sealed record FoundButton(ulong Address, string Path, string ClassName, bool IsEnable, bool IsInteractable)
{
    public bool CanTap => IsEnable && IsInteractable;
}

/// <summary>
/// Finds every <c>Gallop.ButtonCommon</c>-derived instance reachable through reference fields from the
/// current scene's view and view controller. The field path (e.g. <c>SingleModeMainView._blackMaskButton</c>)
/// serves as the button's name for the advisor to target later.
/// </summary>
public sealed class ButtonFinder(ManagedReader reader)
{
    public const string ButtonClass = "Gallop.ButtonCommon";
    public const int DefaultMaxDepth = 5;

    /// <summary>
    /// Buttons reachable from <c>SceneManager._currentViewBase</c>, its view controller, and every open
    /// dialog in <c>DialogManager._dialogList</c> (which is where Close/OK buttons on popups live).
    /// </summary>
    public IReadOnlyList<FoundButton> FromCurrentScene(int maxDepth = DefaultMaxDepth)
    {
        ulong buttonClass = reader.Runtime.GetClass(ButtonClass);
        if (buttonClass == 0) return [];

        var found = new List<FoundButton>();
        var visited = new HashSet<ulong>();

        ulong scene = reader.SingletonInstance("Gallop.SceneManager");
        if (scene != 0)
        {
            Walk(reader.Ptr(scene, "_currentViewBase"), "View", 0, maxDepth, buttonClass, found, visited);
            Walk(reader.Ptr(scene, "_currentViewController"), "Controller", 0, maxDepth, buttonClass, found, visited);
        }

        ulong dialogManager = reader.SingletonInstance("Gallop.DialogManager");
        if (dialogManager != 0)
        {
            var dialogs = reader.ListItems(reader.Ptr(dialogManager, "_dialogList"));
            for (int i = 0; i < dialogs.Count; i++)
                Walk(dialogs[i], $"Dialog[{i}]", 0, maxDepth, buttonClass, found, visited);
        }

        return found;
    }

    private const int MaxParentChain = 10;

    private void Walk(ulong obj, string path, int depth, int maxDepth, ulong buttonClass,
        List<FoundButton> found, HashSet<ulong> visited)
    {
        if (depth > maxDepth || obj == 0 || !MemoryExtensions.IsPlausiblePointer(obj) || !visited.Add(obj)) return;

        ulong klass;
        try { klass = reader.ClassOf(obj); }
        catch (MemoryReadException) { return; }
        if (!MemoryExtensions.IsPlausiblePointer(klass)) return;

        // Walk the inheritance chain once, defensively (a garbage object may yield a plausible-looking
        // class pointer whose Parent chain is nonsense).
        var ancestors = new List<ulong>(MaxParentChain);
        try
        {
            for (ulong k = klass; k != 0 && ancestors.Count < MaxParentChain; k = reader.Runtime.Parent(k))
            {
                if (!MemoryExtensions.IsPlausiblePointer(k)) break;
                ancestors.Add(k);
            }
        }
        catch (MemoryReadException) { return; }
        if (ancestors.Count == 0) return;

        // Stop at buttons themselves — their internals are click targets, not more buttons.
        if (ancestors.Contains(buttonClass))
        {
            try
            {
                string name = reader.Runtime.QualifiedClassName(klass);
                found.Add(new FoundButton(obj, Clean(path), name,
                    IsEnable: reader.Bool(obj, "_isEnable"),
                    IsInteractable: reader.Bool(obj, "m_Interactable")));
            }
            catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException) { }
            return;
        }

        // Recurse into reference / collection fields (own class and inherited). Skip Unity natives.
        foreach (ulong k in ancestors)
        {
            string ownerName = SafeName(k);
            if (ownerName is "System.Object" or "UnityEngine.Object" or "UnityEngine.Component" or "UnityEngine.MonoBehaviour") break;
            IReadOnlyList<Il2CppField> fields;
            try { fields = reader.Runtime.DeclaredFields(k); }
            catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException) { continue; }
            foreach (var field in fields)
            {
                if (field.IsStatic || field.IsLiteral) continue;
                if (field.Kind is not (Il2CppTypeKind.Class or Il2CppTypeKind.GenericInstance or Il2CppTypeKind.SzArray)) continue;
                ulong child;
                try { child = reader.Memory.ReadPointer(obj + (ulong)field.Offset); }
                catch (MemoryReadException) { continue; }
                if (child == 0 || !MemoryExtensions.IsPlausiblePointer(child)) continue;
                string childPath = $"{path}.{field.Name}";

                // Transparently walk List<T> of reference types and arrays of reference types so UI tiles
                // stored in them are reachable. Field.Kind tells us the container shape; we rely on
                // Walk's own plausibility checks to reject value-type payloads that leak through.
                if (field.Kind == Il2CppTypeKind.GenericInstance)
                {
                    ulong childClassPtr;
                    try { childClassPtr = reader.ClassOf(child); } catch (MemoryReadException) { continue; }
                    if (SafeName(childClassPtr).StartsWith("System.Collections.Generic.List"))
                    {
                        TryWalkContainer(() => reader.ListItems(child), childPath, depth, maxDepth, buttonClass, found, visited);
                        continue;
                    }
                }
                else if (field.Kind == Il2CppTypeKind.SzArray)
                {
                    TryWalkContainer(() => reader.ArrayItems(child), childPath, depth, maxDepth, buttonClass, found, visited);
                    continue;
                }
                Walk(child, childPath, depth + 1, maxDepth, buttonClass, found, visited);
            }
        }
    }

    private string SafeName(ulong klass)
    {
        try { return reader.Runtime.QualifiedClassName(klass); }
        catch { return ""; }
    }

    private void TryWalkContainer(Func<IReadOnlyList<ulong>> enumerate, string path,
        int depth, int maxDepth, ulong buttonClass, List<FoundButton> found, HashSet<ulong> visited)
    {
        IReadOnlyList<ulong> items;
        try { items = enumerate(); }
        catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException) { return; }
        for (int i = 0; i < items.Count; i++)
            Walk(items[i], $"{path}[{i}]", depth + 1, maxDepth, buttonClass, found, visited);
    }

    // Strip the auto-property angle brackets from "<Foo>k__BackingField" for readability.
    private static string Clean(string path) => path.Replace("<", "").Replace(">k__BackingField", "");
}
