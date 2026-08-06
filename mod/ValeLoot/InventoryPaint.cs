using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Il2CppInterop.Runtime;

namespace ValeLoot;

/// <summary>
/// Highlighting the player's own inventory cells — the whole point of doing this in-process.
///
/// An out-of-process screen-space marker grid — the obvious alternative — could only ever be an
/// approximation: it draws a rectangle of cells over where the game's inventory is BELIEVED to be,
/// which costs a per-resolution calibration, a manual row nudge every time the panel scrolls, page
/// arithmetic in the game's own paging unit, and a tab-row click watcher to guess which tab is open.
/// Scroll, sort or search in game and every mark is describing the wrong slot. In here there is
/// nothing to align: we mark the cell the game itself bound to the item, so scrolling, sorting,
/// searching and paging are free and correct by construction.
///
/// ## What it paints, and why that surface
///
/// `UIInventoryItem` carries a `Highlight` CanvasGroup (0x78) and a `SetHighlight(bool)` that fades it
/// in over 0.1s. The disassembly says the game drives that overlay for SELECTION — every caller is a
/// picker (`UICrafter`, `UIRefine`, `UIEssence`, `UIGemRefine`, `UIGemRemoval`, `UICardRemoval`,
/// `UIWaypoint`) and the tab-level `UIInventoryTab&lt;T&gt;.SetHighlight` is called from exactly two
/// places, both in `UIWardrobe`. So in the equipment, artifact, grimoire, card, gem, junk and
/// consumable tabs that overlay is UNUSED: it is already sized and positioned per cell, it fades for
/// free, and driving it there cannot fight the game. The wardrobe tab is deliberately skipped for that
/// reason, by class name.
///
/// ## Where the item comes from
///
/// `UIInventoryTab&lt;T&gt;` holds `InventoryItemsUID`, a live `Dictionary&lt;string, UIInventoryItem&gt;`
/// from item uid to the cell currently showing it. That map is maintained by the game, so this file
/// walks it rather than trying to work out which cell is which. Each cell's item is then read by
/// `ItemReader` and judged by the player's own rules, in process, with nothing connected: the rules
/// live in a text file, the evaluation happens here, and the colour is decided before the frame ends.
///
/// ## The pooling hazard, from the disassembly rather than from a guess
///
/// Cells are pooled (`PoolingUtils.Spawn/Despawn&lt;UIInventoryItem&gt;`), and `UIInventoryItem.Clear`
/// resets the sprite, name, description, type, weight, count, favourite and lock flags — but it does
/// NOT touch `Highlight` and does not null `Data`. A mark written once would therefore ride a recycled
/// cell onto an unrelated item. The defence is that a paint pass sets EVERY cell in the dictionary
/// explicitly, on or off, rather than only touching matches: absence of a verdict is a value we write,
/// not a case we skip.
///
/// ## Cost
///
/// Painting is driven by the tab's own `Redraw`, so it runs when the panel actually changes — open,
/// scroll, page, filter, sort, or an inventory mutation — and never per frame. One pass is a dictionary
/// walk plus one delegate call per visible cell (~100 worst case at the game's page size).
/// </summary>
internal static class InventoryPaint
{
    /**
     * The two repaint entry points, with the signatures the disassembly gives them.
     *
     * `RenderPage()` repaints the whole visible page — opening the panel, scrolling, paging, filtering.
     * `Redraw(string uid)` repaints ONE cell: it looks the uid up in both dictionaries and calls
     * `UIInventoryItem.Draw`, which is how an inventory mutation lands. Getting these arities wrong is
     * not a missing feature but a corrupted call frame, so they are bound exactly as declared —
     * `Redraw/1` was first bound as `Redraw/0`, which simply failed to resolve (the lucky outcome).
     */
    private delegate void RenderPageFn(IntPtr self, IntPtr methodInfo);
    private delegate void RedrawFn(IntPtr self, IntPtr uid, IntPtr methodInfo);

