using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace ValeLoot;

/// <summary>
/// Name-bound il2cpp metadata lookups, built on the raw exports Il2CppInterop re-exposes.
///
/// Everything resolves BY NAME at runtime — deliberately no reference to the generated
/// BepInEx/interop proxies. RVAs shuffle every game patch (see the cross-build fingerprint drift
/// notes in the site repo); names survive. The cost is a little pointer walking here.
/// </summary>
internal static class Il2CppMeta
{
    /// <summary>A resolved method: enough to census it, hook it, and read its arguments.</summary>
    internal sealed record MethodInfo(
        IntPtr Class,
        IntPtr Method,
        IntPtr NativePtr,
        string Name,
        int ParamCount,
        string[] ParamTypeNames);

    /// <summary>
    /// Find a class by trying (assembly, namespace) candidates in order. Which assembly a type lives
    /// in is not something to assert from a dump: the game's own code is split across
    /// Assembly-CSharp.dll and Assembly-CSharp-firstpass.dll, and engine types such as TMP_Text ship
    /// as either Unity.TextMeshPro.dll or TextMeshPro.dll depending on how the package was imported.
    /// Trying a short list costs nothing and survives a reshuffle.
    /// </summary>
    public static IntPtr FindClass(string ns, string name, params string[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            IntPtr klass = IL2CPP.GetIl2CppClass(assembly, ns, name);
            if (klass != IntPtr.Zero) return klass;
        }
        return IntPtr.Zero;
    }

