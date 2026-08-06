using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ValeLoot;

/// <summary>
/// The game's own item catalog, read live out of the process.
///
/// The mod needs reference data for two things: the ABSOLUTE stat form in the rule language
/// (`Stat Agi &gt;= 3` — the "+3" the game prints, which needs that item's base cap), and
/// discoverability (a player should not have to guess how an item or a stat is spelled). Both are
/// answered from `App.ServerRuntime`, which is the client's copy of the whole config database, keyed
/// by id, with every field named.
///
/// ## Why nothing is shipped alongside the DLL
///
/// The obvious move is to extract a catalog from the binary and ship it as JSON. That file is wrong
/// the morning after a content patch and right only until the next one. The in-process read is
/// correct by construction on every build, including builds that do not exist yet — and it costs a
/// dictionary walk once per session.
///
/// ## `ServerRuntime`, not `ClientRuntime`, and it is null at boot
///
/// The field says server and the client fills it anyway: IL2CPP ships one image for both roles, and
/// the config database is one of the things the client genuinely loads. Reading the name and picking
/// `ClientRuntime` gets you a null.
///
/// It is ALSO null when BepInEx runs `Load()`, because the client has not loaded its configs yet. So
/// resolution here is LAZY: <see cref="Install"/> only binds metadata, and the first lookup that
/// actually needs data retries the static read. Treating the boot-time null as failure would disable
/// the feature permanently for every player, which is the trap this design exists to avoid.
///
/// ## Never "true" when it cannot answer
///
/// A condition this file cannot answer is reported as unanswerable and the caller must decline the
/// match. Answering "yes" to an unknown widens the block it was in, the whole bag lights up
/// uniformly, and the filter looks broken rather than misread — the failure this project keeps
/// re-learning.
/// </summary>
internal static class ItemCatalog
{
    /// <summary>The generated reference a player greps to spell an item or a stat correctly.</summary>
    public const string ReferenceFileName = "valeloot-items.txt";

    /// <summary>How the reference file records what it was generated from, so a rewrite is cheap to decide.</summary>
    private const string CountMarker = "# equips: ";

    /// <summary>
    /// A cap the game refused to give a range for — this stat is not in this item's substat pool.
    /// A real cap may be zero or negative (`GetSubstatRange` carries the sign explicitly), so
    /// "absent" needs a value no cap can take rather than a falsy one.
    /// </summary>
    private const int NoCap = int.MinValue;

    /// <summary>
    /// How many failed lookups pass before the static read is retried.
    ///
    /// A paint pass asks per cell per stat per rule. On a session where the configs never load — a
    /// game build that renamed `App`, say — an unthrottled retry pays a P/Invoke every one of those,
    /// in the one code path that must not stutter. One integer increment is the cost of a miss
    /// instead; 64 passes is far below a human's notice of the transition.
    /// </summary>
    private const int RetryEvery = 64;

    // Static il2cpp methods take (args..., MethodInfo*) — no `this`. `GetSubstatRange` returns a
    // managed bool, which is one byte on the wire, so it is taken as a byte rather than letting the
    // marshaller pick the 4-byte Win32 BOOL.
    private delegate IntPtr GetSubstatConfigFn(IntPtr config, IntPtr methodInfo);
    private unsafe delegate byte GetSubstatRangeFn(int statType, IntPtr substatConfig, int* min, int* max, IntPtr methodInfo);

    /// <summary>One equip, as the game's own config describes it.</summary>
    internal sealed class Entry
    {
        public string Id = "";
        public string DisplayName = "";
        /// <summary>`EquipType`'s member name, resolved live — never a hardcoded ordinal table.</summary>
        public string TypeName = "";
        public string Set = "";
        public int LevelRequired;

        /// <summary>
        /// The live `EquipConfig`. Held across the session deliberately: the runtime's `Equips`
        /// dictionary keeps it reachable, and il2cpp's collector does not move objects, so the
        /// pointer stays valid as long as the catalog it came from does.
        /// </summary>
        public IntPtr Config;

