using System;
using System.Runtime.InteropServices;
using System.Text;
using Il2CppInterop.Runtime;

namespace ValeLoot;

/// <summary>
/// Saying WHY an item is highlighted, in the panel the player is already looking at.
///
/// A coloured cell says THAT a rule claimed an item. With seven rules live it cannot say which one, and
/// that is the next question every time — the difference between "this is lit" and "this is lit because it
/// beats what I wear".
///
/// ## Why not `UIManager.DrawTooltip`
///
/// That was the first attempt and it was the wrong system. It resolved, hooked and fired, and the very
/// first hover reported its argument: `arg1 was String, uid via key string => "Inventory"`. It is the
/// generic keyed tooltip (a `RectTransform` plus a lookup key), used for UI chrome, and it never carries an
/// item. One log line, no restart, no guessing.
///
/// ## The item hover path
///
/// `HoverInfoHandler` is a `MonoBehaviour` implementing `IPointerEnterHandler`, carrying
/// `OnHoverEnter`/`OnHoverExit` (`Action&lt;PointerEventData&gt;`) that its OWNER assigns. Nothing calls
/// `Begin` in code — it is wired in the prefab — so the panel cannot be found by cross-reference. What CAN
/// be relied on is the object graph: the handler sits on the same GameObject as the `UIInventoryItem`, so
/// the hovered cell (and through `Data`, the item's uid) is one `GetComponent` away.
///
/// ## Not finding the panel at all
///
/// The obvious design — walk the canvas, find the info panel, append to it — was built, and it was wrong
/// twice in ways that each cost a relaunch: the name heuristic ("something called info/hover/tooltip")
/// picked a chat panel, and appending after the original meant the game repopulated the panel and erased
/// the line. Both disappeared when the write moved INSIDE the game's own write: `TMP_Text.set_text` is
/// hooked, and any text long enough to be an item tooltip gets the verdict line amended onto it. There is
/// no panel to identify, no object named after what it is, and no race to lose — we are inside the write
/// that would have overwritten us.
///
/// The verdict is latched on hover and consumed by the next qualifying write. Latching BEFORE calling the
/// original is load-bearing: the original populates the tooltip, so a latch taken afterwards describes the
/// PREVIOUS item, which reads as "the note is always one hover behind".
///
/// ## Idempotence
///
/// Panels are reused between hovers, and a hover can fire more than one enter. Each injected line starts
/// with a zero-width marker; if the text already carries it, nothing is written.
/// </summary>
internal static class TooltipInject
{
    private delegate void PointerFn(IntPtr self, IntPtr eventData, IntPtr methodInfo);
    private delegate IntPtr GetComponentFn(IntPtr self, IntPtr type, IntPtr methodInfo);
    private delegate IntPtr TransformFn(IntPtr self, IntPtr methodInfo);
    private delegate int CountFn(IntPtr self, IntPtr methodInfo);
    private delegate IntPtr GetChildFn(IntPtr self, int index, IntPtr methodInfo);
    private delegate IntPtr GetTextFn(IntPtr self, IntPtr methodInfo);
    private delegate void SetTextFn(IntPtr self, IntPtr value, IntPtr methodInfo);
    private delegate bool ActiveFn(IntPtr self, IntPtr methodInfo);

    /// <summary>A zero-width space: invisible, and not something the game's own text will contain.</summary>
    private const string Marker = "\u200b";

    private static object? _detour;
    private static PointerFn? _hook;
    private static PointerFn? _original;
    private static GetComponentFn? _getComponent;
    private static TransformFn? _getTransform;
    private static TransformFn? _getParent;
    private static TransformFn? _getGameObject;
    private static TransformFn? _getName;
    private static CountFn? _getChildCount;
    private static GetChildFn? _getChild;
    private static GetTextFn? _getText;
    private static SetTextFn? _setText;
    private static ActiveFn? _activeInHierarchy;
    private static IntPtr _textType;
    private static IntPtr _cellType;
    private static int _dataFieldOffset = -1;
    private static int _uidFieldOffset = -1;
    private static Action<string>? _log;