    /**
     * Every engine call from here takes only pointers, ints and floats.
     *
     * The first attempt called `Graphic.set_color(Color)` — a 16-byte struct by value, which is an ABI
     * question the process gets exactly one chance to answer, and it also resolved the wrong overload of
     * `GetComponentsInChildren` (see `Il2CppMeta.FindOverload`). Between them they took the game down
     * with an `AccessViolationException` inside the hook the first time a bag was opened. Writing the
     * `m_Color` FIELD and calling the no-argument `SetAllDirty()` does the same work with nothing to get
     * wrong. Keep it that way: a highlight is not worth a crash.
     */
    private delegate void SetAlphaFn(IntPtr self, float alpha, IntPtr methodInfo);
    private delegate void VoidFn(IntPtr self, IntPtr methodInfo);
    private delegate IntPtr GetComponentFn(IntPtr self, IntPtr type, IntPtr methodInfo);
    private delegate IntPtr TransformFn(IntPtr self, IntPtr methodInfo);
    private delegate int CountFn(IntPtr self, IntPtr methodInfo);
    private delegate IntPtr GetChildFn(IntPtr self, int index, IntPtr methodInfo);

    /**
     * One item's verdict: how loudly, in what colour, which rule said so, and what it sounds like.
     *
     * `Level == 0` means "not marked". `Hex` is kept alongside the parsed floats because the tooltip
     * writes TMP rich text, which wants `#rrggbb` back as a string; re-formatting the floats would be a
     * second chance to disagree with the cell about a colour.
     */
    internal readonly struct Mark
    {
        public readonly int Level;
        public readonly float R;
        public readonly float G;
        public readonly float B;
        public readonly string Hex;
        public readonly string Label;
        public readonly string Rule;
        /// <summary>Sound name to play if this uid is arriving, or null for silence.</summary>
        public readonly string? Sound;

        public Mark(int level, (float R, float G, float B) rgb, string hex, string label, string rule, string? sound)
        {
            Level = level;
            R = rgb.R;
            G = rgb.G;
            B = rgb.B;
            Hex = hex;
            Label = label;
            Rule = rule;
            Sound = sound;
        }
    }

    /// <summary>The verdict for one item, or null. Read by the tooltip injector on the main thread.</summary>
    public static bool TryGetMark(string uid, out Mark mark) => _marks.TryGetValue(uid, out mark);

    /**
     * How visible each level is.
     *
     * Deliberately far apart rather than evenly spaced: the point of three levels is that a glow is
     * unmistakable and a dot is background, so the gap between them matters more than the linearity.
     * `UIInventoryItem.SetHighlight` fades to 1.0, which is why every match used to look the same.
     */
    private const float DotAlpha = 0.28f;
    private const float MarkAlpha = 0.62f;
    private const float GlowAlpha = 1f;

    /// <summary>Offset of `UIInventoryItem.Highlight` (CanvasGroup), resolved by name at install.</summary>
    private static int _highlightFieldOffset = -1;

    /// <summary>
    /// Tabs whose Redraw is hooked. `Cosmetics` is absent on purpose: `UIWardrobe` drives the same
    /// highlight overlay there for its preview selection, and two writers would fight.
    /// </summary>
    private static readonly string[] TabClasses =
    {
        "UIInventoryTab_Equips",
        "UIInventoryTab_Artifacts",
        "UIInventoryTab_Grimoires",
        "UIInventoryTab_Cards",
        "UIInventoryTab_Gems",
        "UIInventoryTab_Junks",
        "UIInventoryTab_Consumables",
    };

    /**
     * uid -> what was drawn on that cell, rebuilt by every paint pass.
     *
     * It exists for the hover note, which needs to answer "which rule claimed this item" at a moment
     * that is not a paint pass. Everything that writes or reads it — the paint pass and
     * `HoverInfoHandler.OnPointerEnter` — runs on Unity's main thread, so it is a plain dictionary
     * with no lock and no reference swap. It used to be a volatile swap because a socket thread
     * delivered the table; the rules moved in-process, and the concurrency went with them.
     */
    private static readonly Dictionary<string, Mark> _marks = new(StringComparer.Ordinal);

    /// <summary>Reused per cell. One buffer for the whole session — see LootFilter.ItemFacts.</summary>
    private static readonly LootFilter.ItemFacts _facts = new();

    private static readonly List<object> _detours = new();
    private static readonly List<RenderPageFn> _pageHooks = new();
    private static readonly List<RenderPageFn?> _pageOriginals = new();
    private static readonly List<RedrawFn> _itemHooks = new();
    private static readonly List<RedrawFn?> _itemOriginals = new();
    private static SetAlphaFn? _setAlpha;
    private static GetComponentFn? _getComponent;
    private static TransformFn? _getTransform;
    private static CountFn? _getChildCount;
    private static GetChildFn? _getChild;
    private static VoidFn? _setAllDirty;
    /// <summary>A `System.Type` for `UnityEngine.UI.Graphic`, for the non-generic component search.</summary>
    private static IntPtr _graphicType;
    /// <summary>Offset of `Graphic.m_Color`, written directly instead of through `set_color(Color)`.</summary>
    private static int _colorFieldOffset = -1;
    /// <summary>Logged once, as proof the tint reached a real graphic rather than merely resolving.</summary>
    private static bool _tintReported;

