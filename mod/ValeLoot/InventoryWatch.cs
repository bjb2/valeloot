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
/// `InventoryData`, and that holds every bag the player owns — `Equips`, `Artifacts`, `Cards`, `Gems`,
/// `Consumables`, `Cosmetics` and `Junks` — as `Dictionary&lt;string, T&gt;`. Those dictionaries are
/// the player's loot whether or not a single cell is drawn.
///
/// ## Seven dictionaries, and why only one of them is required
///
/// `Equips` is the bag: without it there is no feature and the watcher says so. The other six are
/// looked up the same way and each is skipped, named, on a build that moved it — a rename of `Cards`
/// must not be able to take equipment sounds down with it. <see cref="Summary"/> lists every offset,
/// so a player's pasted boot line says exactly which dictionaries were watched.
///
/// ## Stacks, which is what makes cards, junk and consumables different
///
/// `Equips`, `Artifacts` and `Gems` are keyed by an item UID: one entry is one item, and a pickup is
/// a key nobody has seen. Cards, junk and consumables are `StackableItemData` — one entry per item
/// ID, with the copies counted in `Count` — so the second copy of a card you already own adds NO key
/// and changes NO dictionary size. A uid-set diff cannot see it, and a size comparison cannot either.
///
/// So the baseline holds a COUNT per key rather than a set of keys, an arrival is "absent, or more of
/// it than last time", and the cheap check below sums stack counts as well as dictionary sizes. The
/// stack field's offset is resolved by name off the first value each dictionary hands back, so a
/// dictionary of items that do not stack costs nothing and needs no list of which classes those are.
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
/// On the frames that do run, the FIRST thing is a size comparison, not a walk. A `Dictionary`'s live
/// count is `_count - _freeCount`, both plain fields, so "nothing happened" — which is nearly every
/// check — costs a handful of integer reads and returns. The one dictionary that stacks adds a
/// pointer sweep summing one `int` per entry: no allocation, no string reads, and it is what lets a
/// duplicate card ping at all. Walking, which reads a string per entry, only happens when one of
/// those numbers actually moved.
///
/// The one thing this misses is an item REPLACED by a different item inside the same quarter second:
/// sell one, loot one, both numbers unchanged, no walk. That is accepted rather than defended
/// against. The uid is not lost, only late — the next change walks, finds it absent from the
/// baseline, and pings then. Paying for a full walk four times a second forever to make a rare case
/// punctual is the wrong trade for a chime.
///
/// ## Priming, and what re-primes
///
/// The first successful observation fills the baseline SILENTLY. The bag you already own is not an
/// arrival, and a differ that treats its first snapshot as news is a mistake this project has already
/// written down (`knowledge/system/the-first-observation-is-not-a-delta.md`, after one treated its
/// first snapshot as 124 new items). The baseline is dropped and re-primed whenever the observed
/// character's uid changes, and whenever there is no character at all — which is login, logout and
/// character select, without needing to hook any of them. Switching characters must not ping the
/// whole new bag.
///
/// A filter reload does NOT re-prime, and does not need to: the baseline is every uid you OWN, not
/// the uids some rule claimed, so a new rule cannot manufacture an arrival for a bag you have been
/// carrying all evening.
///
/// ## Removals, so a re-looted item pings again
///
/// Each walk REPLACES the baseline with exactly what is in the bag now, by filling a second map and
/// swapping the two buffers. A key that left is forgotten, so looting the same item again is an
/// arrival again, and a card stack that shrank is an arrival again at its old size. The map is
/// bounded by the size of the bag rather than by the length of the session. Two maps, no allocation
/// per walk beyond the uid strings il2cpp hands back — and those are only allocated on a walk, which
/// only happens when something changed.
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
/// covers equipment, cards and gems, all three of which the catalog indexes from the game's own
/// configs; an artifact's display name is not in any of them, so a `Name` line aimed at one matches
/// its `Id` here or not at all. Rolls, refine, favourite and chaos are identical either way.
/// </summary>
internal static class InventoryWatch
{
    /**
     * Frames between checks. 15 is a quarter of a second at 60fps.
     *
     * Small enough that the chime still lands while the loot beam is on screen, large enough that the
     * common case — four dictionary sizes and one stack sum, unchanged — costs nothing worth
     * measuring.
     */
    private const int TickInterval = 15;