    /**
     * The verdict line for the item currently under the pointer, or null.
     *
     * Volatile because it is written on the UI thread and read on the same one, but through a different hook
     * — and because the last field of this kind that was not volatile cost a round of "the probe is broken".
     */
    private static volatile string? _pending;
    /// <summary>Guards re-entry: appending calls `set_text`, which arrives straight back in the hook.</summary>
    private static bool _writing;
    private static SetTextFn? _setTextOriginal;
    private static SetTextFn? _setTextHook;
    private static object? _setTextDetour;

    /**
     * Shortest text worth treating as an item tooltip.
     *
     * Measured, not guessed: the probe read the real tooltip at 1288 chars and the next longest UI text
     * (chat) at 189. A floor between them separates them without naming any object, which matters because
     * nothing in that hierarchy is named after what it is.
     */
    private const int MinTooltipChars = 400;

    public static bool Installed { get; private set; }
    /// <summary>Config switch: the note can be turned off without losing the cell colours.</summary>
    public static bool Enabled = true;
    public static long Hovers;
    public static long Injected;
    public static long Misses;
    public static long Errors;

    public static bool Install(Action<string> log)
    {
        _log = log;

        IntPtr handler = Il2CppMeta.FindClass("", "HoverInfoHandler", HookCensus.GameAssemblies);
        IntPtr cell = Il2CppMeta.FindClass("", "UIInventoryItem", HookCensus.GameAssemblies);
        IntPtr refinable = Il2CppMeta.FindClass("", "RefinableItemData", HookCensus.GameAssemblies);
        IntPtr text = Il2CppMeta.FindClass("TMPro", "TMP_Text", "Unity.TextMeshPro.dll", "TextMeshPro.dll");
        IntPtr component = Il2CppMeta.FindClass("UnityEngine", "Component", "UnityEngine.CoreModule.dll");
        IntPtr transform = Il2CppMeta.FindClass("UnityEngine", "Transform", "UnityEngine.CoreModule.dll");
        IntPtr unityObject = Il2CppMeta.FindClass("UnityEngine", "Object", "UnityEngine.CoreModule.dll");
        IntPtr gameObject = Il2CppMeta.FindClass("UnityEngine", "GameObject", "UnityEngine.CoreModule.dll");

        Il2CppMeta.MethodInfo? onEnter = Il2CppMeta.FindMethodRuntime(handler, "OnPointerEnter", 1);
        if (onEnter is null || onEnter.NativePtr == IntPtr.Zero)
        {
            log("tooltip inject NOT ready: HoverInfoHandler.OnPointerEnter did not resolve");
            return false;
        }

        // Exact signatures throughout: `GetComponent`, `get_text` and `SetActive` are all overloaded, and
        // trusting arity on an overloaded engine method is what crashed the paint path.
        Il2CppMeta.MethodInfo? getComponent = Il2CppMeta.FindOverload(component, "GetComponent", "System.Type");
        Il2CppMeta.MethodInfo? getTransform = Il2CppMeta.FindOverload(component, "get_transform");
        Il2CppMeta.MethodInfo? getParent = Il2CppMeta.FindOverload(transform, "get_parent");
        Il2CppMeta.MethodInfo? getGameObject = Il2CppMeta.FindOverload(component, "get_gameObject");
        Il2CppMeta.MethodInfo? getName = Il2CppMeta.FindOverload(unityObject, "get_name");
        Il2CppMeta.MethodInfo? childCount = Il2CppMeta.FindOverload(transform, "get_childCount");
        Il2CppMeta.MethodInfo? getChild = Il2CppMeta.FindOverload(transform, "GetChild", "System.Int32");
        Il2CppMeta.MethodInfo? getText = Il2CppMeta.FindOverload(text, "get_text");
        Il2CppMeta.MethodInfo? setText = Il2CppMeta.FindOverload(text, "set_text", "System.String");
        Il2CppMeta.MethodInfo? active = Il2CppMeta.FindOverload(gameObject, "get_activeInHierarchy");

        if (getComponent is null || getTransform is null || getParent is null || getGameObject is null
            || getName is null || childCount is null || getChild is null || getText is null || setText is null
            || active is null || text == IntPtr.Zero || cell == IntPtr.Zero)
        {
            log("tooltip inject NOT ready: TMP_Text / component walk unresolved");
            return false;
        }

        _getComponent = Marshal.GetDelegateForFunctionPointer<GetComponentFn>(getComponent.NativePtr);
        _getTransform = Marshal.GetDelegateForFunctionPointer<TransformFn>(getTransform.NativePtr);
        _getParent = Marshal.GetDelegateForFunctionPointer<TransformFn>(getParent.NativePtr);
        _getGameObject = Marshal.GetDelegateForFunctionPointer<TransformFn>(getGameObject.NativePtr);
        _getName = Marshal.GetDelegateForFunctionPointer<TransformFn>(getName.NativePtr);
        _getChildCount = Marshal.GetDelegateForFunctionPointer<CountFn>(childCount.NativePtr);
        _getChild = Marshal.GetDelegateForFunctionPointer<GetChildFn>(getChild.NativePtr);
        _getText = Marshal.GetDelegateForFunctionPointer<GetTextFn>(getText.NativePtr);
        _setText = Marshal.GetDelegateForFunctionPointer<SetTextFn>(setText.NativePtr);
        _activeInHierarchy = Marshal.GetDelegateForFunctionPointer<ActiveFn>(active.NativePtr);
        _textType = IL2CPP.il2cpp_type_get_object(IL2CPP.il2cpp_class_get_type(text));
        _cellType = IL2CPP.il2cpp_type_get_object(IL2CPP.il2cpp_class_get_type(cell));
        _dataFieldOffset = Il2CppMeta.FieldOffset(cell, "Data");
        // `UID` is an auto-property, so the FIELD is `<UID>k__BackingField` — asking for "UID" returns -1
        // and reads exactly like "the game renamed it". Declared on RefinableItemData, which both
        // EquipData and ArtifactData derive, so one offset covers equipment and artifacts.
        _uidFieldOffset = Il2CppMeta.PropertyFieldOffset(refinable, "UID");

        if (_dataFieldOffset < 0 || _uidFieldOffset < 0)
        {
            log($"tooltip inject NOT ready: Data at 0x{_dataFieldOffset:x}, UID at 0x{_uidFieldOffset:x}");
            return false;
        }

        try
        {
            _hook = Detour;
            _detour = Detours.Apply(onEnter.NativePtr, _hook, out PointerFn? original);
            _original = original;

            /**
             * The write hook. Without it the append happens before the game populates the panel and is
             * overwritten — right object, wrong moment (the counter climbing 1:1 with hovers is that
             * signature). Amending the value the game is setting cannot lose that race.
             */
            _setTextHook = SetTextDetour;
            _setTextDetour = Detours.Apply(setText.NativePtr, _setTextHook, out SetTextFn? setOriginal);
            _setTextOriginal = setOriginal;

            Installed = true;
            log($"tooltip inject ready (OnPointerEnter + TMP_Text.set_text; Data 0x{_dataFieldOffset:x}, UID 0x{_uidFieldOffset:x}, floor {MinTooltipChars} chars)");
        }
        catch (Exception e)
        {
            log($"tooltip inject could not hook HoverInfoHandler.OnPointerEnter — {e.Message}");
        }
        return Installed;
    }