        /**
         * Stat ordinal -> base cap, filled on demand and never evicted.
         *
         * This cache is the reason the value form is usable at all. A repaint judges every visible
         * cell against every rule, so an uncached `GetSubstatConfig` + `GetSubstatRange` pair per
         * cell per stat per pass is two native calls in the hot path, several hundred times a
         * redraw. Caps do not change while the process lives, so the second answer is free.
         */
        public readonly Dictionary<int, int> Caps = new();
    }

    /// <summary>Metadata bound. False means the catalog can never resolve this session.</summary>
    public static bool Installed { get; private set; }

    /// <summary>Configs read and indexed. False means every catalog-backed condition must decline.</summary>
    public static bool Ready { get; private set; }

    /// <summary>Equips the catalog holds. Zero until <see cref="Ready"/>.</summary>
    public static int Count { get; private set; }

    public static string Summary { get; private set; } = "not installed";

    /// <summary>Every equip the catalog holds — the reference file's source, and nothing else's.</summary>
    public static IReadOnlyCollection<Entry> All => _entries.Values;

    private static Action<string> _log = _ => { };
    private static string _configDirectory = "";

    private static IntPtr _appClass;
    private static int _equipsOffset = -1;
    private static int _substatsMapOffset = -1;
    private static int _idOffset = -1;
    private static int _displayNameOffset = -1;
    private static int _typeOffset = -1;
    private static int _setOffset = -1;
    private static int _levelOffset = -1;
    private static int _substatValuesOffset = -1;

    private static GetSubstatConfigFn? _getSubstatConfig;
    private static GetSubstatRangeFn? _getSubstatRange;

    /// <summary>`EquipType` ordinal -> member name, read from live metadata at boot. Never hardcoded.</summary>
    private static readonly Dictionary<int, string> _equipTypeNames = new();

    private static readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private static int _misses;
    private static bool _saidUnavailable;
    private static bool _saidValueFormDead;

    /**
     * Bind the metadata. True when the catalog CAN resolve, not that it has.
     *
     * `configDirectory` is where the generated reference file lands — the same directory the filter
     * file lives in, so the thing a player greps is next to the thing they edit.
     */
    public static bool Install(string configDirectory, Action<string> log)
    {
        _log = log;
        _configDirectory = configDirectory;

        _appClass = Il2CppMeta.FindClass("", "App", HookCensus.GameAssemblies);
        IntPtr runtimeClass = Il2CppMeta.FindClass("", "GameServerRuntime", HookCensus.GameAssemblies);
        IntPtr equipConfig = Il2CppMeta.FindClass("", "EquipConfig", HookCensus.GameAssemblies);
        IntPtr substatRuntime = Il2CppMeta.FindClass("", "EquipSubstatRuntime", HookCensus.GameAssemblies);
        IntPtr equipType = Il2CppMeta.FindClass("", "EquipType", HookCensus.GameAssemblies);
        IntPtr formula = Il2CppMeta.FindClass("", "Formula", HookCensus.GameAssemblies);

        _equipsOffset = Il2CppMeta.FieldOffset(runtimeClass, "Equips");
        // Resolved for the log line only. Nothing indexes it: `Formula.GetSubstatConfig` does that
        // lookup, and it also derives the default pool for an equip whose `Substats` is empty. It is
        // still worth reporting, because if it ever reads missing, the game moved the whole database.
        _substatsMapOffset = Il2CppMeta.FieldOffset(runtimeClass, "EquipSubstats");

        // `Id` and `DisplayName` are declared on the BASE (`BaseConfig`), four classes up from
        // `EquipConfig`, so the walk-up form is not optional here. They are plain serialised fields
        // rather than auto-properties, which is why this is not the backing-field lookup the data
        // classes need.
        _idOffset = Il2CppMeta.FieldOffsetUp(equipConfig, "Id");
        _displayNameOffset = Il2CppMeta.FieldOffsetUp(equipConfig, "DisplayName");
        _typeOffset = Il2CppMeta.FieldOffsetUp(equipConfig, "Type");
        _setOffset = Il2CppMeta.FieldOffsetUp(equipConfig, "Set");
        _levelOffset = Il2CppMeta.FieldOffsetUp(equipConfig, "LevelRequired");
        _substatValuesOffset = Il2CppMeta.FieldOffsetUp(substatRuntime, "Values");

        foreach ((string name, int value) in Il2CppMeta.EnumValues(equipType))
        {
            // Later duplicates lose: an alias member should not rename the canonical one.
            if (!_equipTypeNames.ContainsKey(value)) _equipTypeNames[value] = name;
        }

        BindFormula(formula);

        int appField = _appClass == IntPtr.Zero ? -1 : Il2CppMeta.FieldOffset(_appClass, "ServerRuntime");
        Installed = appField >= 0 && _equipsOffset >= 0 && _displayNameOffset >= 0;

        // The offsets go in the log line on purpose. On a future game build that breaks this, a
        // player's pasted log is the only evidence of WHAT moved, and one word per field says it.
        Summary = $"App.ServerRuntime {Hex(appField)}, Equips {Hex(_equipsOffset)}, EquipSubstats {Hex(_substatsMapOffset)}, "
                + $"Id {Hex(_idOffset)}, DisplayName {Hex(_displayNameOffset)}, Type {Hex(_typeOffset)}, "
                + $"Set {Hex(_setOffset)}, LevelRequired {Hex(_levelOffset)}, Values {Hex(_substatValuesOffset)}, "
                + $"{_equipTypeNames.Count} equip types, ranges {(RangesReadable ? "readable" : "UNREADABLE")}";

        if (!Installed)
        {
            _log($"item catalog NOT ready ({Summary}) — `Stat <name> >= <value>` rules cannot be answered this session");
            return false;
        }

        // Deliberately not resolved here: the client has not loaded its configs when a plugin loads,
        // so the static is null and would look exactly like a rename. The first lookup retries.
        _log($"item catalog bound, waiting for the client to load configs ({Summary})");
        return true;
    }