    /**
     * Tuning that a player can reach without a rebuild, read from `BepInEx/config/com.savi.valeloot.cfg`.
     *
     * Five build/deploy/relaunch/login cycles went into getting this tint onto the right object, and the
     * two values that mattered during them are the two that are configurable now: whether to tint at all,
     * and how far below the overlay object to walk. A player on a future game build whose cell layout has
     * moved can turn the tint off, or widen the walk, without waiting for a release.
     */
    public static bool TintEnabled = true;
    /// <summary>How many levels below the overlay object to tint. 0 = the object itself only.</summary>
    public static int TintDepth = 2;
    private static Action<string>? _log;
    private static int _uidFieldOffset = -1;

    public static bool Installed { get; private set; }
    public static long Passes;
    public static long CellsLit;
    public static long CellsCleared;
    public static long Errors;
    public static int MarkCount => _marks.Count;

    /// <summary>Drop everything remembered about what is drawn, so the next pass decides afresh.</summary>
    public static void Forget() => _marks.Clear();

    /// <summary>Apply configured tuning, and say what it is — a silent knob is an unfalsifiable one.</summary>
    public static void Configure(bool tint, int depth)
    {
        TintEnabled = tint;
        TintDepth = depth < 0 ? 0 : depth > 4 ? 4 : depth;
        _tintReported = false;   // report again, so a change proves itself in the log
        _log?.Invoke($"inventory paint: tint {(TintEnabled ? "on" : "off")}, depth {TintDepth}");
    }

