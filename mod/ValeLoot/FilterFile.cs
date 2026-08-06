using System;
using System.IO;
using System.Text;

namespace ValeLoot;

/// <summary>
/// The player's rule file on disk, and the only thing the mod needs in order to work.
///
/// `BepInEx/config/valeloot-filter.txt`. It is written with a worked example the first time the mod
/// runs, because an empty file is indistinguishable from a broken mod: the first launch should light
/// something up so the player knows the plumbing is sound before they start writing rules.
///
/// ## Reload without restarting the game
///
/// Editing a filter is a loop — change a line, look at the bag, change it again — and a loop that
/// costs a relaunch and a login is a loop nobody runs. So the file is watched, and a change is picked
/// up on the next inventory repaint.
///
/// The watcher deliberately does NOT reload in place. `FileSystemWatcher` raises on a thread pool
/// thread, and half of what a reload touches is read by il2cpp UI code on Unity's main thread; parsing
/// off-thread and then swapping a reference would be defensible, but an editor that saves by
/// write-truncate-rename raises two or three events per save, and re-parsing three times per keystroke
/// batch is work done for nothing. Instead the watcher only sets a flag, and the paint pass — already
/// on the main thread, already the moment the result becomes visible — does the reload. One reload per
/// save, no locks, no races, and the reload lands exactly when the player looks.
/// </summary>
internal static class FilterFile
{
    public const string FileName = "valeloot-filter.txt";

    private static string _path = "";
    private static FileSystemWatcher? _watcher;
    private static Action<string> _log = _ => { };

    /// <summary>Set by the watcher, cleared by the paint pass. Volatile: written off the main thread.</summary>
    private static volatile bool _dirty;

    private static FilterParser.ParsedFilter _filter = new();

    /// <summary>The live rule list. Replaced wholesale on reload; never mutated in place.</summary>
    public static FilterParser.ParsedFilter Current => _filter;

    public static int Reloads;
    public static string LastLoadSummary = "not loaded";

    public static string Path => _path;