    /// <summary>Both `Formula` helpers bound. False leaves names and types working and caps not.</summary>
    private static bool RangesReadable => _getSubstatConfig is not null && _getSubstatRange is not null;

    private static void BindFormula(IntPtr formula)
    {
        if (formula == IntPtr.Zero) return;

        // Resolved by parameter TYPE, never by arity — the lesson that cost this project a game
        // process. `GetSubstatRange`'s two `out int`s are byref, which il2cpp spells with a trailing
        // `&`; the fallback matches on the two REFERENCE parameters plus arity, so a runtime that
        // spells byref differently loses the belt but keeps the braces.
        Il2CppMeta.MethodInfo? config = Il2CppMeta.FindOverload(formula, "GetSubstatConfig", "EquipConfig");
        Il2CppMeta.MethodInfo? range =
            Il2CppMeta.FindOverload(formula, "GetSubstatRange", "StatType", "EquipSubstatRuntime", "System.Int32&", "System.Int32&")
            ?? Il2CppMeta.FindMethod(formula, "GetSubstatRange", m =>
                m.ParamCount == 4 && m.ParamTypeNames[0] == "StatType" && m.ParamTypeNames[1] == "EquipSubstatRuntime");

        if (config is null || config.NativePtr == IntPtr.Zero || range is null || range.NativePtr == IntPtr.Zero) return;
        _getSubstatConfig = Marshal.GetDelegateForFunctionPointer<GetSubstatConfigFn>(config.NativePtr);
        _getSubstatRange = Marshal.GetDelegateForFunctionPointer<GetSubstatRangeFn>(range.NativePtr);
    }

    public static void Uninstall()
    {
        Installed = false;
        Ready = false;
        Count = 0;
        _entries.Clear();
        _equipTypeNames.Clear();
        _getSubstatConfig = null;
        _getSubstatRange = null;
        // The once-flags reset with everything else: a reloaded plugin has to be able to say
        // "catalog ready" again, or its log goes quiet about the only transition that matters.
        _misses = 0;
        _saidUnavailable = false;
        _saidValueFormDead = false;
    }

    /// <summary>An offset for the log — "missing" rather than 0xffffffff, which reads like an address.</summary>
    private static string Hex(int offset) => offset < 0 ? "missing" : $"0x{offset:x}";