    /**
     * Consecutive checks with a live inventory and still no baseline, before this calls itself broken.
     *
     * 240 checks is about a minute of play at four a second. Long enough that no ordinary login,
     * zone change or character update can reach it; short enough that a player about to write "the
     * sounds do not work" already has the answer in their log.
     */
    private const int StuckAfterChecks = 240;

    /**
     * How long with NO live inventory from any `PlayerSave` before it counts as a logout.
     *
     * Milliseconds, not ticks, and that is the point: `Update` fires once per player near you, so a
     * tick count means something different in a town than it does in a field. Two seconds of nobody
     * holding an inventory is a logout or a character select; a zone change blanks it for a frame or
     * two and comes straight back with the same bag.
     */
    private const int LogoutAfterMs = 2000;

    /**
     * One bag dictionary on `InventoryData`.
     *
     * `Stack` is discovered rather than declared: the first value a dictionary hands back names its
     * own class, and `Count` either exists on it or does not. That is one lookup per dictionary per
     * session and no list of which item classes stack — a list that would go stale on the first
     * patch that makes gems stackable.
     */
    private struct Bag
    {
        public readonly string Field;
        /// <summary>Absence disables the whole watcher. True for `Equips` and nothing else.</summary>
        public readonly bool Required;
        public int Offset;
        /// <summary>`Count`'s offset on the value class: -1 for "does not stack, or not seen yet".</summary>
        public int Stack;

        public Bag(string field, bool required)
        {
            Field = field;
            Required = required;
            Offset = -1;
            Stack = -1;
        }
    }

    /**
     * The dictionaries watched, in walk order — every bag `InventoryData` holds.
     *
     * Walk order is what decides which sound wins when several things land at once — see
     * <see cref="LootSound.Arrivals"/>, which plays the first in the batch. Equipment first because
     * it is the loot a player is actually hunting; junk last because it is the loot they are not.
     *
     * Nothing is excluded by CATEGORY, and an earlier draft of this file did exclude junk and
     * consumables on the grounds that they arrive by the hundred. That was the wrong cut: the
     * FILTER decides what is worth a noise, and a bucket the watcher refuses to look at is a rule
     * the player cannot write. The lure called "Buzzing Hive Fragment" is a `ConsumableData` and it
     * is a boss summon, not a potion. What stops a metronome is a `Show` block with conditions on
     * it, which is the player's own choice and already true of equipment.
     */
    private static readonly Bag[] _bags =
    {
        new("Equips", true),
        new("Artifacts", false),
        new("Cards", false),
        new("Gems", false),
        new("Consumables", false),
        new("Cosmetics", false),
        new("Junks", false),
    };

    /// <summary>This tick's dictionary pointers, parallel to <see cref="_bags"/>. Reused, never grown.</summary>
    private static readonly IntPtr[] _live = new IntPtr[_bags.Length];

    public static bool Installed { get; private set; }

    /// <summary>The offsets this resolved to, for the boot line and for `status`.</summary>
    public static string Summary { get; private set; } = "not installed";

    public static long Checks;
    public static long Walks;
    public static long Arrivals;
    public static long Primes;


    /**
     * Why the baseline was dropped, counted per reason.
     *
     * Four different events all end in `Forget()`, and from outside they are indistinguishable — the
     * baseline is simply empty again. Working out which one is firing by reasoning about a 4 Hz tick
     * sampled over HTTP does not converge; counting them does, and it costs one increment on a path
     * that already decided to throw the bag away.
     */
    public static long ForgetNoCharacter;
    public static long ForgetSwitch;
    public static long ForgetMissingBag;
    public static long ForgetShapeChange;

    /// <summary>Ticks whose `PlayerSave` held no inventory — somebody else's character. In a town
    /// this dwarfs <see cref="Checks"/>, which is the whole reason a null one must mean nothing.</summary>
    public static long Strangers;
    /// <summary>Whether a baseline exists yet. False means no character has been observed.</summary>
    public static bool Primed => _primed;
    /// <summary>How many keys the baseline holds — your whole bag, once primed.</summary>
    public static int Held => _baseline.Count;

    private static Action<string> _log = _ => { };

    private static int _saveData = -1;
    private static int _inventory = -1;

    /// <summary>`_count`/`_freeCount` per dictionary runtime class — one entry per instantiation.</summary>
    private static readonly Dictionary<IntPtr, (int Count, int Free)> _countFields = new();

