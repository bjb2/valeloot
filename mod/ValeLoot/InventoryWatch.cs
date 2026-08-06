using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ValeLoot;

/// <summary>
/// Watching the player's own inventory DATA so a loot sound fires when an item is picked up.
///
/// ## Why this is not the paint pass
///
/// The sound used to be fed by the inventory repaint: the paint pass reported the uids it lit, and a
/// uid nobody had seen before was called an arrival. That is a proxy for "picked up", and it is wrong
/// in three ways. Loot taken with the bag CLOSED made no noise until the panel was next opened. Paging
/// into a part of the bag not yet looked at this session pinged for items that were not arrivals at
/// all, because the game binds one page of cells at a time and first-sighting is the most a UI can
/// report. And the whole thing was coupled to a rendering path a beep has no business depending on.
///
/// So the diff moved to the data. `PlayerSave.Data` is the live `CharacterData`, its `Inventory` is an
/// `InventoryData`, and that holds `Equips` and `Artifacts` as `Dictionary&lt;string, T&gt;` keyed by
/// item uid. Those two dictionaries are the player's loot whether or not a single cell is drawn.
///
/// ## Main thread, and nowhere else
///
/// Every read below happens on the tick <see cref="EditorServer"/> already owns — one detour on
/// `PlayerSave.Update`, a Unity message the engine dispatches per frame. There is no second hook on
/// that method and no il2cpp read off the main thread; **touching il2cpp objects from another thread
/// is how this project crashed the game once already**.
///
/// ## What one frame costs
///
/// A FRAME counter, not a clock: this runs inside a per-frame engine callback, so counting the ticks
/// it is already receiving is one increment and one compare, where `DateTime.UtcNow` would be a
/// syscall 60 times a second to answer a question about a beep. Every <see cref="TickInterval"/>th
/// frame is roughly four checks a second at 60fps — indistinguishable to a player from every frame,
/// and fifteen times cheaper. It also degrades in the right direction: on a machine dropping to 20fps
/// the work drops with it, at exactly the moment there is least to spare.
///
/// On the frames that do run, the FIRST thing is a count comparison, not a walk. A `Dictionary`'s
/// live count is `_count - _freeCount`, both plain fields, so "nothing happened" — which is nearly
/// every check — costs four integer reads and returns. Walking only happens when the bag's size
/// actually moved.
///
/// The one thing a count misses is an item REPLACED by a different item inside the same quarter
/// second: sell one, loot one, count unchanged, no walk. That is accepted rather than defended
/// against. The uid is not lost, only late — the next change of size walks, finds it absent from the
/// baseline, and pings then. Paying for a full walk four times a second forever to make a rare case
/// punctual is the wrong trade for a chime.
///
/// ## Priming, and what re-primes
///
/// The first successful observation fills the baseline SILENTLY. The bag you already own is not an
/// arrival, and a differ that treats its first snapshot as news is a mistake this project has already
/// written down (`knowledge/system/the-first-observation-is-not-a-delta.md`, after one treated its
/// first snapshot as 124 new items). The baseline is dropped and re-primed whenever the observed
/// `CharacterData` or `InventoryData` pointer changes identity, and whenever there is no character at
/// all — which is login, logout and character select, without needing to hook any of them. Switching
/// characters must not ping the whole new bag.
///
/// A filter reload does NOT re-prime, and does not need to: the baseline is every uid you OWN, not
/// the uids some rule claimed, so a new rule cannot manufacture an arrival for a bag you have been
/// carrying all evening.
///
/// ## Removals, so a re-looted item pings again
///
/// Each walk REPLACES the baseline with exactly what is in the bag now, by filling a second set and
/// swapping the two buffers. A uid that left is forgotten, so looting the same item again is an
/// arrival again, and the set is bounded by the size of the bag rather than by the length of the
/// session. Two sets, no allocation per walk beyond the uid strings il2cpp hands back — and those are
/// only allocated on a walk, which only happens when something changed.
///
/// ## Judging
///
/// New items go through <see cref="InventoryPaint.Judge"/> — the same evaluator, on the same rule
/// list, that decides the colour. A rule with no `Sound` line makes no noise; a `Hide` block and an
/// always-hide override make no noise by definition, because they return no mark at all. The sound
/// and the colour cannot name different rules, because there is only one judge.
///
/// The facts come from <see cref="ItemReader.ReadData"/>, which has no cell to read the displayed
/// name off, so `Name`/`Type` conditions resolve through <see cref="ItemCatalog"/> instead. That
/// covers equipment; an artifact's display name is not in the catalog, so a `Name` line aimed at one
/// matches its `Id` here or not at all. Rolls, refine, favourite and chaos are identical either way.
/// </summary>
internal static class InventoryWatch
{
    /**
     * Frames between checks. 15 is a quarter of a second at 60fps.
     *
     * Small enough that the chime still lands while the loot beam is on screen, large enough that the
     * common case — two dictionary counts, unchanged — costs nothing worth measuring.
     */
    private const int TickInterval = 15;

