using System;

namespace ValeLoot;

/// <summary>
/// The loot filter model, and the evaluation that turns an item into a colour.
///
/// This is a port of the overlay's `loot-filter.ts` semantics into the mod, and the port exists for
/// one reason: THE MOD MUST BE COMPLETE ON ITS OWN. A player who has never heard of the overlay
/// installs the plugin, edits a text file, and their bag lights up. Nothing is pushed in over a
/// socket, nothing else has to be running, and the mod holds the rules and does the judging.
///
/// The semantics are deliberately identical to the overlay's, so one filter file reads the same in
/// both places: an ORDERED list of rules, FIRST MATCH WINS, each rule deciding how an item is
/// PRESENTED — a colour, a short tag, how hard the cell is highlighted, and whether it makes a noise
/// when it arrives. Ordering is the whole ergonomic trick: put "triple top roll" above "vendor trash"
/// and stop thinking about overlap.
///
/// ## Presentation is the entire vocabulary, deliberately
///
/// A rule cannot ACT on an item. There is no field here that could express any automation, and there
/// is no code in this plugin that could carry one out. That is a product decision, not an omission:
/// the game's staff allow client-side tools that change "how the loot color is, sound and how it
/// looks in the inventory" and nothing past that line. Keeping the TYPE incapable of an action is
/// cheaper than keeping a policy — nothing downstream has to be trusted to check.
///
/// ## Rolls, and the two questions the `%` separates
///
/// `StatData.Value` IS the roll percentage, 0..100 — not the number the game prints. The game derives
/// what it prints: `displayed = cap * (2/3 + roll/300)`, rounded. So "top roll" questions are free and
/// in-process, and `TopRolls`, `AvgRoll` and `Stat Agi >= 90%` all work from the item alone. The
/// ABSOLUTE form, `Stat Agi >= 3`, needs the item's base cap, which <see cref="ItemCatalog"/> reads
/// out of the game's own configs.
///
/// The two stay SEPARATE fields on <see cref="StatCondition"/> rather than one number with a flag,
/// because they are different questions and the whole point of the `%` suffix is that the player
/// chose which one to ask. A filter that quietly answers the other one grades every item wrong.
///
/// ## Cost
///
/// Evaluation runs over every visible cell on every inventory repaint, inside a UI callback that must
/// not stutter. So it allocates nothing per item: no lower-casing (comparisons are
/// OrdinalIgnoreCase), no substring building, no regex. <see cref="ItemFacts"/> is a single buffer the
/// reader refills per cell rather than an object per item.
/// </summary>
internal static class LootFilter
{
    /// <summary>Most substat lines one item can carry. Bounds the fact buffer; overflow is dropped.</summary>
    public const int MaxStats = 16;

    /// <summary>Roll percentage that counts as a top roll unless the filter says otherwise.</summary>
    public const int DefaultThreshold = 90;

    /**
     * One item, flattened to what a rule can ask about — refilled per cell, never allocated per cell.
     *
     * `StatRolls` holds `StatData.Value`, which is the ROLL PERCENTAGE. It can exceed 100: the game
     * branches on `Value > 100` for the chaos over-roll case, so the array is not clamped and
     * <see cref="LootCondition.OverRoll"/> exists to ask about exactly that.
     *
     * `StatTiers` is the quality-tier string the game itself prints for the line. Nothing filters on it
     * — inventing a condition on a string whose vocabulary we have not read would be a guess — but it
     * is what makes a `probe` reply describe a real item rather than a row of numbers.
     */
    internal sealed class ItemFacts
    {
        /// <summary>The name the player sees on the cell.</summary>
        public string Name = "";
        /// <summary>`InventoryItemData.Id` — the catalog id, which a filter may also match on.</summary>
        public string Id = "";
        /// <summary>The type the cell displays ("Accessory", "Rifle", …) — what the player reads.</summary>
        public string Type = "";
        public int Refine;
        public bool Favorite;
        public bool HasChaos;