    public static bool Install(Action<string> log)
    {
        _log = log;

        /**
         * The cell field the highlight lives on, by NAME.
         *
         * The dump says 0x78 today. Hardcoding that would be the one assumption in this file guaranteed to
         * rot: field offsets move whenever a serialized field is added above them, which is an ordinary
         * content patch, and a stale offset reads a neighbouring pointer as a CanvasGroup.
         */
        IntPtr cellClass = Il2CppMeta.FindClass("", "UIInventoryItem", HookCensus.GameAssemblies);
        _highlightFieldOffset = Il2CppMeta.FieldOffset(cellClass, "Highlight");
        if (_highlightFieldOffset < 0)
        {
            log("inventory paint NOT ready: UIInventoryItem.Highlight field did not resolve");
            return false;
        }

        /**
         * Engine setters, resolved by name like everything else here.
         *
         * `CanvasGroup.alpha` carries the LEVEL and is the one hard requirement: without it there is no
         * highlight at all. The tint path (`Component.GetComponent(Type)` + `Graphic.color`) is optional —
         * if the overlay turns out to have no Image on it, intensity alone still distinguishes a glow from
         * a dot, and saying so beats refusing to install.
         *
         * `FindClass(ns, name, assemblies)` — the argument order this file already got wrong once. Unity
         * types live in their module assemblies, not Assembly-CSharp.
         */
        IntPtr canvasGroup = Il2CppMeta.FindClass("UnityEngine", "CanvasGroup", "UnityEngine.UIModule.dll", "UnityEngine.CoreModule.dll");
        Il2CppMeta.MethodInfo? setAlpha = Il2CppMeta.FindMethodRuntime(canvasGroup, "set_alpha", 1);
        if (setAlpha is null || setAlpha.NativePtr == IntPtr.Zero)
        {
            log("inventory paint NOT ready: CanvasGroup.set_alpha did not resolve");
            return false;
        }
        _setAlpha = Marshal.GetDelegateForFunctionPointer<SetAlphaFn>(setAlpha.NativePtr);

        /**
         * The tint path, resolved by exact signature.
         *
         * `Graphic`, not `Image`: the overlay may draw with any Graphic subclass, and `m_Color` is declared
         * on the base, so searching for the base cannot miss a shape. Every lookup names its parameter
         * types — the crash that preceded this code came from trusting arity alone.
         *
         * The whole path is OPTIONAL. If any piece is missing, the level still shows as intensity, which is
         * the difference between a degraded highlight and a dead one.
         */
        IntPtr component = Il2CppMeta.FindClass("UnityEngine", "Component", "UnityEngine.CoreModule.dll");
        IntPtr transform = Il2CppMeta.FindClass("UnityEngine", "Transform", "UnityEngine.CoreModule.dll");
        IntPtr graphic = Il2CppMeta.FindClass("UnityEngine.UI", "Graphic", "UnityEngine.UI.dll", "Unity.ugui.dll");
        Il2CppMeta.MethodInfo? getComponent = Il2CppMeta.FindOverload(component, "GetComponent", "System.Type");
        Il2CppMeta.MethodInfo? getTransform = Il2CppMeta.FindOverload(component, "get_transform");
        Il2CppMeta.MethodInfo? childCount = Il2CppMeta.FindOverload(transform, "get_childCount");
        Il2CppMeta.MethodInfo? getChild = Il2CppMeta.FindOverload(transform, "GetChild", "System.Int32");
        Il2CppMeta.MethodInfo? setAllDirty = Il2CppMeta.FindOverload(graphic, "SetAllDirty");
        _colorFieldOffset = Il2CppMeta.FieldOffsetUp(graphic, "m_Color");

        bool tintReady = getComponent is not null && getTransform is not null && childCount is not null
            && getChild is not null && setAllDirty is not null && _colorFieldOffset >= 0 && graphic != IntPtr.Zero;
        if (tintReady)
        {
            _getComponent = Marshal.GetDelegateForFunctionPointer<GetComponentFn>(getComponent!.NativePtr);
            _getTransform = Marshal.GetDelegateForFunctionPointer<TransformFn>(getTransform!.NativePtr);
            _getChildCount = Marshal.GetDelegateForFunctionPointer<CountFn>(childCount!.NativePtr);
            _getChild = Marshal.GetDelegateForFunctionPointer<GetChildFn>(getChild!.NativePtr);
            _setAllDirty = Marshal.GetDelegateForFunctionPointer<VoidFn>(setAllDirty!.NativePtr);
            // The non-generic component search wants a managed System.Type instance, not a class pointer.
            _graphicType = IL2CPP.il2cpp_type_get_object(IL2CPP.il2cpp_class_get_type(graphic));
            log($"inventory paint: tint path ready (Graphic.m_Color at 0x{_colorFieldOffset:x}, depth {TintDepth})");
        }
        else
        {
            log("inventory paint: no tint path — levels will show as intensity only");
        }

        /**
         * Both repaint paths are hooked, because they answer different questions:
         * `RenderPage()` is "the panel changed" (opened, scrolled, paged, filtered) and `Redraw(uid)` is
         * "one item changed" (a pickup, a refine, a favourite). Hooking only the first leaves a freshly
         * looted item unpainted until the player scrolls; hooking only the second never paints the panel
         * the player just opened.
         *
         * One native body may back several instantiations of the generic base, so identical targets are
         * detoured once — detouring the same address twice would chain our own hook to itself.
         */
        var seen = new HashSet<IntPtr>();
        int pages = 0;
        int items = 0;
        foreach (string tabClass in TabClasses)
        {
            IntPtr klass = Il2CppMeta.FindClass("", tabClass, HookCensus.GameAssemblies);
            if (klass == IntPtr.Zero) { log($"inventory paint: class {tabClass} not found"); continue; }

            Il2CppMeta.MethodInfo? renderPage = Il2CppMeta.FindMethodRuntime(klass, "RenderPage", 0);
            if (renderPage is not null && renderPage.NativePtr != IntPtr.Zero && seen.Add(renderPage.NativePtr))
            {
                int index = _pageHooks.Count;
                RenderPageFn hook = (self, methodInfo) => RenderPageDetour(index, self, methodInfo);
                _pageHooks.Add(hook);
                _pageOriginals.Add(null);
                try
                {
                    _detours.Add(Detours.Apply(renderPage.NativePtr, hook, out RenderPageFn? original));
                    _pageOriginals[index] = original;
                    pages++;
                }
                catch (Exception e) { log($"inventory paint could not hook {tabClass}.RenderPage — {e.Message}"); }
            }

            Il2CppMeta.MethodInfo? redraw = Il2CppMeta.FindMethodRuntime(klass, "Redraw", 1);
            if (redraw is not null && redraw.NativePtr != IntPtr.Zero && seen.Add(redraw.NativePtr))
            {
                int index = _itemHooks.Count;
                RedrawFn hook = (self, uid, methodInfo) => RedrawDetour(index, self, uid, methodInfo);
                _itemHooks.Add(hook);
                _itemOriginals.Add(null);
                try
                {
                    _detours.Add(Detours.Apply(redraw.NativePtr, hook, out RedrawFn? original));
                    _itemOriginals[index] = original;
                    items++;
                }
                catch (Exception e) { log($"inventory paint could not hook {tabClass}.Redraw — {e.Message}"); }
            }

            if (renderPage is null && redraw is null)
            {
                /**
                 * Say what was actually there.
                 *
                 * A bare "did not resolve" sent this hunt through three game restarts guessing at argument
                 * order, generic initialisation and arity — the answer was `Redraw/1`, visible the moment
                 * the declared methods were printed. The hierarchy dump turns the next failure, a rename
                 * on patch day included, into one line of evidence.
                 */
                log($"inventory paint: no repaint method on {tabClass}; hierarchy follows");
                for (IntPtr current = klass; current != IntPtr.Zero; current = IL2CPP.il2cpp_class_get_parent(current))
                {
                    var names = new List<string>();
                    foreach (Il2CppMeta.MethodInfo m in Il2CppMeta.Methods(current)) names.Add($"{m.Name}/{m.ParamCount}");
                    log($"  {Il2CppMeta.ClassName(current)}: {(names.Count == 0 ? "(no declared methods)" : string.Join(", ", names))}");
                }
            }
        }

        Installed = pages + items > 0;
        log(Installed
            ? $"inventory paint ready: {pages} RenderPage + {items} Redraw body/bodies hooked"
            : "inventory paint NOT ready: no repaint method resolved on any tab");
        return Installed;
    }

