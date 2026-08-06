using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace ValeLoot;

/// <summary>
/// `BepInEx/config/valeloot-bag.txt` — the player's bag written out exactly as the filter sees it, so
/// the standalone editor can show real per-rule counts.
///
/// The editor is one HTML file opened from disk. It has nothing behind it and no way to ask the game
/// anything, which is the whole point of it: a rule editor that needs a companion process running
/// is a rule editor the player does not have. So the counts have to arrive the only way anything gets
/// out of this mod — as a text file next to the rules.
///
/// ## Why this is its own file rather than part of FilterFile
///
/// `FilterFile` owns the file the player EDITS: it creates it, watches it, and reloads it. This owns a
/// file the player never edits and the mod never reads back, produced as a side effect of the repaint
/// path. Two opposite lifecycles, and the only thing they share is a directory. `ItemCatalog` already
/// owns its own generated file for the same reason.
///
/// ## What the file contains, and why it says so out loud
///
/// WHAT YOU OWN, keyed by uid — not the current page, and not a session high-water mark.
///
/// Two sources, because neither is sufficient alone. The paint pass supplies CONTENT: the game binds
/// one page of cells at a time, so a snapshot built from a single pass describes about a dozen items
/// of a two-hundred-item bag, and uids are stable, so accumulating across passes converges on the
/// whole bag as the player scrolls.
///
/// <see cref="InventoryWatch"/> supplies MEMBERSHIP, through <see cref="Retain"/>. That is the half a
/// repaint cannot do at all: a uid missing from a pass means "sold" and "on a page you have not
/// looked at" equally, so for a long time this file could only accumulate and say so in its header.
/// It was not enough — a player watched it reach seven hundred items, most of them in his bank, and
/// reported that it never resets. The watcher reads the inventory DATA, so it knows what is gone.
///
/// A file left over from an earlier session is deleted at boot rather than kept: it would claim to be
/// a bag that has since been played for three hours, and nothing in it lets the editor notice. It
/// comes back the first time the bag is drawn.
///
/// ## Cost, because this hangs off the repaint path
///
/// An inventory redraw fires on open, scroll, sort, search, paging and every inventory mutation. So:
///
/// - Per cell, per pass: one cheap hash over (uid, refine, favourite, roll list) and one dictionary
///   probe. Nothing is formatted and no catalog lookup happens for an item whose facts are unchanged,
///   which is every item on every pass after the first one that saw it.
/// - Per pass: one comparison of the accumulated signature against the signature last written. Equal
///   means the bag's content has not changed and the disk is not touched. That comparison is what
///   makes scrolling free — scrolling back over items already seen adds nothing, so it writes nothing.
/// - The write itself happens on a thread pool thread, from a private copy of the rows taken on the
///   main thread. Building and writing ~200 lines is not something a UI callback should be doing, and
///   the snapshot is a preview: it can land a frame late. One write is in flight at a time; a pass
///   that finds the writer busy does nothing and the next pass carries the newer content.
/// - The bytes land by write-to-temp then replace, so an editor reading the file concurrently sees
///   either the whole previous snapshot or the whole new one, never half of each.
///
/// Logging follows the same discipline: the first successful write is logged, and after that only a
/// change in item count. A log line per pass would be a log line per scroll tick.
/// </summary>
internal static class BagSnapshot
{
    public const string FileName = "valeloot-bag.txt";

    /// <summary>Bumped only for a change the editor's parser must notice. Written as `# version N`.</summary>
    private const int FormatVersion = 1;

    /// <summary>
    /// A ceiling on accumulation, so a long session cannot grow this without bound. Well above any
    /// real bag; if it is ever hit the header and the log both say the file is short.
    /// </summary>
    private const int MaxRows = 4000;

    /// <summary>
    /// One item, captured. Immutable and replaced wholesale rather than mutated, which is what lets a
    /// writer thread hold a plain array of these while the main thread keeps observing cells.
    /// </summary>
    private sealed class Row
    {
        public readonly long Hash;
        public readonly string Uid;
        public readonly string ItemId;
        public readonly string DisplayName;
        public readonly string Type;
        public readonly int Refine;
        public readonly bool Favorite;
        public readonly string[] StatNames;
        public readonly int[] StatRolls;
        /// <summary>The value the game prints for each line, or -1 where the catalog could not say.</summary>
        public readonly int[] StatPrinted;