        public int StatCount;
        public readonly string[] StatNames = new string[MaxStats];
        /// <summary>`StatType` ordinals, parallel to the names — what the catalog keys a cap by.</summary>
        public readonly int[] StatTypes = new int[MaxStats];
        public readonly int[] StatRolls = new int[MaxStats];
        public readonly string[] StatTiers = new string[MaxStats];

        public void Reset()
        {
            Name = "";
            Id = "";
            Type = "";
            Refine = 0;
            Favorite = false;
            HasChaos = false;
            StatCount = 0;
        }

        public void AddStat(string name, int statType, int roll, string tier)
        {
            if (StatCount >= MaxStats) return;
            StatNames[StatCount] = name;
            StatTypes[StatCount] = statType;
            StatRolls[StatCount] = roll;
            StatTiers[StatCount] = tier;
            StatCount++;
        }

        /// <summary>Lines rolling at or above the threshold — the "triple top roll" count.</summary>
        public int TopRolls(int threshold)
        {
            int count = 0;
            for (int i = 0; i < StatCount; i++) if (StatRolls[i] >= threshold) count++;
            return count;
        }

        /**
         * Mean roll, rounded to a whole percent, or -1 when the item has no lines.
         *
         * Rounded, and then compared as a whole number, so `AvgRoll < 35` means exactly what a player
         * gets by averaging the percentages they can see. The overlay compares unrounded and nudges its
         * strict bounds by 1e-9 because it draws the unrounded figure in its own HUD; in here there is
         * no such HUD, so a boundary the player cannot reproduce by hand would be a boundary they
         * cannot trust.
         *
         * No lines is -1 rather than 0: "average roll below 35" is a statement about an item that rolled
         * badly, not about one that cannot roll at all.
         */
        public int AverageRoll()
        {
            if (StatCount == 0) return -1;
            int sum = 0;
            for (int i = 0; i < StatCount; i++) sum += StatRolls[i];
            return (int)Math.Round(sum / (double)StatCount, MidpointRounding.AwayFromZero);
        }

        public bool HasOverRoll()
        {
            for (int i = 0; i < StatCount; i++) if (StatRolls[i] > 100) return true;
            return false;
        }
    }

    /**
     * One required substat line, in one of its two forms: `Stat Agi >= 90%` or `Stat Agi >= 3`.
     *
     * The `%` form is a floor on ROLL QUALITY, which the item carries. The bare form is a floor on
     * the VALUE THE GAME PRINTS, which needs the item's base cap out of the catalog. They are two
     * fields rather than one number and a flag on purpose: they are different questions, the parser
     * sets exactly one of them per line, and nothing downstream can mix them up by accident.
     */
    internal sealed class StatCondition
    {
        public string Stat = "";
        /// <summary>Roll-quality floor in percent — the `%` form. Null when the line did not ask.</summary>
        public int? MinRollPct;
        /// <summary>Printed-value floor — the bare form. Null when the line did not ask.</summary>
        public int? MinValue;
    }

    /// <summary>What a rule tests. Every field absent means "any item", which only a Show block may mean.</summary>
    internal sealed class LootCondition
    {
        public string[]? Types;
        /**
         * Any of these, as a case-insensitive substring of the name, the id or the catalog's name.
         *
         * A LIST rather than one string because the commonest real rule is "these four drops I care
         * about", and the alternative is four `Show` blocks that differ only in one word — four
         * places to change the colour, and four chances to get the order wrong. `Type` already reads
         * as a list for the same reason, so `Name "Buzzing Hive Fragment", "Abyssal Idol"` is the
         * spelling a player would guess.
         */
        public string[]? Names;
        public int? MinRefine;
        public int? MinTopRolls;
        public int? MaxTopRolls;
        public int? MinAvgRoll;
        public int? MaxAvgRoll;
        public StatCondition[]? Stats;
        /// <summary>Every listed stat must match, rather than just one. `AnyStat` flips it.</summary>
        public bool StatsAll = true;
        public bool? HasChaos;
        public bool? Favorite;
        /// <summary>A line rolled past 100 — the chaos over-roll, and unambiguously a great item.</summary>
        public bool? OverRoll;