    /// <summary>What this is doing, for the log — the counters that separate "bound" from "answering".</summary>
    public static string Status()
        => $"item catalog: installed {Installed}, ready {Ready}, {Count} equips, "
         + $"{_equipTypeNames.Count} equip types, caps {(RangesReadable ? "readable" : "UNREADABLE")}";

    /// <summary>The catalog's name for an item, or null when it does not know it (or is not ready).</summary>
    public static string? DisplayName(string itemId) => Lookup(itemId)?.DisplayName;

    /// <summary>`EquipType`'s member name for an item, or null when unknown (or not ready).</summary>
    public static string? TypeName(string itemId) => Lookup(itemId)?.TypeName;

    /**
     * The value the game PRINTS for one substat line, from its roll percentage.
     *
     * False means "cannot judge" — the catalog is not ready, the item is not an equip the catalog
     * knows, or that stat is not in the item's substat pool. The caller MUST decline the match on
     * false. It must never read as zero: zero is a real printed value and a filter that treats an
     * unanswerable condition as a satisfied one lights up the whole bag.
     */
    public static bool TryScaledValue(string itemId, int statType, int rollPct, out int value)
    {
        value = 0;
        if (!EnsureCatalog())
        {
            WarnValueFormOnce();
            return false;
        }
        if (!RangesReadable)
        {
            WarnValueFormOnce();
            return false;
        }
        if (string.IsNullOrEmpty(itemId) || !_entries.TryGetValue(itemId, out Entry? entry)) return false;

        int cap = CapFor(entry, statType);
        if (cap == NoCap) return false;

        value = ScaledValue(cap, rollPct);
        return true;
    }

    /**
     * A roll percentage and a base cap -> the number the game prints. Pure, and separate so it can
     * be checked against the range endpoints `Formula.GetSubstatRange` computes independently.
     *
     * Mirrors `Formula.GetSubstatScaledValue` — seven instructions, no state, so calling into the
     * game for it would cost a P/Invoke to save nothing. The constants are the game's own
     * single-precision literals rather than 1/3 and 2/3, and the multiply is in float, because the
     * game does it that way and a "more correct" double here would disagree with the number on the
     * player's screen at the boundary.
     *
     * Rounding is away from zero, which is what the game's `Round` extension asks `Math.Round` for
     * (digits 0, MidpointRounding.AwayFromZero) — not the .NET default.
     *
     * See knowledge/spiritvale/substat-value-is-the-roll-not-the-stat.md: `StatData.Value` is the
     * ROLL PERCENT, 0..100, and this is the only way back to the printed number.
     */
    internal static int ScaledValue(int cap, int rollPct)
    {
        float scaled = rollPct / 100f * 0.333333f + 0.666667f;
        return (int)Math.Round((double)(cap * scaled), MidpointRounding.AwayFromZero);
    }

    private static Entry? Lookup(string itemId)
    {
        if (string.IsNullOrEmpty(itemId) || !EnsureCatalog()) return null;
        return _entries.TryGetValue(itemId, out Entry? entry) ? entry : null;
    }

    private static int CapFor(Entry entry, int statType)
    {
        if (entry.Caps.TryGetValue(statType, out int cached)) return cached;

        int cap = NoCap;
        if (entry.Config != IntPtr.Zero && _getSubstatConfig is not null && _getSubstatRange is not null)
        {
            // `GetSubstatConfig` also derives the default pool when `EquipConfig.Substats` is empty,
            // which is why it is worth calling instead of indexing `EquipSubstats` ourselves.
            IntPtr substatConfig = _getSubstatConfig(entry.Config, IntPtr.Zero);

            // `GetSubstatRange` tolerates a null config but dereferences its `Values` dictionary
            // unconditionally once it has one. A managed null-reference thrown inside a raw call
            // like this one does not come back as a catchable exception, so the guard is the whole
            // defence — and the offset it reads through was resolved by name at install.
            bool valuesPresent = substatConfig != IntPtr.Zero
                              && _substatValuesOffset >= 0
                              && Marshal.ReadIntPtr(substatConfig, _substatValuesOffset) != IntPtr.Zero;
            if (valuesPresent)
            {
                unsafe
                {
                    int min = 0;
                    int max = 0;
                    if (_getSubstatRange(statType, substatConfig, &min, &max, IntPtr.Zero) != 0) cap = max;
                }
            }
        }

        // The refusal is cached too. A rule naming a stat no item in the bag can roll would
        // otherwise pay both native calls on every cell of every repaint, forever.
        entry.Caps[statType] = cap;
        return cap;
    }