        public Row(long hash, string uid, string itemId, string displayName, string type, int refine,
                   bool favorite, string[] statNames, int[] statRolls, int[] statPrinted)
        {
            Hash = hash;
            Uid = uid;
            ItemId = itemId;
            DisplayName = displayName;
            Type = type;
            Refine = refine;
            Favorite = favorite;
            StatNames = statNames;
            StatRolls = statRolls;
            StatPrinted = statPrinted;
        }
    }

    /// <summary>uid -> item, accumulated across passes. Main thread only.</summary>
    private static readonly Dictionary<string, Row> _rows = new(StringComparer.Ordinal);

    private static string _path = "";
    private static Action<string> _log = _ => { };
    private static bool _installed;

    /// <summary>Sum of every row's hash. Order-independent by construction, so a pass order change is not a content change.</summary>
    private static long _rowSum;

    private static int _threshold = LootFilter.DefaultThreshold;

    /// <summary>
    /// Folded into every row hash so that a catalog resolving mid-session restates the printed values
    /// of rows captured before it did, instead of leaving them as `-` for the rest of the session.
    /// </summary>
    private static long _catalogGeneration;

    /// <summary>Signature of the content last successfully on disk. Written by the writer thread.</summary>
    private static long _writtenSignature = long.MinValue;

    /// <summary>1 while a write is in flight. One at a time: a scroll burst must not queue a write per pass.</summary>
    private static int _writing;

    /// <summary>Set by the writer thread, drained by the next pass. The log is the main thread's to call.</summary>
    private static string? _pendingLog;

    /// <summary>Item count of the last logged write, so an unchanged count stays silent.</summary>
    private static int _reportedCount = -1;

    private static bool _saidTruncated;
    private static bool _truncated;
    /// <summary>Whether any cell has ever been captured. Separates "nothing seen yet" from "nothing left".</summary>
    private static bool _everObserved;

    /// <summary>Signature of the content last handed to <see cref="EditorServer"/>. Main thread only.</summary>
    private static long _publishedSignature = long.MinValue;

    /// <summary>
    /// Point the writer at the config folder and clear any snapshot left by an earlier session.
    ///
    /// Never fatal: without this file the editor loses its counts and nothing else, so a failure here
    /// is one log line and the mod carries on.
    /// </summary>
    public static void Install(string configDirectory, Action<string> log)
    {
        _log = log;
        _path = Path.Combine(configDirectory, FileName);
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (Exception e)
        {
            log($"could not clear the previous {FileName} \u2014 {e.Message}. It is overwritten the first "
              + "time you open your bag.");
        }
        _installed = true;
    }

    public static void Uninstall()
    {
        _installed = false;
        _rows.Clear();
        _rowSum = 0;
        _everObserved = false;
        _reportedCount = -1;
        _publishedSignature = long.MinValue;
    }

    /// <summary>
    /// Top of a paint pass. Picks up the threshold the file's `topRolls` column is counted against,
    /// notes whether the catalog can answer yet, and drains anything the writer thread wants said.
    /// </summary>
    public static void BeginPass(int threshold)
    {
        if (!_installed) return;

        string? pending = Interlocked.Exchange(ref _pendingLog, null);
        if (pending is not null) _log(pending);

        _threshold = threshold;
        _catalogGeneration = ItemCatalog.Ready ? ItemCatalog.Count : 0;
    }

    /// <summary>
    /// One visible cell, already read by the paint pass. Cheap and allocation-free unless this item's
    /// facts differ from what was captured last time it was seen.
    /// </summary>
    public static void Observe(string uid, LootFilter.ItemFacts facts)
    {
        if (!_installed || uid.Length == 0) return;

        long hash = RowHash(uid, facts);
        if (_rows.TryGetValue(uid, out Row? existing))
        {
            // The common case by a wide margin: the same item, unchanged, seen again. No format, no
            // catalog lookup, no allocation.
            if (existing.Hash == hash) return;
            _rowSum = unchecked(_rowSum - existing.Hash + hash);
        }
        else
        {
            if (_rows.Count >= MaxRows)
            {
                _truncated = true;
                if (!_saidTruncated)
                {
                    _saidTruncated = true;
                    _log($"{FileName} stopped at {MaxRows} items \u2014 the editor's counts cover those and "
                       + "say so. Nothing else is affected.");
                }
                return;
            }
            _rowSum = unchecked(_rowSum + hash);
        }

        _everObserved = true;
        _rows[uid] = Capture(uid, hash, facts);
    }