    /// <summary>Every method on a class (not inherited), with parameter type names for matching.</summary>
    public static List<MethodInfo> Methods(IntPtr klass)
    {
        var result = new List<MethodInfo>();
        if (klass == IntPtr.Zero) return result;
        IntPtr iter = IntPtr.Zero;
        IntPtr method;
        while ((method = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
        {
            string name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_method_get_name(method)) ?? "";
            uint count = IL2CPP.il2cpp_method_get_param_count(method);
            var types = new string[count];
            for (uint i = 0; i < count; i++)
            {
                IntPtr type = IL2CPP.il2cpp_method_get_param(method, i);
                IntPtr typeName = type == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_type_get_name(type);
                types[i] = typeName == IntPtr.Zero ? "?" : Marshal.PtrToStringAnsi(typeName) ?? "?";
            }
            // methodPointer is the first field of Il2CppMethod (the native code address). Reading it
            // through the struct layout used by il2cpp across 2019+ has been stable: *(void**)method.
            IntPtr native = Marshal.ReadIntPtr(method);
            result.Add(new MethodInfo(klass, method, native, name, (int)count, types));
        }
        return result;
    }

    /// <summary>First method matching a name and a predicate on its parameter type names.</summary>
    public static MethodInfo? FindMethod(IntPtr klass, string name, Func<MethodInfo, bool>? where = null)
    {
        foreach (var m in Methods(klass))
            if (m.Name == name && (where is null || where(m)))
                return m;
        return null;
    }

    /// <summary>
    /// Byte offset of an instance field declared anywhere in the hierarchy, or -1.
    ///
    /// Walking parents is not optional here: the field the paint pass needs (`InventoryItemsUID`) is
    /// declared on the generic base `UIInventoryTab&lt;EquipData&gt;` while the live object's class is
    /// the concrete tab, and `il2cpp_class_get_field_from_name` only sees one class at a time.
    /// </summary>
    public static int FieldOffsetUp(IntPtr klass, string fieldName)
    {
        for (IntPtr current = klass; current != IntPtr.Zero; current = IL2CPP.il2cpp_class_get_parent(current))
        {
            int offset = FieldOffset(current, fieldName);
            if (offset >= 0) return offset;
        }
        return -1;
    }

    /// <summary>
    /// Resolve a method the way the RUNTIME does: by name and parameter count, through the hierarchy.
    ///
    /// Walking the hierarchy and iterating DECLARED methods — the obvious approach, and the one
    /// `FieldOffsetUp` takes for fields — is enough for ordinary classes and not enough for an
    /// inflated generic base: `UIInventoryTab_Equips` inherits everything worth hooking from
    /// `UIInventoryTab&lt;EquipData&gt;`, and iterating that parent's methods before the runtime has
    /// initialized it yields nothing — indistinguishable, from the outside, from a game patch having
    /// removed the method. `il2cpp_class_get_method_from_name` initializes the class and searches the
    /// hierarchy, so it answers for the generic case too.
    /// </summary>
    public static MethodInfo? FindMethodRuntime(IntPtr klass, string name, int paramCount)
    {
        if (klass == IntPtr.Zero) return null;
        IntPtr method = IL2CPP.il2cpp_class_get_method_from_name(klass, name, paramCount);
        if (method == IntPtr.Zero) return null;
        IntPtr native = Marshal.ReadIntPtr(method);
        var types = new string[paramCount];
        for (uint i = 0; i < paramCount; i++)
        {
            IntPtr type = IL2CPP.il2cpp_method_get_param(method, i);
            IntPtr typeName = type == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_type_get_name(type);
            types[i] = typeName == IntPtr.Zero ? "?" : Marshal.PtrToStringAnsi(typeName) ?? "?";
        }
        return new MethodInfo(klass, method, native, name, paramCount, types);
    }

    /**
     * Resolve an overload by its parameter TYPE NAMES, not just its arity.
     *
     * `FindMethodRuntime` matches name + parameter count, which is how this project crashed a game
     * process: `Component.GetComponentsInChildren` has a `(System.Type, System.Boolean)` overload AND a
     * generic `(System.Boolean, List&lt;T&gt;)` one, both with two parameters. The runtime handed back the
     * wrong one, the call passed a `true` where an object reference was expected, and the callee
     * dereferenced it — `AccessViolationException` inside the hook, taking the game down with it.
     *
     * Any engine call with overloads MUST come through here. Arity is not a signature.
     */
    public static MethodInfo? FindOverload(IntPtr klass, string name, params string[] paramTypeNames)
    {
        for (IntPtr current = klass; current != IntPtr.Zero; current = IL2CPP.il2cpp_class_get_parent(current))
        {
            foreach (MethodInfo m in Methods(current))
            {
                if (m.Name != name || m.ParamCount != paramTypeNames.Length) continue;
                bool match = true;
                for (int i = 0; i < paramTypeNames.Length; i++)
                {
                    if (m.ParamTypeNames[i] != paramTypeNames[i]) { match = false; break; }
                }
                if (match) return m;
            }
        }
        return null;
    }

    /**
     * Offset of a PROPERTY's backing field, by the property's name.
     *
     * C# compiles `public string UID { get; set; }` to a field called `<UID>k__BackingField`, so a lookup
     * for "UID" finds nothing and reports -1 — which reads exactly like "the game renamed it". Every
     * `{ get; set; }` on the game's data classes is in this shape (the dump prints them as
     * `declared: <UID>k__BackingField`), so this is the form to reach for on game data, not the plain one.
     */
    public static int PropertyFieldOffset(IntPtr klass, string propertyName)
    {
        int direct = FieldOffsetUp(klass, propertyName);
        return direct >= 0 ? direct : FieldOffsetUp(klass, $"<{propertyName}>k__BackingField");
    }

    /// <summary>Byte offset of an instance field, or -1. Offsets include the object header.</summary>
    public static int FieldOffset(IntPtr klass, string fieldName)
    {
        if (klass == IntPtr.Zero) return -1;
        IntPtr field = IL2CPP.il2cpp_class_get_field_from_name(klass, fieldName);
        if (field == IntPtr.Zero) return -1;
        return (int)IL2CPP.il2cpp_field_get_offset(field);
    }

    /**
     * Every member of an enum, as (name, value), read from the live metadata.
     *
     * Never hardcode an enum's order. `StatType` decides what "Agi" means in a filter line, and its
     * ordinal order MOVES between game builds — a hardcoded table would silently start filtering on a
     * different stat after a patch, which is the worst class of bug this project can ship: everything
     * still works, and the answers are wrong.
     *
     * Enum members are STATIC LITERAL fields; the enum's own storage field (`value__`) is an instance
     * field, so filtering on the static flag is what separates the two. 0x10 is FIELD_ATTRIBUTE_STATIC.
     */
    public static List<(string Name, int Value)> EnumValues(IntPtr klass)
    {
        var result = new List<(string, int)>();
        if (klass == IntPtr.Zero) return result;
        IntPtr iter = IntPtr.Zero;
        IntPtr field;
        while ((field = IL2CPP.il2cpp_class_get_fields(klass, ref iter)) != IntPtr.Zero)
        {
            if ((IL2CPP.il2cpp_field_get_flags(field) & 0x10) == 0) continue;
            string name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_field_get_name(field)) ?? "";
            if (name.Length == 0) continue;
            int value = 0;
            unsafe { IL2CPP.il2cpp_field_static_get_value(field, &value); }
            result.Add((name, value));
        }
        return result;
    }