    /**
     * Resolve on first use, then never again.
     *
     * `App.ServerRuntime` is populated when the client loads its configs, which happens after the
     * plugin loads. A null here is a "not yet", not a failure, and the transition is reported once
     * in each direction — a paint pass runs on every inventory redraw, and a line per pass makes the
     * one file a player can paste useless.
     */
    private static bool EnsureCatalog()
    {
        if (Ready) return true;
        if (!Installed) return false;
        if (++_misses % RetryEvery != 1) return false;

        IntPtr runtime = Il2CppMeta.StaticObjectField(_appClass, "ServerRuntime");
        if (runtime == IntPtr.Zero)
        {
            ReportUnavailableOnce("App.ServerRuntime is null");
            return false;
        }

        IntPtr equips = _equipsOffset >= 0 ? Marshal.ReadIntPtr(runtime, _equipsOffset) : IntPtr.Zero;
        List<(IntPtr Key, IntPtr Value)> rows = Il2CppMeta.DictionaryEntries(equips);
        if (rows.Count == 0)
        {
            ReportUnavailableOnce("App.ServerRuntime.Equips is empty");
            return false;
        }

        foreach ((IntPtr key, IntPtr value) in rows)
        {
            if (value == IntPtr.Zero) continue;
            // The dictionary key is the id, and so is the config's own `Id`. The key is preferred
            // because it is what the game itself looks an item up by, and what `InventoryItemData.Id`
            // on a live cell will equal.
            string id = Il2CppMeta.ReadString(key) ?? "";
            if (id.Length == 0) id = Il2CppMeta.ReadStringField(value, _idOffset) ?? "";
            if (id.Length == 0) continue;

            int type = _typeOffset >= 0 ? Marshal.ReadInt32(value, _typeOffset) : int.MinValue;
            _entries[id] = new Entry
            {
                Id = id,
                DisplayName = Il2CppMeta.ReadStringField(value, _displayNameOffset) ?? "",
                TypeName = _equipTypeNames.TryGetValue(type, out string? typeName) ? typeName : "",
                Set = Il2CppMeta.ReadStringField(value, _setOffset) ?? "",
                LevelRequired = _levelOffset >= 0 ? Marshal.ReadInt32(value, _levelOffset) : 0,
                Config = value,
            };
        }

        Count = _entries.Count;
        Ready = Count > 0;
        if (!Ready)
        {
            ReportUnavailableOnce("no equip config in App.ServerRuntime.Equips could be read");
            return false;
        }

        _log($"catalog ready: {Count} equips");
        WriteReference();
        return true;
    }

    private static void ReportUnavailableOnce(string reason)
    {
        if (_saidUnavailable) return;
        _saidUnavailable = true;
        _log($"catalog unavailable: {reason} — retrying on use. Item names and types fall back to the "
           + "cell text, and `Stat <name> >= <value>` rules match nothing until it resolves.");
    }

    private static void WarnValueFormOnce()
    {
        if (_saidValueFormDead) return;
        _saidValueFormDead = true;
        _log("a rule asks for a printed stat value (`Stat <name> >= <number>`, no %) and the item catalog "
           + "is not available — that rule matches NOTHING rather than everything. Roll-quality rules "
           + "(`>= 90%`) are unaffected.");
    }

    /**
     * The generated reference file — the "easy to write" half of this feature.
     *
     * Rewritten only when the equip count changes, which in practice means "after a content patch".
     * A player may well have this file open in an editor; rewriting it on every launch to produce
     * identical bytes would reload it under them for nothing.
     */
    private static void WriteReference()
    {
        if (_configDirectory.Length == 0) return;
        string path = Path.Combine(_configDirectory, ReferenceFileName);
        try
        {
            if (ReferenceIsCurrent(path, Count)) return;
            Directory.CreateDirectory(_configDirectory);
            File.WriteAllText(path, BuildReference(_entries.Values, ItemReader.StatNames, Count),
                              new UTF8Encoding(false));
            _log($"wrote {ReferenceFileName} ({Count} equips) to {path}");
        }
        catch (Exception e)
        {
            // Not fatal to anything. Rules still work; the player just has nothing to grep.
            _log($"could not write {ReferenceFileName} — {e.Message}. Rules are unaffected.");
        }
    }