        public bool IsEmpty =>
            Types is null && Names is null && MinRefine is null
            && MinTopRolls is null && MaxTopRolls is null && MinAvgRoll is null && MaxAvgRoll is null
            && (Stats is null || Stats.Length == 0)
            && HasChaos is null && Favorite is null && OverRoll is null;
    }

    /**
     * How hard a match shouts in the bag, as the level the cell overlay is driven to.
     *
     *   dot (1)   quiet — the cell carries the rule's colour, nothing more
     *   mark (2)  clearly lit: worth a second look
     *   glow (3)  unmistakable
     *
     * Three levels rather than a boolean, because a bag where 25 of 31 slots shout says nothing.
     */
    public const int LevelDot = 1;
    public const int LevelMark = 2;
    public const int LevelGlow = 3;

    /// <summary>`dot`/`mark`/`glow` -> level, or 0 for anything else.</summary>
    public static int ParseLevel(string word) => word switch
    {
        "dot" => LevelDot,
        "mark" => LevelMark,
        "glow" => LevelGlow,
        _ => 0,
    };

    public static string LevelName(int level) => level switch
    {
        LevelGlow => "glow",
        LevelMark => "mark",
        LevelDot => "dot",
        _ => "none",
    };

    /**
     * `#rrggbb` -> 0..1 floats, falling back to white.
     *
     * No gamma conversion: the colour is the one written in the filter file, and a "more correct"
     * transform here would make the cell disagree with the hex the player typed.
     *
     * Parsed once per rule at load rather than per cell per repaint — see `LootRule.R/G/B`.
     */
    public static (float R, float G, float B) ParseColor(string? hex)
    {
        if (hex is null) return (1f, 1f, 1f);
        int start = hex.Length > 0 && hex[0] == '#' ? 1 : 0;
        if (hex.Length - start < 6) return (1f, 1f, 1f);
        try
        {
            int value = Convert.ToInt32(hex.Substring(start, 6), 16);
            return (((value >> 16) & 0xFF) / 255f, ((value >> 8) & 0xFF) / 255f, (value & 0xFF) / 255f);
        }
        catch { return (1f, 1f, 1f); }
    }

    internal sealed class LootRule
    {
        public string Name = "";
        public string Color = "#4ade80";
        /// <summary>`Color`, pre-parsed at load. A repaint must not parse a hex string per cell.</summary>
        public float R = 0.29f;
        public float G = 0.87f;
        public float B = 0.5f;
        public string Label = "";
        public int Level = LevelDot;
        /// <summary>Played once when a match first appears in the bag. Null for silence.</summary>
        public string? Sound;
        /**
         * Matched, and deliberately silent — the filter language's `Hide`.
         *
         * A muted match draws nothing and plays nothing. It is NOT "no rule matched": the rule claimed
         * the item and chose silence, which is how a filter stops lighting up vendor fodder while still
         * letting a later rule be quiet about nothing.
         */
        public bool Mute;
        public LootCondition When = new();
        /// <summary>Source line of the block opener, so a message can point at the player's own file.</summary>
        public int Line;
    }

    /// <summary>
    /// First matching rule, or null. Order is the filter's meaning, so this never reorders or scores.
    ///
    /// `AlwaysShow`/`AlwaysHide` are applied by the caller BEFORE this, because a per-item override
    /// exists precisely to escape rule order.
    /// </summary>
    public static LootRule? Match(ItemFacts item, LootRule[] rules, int threshold)
    {
        for (int i = 0; i < rules.Length; i++)
        {
            if (Matches(item, rules[i].When, threshold)) return rules[i];
        }
        return null;
    }

