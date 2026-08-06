using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ValeLoot;

/// <summary>
/// Reading one item into the flat facts a rule is evaluated against — from a UI cell, or from the
/// item data pointer alone.
///
/// This is the ONLY file that knows the game's data layout, and every offset in it is resolved BY
/// NAME at install. Nothing is hardcoded: RVAs and field offsets shuffle on every game patch, names
/// survive, and the boot census turns a rename into a loud MISS instead of a mod that paints the
/// wrong things.
///
/// ## Auto-properties, and the -1 that reads like a rename
///
/// The game's data classes declare `public string UID { get; set; }`, which the compiler stores in a
/// field called `&lt;UID&gt;k__BackingField`. Asking for "UID" returns -1, which is indistinguishable
/// from "the game renamed it". Every lookup here therefore goes through
/// <see cref="Il2CppMeta.PropertyFieldOffset"/>, which tries the plain name and then the backing
/// field — Unity's serialised fields on the UI components are plain, the data classes' are not, and
/// one call covers both without the caller having to know which is which.
///
/// ## `StatData.Value` is the ROLL, not the number on screen
///
/// This is the fact the whole filter language rests on, and it is easy to get backwards. `Value` is a
/// roll percentage, 0..100. What the game PRINTS it derives:
/// `displayed = cap * (2/3 + roll/300)`, rounded — `Formula.GetSubstats` computes exactly
/// `Value / 100 * 0.333333 + 0.666667` and multiplies by the stat's base cap. It also branches on
/// `Value &gt; 100`, which is the chaos over-roll. So roll quality is free in here, and the printed
/// value costs one cap lookup — which <see cref="ItemCatalog"/> does, from the game's own configs.
/// That is why the ordinal is carried alongside the name: the cap is keyed by `StatType`.
///
/// ## Why the layout is cached per runtime class
///
/// A bag holds equipment, artifacts, cards, gems and consumables, and their data classes are
/// different types sharing a base. Rather than enumerate which classes exist — a list that goes stale
/// on the first patch that adds one — the reader resolves the fields it wants on whatever class the
/// object actually is, once, and caches the answer by class pointer. A class lacking a field caches
/// -1 for it and that field reads as absent, so a consumable with no substats costs one dictionary
/// lookup rather than a special case.
///
/// ## Where the displayed name and type come from
///
/// From the CELL, when there is one: `UIInventoryItem` holds the `Name` and `Type` TMP_Text
/// components the player is looking at. A filter says `Name Kunai` because the player read "Kunai" on
/// screen, and matching what is on screen needs no config lookup and cannot disagree with it. The
/// catalog `Id` is matched as well, so a filter written elsewhere still works — and
/// <see cref="ItemCatalog"/> adds the config's own `DisplayName` on top, which is what covers a cell
/// whose text is truncated to fit. The cell text stays the fallback, so a session with no catalog
/// filters exactly as it did before there was one.
///
/// <see cref="ReadData"/> has no cell, so it leaves both empty and leans on that catalog fallback —
/// see its own comment for what that costs. Everything else it reads is byte-for-byte what the cell
/// path reads, because it is the same code: the cell path resolves `Data` and calls it.
/// </summary>
internal static class ItemReader
{
    private delegate IntPtr GetTextFn(IntPtr self, IntPtr methodInfo);

    public static bool Installed { get; private set; }

    /// <summary>Set when substats resolved. False means roll conditions cannot match, and the log says so.</summary>
    public static bool StatsReadable { get; private set; }

    public static string Summary { get; private set; } = "not installed";

    private static GetTextFn? _getText;

    private static int _cellData = -1;
    private static int _cellName = -1;
    private static int _cellType = -1;

    private static int _statType = -1;
    private static int _statValue = -1;
    private static int _statValueStr = -1;

    /// <summary>`StatType` ordinal -> member name, read from live metadata at boot. Never hardcoded.</summary>
    private static readonly Dictionary<int, string> _statNames = new();

    /// <summary>Field offsets for one concrete data class, resolved by name on first sight.</summary>
    private readonly struct Layout
    {
        public readonly int Id;
        public readonly int Favorite;
        public readonly int Refine;
        public readonly int Substats;
        public readonly int ChaosType;

        public Layout(int id, int favorite, int refine, int substats, int chaosType)
        {
            Id = id;
            Favorite = favorite;
            Refine = refine;
            Substats = substats;
            ChaosType = chaosType;
        }
    }

    private static readonly Dictionary<IntPtr, Layout> _layouts = new();