    /**
     * Keep only the uids the player still owns, and forget the rest.
     *
     * This is the half a repaint cannot do. The paint pass only ever sees cells the game has bound,
     * so a uid missing from a pass means "sold" and "on a page you have not scrolled to" equally,
     * and the snapshot had no choice but to accumulate and never remove. The file said so in its own
     * header, and a player still ended up staring at seven hundred items, most of them in his bank:
     * "it never resets".
     *
     * <see cref="InventoryWatch"/> is the missing authority. It reads the player's inventory DATA —
     * every bag, every key, no cell involved — so its baseline is exactly what is owned, and it is
     * handed here at the end of the walk that produced it.
     *
     * The two key spaces are the same one, which is what makes this safe: equipment is keyed by its
     * item UID in both, and a stackable is keyed by its item ID in both (`StackableItemData` has no
     * UID at all). Verified against a live bag — a card row reads
     * `Abomination \t Abomination \t Abomination Card \t Card`, uid and id identical, which is the
     * `InventoryData.Cards` key.
     *
     * Called on a walk, which only happens when the bag actually changed, so the common frame pays
     * nothing for this.
     */
    public static void Retain(IReadOnlyDictionary<string, int> held)
    {
        if (!_installed || _rows.Count == 0) return;

        // Collected first rather than removed in place: mutating a dictionary while enumerating it
        // throws, and the allocation only happens on a walk that actually lost something.
        List<string>? gone = null;
        foreach (KeyValuePair<string, Row> entry in _rows)
        {
            if (held.ContainsKey(entry.Key)) continue;
            (gone ??= new List<string>()).Add(entry.Key);
        }
        if (gone is null) return;

        foreach (string uid in gone)
        {
            if (!_rows.TryGetValue(uid, out Row? row)) continue;
            _rowSum = unchecked(_rowSum - row.Hash);
            _rows.Remove(uid);
        }

        // The ceiling caveat has to be able to become untrue again: a bag that dropped back under
        // the limit is no longer a truncated snapshot, and the header should stop claiming it is.
        if (_truncated && _rows.Count < MaxRows) _truncated = false;

        // Publish and write from HERE, because a dismantle with the panel shut produces no paint
        // pass at all — and "the number does not go down until you open your bag" is the same bug
        // wearing a smaller hat.
        EndPass();
        PublishToEditor();
    }

    /// <summary>
    /// End of a paint pass. Writes only when the accumulated content differs from what is on disk.
    ///
    /// This is the guard that keeps scrolling off the disk: the signature is over the row hashes, the
    /// row count and the threshold, so a pass that re-sees items already captured produces the same
    /// signature and returns without touching the file.
    /// </summary>
    public static void EndPass()
    {
        // `_everObserved`, not `_rows.Count`: a bag reaped down to nothing is a real state that has
        // to reach the file, or "I dropped everything" reads as "the mod stopped updating". What the
        // guard is really for is the window before the first pass, when a count of zero means
        // "nothing seen yet" and writing it would replace "not loaded" with a confident "0 items".
        if (!_installed || !_everObserved) return;

        long signature = Signature();
        if (signature == Interlocked.Read(ref _writtenSignature)) return;

        // A write is already in flight. Do nothing: it is either writing this same content, or the
        // next pass will find the signature still stale and carry the newer content then.
        if (Interlocked.CompareExchange(ref _writing, 1, 0) != 0) return;

        // A private copy, taken here on the main thread. Rows are immutable and replaced rather than
        // mutated, so the writer thread can hold this array while passes keep observing cells.
        var rows = new Row[_rows.Count];
        _rows.Values.CopyTo(rows, 0);
        int threshold = _threshold;
        bool truncated = _truncated;

        ThreadPool.UnsafeQueueUserWorkItem(_ => Write(rows, threshold, truncated, signature), null);
    }