    /// <summary>Key -&gt; how many of it the bag held at the last walk. Swapped with `_scratch`.</summary>
    private static Dictionary<string, int> _baseline = new(StringComparer.Ordinal);
    private static Dictionary<string, int> _scratch = new(StringComparer.Ordinal);
    private static int _uidField = -1;

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
    /// <summary>Which bags existed at the last check. A change re-primes, so a dictionary the game
    /// allocates later arrives as baseline rather than as a bagful of pickups.</summary>
    private static int _present;
    /// <summary>Consecutive checks with an inventory present and nothing primed. Reset by priming.</summary>
    private static int _unprimedChecks;
    /// <summary>The stuck warning is said once per session, never repeated.</summary>
    private static bool _saidStuck;
    private static long _lastTotal = -1;
    private static int _frames;
    private static bool _countWarned;

    /// <summary>When a live inventory was last seen, from `TickCount64`. TIME, not ticks: the tick
    /// rate scales with how many players are standing near you, so a count would mean nothing.</summary>
    private static long _lastLive;

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
        // Not required for the feature: without it a character SWITCH would replay the new bag once.
        // That is a bad minute, where treating an ordinary update as a switch was a dead feature.
        _uidField = Il2CppMeta.PropertyFieldOffset(characterData, "UID");

        var offsets = new System.Text.StringBuilder();
        offsets.Append("PlayerSave.Data ").Append(Hex(_saveData))
               .Append(", CharacterData.Inventory ").Append(Hex(_inventory));

        bool required = true;
        var skipped = new List<string>();
        for (int i = 0; i < _bags.Length; i++)
        {
            _bags[i].Offset = Il2CppMeta.PropertyFieldOffset(inventoryData, _bags[i].Field);
            _bags[i].Stack = -1;
            offsets.Append(", InventoryData.").Append(_bags[i].Field).Append(' ').Append(Hex(_bags[i].Offset));
            if (_bags[i].Offset >= 0) continue;
            if (_bags[i].Required) required = false;
            else skipped.Add(_bags[i].Field);
        }
        offsets.Append(", CharacterData.UID ").Append(Hex(_uidField));
        Summary = offsets.ToString();

        Installed = _saveData >= 0 && _inventory >= 0 && required;
        if (!Installed)
        {
            // Said as a consequence, not as a status. There is deliberately no fallback to the old
            // paint-driven ping: a sound on a repaint is not the sound this is for, and quietly
            // reverting to it would leave the player believing they have pickup sounds when they have
            // first-sighting sounds instead.
            log($"pickup watch NOT ready ({Summary}) — the inventory data path did not resolve, so NO "
              + "loot sound will play this session. It does not fall back to pinging on a repaint. "
              + "Highlighting, the hover note and the editor are unaffected.");
            return false;
        }