    public static bool Matches(ItemFacts item, LootCondition when, int threshold)
    {
        if (when.Types is not null)
        {
            // The cell's text OR the catalog's own `EquipType` member name. Both, rather than the
            // catalog winning, because a filter written against what the cell prints must keep
            // working — and the enum name is what covers a cell whose text is localised.
            string catalogType = ItemCatalog.TypeName(item.Id) ?? "";
            bool hit = false;
            for (int i = 0; i < when.Types.Length; i++)
            {
                string want = when.Types[i];
                if (string.Equals(item.Type, want, StringComparison.OrdinalIgnoreCase)
                    || (catalogType.Length > 0 && string.Equals(catalogType, want, StringComparison.OrdinalIgnoreCase)))
                {
                    hit = true;
                    break;
                }
            }
            if (!hit) return false;
        }

        // Matched against the DISPLAYED name, the catalog id, and the config's own display name.
        // Players think in display names ("Kunai"), filters shared from elsewhere may carry ids, and
        // asking which one a given string is would make the commonest line in any filter the one that
        // needs explaining. The config name is the third because a cell truncates its text to fit, a
        // pickup has no cell at all, and several kinds of item have an id nothing like their name —
        // "Buzzing Hive Fragment" is `Lure Sting`.
        //
        // ANY of the listed names is enough. Every one of them asks the same three questions, so a
        // list is exactly as strong as the `Show` blocks it replaces.
        if (when.Names is not null)
        {
            string catalogName = ItemCatalog.DisplayName(item.Id) ?? "";
            bool named = false;
            for (int i = 0; i < when.Names.Length; i++)
            {
                string want = when.Names[i];
                if (item.Name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0
                    || item.Id.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0
                    || (catalogName.Length > 0
                        && catalogName.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    named = true;
                    break;
                }
            }
            if (!named) return false;
        }

        if (when.MinRefine is int minRefine && item.Refine < minRefine) return false;
        if (when.HasChaos is bool chaos && item.HasChaos != chaos) return false;
        if (when.Favorite is bool favorite && item.Favorite != favorite) return false;
        if (when.OverRoll is bool over && item.HasOverRoll() != over) return false;

        if (when.MinTopRolls is not null || when.MaxTopRolls is not null)
        {
            int top = item.TopRolls(threshold);
            if (when.MinTopRolls is int minTop && top < minTop) return false;
            if (when.MaxTopRolls is int maxTop && top > maxTop) return false;
        }

        if (when.MinAvgRoll is not null || when.MaxAvgRoll is not null)
        {
            int average = item.AverageRoll();
            if (average < 0) return false;
            if (when.MinAvgRoll is int minAvg && average < minAvg) return false;
            if (when.MaxAvgRoll is int maxAvg && average > maxAvg) return false;
        }

        if (when.Stats is not null && when.Stats.Length > 0)
        {
            int hits = 0;
            for (int i = 0; i < when.Stats.Length; i++)
            {
                if (MatchesStat(item, when.Stats[i])) hits++;
                else if (when.StatsAll) return false;
            }
            if (!when.StatsAll && hits == 0) return false;
        }

        return true;
    }

    private static bool MatchesStat(ItemFacts item, StatCondition want)
    {
        for (int i = 0; i < item.StatCount; i++)
        {
            if (!string.Equals(item.StatNames[i], want.Stat, StringComparison.OrdinalIgnoreCase)) continue;
            if (want.MinRollPct is int minRoll && item.StatRolls[i] < minRoll) continue;
            if (want.MinValue is int minValue)
            {
                // An unanswerable condition is NOT a match. `TryScaledValue` returns false when the
                // catalog has not resolved, when the item is not one it knows, or when the stat is
                // not in that item's pool — and treating any of those as satisfied would widen the
                // block to the whole bag. It says so in the log once, and declines here.
                if (!ItemCatalog.TryScaledValue(item.Id, item.StatTypes[i], item.StatRolls[i], out int printed)) continue;
                if (printed < minValue) continue;
            }
            return true;
        }
        return false;
    }
}