    /**
     * Hand the accumulated rows to <see cref="EditorServer"/> when they have changed.
     *
     * MAIN THREAD ONLY, and it is called from the editor's per-frame pump rather than from
     * <see cref="EndPass"/> on purpose: an HTTP reader is not rate-limited by a disk write, so the
     * served bag should update on the pass after a change even when `EndPass` skipped the file
     * because its writer was still busy with the previous one. The change check is the same
     * signature, so a scroll over items already seen costs three arithmetic ops and returns.
     *
     * The projection happens HERE, on this thread, because <see cref="Row"/> and `_rows` are this
     * file's and unsynchronised by design. Rows are immutable and always replaced, so the immutable
     * objects built from them stay correct no matter what later passes do — which is what makes it
     * safe for a listener thread to hold the result. It must never hold `_rows` itself.
     *
     * `topRolls` and `avgRoll` are deliberately NOT sent: they are functions of the roll list and the
     * threshold, and the threshold changes the moment the player saves `Threshold 95`.
     */
    public static void PublishToEditor()
    {
        if (!_installed || !_everObserved) return;

        long signature = Signature();
        if (signature == _publishedSignature) return;
        _publishedSignature = signature;

        var items = new EditorServer.BagItem[_rows.Count];
        int at = 0;
        foreach (Row row in _rows.Values)
        {
            var lines = new EditorServer.BagLine[row.StatNames.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                lines[i] = new EditorServer.BagLine(row.StatNames[i], row.StatRolls[i], row.StatPrinted[i]);
            }
            items[at++] = new EditorServer.BagItem(row.Uid, row.ItemId, row.DisplayName, row.Type,
                                                   row.Refine, row.Favorite, lines);
        }

        EditorServer.PublishBag(items, _threshold, _truncated);
    }

    /// <summary>Everything that decides the file's bytes, in one comparable number.</summary>
    private static long Signature() => unchecked(_rowSum * 31 + _rows.Count * 131 + _threshold);

    /**
     * Has this item changed since it was last captured?
     *
     * Deliberately over the cheap facts only — uid, refine, favourite and the roll list — plus the
     * catalog generation, because those are everything that can differ for a given uid. Name, type
     * and id are fixed for an item, and the printed values are a function of the rolls and the
     * catalog. No string hashing beyond the uid, no allocation, no engine call.
     */
    private static long RowHash(string uid, LootFilter.ItemFacts facts)
    {
        unchecked
        {
            long hash = 1469598103934665603L;    // FNV-1a 64-bit offset basis
            for (int i = 0; i < uid.Length; i++) hash = (hash ^ uid[i]) * 1099511628211L;
            hash = (hash ^ (uint)facts.Refine) * 1099511628211L;
            hash = (hash ^ (facts.Favorite ? 1L : 0L)) * 1099511628211L;
            for (int i = 0; i < facts.StatCount; i++)
            {
                hash = (hash ^ (uint)facts.StatTypes[i]) * 1099511628211L;
                hash = (hash ^ (uint)facts.StatRolls[i]) * 1099511628211L;
            }
            return (hash ^ _catalogGeneration) * 1099511628211L;
        }
    }

    /**
     * Turn one cell's facts into a row.
     *
     * This is the only place that costs anything per item, and it runs only for an item whose hash
     * changed — a new item, a refine, a favourite toggle, or the pass right after the catalog
     * resolved. The catalog is asked here rather than per cell per pass for exactly that reason.
     *
     * The catalog's display name and type win over the cell's text where it has them, because a cell
     * truncates to fit and the editor is matching what a rule was written against. Where it has
     * nothing — cards, gems and consumables are not in the equip catalog — the cell text stands.
     */
    private static Row Capture(string uid, long hash, LootFilter.ItemFacts facts)
    {
        int count = facts.StatCount;
        var names = new string[count];
        var rolls = new int[count];
        var printed = new int[count];
        for (int i = 0; i < count; i++)
        {
            names[i] = Clean(facts.StatNames[i]);
            rolls[i] = facts.StatRolls[i];
            printed[i] = ItemCatalog.TryScaledValue(facts.Id, facts.StatTypes[i], facts.StatRolls[i], out int value)
                ? value
                : -1;
        }

        string display = ItemCatalog.DisplayName(facts.Id) ?? "";
        if (display.Length == 0) display = facts.Name;
        string type = ItemCatalog.TypeName(facts.Id) ?? "";
        if (type.Length == 0) type = facts.Type;

        return new Row(hash, Clean(uid), Clean(facts.Id), Clean(display), Clean(type),
                       facts.Refine, facts.Favorite, names, rolls, printed);
    }