    public static bool Install(Action<string> log)
    {
        IntPtr cell = Il2CppMeta.FindClass("", "UIInventoryItem", HookCensus.GameAssemblies);
        IntPtr text = Il2CppMeta.FindClass("TMPro", "TMP_Text", HookCensus.TextAssemblies);
        IntPtr statData = Il2CppMeta.FindClass("", "StatData", HookCensus.GameAssemblies);
        IntPtr statType = Il2CppMeta.FindClass("", "StatType", HookCensus.GameAssemblies);

        _cellData = Il2CppMeta.PropertyFieldOffset(cell, "Data");
        _cellName = Il2CppMeta.PropertyFieldOffset(cell, "Name");
        _cellType = Il2CppMeta.PropertyFieldOffset(cell, "Type");

        if (_cellData < 0)
        {
            log("item reader NOT ready: UIInventoryItem.Data did not resolve — no rule can be evaluated");
            Summary = "UIInventoryItem.Data missing";
            return false;
        }

        // Overloads are resolved by parameter TYPE, never by arity: trusting arity on an overloaded
        // engine method is what crashed this process once already.
        Il2CppMeta.MethodInfo? getText = Il2CppMeta.FindOverload(text, "get_text");
        if (getText is not null && getText.NativePtr != IntPtr.Zero)
        {
            _getText = Marshal.GetDelegateForFunctionPointer<GetTextFn>(getText.NativePtr);
        }
        else
        {
            // Not fatal: without it `Name` and `Type` cannot match, but rolls, refine and favourite
            // still can. Saying which half went missing beats "the filter stopped working".
            log("item reader: TMP_Text.get_text did not resolve — Name/Type conditions cannot match");
        }

        _statType = Il2CppMeta.PropertyFieldOffset(statData, "Type");
        _statValue = Il2CppMeta.PropertyFieldOffset(statData, "Value");
        _statValueStr = Il2CppMeta.PropertyFieldOffset(statData, "ValueStr");

        foreach ((string name, int value) in Il2CppMeta.EnumValues(statType))
        {
            // Later duplicates lose: an alias member should not rename the canonical one.
            if (!_statNames.ContainsKey(value)) _statNames[value] = name;
        }

        StatsReadable = _statType >= 0 && _statValue >= 0 && _statNames.Count > 0;
        if (!StatsReadable)
        {
            log($"item reader: substats unreadable (StatData.Type {_statType}, StatData.Value {_statValue}, "
              + $"{_statNames.Count} StatType names) — Stat/TopRolls/AvgRoll cannot match this session");
        }

        Installed = true;
        Summary = $"Data 0x{_cellData:x}, Name 0x{_cellName:x}, Type 0x{_cellType:x}, "
                + $"{_statNames.Count} stat names, rolls {(StatsReadable ? "readable" : "UNREADABLE")}";
        log($"item reader ready ({Summary})");
        return true;
    }

    public static void Uninstall()
    {
        Installed = false;
        _layouts.Clear();
        _statNames.Clear();
        _getText = null;
    }

    /// <summary>Names the stat behind an ordinal, or "" when the enum did not resolve.</summary>
    public static string StatName(int ordinal) => _statNames.TryGetValue(ordinal, out string? name) ? name : "";

    /// <summary>Every stat name a `Stat` line may use — the catalog's reference file lists these.</summary>
    public static IReadOnlyCollection<string> StatNames => _statNames.Values;

    /**
     * Fill <paramref name="facts"/> from a live `UIInventoryItem`. False when the cell holds nothing
     * readable.
     *
     * Called once per visible cell per repaint, so it allocates only the strings il2cpp hands back.
     * `facts` is the caller's single reusable buffer — there is deliberately no per-item object here.
     *
     * The CELL only owns two of the facts: the displayed `Name` and `Type`. Everything else lives on
     * the item data behind it, which is why the body below is one `Data` read and a delegation to
     * <see cref="ReadData"/>. The pickup watcher reads the same items straight out of the player's
     * inventory data with no cell in sight, and two readers that could disagree about the same item
     * would be a bug factory: the sound and the colour must never name different rules.
     */
    public static bool Read(IntPtr cellObject, LootFilter.ItemFacts facts)
    {
        if (!Installed || cellObject == IntPtr.Zero) { facts.Reset(); return false; }

        IntPtr data = Marshal.ReadIntPtr(cellObject, _cellData);
        // A pooled cell keeps its old `Data` after `Clear()`, so an empty slot is not necessarily null.
        // Callers only reach here for cells the game's own uid map points at, which is what makes the
        // pointer trustworthy — `ReadData`'s null check is for the torn moment during a repaint.
        if (!ReadData(data, facts)) return false;

        facts.Name = ReadText(cellObject, _cellName);
        facts.Type = ReadText(cellObject, _cellType);
        return true;
    }