    public static void Uninstall()
    {
        Detours.Undo(ref _setTextDetour);
        Detours.Undo(ref _detour);
        Installed = false;
    }

    /**
     * The hover records WHAT is hovered; `TMP_Text.set_text` decides WHEN to write.
     *
     * The latch is taken BEFORE the original, and that order is the whole feature. The owner's
     * `OnHoverEnter` — which populates the tooltip — runs INSIDE the original, so latching afterwards means
     * the text is written while `_pending` still holds the PREVIOUS item. Symptom, exactly as reported: the
     * first hover shows no note, and the second shows the first item's note on the second item's tooltip.
     * One hover behind.
     *
     * (The earlier design appended directly and therefore had to run after the original. Moving the write
     * into the `set_text` hook inverted the requirement — a reordering that is invisible unless you know
     * which call does the populating.)
     */
    private static void Detour(IntPtr self, IntPtr eventData, IntPtr methodInfo)
    {
        if (Enabled && self != IntPtr.Zero)
        {
            try { OnHover(self); }
            catch { Errors++; }
        }
        _original?.Invoke(self, eventData, methodInfo);
    }

    private static void OnHover(IntPtr handler)
    {
        Hovers++;

        // The handler shares its GameObject with the cell, which is how a generic hover dispatcher still
        // tells us which item is under the pointer.
        IntPtr cell = _getComponent!(handler, _cellType, IntPtr.Zero);
        if (cell == IntPtr.Zero) { Misses++; _pending = null; return; }
        IntPtr data = Marshal.ReadIntPtr(cell, _dataFieldOffset);
        if (data == IntPtr.Zero) { Misses++; _pending = null; return; }
        string? uid = Il2CppMeta.ReadStringField(data, _uidFieldOffset);
        if (uid is null || !InventoryPaint.TryGetMark(uid, out InventoryPaint.Mark mark) || mark.Level == 0)
        {
            Misses++;
            _pending = null;
            return;
        }

        /**
         * TMP rich text in the rule's own colour, so the line reads the same as the cell it is
         * explaining. The rule's own name IS the explanation — there is no second vocabulary to
         * invent, because the player wrote the name.
         *
         * A rule with no `Tag` drops the bold prefix entirely rather than repeating its own name
         * twice: `KEEP — rule "Kunai keepers"` is worth two clauses, `Kunai keepers — rule "Kunai
         * keepers"` is not.
         */
        _pending = mark.Label.Length > 0
            ? $"{Marker}<color={mark.Hex}><b>{mark.Label}</b> — {mark.Rule}</color>"
            : $"{Marker}<color={mark.Hex}>{mark.Rule}</color>";
    }