    public static bool Installed { get; private set; }

    /// <summary>The offsets this resolved to, for the boot line and for `status`.</summary>
    public static string Summary { get; private set; } = "not installed";

    public static long Checks;
    public static long Walks;
    public static long Arrivals;
    public static long Primes;

    /// <summary>Whether a baseline exists yet. False means no character has been observed.</summary>
    public static bool Primed => _primed;
    /// <summary>How many uids the baseline holds — your whole bag, once primed.</summary>
    public static int Held => _baseline.Count;

    private static Action<string> _log = _ => { };

    private static int _saveData = -1;
    private static int _inventory = -1;
    private static int _equips = -1;
    private static int _artifacts = -1;

    /// <summary>`_count`/`_freeCount` per dictionary runtime class. Two entries, resolved by name.</summary>
    private static readonly Dictionary<IntPtr, (int Count, int Free)> _countFields = new();

    /// <summary>The uids in the bag as of the last walk. Swapped with `_scratch`, never rebuilt.</summary>
    private static HashSet<string> _baseline = new(StringComparer.Ordinal);
    private static int _uidField = -1;
    private static HashSet<string> _scratch = new(StringComparer.Ordinal);

    private static readonly List<(string Uid, string Sound)> _pickups = new();
    private static readonly LootFilter.ItemFacts _facts = new();

    /**
     * The character's UID, not the address of its `CharacterData`.
     *
     * This started as a pointer-identity check, which ate EVERY arrival: the server reports a mutation
     * by shipping a whole fresh `CharacterData`, so the object changes address on every pickup, and
     * "the object changed, so this must be a different character" wiped the baseline four times a
     * second. Symptom: `primed` true, `held` steady at your bag size, `arrivals` stuck at 0 forever.
     * The uid is the only identity that survives an update — see
     * knowledge/spiritvale/the-first-observation-is-not-a-delta.md, which this project wrote about the
     * same fresh-snapshot behaviour on the wire.
     */
    private static string _characterUid = "";
    private static bool _primed;
    private static int _lastCount = -1;
    private static int _frames;
    private static bool _countWarned;

    /**
     * Resolve the data path by NAME, and say plainly what it cost if it did not resolve.
     *
     * Every hop is a `{ get; set; }` on one of the game's data classes, so every lookup goes through
     * `PropertyFieldOffset` — asking for "Inventory" would find nothing and report -1, which reads
     * exactly like a rename. The offsets go in the log on purpose: on a future game build a player's
     * pasted boot line is the only evidence of WHICH hop moved.
     */
    public static bool Install(Action<string> log)
    {
        _log = log;

        IntPtr playerSave = Il2CppMeta.FindClass("", "PlayerSave", HookCensus.GameAssemblies);
        IntPtr characterData = Il2CppMeta.FindClass("", "CharacterData", HookCensus.GameAssemblies);
        IntPtr inventoryData = Il2CppMeta.FindClass("", "InventoryData", HookCensus.GameAssemblies);

        _saveData = Il2CppMeta.PropertyFieldOffset(playerSave, "Data");
        _inventory = Il2CppMeta.PropertyFieldOffset(characterData, "Inventory");
        _equips = Il2CppMeta.PropertyFieldOffset(inventoryData, "Equips");
        _artifacts = Il2CppMeta.PropertyFieldOffset(inventoryData, "Artifacts");
        // Not required for the feature: without it a character SWITCH would replay the new bag once.
        // That is a bad minute, where treating an ordinary update as a switch was a dead feature.
        _uidField = Il2CppMeta.PropertyFieldOffset(characterData, "UID");

        Summary = $"PlayerSave.Data {Hex(_saveData)}, CharacterData.Inventory {Hex(_inventory)}, "
                + $"InventoryData.Equips {Hex(_equips)}, InventoryData.Artifacts {Hex(_artifacts)}, "
                + $"CharacterData.UID {Hex(_uidField)}";

        Installed = _saveData >= 0 && _inventory >= 0 && _equips >= 0 && _artifacts >= 0;
        if (Installed)
        {
            log($"pickup watch ready ({Summary}) — loot sounds fire when an item lands in your bag, "
              + "open or closed, checked 4x a second off the frame tick.");
        }
        else
        {
            // Said as a consequence, not as a status. There is deliberately no fallback to the old
            // paint-driven ping: a sound on a repaint is not the sound this is for, and quietly
            // reverting to it would leave the player believing they have pickup sounds when they have
            // first-sighting sounds instead.
            log($"pickup watch NOT ready ({Summary}) — the inventory data path did not resolve, so NO "
              + "loot sound will play this session. It does not fall back to pinging on a repaint. "
              + "Highlighting, the hover note and the editor are unaffected.");
        }
        return Installed;
    }