    /**
     * The object held by a STATIC reference field, or Zero.
     *
     * Reached through `il2cpp_field_static_get_value` rather than by adding the field's offset to the
     * class's static storage block by hand. The storage block does live at a fixed spot in
     * `Il2CppClass`, and reaching through it works — right up until the runtime this mod is loaded
     * into is built differently, at which point the read is a plausible-looking pointer to something
     * else. The export cannot be wrong about a layout it owns.
     *
     * The class is initialized first. Static storage is allocated by the class initializer, and this
     * is used on `App`, whose statics a plugin can easily reach before the game has touched them —
     * asking for the value of a field whose storage does not exist yet is a null dereference inside
     * the runtime rather than the Zero this returns.
     */
    public static IntPtr StaticObjectField(IntPtr klass, string fieldName)
    {
        if (klass == IntPtr.Zero) return IntPtr.Zero;
        IntPtr field = IL2CPP.il2cpp_class_get_field_from_name(klass, fieldName);
        if (field == IntPtr.Zero) return IntPtr.Zero;
        IL2CPP.il2cpp_runtime_class_init(klass);
        IntPtr value = IntPtr.Zero;
        unsafe { IL2CPP.il2cpp_field_static_get_value(field, &value); }
        return value;
    }

    /// <summary>The runtime class of a live il2cpp object (its first field), or Zero.</summary>
    public static IntPtr ClassOf(IntPtr obj) => obj == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(obj);

    /// <summary>A class's simple name — how the paint pass tells a wardrobe tab from a bag tab, and
    /// how a probe reply names the components it found on a cell.</summary>
    public static string ClassName(IntPtr klass)
    {
        if (klass == IntPtr.Zero) return "";
        IntPtr name = IL2CPP.il2cpp_class_get_name(klass);
        return name == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(name) ?? "";
    }

    /// <summary>
    /// Read a System.String out of il2cpp. Layout on 64-bit: klass(8) monitor(8) length(4) then
    /// UTF-16 chars at 0x14. Length is sanity-capped — this reads objects handed to us by native
    /// code, and a garbage pointer must return null rather than march through address space.
    /// </summary>
    public static string? ReadString(IntPtr str)
    {
        if (str == IntPtr.Zero) return null;
        int length = Marshal.ReadInt32(str, 0x10);
        if (length < 0 || length > 0x100000) return null;
        return length == 0 ? "" : Marshal.PtrToStringUni(str + 0x14, length);
    }

    /// <summary>Read a string-typed instance field by offset (-1 offset yields null).</summary>
    public static string? ReadStringField(IntPtr obj, int offset)
        => obj == IntPtr.Zero || offset < 0 ? null : ReadString(Marshal.ReadIntPtr(obj, offset));

    /// <summary>Element count of an il2cpp array (max_length at 0x18), or -1.</summary>
    public static int ArrayLength(IntPtr array)
    {
        if (array == IntPtr.Zero) return -1;
        long max = Marshal.ReadInt64(array, 0x18);
        return max < 0 || max > int.MaxValue ? -1 : (int)max;
    }