    public static void Uninstall()
    {
        for (int i = 0; i < _detours.Count; i++)
        {
            object? handle = _detours[i];
            Detours.Undo(ref handle);
        }
        _detours.Clear();
        _pageHooks.Clear();
        _pageOriginals.Clear();
        _itemHooks.Clear();
        _itemOriginals.Clear();
        Installed = false;
    }

    /**
     * Paint AFTER the game has finished its own repaint.
     *
     * Order matters: the original rebinds cells to items and repopulates `InventoryItemsUID`, so painting
     * first would mark the panel's previous contents.
     *
     * Both detours run the same full pass rather than a targeted one. A pass is a dictionary walk and one
     * call per visible cell, and doing the whole panel is what makes the mark state idempotent: there is
     * no way for a cell to be left holding a verdict that belonged to a recycled item.
     */
    private static void RenderPageDetour(int index, IntPtr self, IntPtr methodInfo)
    {
        RenderPageFn? original = index < _pageOriginals.Count ? _pageOriginals[index] : null;
        original?.Invoke(self, methodInfo);
        // A hook body must never let an exception cross back into il2cpp code.
        try { Paint(self); }
        catch { Errors++; }
    }

    private static void RedrawDetour(int index, IntPtr self, IntPtr uid, IntPtr methodInfo)
    {
        RedrawFn? original = index < _itemOriginals.Count ? _itemOriginals[index] : null;
        original?.Invoke(self, uid, methodInfo);
        try { Paint(self); }
        catch { Errors++; }
    }