    public static void Uninstall()
    {
        Installed = false;
        Forget();
        _countFields.Clear();
        _log = _ => { };
    }

    /**
     * One frame, from `PlayerSave.Update`. `playerSave` is the hook's `self`.
     *
     * Guarded by the caller, which disarms its whole tick on the first exception — an exception
     * escaping into native code is a crash, not a stack trace.
     */
    public static void Tick(IntPtr playerSave)
    {
        if (!Installed || playerSave == IntPtr.Zero) return;
        if (++_frames < TickInterval) return;
        _frames = 0;
        Checks++;

        IntPtr character = Marshal.ReadIntPtr(playerSave, _saveData);
        IntPtr inventory = character == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(character, _inventory);
        if (inventory == IntPtr.Zero)
        {
            // No character: the character-select screen, or a logout. The next login primes again.
            Forget();
            return;
        }

        /**
         * A character SWITCH must re-prime; a character UPDATE must not.
         *
         * Comparing object addresses cannot tell those apart in this game, because an update replaces
         * the whole `CharacterData`. So compare the uid, which changes only when you actually log into
         * a different character. A missing uid is treated as "same as before" rather than as a switch:
         * failing to read one field must not be able to silence the feature.
         */
        string uid = _characterUid;
        if (_uidField >= 0)
        {
            string? read = Il2CppMeta.ReadStringField(character, _uidField);
            if (!string.IsNullOrEmpty(read)) uid = read!;
        }
        if (!string.Equals(uid, _characterUid, StringComparison.Ordinal))
        {
            Forget();
            _characterUid = uid;
        }

        IntPtr equips = Marshal.ReadIntPtr(inventory, _equips);
        IntPtr artifacts = Marshal.ReadIntPtr(inventory, _artifacts);
        // "First SUCCESSFUL observation": priming against a half-built inventory would call the real
        // contents an arrival the moment the other dictionary appeared.
        if (equips == IntPtr.Zero || artifacts == IntPtr.Zero) { Forget(); return; }

        int equipCount = LiveCount(equips);
        int artifactCount = LiveCount(artifacts);
        if (equipCount < 0 || artifactCount < 0)
        {
            // The cheap check is an optimisation, not the feature. If `Dictionary`'s own fields ever
            // move, walk every check instead of going silent — four walks a second is affordable, and
            // a sound that stopped working without a word is not.
            if (!_countWarned)
            {
                _countWarned = true;
                _log("pickup watch: Dictionary._count/_freeCount did not resolve, so the bag is walked on "
                   + "every check instead of only when its size changes. Sounds still work; this costs a "
                   + "little more per frame.");
            }
        }
        else
        {
            int total = equipCount + artifactCount;
            if (_primed && total == _lastCount) return;
            _lastCount = total;
        }

        Walk(equips, artifacts);
    }

    /// <summary>What it is doing, for the log and for a `status` reply.</summary>
    public static string Status()
        => Installed
         ? $"pickup watch: {Summary}, {(_primed ? "primed" : "waiting for a character")}, "
         + $"{_baseline.Count} uid(s) held, {Checks} check(s), {Walks} walk(s), {Arrivals} pickup(s)"
         : $"pickup watch: NOT installed ({Summary}) — no loot sound can play this session";