    /// <summary>
    /// Find (or create) the filter file, load it, and start watching. Returns false only if the file
    /// could not be created — in which case the mod runs on an empty rule list and says so.
    /// </summary>
    public static bool Install(string configDirectory, Action<string> log)
    {
        _log = log;
        _path = System.IO.Path.Combine(configDirectory, FileName);

        try
        {
            Directory.CreateDirectory(configDirectory);
            if (!File.Exists(_path))
            {
                File.WriteAllText(_path, DefaultFilter, new UTF8Encoding(false));
                log($"wrote a starter filter to {_path}");
            }
        }
        catch (Exception e)
        {
            log($"could not create {_path} — {e.Message}. No rules will load this session.");
            return false;
        }

        Load();

        try
        {
            _watcher = new FileSystemWatcher(configDirectory, FileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            _watcher.Changed += (_, _) => _dirty = true;
            _watcher.Created += (_, _) => _dirty = true;
            _watcher.Renamed += (_, _) => _dirty = true;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e)
        {
            // Not fatal. Without a watcher the file still loads at boot and on an explicit reload; the
            // player just has to ask for it. Saying so beats a silently dead edit loop.
            log($"filter file will not auto-reload — {e.Message}. Edits need a game restart or a `reload` command.");
        }
        return true;
    }

    /// <summary>
    /// Called at the top of a paint pass. Cheap when nothing changed: one volatile read.
    ///
    /// Returns true when the rules were replaced, so the caller can drop anything it had cached about
    /// what those rules decided.
    /// </summary>
    public static bool ReloadIfChanged()
    {
        if (!_dirty) return false;
        _dirty = false;
        Load();
        return true;
    }

    /// <summary>Parse the file and swap it in. Never throws: a filter you cannot read is not a crash.</summary>
    public static void Load()
    {
        string text;
        try
        {
            text = File.ReadAllText(_path);
        }
        catch (Exception e)
        {
            // The commonest cause is the editor still holding the file open mid-save. Keeping the last
            // good rules beats going dark, and the next save raises another event.
            _log($"could not read {FileName} — {e.Message}. Keeping the rules already loaded.");
            return;
        }

        FilterParser.ParsedFilter parsed = FilterParser.Parse(text);
        _filter = parsed;
        Reloads++;

        int sounds = 0;
        foreach (LootFilter.LootRule rule in parsed.Rules) if (rule.Sound is not null) sounds++;

        LastLoadSummary =
            $"{parsed.Rules.Length} rule(s), {parsed.Pinned.Length} always-show, {parsed.Muted.Length} always-hide, "
          + $"{sounds} with sound, {parsed.Errors.Length} error(s)";
        _log($"filter loaded: {LastLoadSummary}");

        /**
         * Errors are logged one per line, with the line number, and they are logged as ERRORS.
         *
         * A rejected block is a rule the player believes is running. Burying that in an info line, or
         * summarising it as a count, produces the exact failure this whole design is trying to avoid: a
         * filter that looks installed and quietly does less than it says.
         */
        foreach (FilterParser.FilterError error in parsed.Errors) _log($"filter ERROR {error}");

        if (parsed.Rules.Length == 0 && parsed.Errors.Length == 0)
        {
            _log($"filter has no rules — nothing will be highlighted. Edit {_path} and save; it reloads by itself.");
        }
    }

    public static void Uninstall()
    {
        try { _watcher?.Dispose(); } catch { /* teardown must never throw */ }
        _watcher = null;
    }

    public static string StatusJson()
        => "{\"kind\":\"filter\",\"path\":" + JsonString(_path)
         + ",\"rules\":" + _filter.Rules.Length
         + ",\"pinned\":" + _filter.Pinned.Length
         + ",\"muted\":" + _filter.Muted.Length
         + ",\"errors\":" + _filter.Errors.Length
         + ",\"reloads\":" + Reloads
         + "}";

    private static string JsonString(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (char c in value)
        {
            if (c == '"' || c == '\\') builder.Append('\\').Append(c);
            else if (c < ' ') builder.Append(' ');
            else builder.Append(c);
        }
        return builder.Append('"').ToString();
    }

    /**
     * The file a fresh install gets.
     *
     * It is a working filter, not a blank page with instructions: the first thing a new player should
     * see is their own bag reacting, which proves the hooks landed before they have written anything.
     * The rules are deliberately broad and gentle — a favourite marker and a refine marker, both of
     * which any established account will already have items for.
     */
    private const string DefaultFilter = @"# ValeLoot — your rules, your colours.
#
# Save this file and your bag recolours the next time the inventory redraws. No restart, no relog.
# Rules are tried top to bottom and the FIRST match wins, so an item takes the colour of the topmost
# rule that claims it. Specific rules belong at the top, broad ones at the bottom. That ordering is
# the whole trick: you never have to think about overlap, only about priority.
#
# Press F8 in game for the editor, which shows your real bag and what these rules do to it.
# Every item and stat name the game knows is listed in valeloot-items.txt, next to this file, so
# you never have to guess a spelling.
#
# Conditions              what it asks
#   Name       Kunai      part of the item's name or its id
#   Type       Dagger     the item's type; comma-separated means any of them
#   TopRolls   >= 2       how many lines rolled at or above Threshold, below
#   AvgRoll    < 35       average roll quality across its lines, in percent
#   Stat       Agi >= 90% that stat's line rolled in the top tenth of its range
#   Stat       Agi >= 3   that stat PRINTS at least 3 on this item
#   OverRoll              a line above its normal maximum — only a Chaos widen does that
#   Refine     >= 5       refine level at least this
#   Favorite              the star you put on it in game
#   Chaos / NoChaos       has a chaos type, or has not
#   AnyStat               one Stat line is enough (default: every Stat line must match)
#
# Decorations (Show blocks only)
#   Color      #4ade80    the cell's colour, and the colour of the hover note
#   Tag        KEEP       a short word, shown in the hover note
#   Highlight  dot | mark | glow      how loud: barely lit, clearly lit, unmistakable
#   Sound      chime      played once when a matching item is picked up
#                         (built in: blip, chime, ding, alert, thud — or drop a .wav in
#                          valeloot-sounds/ and name it here)


Threshold 90


# ── BY NAME, one item at a time ──────────────────────────────────────────────────
# These ignore rule order completely: for the drop you refuse to lose track of, and the one
# item you are sick of seeing lit up. Uncomment and put your own names in.
#
# AlwaysShow ""Spirit Ward"", ""Windborne Rune""
# AlwaysHide ""Rusty Dagger""


# ── BY ROLL QUALITY ──────────────────────────────────────────────────────────────
# TopRolls counts lines that rolled at or above Threshold. Two is already rare, so it earns a
# glow and a noise. Deliberately not ""TopRolls >= 1"", which claims about a quarter of a bag —
# a rule that catches everything tells you nothing.

Show ""Two top rolls""
    TopRolls  >= 2
    Color     #e9c46a
    Tag       TOP2
    Highlight glow
    Sound     chime


# ── BY TYPE + STAT — the class-specific rule ─────────────────────────────────────
# Narrow to the weapons you actually use, then demand something of them. Most of your real
# rules will end up this shape. Change the types and the stat to suit your character.

Show ""Weapons worth keeping""
    Type      Dagger, Katar, Twinblade
    Stat      Agi >= 3
    Color     #c4a5ff
    Tag       MINE
    Highlight glow


# ── BY A STAT'S ROLL QUALITY (with %) ────────────────────────────────────────────
# ""Agi landed in the top tenth of its range on THIS item"", whatever it prints. Works on
# artifacts too, because a roll percentage needs nothing but the item itself.

Show ""Top-rolled Agi""
    Stat      Agi >= 90%
    Color     #4ade80
    Tag       AGI%
    Highlight mark


# ── BY A STAT'S PRINTED VALUE (no %) ─────────────────────────────────────────────
# The number the game prints on the line — a different question from the block above.
# Attributes cap at 3, so >= 3 means maxed; Crit and AtkSpd run much higher, which is why
# their thresholds are not 3.

Show ""High crit""
    Stat      Crit >= 8
    Color     #f472b6
    Tag       CRIT
    Highlight mark


# ── BY REFINE — work you have already paid for ───────────────────────────────────

Show ""Well refined""
    Refine    >= 5
    Color     #ff9f6b
    Tag       ""+5""
    Highlight mark


# ── BY THE GAME'S OWN FLAG ───────────────────────────────────────────────────────
# Whatever you starred in game. The cheapest signal there is: you already maintain it.

Show ""Favourites""
    Favorite
    Color     #facc15
    Tag       FAV
    Highlight dot


# ── BY CHAOS — a line above its normal maximum ───────────────────────────────────
# Only a Chaos widen can push a roll past 100%, so it is always worth a look. This reads 0
# until one drops, and that is fine — it is a trap set, not a rule that failed.

Show ""Chaos paid off""
    OverRoll
    Color     #ef6f6f
    Tag       CHAOS
    Highlight glow
    Sound     alert


# ── AND THE REST ─────────────────────────────────────────────────────────────────
# A Hide block claims items and then draws nothing — that is its whole job. Anything no rule
# claims at all is simply left alone, which will be most of your bag, and should be.

Hide ""vendor trash""
    AvgRoll   < 35
    TopRolls  < 1
";
}