    /// <summary>True when the file already on disk was generated from this many equips.</summary>
    internal static bool ReferenceIsCurrent(string path, int count)
    {
        if (!File.Exists(path)) return false;
        foreach (string line in File.ReadLines(path))
        {
            // The marker sits in the header, so the scan stops at the first line that is not one.
            if (line.Length > 0 && line[0] != '#') return false;
            if (!line.StartsWith(CountMarker, StringComparison.Ordinal)) continue;
            return int.TryParse(line.Substring(CountMarker.Length).Trim(),
                                NumberStyles.Integer, CultureInfo.InvariantCulture, out int had)
                && had == count;
        }
        return false;
    }

    /// <summary>The whole reference file, as text. Pure: everything it reads is an argument.</summary>
    internal static string BuildReference(IReadOnlyCollection<Entry> source, IReadOnlyCollection<string> statNames, int count)
    {
        var entries = new List<Entry>(source);
        // Grouped by type, then by the level you can use it at, then by name: the order a player
        // scanning for "what swords are there around level 40" actually reads in.
        entries.Sort(static (a, b) =>
        {
            int byType = string.Compare(a.TypeName, b.TypeName, StringComparison.OrdinalIgnoreCase);
            if (byType != 0) return byType;
            if (a.LevelRequired != b.LevelRequired) return a.LevelRequired.CompareTo(b.LevelRequired);
            int byName = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.Compare(a.Id, b.Id, StringComparison.Ordinal);
        });

        var text = new StringBuilder(entries.Count * 80);
        text.Append("# ValeLoot item reference — GENERATED. Editing this file does NOTHING.\n")
            .Append("#\n")
            .Append("# ValeLoot writes this from the game's own catalog, in memory, every time the item count\n")
            .Append("# changes. It is here so you never have to guess a spelling. Your rules go in\n")
            .Append("# ").Append(FilterFile.FileName).Append(", next to this file.\n")
            .Append("#\n")
            .Append("# Anything in the Name column works as a rule line, quoted if it has spaces:\n")
            .Append("#\n")
            .Append("#     Name \"Vampiric Fang Clip\"      matches the name, the id, or the text on the cell\n")
            .Append("#     Type Dagger, Katar             the Type column below\n")
            .Append("#     Stat Agi >= 3                  the printed value, from the Stats section below\n")
            .Append("#     Stat Agi >= 90%                how WELL that line rolled — a different question\n")
            .Append("#\n")
            .Append(CountMarker).Append(count.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("# generated: ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append('\n')
            .Append('\n');

        string currentType = "\u0000";
        foreach (Entry entry in entries)
        {
            if (!string.Equals(entry.TypeName, currentType, StringComparison.Ordinal))
            {
                currentType = entry.TypeName;
                text.Append('\n')
                    .Append("== ").Append(currentType.Length > 0 ? currentType : "(no type)").Append(" ==\n")
                    .Append("  Lv   Name                                     Set                  id\n");
            }
            text.Append("  ")
                .Append(Pad(entry.LevelRequired.ToString(CultureInfo.InvariantCulture), 4))
                .Append(' ')
                .Append(Pad(entry.DisplayName.Length > 0 ? entry.DisplayName : "(unnamed)", 40))
                .Append(' ')
                .Append(Pad(entry.Set, 20))
                .Append(' ')
                .Append(entry.Id)
                .Append('\n');
        }

        text.Append('\n')
            .Append("\n== Stats ==\n")
            .Append("# Names a `Stat` line accepts, read live from the game's StatType enum.\n");
        var stats = new List<string>(statNames);
        stats.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string stat in stats) text.Append("  ").Append(stat).Append('\n');

        return text.ToString();
    }

    private static string Pad(string value, int width) => value.Length >= width ? value : value.PadRight(width);
}