    private static void Paint(IntPtr tab)
    {
        if (tab == IntPtr.Zero) return;

        IntPtr tabClass = Il2CppMeta.ClassOf(tab);
        // The wardrobe reaches this code only if the class list above ever gains it; refuse by name
        // anyway, because the cost of being wrong is fighting UIWardrobe for the same overlay.
        string name = Il2CppMeta.ClassName(tabClass);
        if (name.IndexOf("Cosmetic", StringComparison.Ordinal) >= 0
            || name.IndexOf("Wardrobe", StringComparison.Ordinal) >= 0) return;

        if (_uidFieldOffset < 0) _uidFieldOffset = Il2CppMeta.FieldOffsetUp(tabClass, "InventoryItemsUID");
        if (_uidFieldOffset < 0) return;

        IntPtr dictionary = Marshal.ReadIntPtr(tab, _uidFieldOffset);
        if (dictionary == IntPtr.Zero) return;

        /**
         * The filter reload lands at the top of a pass, and the reason is unchanged: the file watcher
         * runs on a thread pool thread and only sets a flag; the actual re-parse happens on the main
         * thread, one pass after the save, which is also the exact moment the result becomes visible.
         * One reload per save, no locks, and an editor that writes three times per save does not cost
         * three parses.
         *
         * It is no longer the ONLY caller: `InventoryWatch` checks the same flag on its throttled tick,
         * because with the bag shut this pass never runs and its rules would be whatever they were at
         * boot. Whichever gets there first parses; the other reads the result.
         *
         * Nothing sound-related happens here any more. Arrivals are diffed out of the inventory DATA by
         * the watcher, whose baseline is what you OWN rather than what a rule happened to claim, so a
         * new rule cannot manufacture an arrival for a bag you have been carrying all evening.
         */
        FilterFile.ReloadIfChanged();
        FilterParser.ParsedFilter filter = FilterFile.Current;

        _marks.Clear();
        Passes++;
        BagSnapshot.BeginPass(filter.Threshold);
        foreach ((IntPtr key, IntPtr cell) in Il2CppMeta.DictionaryEntries(dictionary))
        {
            if (cell == IntPtr.Zero) continue;
            string? uid = Il2CppMeta.ReadString(key);

            // ONE read per cell, feeding both readers of it: the verdict this cell is painted with,
            // and the row the editor's bag snapshot counts against. The snapshot deliberately does
            // not walk the inventory itself — there is no second walk to disagree with this one.
            bool readable = uid is not null && ItemReader.Read(cell, _facts);
            Mark mark = readable ? Judge(_facts, filter) : default;
            if (readable) BagSnapshot.Observe(uid!, _facts);

            // Every cell is written explicitly, hit or miss. Skipping the misses would leave a recycled
            // cell wearing the previous item's mark, which `UIInventoryItem.Clear` does not undo.
            if (mark.Level > 0)
            {
                _marks[uid!] = mark;
                Draw(cell, mark);
                CellsLit++;
            }
            else
            {
                Draw(cell, default);
                CellsCleared++;
            }
        }

        // Last, and off this thread: `EndPass` compares the bag's content against what is already on
        // disk and queues a write only when they differ. A preview file for an editor is the least
        // urgent thing in this method and it must not be able to delay a frame.
        BagSnapshot.EndPass();
    }

    /**
     * The facts in <paramref name="facts"/>, judged: overrides first, then the rules in file order.
     *
     * `AlwaysShow`/`AlwaysHide` are checked before any rule because that is the whole reason they exist
     * — a per-item override is how a player escapes rule order without rewriting their filter.
     *
     * Shared with <see cref="InventoryWatch"/> rather than private, and that is the point: the colour
     * on a cell and the noise a pickup makes come out of ONE evaluation of one rule list. Two judges
     * could disagree about which rule claimed an item, and the player would have no way to tell which
     * of them was lying.
     */
    internal static Mark Judge(LootFilter.ItemFacts facts, FilterParser.ParsedFilter filter)
    {
        if (Named(facts, filter.Muted)) return default;
        if (Named(facts, filter.Pinned)) return PinnedMark;

        LootFilter.LootRule? rule = LootFilter.Match(facts, filter.Rules, filter.Threshold);
        if (rule is null || rule.Mute) return default;
        return new Mark(
            rule.Level,
            (rule.R, rule.G, rule.B),
            rule.Color,
            rule.Label.Length > 0 ? rule.Label : "",
            $"rule \"{rule.Name}\"",
            rule.Sound);
    }

    /**
     * How an `AlwaysShow` item is drawn.
     *
     * Deliberately loud and deliberately a fixed colour: an override says "whatever else my filter
     * decides, show me this one", and giving it the palette of whichever rule it skipped would make it
     * indistinguishable from an ordinary match.
     */
    private static readonly Mark PinnedMark =
        new(LootFilter.LevelGlow, LootFilter.ParseColor("#facc15"), "#facc15", "PINNED", "always shown", null);

