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

    private void Walk(ulong obj, string path, int depth, int maxDepth, ulong buttonClass,
        List<FoundButton> found, HashSet<ulong> visited)
    {
        if (depth > maxDepth || obj == 0 || !MemoryExtensions.IsPlausiblePointer(obj) || !visited.Add(obj)) return;

        ulong klass;
        try { klass = reader.ClassOf(obj); }
        catch (MemoryReadException) { return; }
        if (!MemoryExtensions.IsPlausiblePointer(klass)) return;

        // Stop at buttons themselves — their internals are click targets, not more buttons.
        for (ulong k = klass; k != 0; k = reader.Runtime.Parent(k))
        {
            if (k != buttonClass) continue;
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

        // Recurse into reference fields (own class and inherited). Skip Unity natives and system containers.
        for (ulong k = klass; k != 0; k = reader.Runtime.Parent(k))
        {
            string ownerName = SafeName(k);
            if (ownerName is "System.Object" or "UnityEngine.Object" or "UnityEngine.Component" or "UnityEngine.MonoBehaviour") break;
            IReadOnlyList<Il2CppField> fields;
            try { fields = reader.Runtime.DeclaredFields(k); } catch (UnsupportedLayoutException) { continue; }
            foreach (var field in fields)
            {
                if (field.IsStatic || field.IsLiteral) continue;
                if (field.Kind is not (Il2CppTypeKind.Class or Il2CppTypeKind.GenericInstance)) continue;
                ulong child;
                try { child = reader.Memory.ReadPointer(obj + (ulong)field.Offset); }
                catch (MemoryReadException) { continue; }
                if (child == 0) continue;
                Walk(child, $"{path}.{field.Name}", depth + 1, maxDepth, buttonClass, found, visited);
            }
        }
    }

    private string SafeName(ulong klass)
    {
        try { return reader.Runtime.QualifiedClassName(klass); }
        catch { return ""; }
    }

    // Strip the auto-property angle brackets from "<Foo>k__BackingField" for readability.
    private static string Clean(string path) => path.Replace("<", "").Replace(">k__BackingField", "");
}