    /**
     * Fill <paramref name="facts"/> from an item DATA pointer — `EquipData`, `ArtifactData`, or
     * whatever else the bag holds. False when the pointer is null or the reader never installed.
     *
     * `Name` and `Type` are left EMPTY, because they are the text on a cell and there is no cell here.
     * A caller with no UI to read gets them from <see cref="ItemCatalog"/> instead, keyed by `Id`,
     * which is exactly what `LootFilter.Match` already falls back to. That fallback covers equipment,
     * because the catalog is the game's equip config database; an artifact's displayed name is not in
     * it, so a `Name` line aimed at an artifact matches on its `Id` from this path or not at all.
     *
     * The per-runtime-class layout cache is shared with the cell path: one dictionary lookup for an
     * item class already seen, one round of name resolution the first time.
     */
    public static bool ReadData(IntPtr itemData, LootFilter.ItemFacts facts)
    {
        facts.Reset();
        if (!Installed || itemData == IntPtr.Zero) return false;

        Layout layout = LayoutFor(Il2CppMeta.ClassOf(itemData));

        facts.Id = Il2CppMeta.ReadStringField(itemData, layout.Id) ?? "";
        facts.Refine = layout.Refine >= 0 ? Marshal.ReadInt32(itemData, layout.Refine) : 0;
        facts.Favorite = layout.Favorite >= 0 && Marshal.ReadByte(itemData, layout.Favorite) != 0;
        // A chaos type of 0 is the enum's "none". Any other member means the item carries one; which
        // one has no filter vocabulary yet, so it is not read.
        facts.HasChaos = layout.ChaosType >= 0 && Marshal.ReadInt32(itemData, layout.ChaosType) != 0;

        if (StatsReadable && layout.Substats >= 0)
        {
            IntPtr list = Marshal.ReadIntPtr(itemData, layout.Substats);
            foreach (IntPtr stat in Il2CppMeta.ListItems(list))
            {
                int ordinal = Marshal.ReadInt32(stat, _statType);
                string name = StatName(ordinal);
                if (name.Length == 0) continue;
                // The ordinal travels with the name because the absolute stat form needs it: the
                // catalog's cap lookup is keyed by `StatType`, and recovering the ordinal from the
                // name later would mean carrying a reverse map that can disagree with this one.
                facts.AddStat(
                    name,
                    ordinal,
                    Marshal.ReadInt32(stat, _statValue),
                    Il2CppMeta.ReadStringField(stat, _statValueStr) ?? "");
            }
        }
        return true;
    }

    private static string ReadText(IntPtr cellObject, int offset)
    {
        if (offset < 0 || _getText is null) return "";
        IntPtr component = Marshal.ReadIntPtr(cellObject, offset);
        if (component == IntPtr.Zero) return "";
        return Il2CppMeta.ReadString(_getText(component, IntPtr.Zero)) ?? "";
    }

    private static Layout LayoutFor(IntPtr klass)
    {
        if (_layouts.TryGetValue(klass, out Layout cached)) return cached;
        var layout = new Layout(
            Il2CppMeta.PropertyFieldOffset(klass, "Id"),
            Il2CppMeta.PropertyFieldOffset(klass, "Favorite"),
            Il2CppMeta.PropertyFieldOffset(klass, "Refine"),
            Il2CppMeta.PropertyFieldOffset(klass, "Substats"),
            Il2CppMeta.PropertyFieldOffset(klass, "ChaosType"));
        _layouts[klass] = layout;
        return layout;
    }

    /// <summary>One item, spelled out — so a `probe` reply describes a real item, not a row of numbers.</summary>
    public static string Describe(LootFilter.ItemFacts facts)
    {
        var parts = new System.Text.StringBuilder();
        parts.Append(facts.Name.Length > 0 ? facts.Name : "(unnamed)");
        if (facts.Id.Length > 0) parts.Append(" [").Append(facts.Id).Append(']');
        if (facts.Type.Length > 0) parts.Append(' ').Append(facts.Type);
        if (facts.Refine > 0) parts.Append(" +").Append(facts.Refine);
        if (facts.Favorite) parts.Append(" fav");
        if (facts.HasChaos) parts.Append(" chaos");
        for (int i = 0; i < facts.StatCount; i++)
        {
            parts.Append(i == 0 ? " — " : ", ")
                 .Append(facts.StatNames[i]).Append(' ').Append(facts.StatRolls[i]).Append('%');
            if (facts.StatTiers[i].Length > 0) parts.Append(" (").Append(facts.StatTiers[i]).Append(')');
        }
        return parts.ToString();
    }
}
