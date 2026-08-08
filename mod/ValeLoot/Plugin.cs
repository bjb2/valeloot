using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;

namespace ValeLoot;

/// <summary>
/// ValeLoot: your loot filter, inside the game.
///
/// You write rules in `BepInEx/config/valeloot-filter.txt`, or in the mod's own editor on F8. The mod
/// colours your inventory cells by those rules, names the rule that claimed an item when you hover
/// it, and makes a noise the moment you pick a match up — bag open or closed. That is the entire mod,
/// and it is COMPLETE ON ITS OWN — no companion app, no account, nothing to install alongside it and
/// nothing to connect to. Unzip it, launch, press F8.
///
/// ## The editor, and the socket it needs
///
/// Press F8 and your default browser opens on `http://127.0.0.1:38512/`, which is the mod's own rule
/// editor with your real bag, your real rules and the game's real item catalog already in it. That
/// needs a listener, so this plugin HAS one, and the honest version of the old claim is:
///
/// It binds **127.0.0.1 only** — never `0.0.0.0`, never a LAN interface — so nothing off this machine
/// can reach it. It serves four things and no others: its own editor page (compiled into this DLL),
/// a JSON snapshot of your own rules/bag/catalog, a save endpoint that writes your own rule file, and
/// a health probe. It carries **no game traffic**, hooks nothing on the game's network path, and
/// contains **no packet capture** — there is no code here that could observe a game packet. Nothing
/// leaves your machine: there is no outbound request anywhere in the plugin. `Editor/Enabled = false`
/// turns it off, and everything else keeps working. See <see cref="EditorServer"/>.
///
/// An earlier version of this file claimed "opens no sockets and contains no network code of any
/// kind". That was true then and is false now, so it has been rewritten rather than softened — a
/// disclosure a player has to infer is not a disclosure. What has NOT changed: no game RPCs, no input
/// simulation, no automation of play. It reads the inventory UI and it draws on it.
///
/// The removed socket's own last lesson still applies and is why the editor's JSON is escaped by hand
/// with care: a report went out over that socket and was silently dropped by a JSON parser for a
/// whole round while the mod cheerfully logged that it had sent it.
///
/// ## Boot order, and why it is this way
///
/// Filter and sounds first, because they are what the mod IS and they need nothing from il2cpp. Then
/// the census, so a rename shows up as a loud MISS before anything tries to use the thing that moved.
/// Then the reader, the paint, the tooltip and the pickup watcher, each reporting what it resolved
/// to. On a future game build that breaks this mod, those lines are the difference between "ValeLoot
/// is broken" and "UIInventoryItem.Highlight moved", and a player can paste them without knowing what
/// either means.
/// </summary>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "com.savi.valeloot";
    public const string PluginName = "ValeLoot";
    public const string PluginVersion = "0.1.0";

    private ConfigEntry<bool>? _sound;
    private ConfigEntry<bool>? _note;
    private ConfigEntry<bool>? _tint;
    private ConfigEntry<int>? _tintDepth;
    private ConfigEntry<bool>? _editor;
    private ConfigEntry<int>? _editorPort;
    private ConfigEntry<string>? _editorHotkey;
    private ConfigEntry<bool>? _bagIndicator;
    private ConfigEntry<int>? _bagYellowPercent;
    private ConfigEntry<int>? _bagRedPercent;
    private ConfigEntry<float>? _bagTintStrength;

    public override void Load()
    {
        // The disclaimer travels with the mod, verbatim, at every surface — README, and here.
        Log.LogWarning("ValeLoot is a client-side loot-presentation mod. Not endorsed by the developer; "
                     + "use at your own risk — you are responsible for your own account.");

        /**
         * Tuning, in `BepInEx/config/com.savi.valeloot.cfg`. Every presentation feature has its own
         * switch because a player on a game build newer than this mod needs a way to turn a broken
         * piece off without waiting for a release — a marker that lands on the wrong object is worse
         * than no marker. The three `[Editor]` entries exist because a listener nobody can switch off
         * is not something to install on someone else's machine.
         */
        _tint = Config.Bind("Highlight", "TintCell", true,
            "Colour the inventory cell itself. Off leaves the game's own plain highlight, and the hover note still works.");
        _tintDepth = Config.Bind("Highlight", "TintDepth", 2,
            "How many levels below the cell's highlight object to colour (0-4). Only change this if cells stop colouring after a game update.");
        _note = Config.Bind("Highlight", "HoverNote", true,
            "Add a line to the item tooltip naming the rule that claimed the item.");
        _sound = Config.Bind("Sound", "Enabled", true,
            "Play a sound when you pick up an item matching a rule with a `Sound` line — bag open or closed.");
        _bagIndicator = Config.Bind("Bag Indicator", "Enabled", true,
            "Tint the HUD inventory button yellow above the first threshold and red above the second.");
        _bagYellowPercent = Config.Bind("Bag Indicator", "YellowPercent", 60,
            "Turn the inventory button yellow above this carried-weight percentage (1-98).");
        _bagRedPercent = Config.Bind("Bag Indicator", "RedPercent", 80,
            "Turn the inventory button red above this carried-weight percentage; must exceed YellowPercent (2-99).");
        _bagTintStrength = Config.Bind("Bag Indicator", "TintStrength", 0.85f,
            "Strength of the yellow or red warning tint (0.10-1.00).");
        _editor = Config.Bind("Editor", "Enabled", true,
            "Serve the rule editor to your browser on 127.0.0.1. It is reachable only from this machine, "
          + "carries no game traffic and sends nothing anywhere. False opens no port at all; the hotkey "
          + "then opens the copy of the editor in this folder as a plain file instead.");
        _editorPort = Config.Bind("Editor", "Port", EditorServer.DefaultPort,
            "Port on 127.0.0.1 for the editor. Change it if something else already has this one — the log "
          + "says so plainly at boot, and every other feature works regardless.");
        _editorHotkey = Config.Bind("Editor", "Hotkey", EditorServer.DefaultHotkey,
            "Key that opens the editor in your default browser. Any UnityEngine.KeyCode name: F8, F9, "
          + "Insert, Backslash, Home...");

        LootSound.Enabled = _sound.Value;
        TooltipInject.Enabled = _note.Value;

        /**
         * The rules, before anything else.
         *
         * If this is a first run it writes a starter filter, because an empty file is indistinguishable
         * from a broken mod: the first launch should light something up so the player knows the plumbing
         * is sound before they start writing rules of their own.
         */
        FilterFile.Install(Paths.ConfigPath, m => Log.LogInfo(m));
        LootSound.Install(Paths.ConfigPath, m => Log.LogInfo(m));

        /**
         * The census, into the log.
         *
         * Every class and field this mod needs, named, with the offset it resolved to — because the way
         * this breaks on a future game build is one rename, and the difference between "ValeLoot is
         * broken" and "UIInventoryItem.Highlight moved" is this block of lines in a log a player can paste.
         */
        HookCensus.Run(m => Log.LogInfo(m));

        /**
         * The reader is the difference between a filter and a guess: without it, no rule can be
         * evaluated against anything. It is fatal to the feature, so it is reported as an error rather
         * than a warning, and the paint hooks are still installed — a mod that draws nothing but is
         * clearly installed is easier to diagnose than one that vanished.
         */
        if (!ItemReader.Install(m => Log.LogInfo(m)))
        {
            Log.LogError("item reader did not install; no item will match any rule this session.");
        }

        /**
         * The game's own item catalog, for the absolute stat form and for the generated
         * `valeloot-items.txt` a player greps to spell things.
         *
         * Optional in the same way the tint is: without it names and types fall back to the text on
         * the cell and everything that worked before still works. It resolves LAZILY — the client
         * has not loaded its configs yet at this point, so `Install` binding metadata is all that
         * can happen here, and "catalog ready: N equips" arrives later, once.
         */
        if (!ItemCatalog.Install(Paths.ConfigPath, m => Log.LogInfo(m)))
        {
            Log.LogWarning("item catalog did not install; `Stat <name> >= <number>` rules will match nothing, "
                         + "and no item reference file will be written.");
        }
        Log.LogInfo(ItemCatalog.Status());

        /**
         * The bag snapshot, `valeloot-bag.txt`, which is how the editor shows real per-rule counts.
         * It is written from the paint pass, so it appears the first time you open your bag and not
         * before, and a stale one from last session is cleared here. The served editor reads the same
         * accumulated rows in memory (`BagSnapshot.PublishToEditor`), so the file is what remains for
         * `Editor/Enabled = false` and for the page opened as a plain file.
         */
        BagSnapshot.Install(Paths.ConfigPath, m => Log.LogInfo(m));

        /**
         * The feature the mod exists for.
         *
         * Failing to install is not fatal to the process, but it must be LOUD: a filter that lights
         * nothing up looks exactly like a filter that matches nothing.
         */
        if (!InventoryPaint.Install(m => Log.LogInfo(m)))
        {
            Log.LogWarning("inventory paint did not install; nothing will be highlighted in game this session.");
        }
        InventoryPaint.Configure(_tint.Value, _tintDepth.Value);
        Log.LogInfo(InventoryPaint.Status());

        /**
         * The hover note. Optional in the same way the tint is: a highlight without an explanation is
         * still a highlight, so a failure here is reported and survived rather than fatal.
         */
        if (!TooltipInject.Install(m => Log.LogInfo(m)))
        {
            Log.LogWarning("tooltip inject did not install; highlights will not say which rule matched.");
        }
        Log.LogInfo(TooltipInject.Status());

        /**
         * The pickup watcher, which is what makes a loot sound a LOOT sound.
         *
         * It diffs the player's inventory data by uid on the editor's per-frame tick, so an item
         * landing in a closed bag still makes its noise. Installed here, after the reader and the
         * filter it depends on and before the tick that drives it, and reported by one line saying
         * which offsets it resolved to — on a game build that moves any of them, that line is the
         * difference between "the sounds broke" and "CharacterData.Inventory moved".
         *
         * A failure costs the sound and nothing else, and it deliberately does NOT fall back to the
         * old paint-driven ping: pinging on a repaint is a different feature wearing this one's name.
         */
        if (!InventoryWatch.Install(m => Log.LogInfo(m)))
        {
            Log.LogWarning("pickup watch did not install; no loot sound will play this session. "
                         + "Highlighting and the hover note are unaffected.");
        }
        Log.LogInfo(InventoryWatch.Status());

        /**
         * The always-visible bag fullness warning. It reads the same weight and limit helpers as the
         * game's inventory panel and rides the existing main-thread tick; losing it costs only the
         * warning. It tints only the button's existing Graphics rather than creating Unity objects.
         */
        if (!BagFillIndicator.Install(_bagIndicator.Value, _bagYellowPercent.Value, _bagRedPercent.Value,
                                      _bagTintStrength.Value, m => Log.LogInfo(m)) && _bagIndicator.Value)
        {
            Log.LogWarning("bag fill indicator did not install; the HUD inventory button will be unchanged.");
        }
        Log.LogInfo(BagFillIndicator.Status());

        /**
         * The editor, and the only socket in this plugin.
         *
         * LAST on purpose, and after the paint hooks rather than before them. It is the piece a player
         * can lose entirely and still have the mod they installed: if the port is busy, or if they set
         * `Enabled = false`, the highlight, the hover note, the sounds and the hot reload are all
         * already installed and reported by the time this runs. Its own failure is one plain log line
         * naming the port, and it hands F8 the `file://` copy instead.
         *
         * It is also where the disclosure lands in the log, at the boot of every session, because a
         * player who installs a cosmetic mod is owed the sentence "this opened a port" without having
         * to read a README to find it.
         */
        EditorServer.Install(Paths.ConfigPath, _editor.Value, _editorPort.Value, _editorHotkey.Value,
                             m => Log.LogInfo(m));
    }

    public override bool Unload()
    {
        // The editor first: it owns a background thread and a port, and both have to be gone before
        // anything it reads is torn down under it.
        EditorServer.Uninstall();
        BagFillIndicator.Uninstall();
        TooltipInject.Uninstall();
        InventoryPaint.Uninstall();
        InventoryWatch.Uninstall();
        ItemReader.Uninstall();
        ItemCatalog.Uninstall();
        BagSnapshot.Uninstall();
        FilterFile.Uninstall();
        return true;
    }
}