    /**
     * Rebuild the baseline from the two dictionaries, and announce what is new.
     *
     * The rules are reloaded HERE as well as at the top of a paint pass, because with the bag shut
     * this is the only thing reading them: a player who saves a rule in the editor and then goes
     * killing things would otherwise be judged by the rules they had at boot. It is one volatile flag
     * read on a walk that already happened because something changed.
     */
    private static void Walk(IntPtr equips, IntPtr artifacts)
    {
        Walks++;
        _scratch.Clear();
        _pickups.Clear();

        FilterFile.ReloadIfChanged();
        FilterParser.ParsedFilter filter = FilterFile.Current;

        Collect(equips, filter);
        Collect(artifacts, filter);

        // Swap rather than rebuild: `_baseline` becomes exactly the bag as it is now, so a uid that
        // left is forgotten and looting it again is an arrival again. The old set is next walk's
        // scratch, cleared at the top — bounded by the bag, never by the session.
        (_baseline, _scratch) = (_scratch, _baseline);

        if (!_primed)
        {
            _primed = true;
            Primes++;
            return;
        }

        if (_pickups.Count == 0) return;
        Arrivals += _pickups.Count;
        LootSound.Arrivals(_pickups);
    }

    private static void Collect(IntPtr dictionary, FilterParser.ParsedFilter filter)
    {
        foreach ((IntPtr key, IntPtr value) in Il2CppMeta.DictionaryEntries(dictionary))
        {
            // The KEY is the uid and the VALUE is the item data — the same shape the paint pass walks
            // for `InventoryItemsUID`, with data on the other side instead of a cell.
            string? uid = Il2CppMeta.ReadString(key);
            if (uid is null || uid.Length == 0) continue;
            if (!_scratch.Add(uid)) continue;

            // On the priming walk nothing is judged at all: the whole bag is the baseline, and reading
            // and evaluating every item to throw the verdict away would be the most expensive frame of
            // the session for no output.
            if (!_primed || _baseline.Contains(uid)) continue;
            if (!ItemReader.ReadData(value, _facts)) continue;

            InventoryPaint.Mark mark = InventoryPaint.Judge(_facts, filter);
            if (mark.Sound is not null) _pickups.Add((uid, mark.Sound));
        }
    }

    /**
     * A `Dictionary`'s live count, or -1 when its fields did not resolve.
     *
     * `_count` alone is the high-water mark of used entry slots and does NOT move when an add reuses
     * a slot a removal freed — which is precisely the pickup-after-a-sale case. `_count - _freeCount`
     * is what `Dictionary.Count` returns, and it moves on every add and every remove.
     *
     * Both offsets are resolved by name, per dictionary runtime class: `Dictionary&lt;string,
     * EquipData&gt;` and `Dictionary&lt;string, ArtifactData&gt;` are different instantiations with
     * different classes, and generic layout exists only per instantiation. Two cache entries.
     */
    private static int LiveCount(IntPtr dictionary)
    {
        IntPtr klass = Il2CppMeta.ClassOf(dictionary);
        if (klass == IntPtr.Zero) return -1;
        if (!_countFields.TryGetValue(klass, out (int Count, int Free) fields))
        {
            fields = (Il2CppMeta.FieldOffset(klass, "_count"), Il2CppMeta.FieldOffset(klass, "_freeCount"));
            _countFields[klass] = fields;
        }
        if (fields.Count < 0 || fields.Free < 0) return -1;
        return Marshal.ReadInt32(dictionary, fields.Count) - Marshal.ReadInt32(dictionary, fields.Free);
    }

    /// <summary>Drop the baseline. The next successful observation primes it again, silently.</summary>
    private static void Forget()
    {
        _baseline.Clear();
        _scratch.Clear();
        _primed = false;
        _lastCount = -1;
        // The uid deliberately SURVIVES: `_primed` going false is what makes the next observation a
        // silent prime, so a logout and a re-login to the same character stays quiet without needing to
        // forget who it was. Clearing it here would make every ordinary Forget look like a switch.
    }

    private static string Hex(int offset) => offset < 0 ? "MISSING" : $"0x{offset:x}";
}