    /**
     * Amend the tooltip as the game writes it.
     *
     * Fires for EVERY `TMP_Text.set_text` in the game, so the early-outs are load-bearing: no pending
     * verdict, or a value too short to be an item tooltip, and this is two comparisons. The re-entrancy
     * guard is not optional — appending calls `set_text` again, which would arrive right back here.
     */
    private static void SetTextDetour(IntPtr self, IntPtr value, IntPtr methodInfo)
    {
        string? pending = _pending;
        if (!Enabled || pending is null || _writing)
        {
            _setTextOriginal?.Invoke(self, value, methodInfo);
            return;
        }

        string incoming = Il2CppMeta.ReadString(value) ?? "";
        // The item tooltip is the long one: ~1300 chars against 189 for chat, so a floor separates them
        // without naming anything. Already-marked text is the game re-setting a value we amended.
        if (incoming.Length < MinTooltipChars || incoming.Contains(Marker))
        {
            _setTextOriginal?.Invoke(self, value, methodInfo);
            return;
        }

        /**
         * ONE hover, ONE append: the latch is consumed here.
         *
         * Leaving it set meant every later long text picked up the same line — the compare tooltip
         * (`LShift`, a second copy of the panel), and any panel that repopulates without a fresh hover. On
         * screen that reads as the right note on the wrong item, which is worse than no note: a verdict
         * about an item you are not looking at is a lie about the one you are.
         *
         * Clearing it costs the case where the game writes the same tooltip body twice per hover — the note
         * would land on the first write and be lost to the second. That has not been observed here, and
         * `injected` climbing 1:1 with hovers is the signal that would say so.
         */
        _pending = null;
        _writing = true;
        try
        {
            _setTextOriginal?.Invoke(self, IL2CPP.ManagedStringToIl2Cpp(incoming + "\n" + pending), methodInfo);
            Injected++;
        }
        catch
        {
            Errors++;
            _setTextOriginal?.Invoke(self, value, methodInfo);
        }
        finally { _writing = false; }
    }

    /// <summary>What this is doing, for the log — the counters that separate "hooked" from "writing".</summary>
    public static string Status()
        => $"tooltip inject: installed {Installed}, enabled {Enabled}, hovers {Hovers}, "
         + $"injected {Injected}, misses {Misses}, errors {Errors}";
}