        log($"pickup watch ready ({Summary}) — loot sounds fire when an item lands in your bag, "
          + "open or closed, checked 4x a second off the frame tick.");
        if (skipped.Count > 0)
        {
            // Named rather than silent: "why do my cards not ping" has to be answerable from the log.
            log($"pickup watch: InventoryData.{string.Join("/", skipped)} did not resolve on this game "
              + "build, so pickups of those make no sound. Everything else is watched as normal, and "
              + "they are still highlighted in the bag.");
        }
        return true;
    }

    public static void Uninstall()
    {
        Installed = false;
        Forget();
        _present = 0;
        _unprimedChecks = 0;
        _lastLive = 0;
        // Reset with everything else: a reloaded plugin has to be able to warn again, or the single
        // line that says the feature is dead is spent on a session that no longer exists.
        _saidStuck = false;
        _countFields.Clear();
        for (int i = 0; i < _bags.Length; i++) _bags[i].Stack = -1;
        _log = _ => { };
    }

    /**
     * One frame, from `PlayerSave.Update`. `playerSave` is the hook's `self`.
     *
     * ## There is one `PlayerSave` per player in range, and only one of them is yours
     *
     * `PlayerSave` is a networked behaviour, so the engine ticks `Update` on an instance for every
     * player near you. Exactly one of those — yours — has a `CharacterData` with an `Inventory`
     * behind it. Everyone else's reads null.
     *
     * This cost the feature an afternoon and a shipped release. The old code treated ANY null
     * inventory as "no character: character select, or a logout" and dropped the baseline, which is
     * right for the one instance that is yours and catastrophic for the eight that are not: standing
     * in a town, 97% of ticks were other people, so the bag was thrown away nine times for every
     * time it was read. Measured, once there were per-reason counters:
     * `checks:3783, noChar:3679, switch:1, missing:0, shape:1`, with `primes == walks` exactly —
     * every walk re-primed from nothing, so no arrival could ever be seen.
     *
     * It also scaled with POPULATION, which is why it looked haunted: the same build read
     * `primed:true, held:421` somewhere quiet an hour earlier, and the shared frame counter ran at
     * ~38 checks a second instead of 4 because every nearby player was driving it.
     *
     * ## Selected by what it HOLDS, not by which object it is
     *
     * The first fix latched the instance pointer — adopt the save that has an inventory, ignore the
     * rest. It does not work: the adopted pointer never matched again (`latches:1, strangers:4751,
     * checks:0`), so whatever the hook hands over is not a stable identity for the local player.
     * Rather than go looking for one, this stops needing it.
     *
     * A live inventory IS the identifier. Only your save has one, so a tick that produces one is
     * yours by construction, and a tick that does not is somebody else's and simply not our
     * business — no counter, no baseline, above all no `Forget`.
     *
     * That leaves "how do we notice a real logout", and the answer cannot be counted in ticks,
     * because the number of ticks is a function of how many people are standing near you. It is
     * measured in TIME: nobody has produced a live inventory for a couple of seconds. A zone change
     * blanks it for a frame or two, a logout does not come back.
     */
    public static void Tick(IntPtr playerSave)
    {
        if (!Installed || playerSave == IntPtr.Zero) return;

        IntPtr character = Marshal.ReadIntPtr(playerSave, _saveData);
        IntPtr inventory = character == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(character, _inventory);

        if (inventory == IntPtr.Zero)
        {
            Strangers++;
            // Somebody else, or the moment before ours exists. Only a sustained absence is a logout,
            // and `_primed` guards it so the character-select screen is not re-forgotten every frame.
            if (_primed && Environment.TickCount64 - _lastLive > LogoutAfterMs)
            {
                ForgetNoCharacter++;
                Forget();
            }
            return;
        }

        _lastLive = Environment.TickCount64;

        if (++_frames < TickInterval) return;
        _frames = 0;
        Checks++;


        /**
         * A feature that can die silently has to notice.
         *
         * `pickup watch ready` is logged once, at boot, which is the least informative moment there
         * is: there is no character yet, so the line reads the same whether the watcher is healthy or
         * about to be dead for the whole session. It then never speaks again. When a null
         * `InventoryData.Cosmetics` made every tick bail, everything that normally proves the plugin
         * is alive stayed green — the bag painted, `checks` ran to 39,821 — and the only tell was one
         * boolean nobody looks at.
         *
         * This is the state that is not ambiguous: the inventory pointer is LIVE (so there is a
         * character, and this is not the login screen) and yet no baseline has ever been taken. A
         * minute of that is not a slow start, it is a broken watcher.
         *
         * Said ONCE, and it names what it found rather than that something is wrong, because the
         * player pasting it is the only evidence of WHICH dictionary moved.
         */
        if (_primed)
        {
            _unprimedChecks = 0;
        }
        else if (++_unprimedChecks == StuckAfterChecks && !_saidStuck)
        {
            _saidStuck = true;
            WarnStuck(inventory);
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
            ForgetSwitch++;
            Forget();
            _characterUid = uid;
        }

        /**
         * A dictionary that is THERE but null is an EMPTY BAG, not a broken read.
         *
         * This cost the whole feature once. The rule used to be "every dictionary this build resolved
         * has to be present, or the inventory is half-built and priming against it would call the
         * real contents an arrival" — which is sound reasoning about `Equips` and wrong about the
         * rest: a character who owns no cosmetics has a null `Cosmetics`, permanently, and the guard
         * turned that into `Forget(); return;` on every tick for ever. Symptom, and the one to
         * recognise again: the bag paints normally, `bagRows` climbs, and `primed` is stuck false
         * with `held` at 0 while `checks` runs into the tens of thousands.
         *
         * So only the REQUIRED bag being null means "no inventory yet". Everything else contributes
         * nothing and is skipped.
         *
         * The original concern is kept by a different mechanism: `present` is a bitmask of which bags
         * exist this tick, and any change to it re-primes silently. A dictionary the game allocates
         * later — the first cosmetic you ever own — therefore lands as baseline rather than as a
         * bagful of arrivals, which is what the old guard was really protecting.
         */
        long total = 0;
        int present = 0;
        bool sizesKnown = true;
        for (int i = 0; i < _bags.Length; i++)
        {
            _live[i] = IntPtr.Zero;
            if (_bags[i].Offset < 0) continue;

            IntPtr dictionary = Marshal.ReadIntPtr(inventory, _bags[i].Offset);
            if (dictionary == IntPtr.Zero)
            {
                if (_bags[i].Required) { ForgetMissingBag++; Forget(); return; }
                continue;
            }
            _live[i] = dictionary;
            present |= 1 << i;

            if (!sizesKnown) continue;
            int size = LiveCount(dictionary);
            if (size < 0) { sizesKnown = false; continue; }
            // Both, because they answer different questions: the size moves when an item is added or
            // removed, the stack sum moves when a card you already own arrives again. Summing them
            // into one number loses nothing — this is a change detector, not a quantity.
            total += size + Il2CppMeta.SumValueInt32(dictionary, _bags[i].Stack);
        }

        if (present != _present)
        {
            _present = present;
            ForgetShapeChange++;
            Forget();
        }

        if (!sizesKnown)
        {
            // The cheap check is an optimisation, not the feature. If `Dictionary`'s own fields ever
            // move, walk every check instead of going silent — four walks a second is affordable, and
            // a sound that stopped working without a word is not.
            if (!_countWarned)
            {
                _countWarned = true;
                _log("pickup watch: Dictionary._count/_freeCount did not resolve, so the bag is walked on "
                   + "every check instead of only when it changes. Sounds still work; this costs a "
                   + "little more per frame.");
            }
        }
        else
        {
            if (_primed && total == _lastTotal) return;
            _lastTotal = total;
        }

        Walk();
    }

    /// <summary>What it is doing, for the log and for a `status` reply.</summary>
    public static string Status()
        => Installed
         ? $"pickup watch: {Summary}, {(_primed ? "primed" : "waiting for a character")}, "
         + $"{_baseline.Count} key(s) held, {Checks} check(s), {Walks} walk(s), {Arrivals} pickup(s), "
         // Included because it is the number that explains the other numbers: it is every tick that
         // belonged to somebody standing near you, and it is normally far larger than `Checks`.
         + $"{Strangers} tick(s) from other players' saves"
         : $"pickup watch: NOT installed ({Summary}) — no loot sound can play this session";

    /**
     * Rebuild the baseline from every watched dictionary, and announce what is new.
     *
     * The rules are reloaded HERE as well as at the top of a paint pass, because with the bag shut
     * this is the only thing reading them: a player who saves a rule in the editor and then goes
     * killing things would otherwise be judged by the rules they had at boot. It is one volatile flag
     * read on a walk that already happened because something changed.
     */
    private static void Walk()
    {
        Walks++;
        _scratch.Clear();
        _pickups.Clear();

        FilterFile.ReloadIfChanged();
        FilterParser.ParsedFilter filter = FilterFile.Current;

        for (int i = 0; i < _bags.Length; i++)
        {
            if (_live[i] != IntPtr.Zero) Collect(i, filter);
        }

        // Swap rather than rebuild: `_baseline` becomes exactly the bag as it is now, so a key that
        // left is forgotten and looting it again is an arrival again. The old map is next walk's
        // scratch, cleared at the top — bounded by the bag, never by the session.
        (_baseline, _scratch) = (_scratch, _baseline);

        /**
         * The bag file's membership, corrected against what is actually owned.
         *
         * `_baseline` is now exactly the bag, so this is the moment — and the only moment — anything
         * in this mod knows that an item is GONE. A repaint cannot tell a sold item from a page you
         * have not scrolled to, which is why the snapshot accumulated for ever and a player ended up
         * looking at seven hundred items he had banked.
         *
         * After the swap on purpose, so the set handed over is this walk's, not the last one's.
         */
        BagSnapshot.Retain(_baseline);

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

    private static void Collect(int bag, FilterParser.ParsedFilter filter)
    {
        foreach ((IntPtr key, IntPtr value) in Il2CppMeta.DictionaryEntries(_live[bag]))
        {
            // The KEY identifies the entry and the VALUE is the item data — the same shape the paint
            // pass walks for `InventoryItemsUID`, with data on the other side instead of a cell. For
            // everything but cards the key is the item's uid; for cards it is the card's id, and the
            // copies are counted in the value.
            string? uid = Il2CppMeta.ReadString(key);
            if (uid is null || uid.Length == 0) continue;

            if (_bags[bag].Stack < 0 && value != IntPtr.Zero)
            {
                // Resolved off a real value once per dictionary, and left at -1 for a class with no
                // `Count` — which is what makes the cheap sweep skip non-stacking dictionaries for
                // free rather than by name.
                _bags[bag].Stack = ItemReader.StackFieldOffset(Il2CppMeta.ClassOf(value));
            }

            int stack = ItemReader.StackCount(value);
            // A key seen twice in one walk keeps its first sighting. Two dictionaries cannot legally
            // share a key, so this is a torn read rather than a real duplicate, and the alternative
            // — adding the two together — would invent an arrival out of it.
            if (_scratch.ContainsKey(uid)) continue;
            _scratch[uid] = stack;

            // On the priming walk nothing is judged at all: the whole bag is the baseline, and reading
            // and evaluating every item to throw the verdict away would be the most expensive frame of
            // the session for no output.
            if (!_primed) continue;
            // Absent is zero held, so a new key and a grown stack are the same question. A stack that
            // SHRANK is not an arrival, and the swap below records the smaller number, so the copy
            // that replaces it later pings.
            _baseline.TryGetValue(uid, out int had);
            if (stack <= had) continue;

            if (!ItemReader.ReadData(value, _facts)) continue;

            InventoryPaint.Mark mark = InventoryPaint.Judge(_facts, filter);
            // One entry per key, whatever the stack gained: ten copies of a card landing at once is
            // still one thing that happened, and `LootSound` plays one sound per batch regardless.
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
     * EquipData&gt;` and `Dictionary&lt;string, CardData&gt;` are different instantiations with
     * different classes, and generic layout exists only per instantiation. One cache entry each.
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
        _lastTotal = -1;
        // The uid deliberately SURVIVES: `_primed` going false is what makes the next observation a
        // silent prime, so a logout and a re-login to the same character stays quiet without needing to
        // forget who it was. Clearing it here would make every ordinary Forget look like a switch.
    }

    /**
     * Say, once, that the watcher has a character and no bag — and say what it actually saw.
     *
     * Written as a CONSEQUENCE followed by evidence, because "pickup watch is stuck" tells a player
     * nothing they can act on or paste usefully. Each dictionary is re-read here rather than trusting
     * the cached mask, so the line describes this moment rather than some earlier one; it runs once
     * in a session, so a handful of pointer reads costs nothing.
     *
     * The three states a bag can be in are spelled differently on purpose:
     *   0x38 present  — resolved and allocated, fine
     *   0x40 NULL     — resolved, and the game has not allocated it. Normal for a kind you own none
     *                   of, and only fatal if it is the required one
     *   MISSING       — the field did not resolve at all, i.e. this game build renamed it
     */
    private static void WarnStuck(IntPtr inventory)
    {
        var found = new System.Text.StringBuilder();
        for (int i = 0; i < _bags.Length; i++)
        {
            if (found.Length > 0) found.Append(", ");
            found.Append(_bags[i].Field).Append(' ');
            if (_bags[i].Offset < 0) { found.Append("MISSING"); continue; }
            IntPtr dictionary = Marshal.ReadIntPtr(inventory, _bags[i].Offset);
            found.Append(Hex(_bags[i].Offset)).Append(dictionary == IntPtr.Zero ? " NULL" : " present");
        }

        _log($"pickup watch STUCK — {StuckAfterChecks} checks with a character loaded and no bag ever "
           + "read, so NO loot sound can play this session. Highlighting, the hover note and the "
           + $"editor are unaffected. What it sees: {found}. Character uid "
           + $"{(_characterUid.Length > 0 ? "read" : "NOT read")}. "
           + "A required dictionary reading NULL or MISSING is the cause; please report this line.");
    }

    private static string Hex(int offset) => offset < 0 ? "MISSING" : $"0x{offset:x}";
}