    /**
     * Build the file and put it on disk. Thread pool thread.
     *
     * Temp file then replace, so a reader never sees a half-written snapshot: the editor may well be
     * polling this file while the player scrolls.
     */
    private static void Write(Row[] rows, int threshold, bool truncated, long signature)
    {
        try
        {
            string text = Build(rows, threshold, truncated);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, _path, overwrite: true);

            Interlocked.Exchange(ref _writtenSignature, signature);

            // First write, and thereafter only a change in count. Anything more is a line per scroll.
            if (_reportedCount != rows.Length)
            {
                _reportedCount = rows.Length;
                _pendingLog = $"wrote {FileName} ({rows.Length} item(s)) to {_path}";
            }
        }
        catch (Exception e)
        {
            // Nothing depends on this file except the editor's counts. Leaving the signature stale is
            // deliberate: the next content change tries again.
            _pendingLog = $"could not write {FileName} \u2014 {e.Message}. Highlighting is unaffected; the "
                        + "editor will have no item counts.";
        }
        finally
        {
            Interlocked.Exchange(ref _writing, 0);
        }
    }

    /// <summary>The whole file, as text. Pure: everything it reads is an argument.</summary>
    private static string Build(Row[] rows, int threshold, bool truncated)
    {
        // A stable order, so the bytes change only when the content does — the editor is watching this
        // file, and a reshuffled but identical snapshot would read as new information.
        Array.Sort(rows, static (a, b) =>
        {
            int byType = string.Compare(a.Type, b.Type, StringComparison.OrdinalIgnoreCase);
            if (byType != 0) return byType;
            int byName = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.Compare(a.Uid, b.Uid, StringComparison.Ordinal);
        });

        var text = new StringBuilder(rows.Length * 96 + 512);
        text.Append("# GENERATED by ValeLoot \u2014 your bag as the filter sees it. Editing this does nothing.\n")
            .Append("# Keyed by uid. Items you sell, bank or dismantle drop out within a second; scroll\n")
            .Append("# or switch tabs to fill in items not seen yet this session.\n")
            .Append("# version ").Append(FormatVersion.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("# threshold ").Append(threshold.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (truncated)
        {
            text.Append("# NOTE: this snapshot stopped at ").Append(MaxRows.ToString(CultureInfo.InvariantCulture))
                .Append(" items, so it is SHORT of your bag.\n");
        }
        text.Append("# generated: ")
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append('\n')
            .Append("# uid\titemId\tdisplayName\ttype\trefine\tfavorite\ttopRolls\tavgRoll\tstats\n");

        foreach (Row row in rows)
        {
            int topRolls = 0;
            int sum = 0;
            for (int i = 0; i < row.StatRolls.Length; i++)
            {
                if (row.StatRolls[i] >= threshold) topRolls++;
                sum += row.StatRolls[i];
            }

            text.Append(row.Uid).Append('\t')
                .Append(row.ItemId.Length > 0 ? row.ItemId : "-").Append('\t')
                .Append(row.DisplayName.Length > 0 ? row.DisplayName : "-").Append('\t')
                .Append(row.Type.Length > 0 ? row.Type : "-").Append('\t')
                .Append(row.Refine.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(row.Favorite ? '1' : '0').Append('\t')
                .Append(topRolls.ToString(CultureInfo.InvariantCulture)).Append('\t');

            // Rounded the same way `AvgRoll` is compared, and `-` rather than 0 for an item with no
            // lines: "average roll below 35" is about an item that rolled badly, not one that cannot
            // roll at all. A `0` here would make every consumable match such a rule in the preview.
            if (row.StatRolls.Length == 0) text.Append('-');
            else text.Append(((int)Math.Round(sum / (double)row.StatRolls.Length, MidpointRounding.AwayFromZero))
                             .ToString(CultureInfo.InvariantCulture));
            text.Append('\t');

            if (row.StatNames.Length == 0) text.Append('-');
            for (int i = 0; i < row.StatNames.Length; i++)
            {
                if (i > 0) text.Append(',');
                text.Append(row.StatNames[i]).Append(':')
                    .Append(row.StatRolls[i].ToString(CultureInfo.InvariantCulture)).Append(':')
                    .Append(row.StatPrinted[i] < 0
                        ? "-"
                        : row.StatPrinted[i].ToString(CultureInfo.InvariantCulture));
            }
            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// Strip the three characters that would break the format. Stripped rather than escaped: an
    /// escape needs an unescape at the other end, and a tab inside a display name is a game bug, not
    /// data worth preserving. Allocation-free for the overwhelming majority of values, which are clean.
    /// </summary>
    private static string Clean(string value)
    {
        int first = -1;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\t' || c == '\n' || c == '\r') { first = i; break; }
        }
        if (first < 0) return value;

        var clean = new StringBuilder(value.Length);
        clean.Append(value, 0, first);
        for (int i = first; i < value.Length; i++)
        {
            char c = value[i];
            if (c != '\t' && c != '\n' && c != '\r') clean.Append(c);
        }
        return clean.ToString();
    }
}