    /// <summary>Does the item's displayed name or catalog id equal one of these, case-insensitively?</summary>
    private static bool Named(LootFilter.ItemFacts facts, string[] names)
    {
        for (int i = 0; i < names.Length; i++)
        {
            if (string.Equals(facts.Name, names[i], StringComparison.OrdinalIgnoreCase)
                || string.Equals(facts.Id, names[i], StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /**
     * Write one cell's highlight: the rule's colour, at an intensity that says how loudly.
     *
     * The alpha is written DIRECTLY rather than through `SetHighlight`, which DOFades to a hardcoded 1.0
     * and so can only say "lit". Three intensities are the difference between a panel where a quarter of
     * the cells look identical and one where the eye lands on the triple top roll first. Nothing in the
     * inventory tabs tweens this CanvasGroup (every `SetHighlight` caller is a picker or the wardrobe), so
     * a direct write has nothing to race.
     *
     * The tint looks on the overlay object AND one level of children, because the first attempt asked the
     * CanvasGroup's own object for a graphic, got null, and produced grey highlights at three correct
     * intensities: a CanvasGroup is a grouping object, and the thing that draws sits beneath it.
     *
     * Colour is only written for a MARKED cell — an unmarked cell is invisible at alpha 0, so tinting it
     * would be work nobody can see, and it keeps the walk bounded by the number of matches rather than by
     * the size of the panel.
     */
    private static void Draw(IntPtr cell, Mark mark)
    {
        IntPtr group = Marshal.ReadIntPtr(cell, _highlightFieldOffset);
        if (group == IntPtr.Zero) return;

        _setAlpha?.Invoke(group, mark.Level switch { 3 => GlowAlpha, 2 => MarkAlpha, 1 => DotAlpha, _ => 0f }, IntPtr.Zero);
        if (mark.Level == 0 || !TintEnabled || _getComponent is null || _graphicType == IntPtr.Zero) return;

        /**
         * EVERY graphic in the overlay's subtree, not the first one found.
         *
         * The previous version tinted the CanvasGroup's own graphic and returned — and that object has
         * one, so it never walked down to the frame the player can actually see. Result: the write
         * landed ("tinted 1 graphic(s)") and nothing changed colour on screen. A short-circuit on the
         * parent is indistinguishable from success in every signal the mod emits, which is why the probe
         * below exists.
         */
        int painted = TintSubtree(group, mark, 0);
        if (!_tintReported)
        {
            _tintReported = true;
            _log?.Invoke($"inventory paint: first marked cell tinted {painted} graphic(s)");
        }
    }

    /// <summary>Tint this object's graphics and its descendants', bounded in depth and count.</summary>
    private static int TintSubtree(IntPtr owner, Mark mark, int depth)
    {
        int painted = Tint(owner, mark);
        if (depth >= TintDepth || _getTransform is null || _getChildCount is null || _getChild is null) return painted;

        IntPtr root = _getTransform(owner, IntPtr.Zero);
        if (root == IntPtr.Zero) return painted;
        int children = _getChildCount(root, IntPtr.Zero);
        // Bounded: the overlay is a frame, not a scene graph, and this runs inside a hook on every
        // repaint — an unbounded walk is how a highlight becomes a stutter.
        for (int i = 0; i < children && i < 8; i++)
        {
            IntPtr child = _getChild(root, i, IntPtr.Zero);
            if (child != IntPtr.Zero) painted += TintSubtree(child, mark, depth + 1);
        }
        return painted;
    }

    /// <summary>Tint the graphic on one object, if it has one. Returns 1 when it did.</summary>
    private static int Tint(IntPtr owner, Mark mark)
    {
        IntPtr graphic = _getComponent!(owner, _graphicType, IntPtr.Zero);
        if (graphic == IntPtr.Zero) return 0;
        /**
         * The colour is written as four floats at `m_Color` and then declared dirty.
         *
         * Alpha stays 1 here: intensity belongs to the CanvasGroup, so one value decides it. `SetAllDirty`
         * is what makes the write visible — a field write alone leaves the mesh as it was until something
         * else happens to rebuild it, which reads as "the tint works sometimes".
         */
        Marshal.WriteInt32(graphic, _colorFieldOffset, BitConverter.SingleToInt32Bits(mark.R));
        Marshal.WriteInt32(graphic, _colorFieldOffset + 4, BitConverter.SingleToInt32Bits(mark.G));
        Marshal.WriteInt32(graphic, _colorFieldOffset + 8, BitConverter.SingleToInt32Bits(mark.B));
        Marshal.WriteInt32(graphic, _colorFieldOffset + 12, BitConverter.SingleToInt32Bits(1f));
        _setAllDirty?.Invoke(graphic, IntPtr.Zero);
        return 1;
    }

    /// <summary>What this is doing, for the log — the counters that separate "installed" from "drawing".</summary>
    public static string Status()
        => $"inventory paint: installed {Installed}, marks {MarkCount}, passes {Passes}, "
         + $"lit {CellsLit}, cleared {CellsCleared}, errors {Errors}";
}