    /// <summary>
    /// Walk a <c>Dictionary&lt;string, T&gt;</c> and yield its live (key, value) pointers.
    ///
    /// EVERY offset here is resolved by name at runtime, including the Entry stride, because the
    /// IL2CPP dump reports 0x0 for every field of a generic type — layout exists only per
    /// instantiation. Hardcoding the textbook layout (hashCode 0, next 4, key 8, value 16, stride
    /// 24) would be an assumption about generic sharing, and this file has already paid once for
    /// assuming instead of reading.
    ///
    /// Returns an EMPTY list rather than guessing if any field fails to resolve: a silently
    /// mis-strided walk would emit plausible-looking garbage into the player's loot ledger, which
    /// is worse than reporting nothing.
    /// </summary>
    public static List<(IntPtr Key, IntPtr Value)> DictionaryEntries(IntPtr dict)
    {
        var result = new List<(IntPtr, IntPtr)>();
        if (dict == IntPtr.Zero) return result;

        IntPtr dictClass = ClassOf(dict);
        int entriesOffset = FieldOffset(dictClass, "_entries");
        int countOffset = FieldOffset(dictClass, "_count");
        if (entriesOffset < 0 || countOffset < 0) return result;

        IntPtr entries = Marshal.ReadIntPtr(dict, entriesOffset);
        int count = Marshal.ReadInt32(dict, countOffset);
        int capacity = ArrayLength(entries);
        if (entries == IntPtr.Zero || count <= 0 || capacity < 0) return result;
        if (count > capacity) count = capacity;             // never read past the array

        // Entry is a STRUCT stored inline, so its field offsets include the boxed object header
        // that inline elements do not have — subtract it. Stride likewise comes from the element
        // class rather than sizeof(the fields we happen to know about).
        IntPtr entryClass = IL2CPP.il2cpp_class_get_element_class(ClassOf(entries));
        if (entryClass == IntPtr.Zero) return result;
        int keyOffset = FieldOffset(entryClass, "key") - 0x10;
        int valueOffset = FieldOffset(entryClass, "value") - 0x10;
        int nextOffset = FieldOffset(entryClass, "next") - 0x10;
        int stride = (int)IL2CPP.il2cpp_class_instance_size(entryClass) - 0x10;
        if (keyOffset < 0 || valueOffset < 0 || nextOffset < 0 || stride <= 0) return result;

        for (int i = 0; i < count; i++)
        {
            IntPtr entry = entries + 0x20 + (i * stride);
            // A removed slot keeps its place in the array: .NET marks it with next < -1 and clears
            // the reference key. Both are checked because either alone can be true transiently.
            if (Marshal.ReadInt32(entry, nextOffset) < -1) continue;
            IntPtr key = Marshal.ReadIntPtr(entry, keyOffset);
            if (key == IntPtr.Zero) continue;
            result.Add((key, Marshal.ReadIntPtr(entry, valueOffset)));
        }
        return result;
    }

    /// <summary>Walk a <c>List&lt;T&gt;</c> of reference types (`_items` backing array + `_size`).</summary>
    public static List<IntPtr> ListItems(IntPtr list)
    {
        var result = new List<IntPtr>();
        if (list == IntPtr.Zero) return result;

        IntPtr listClass = ClassOf(list);
        int itemsOffset = FieldOffset(listClass, "_items");
        int sizeOffset = FieldOffset(listClass, "_size");
        if (itemsOffset < 0 || sizeOffset < 0) return result;

        IntPtr items = Marshal.ReadIntPtr(list, itemsOffset);
        int size = Marshal.ReadInt32(list, sizeOffset);
        int capacity = ArrayLength(items);
        if (items == IntPtr.Zero || size <= 0 || capacity < 0) return result;
        if (size > capacity) size = capacity;

        for (int i = 0; i < size; i++)
        {
            IntPtr element = Marshal.ReadIntPtr(items + 0x20 + (i * IntPtr.Size));
            if (element != IntPtr.Zero) result.Add(element);
        }
        return result;
    }
}
