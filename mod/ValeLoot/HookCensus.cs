using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ValeLoot;

/// <summary>
/// The boot-time hook census: resolve every il2cpp target we care about BY NAME, report N/M, and
/// stream the result. A game patch that renames or moves something must show up here as a loud
/// "resolved 5/7" on day one — never as a mod that half-works with stale assumptions. Silence is
/// the failure mode we are buying our way out of: a highlight that stops appearing is otherwise
/// indistinguishable from a rule that stopped matching.
///
/// The list below is exactly what this mod binds, and nothing more. There is no transport or packet
/// entry here, because there is no code in this plugin that would use one — the editor's loopback
/// listener is not attached to the game at all, so it has nothing to census. `PlayerSave` IS here,
/// three times over: `Update` is the per-frame Unity message the main-thread tick rides on, and
/// `Data` is the head of the chain the pickup watcher walks to `CharacterData.Inventory` and the two
/// uid-keyed dictionaries under it. Those are the only player-side entries in this list, and they are
/// here because a rename in that chain is a mod that plays no loot sounds and says nothing about it.
/// </summary>
internal static class HookCensus
{
    /// <summary>Where the game's own managed code lives.</summary>
    public static readonly string[] GameAssemblies =
    {
        "Assembly-CSharp-firstpass.dll",
        "Assembly-CSharp.dll",
    };

    /// <summary>TextMeshPro ships under either name depending on how the package was imported.</summary>
    public static readonly string[] TextAssemblies =
    {
        "Unity.TextMeshPro.dll",
        "TextMeshPro.dll",
    };

    public sealed record Target(string Label, string Namespace, string Class, string? Method);

    /// <summary>
    /// The classes this mod hooks or reads. Methods are censused when named, otherwise class
    /// presence is the check — the exact overload is resolved at install time, by parameter type,
    /// where the hook is actually applied.
    /// </summary>
    public static readonly Target[] Targets =
    {
        // The inventory paint surface. `UIInventoryTab_Equips` stands in for the tab family whose
        // repaint methods carry the paint pass; the two fields it needs are censused below, by name,
        // because a rename there leaves highlighting silently painting nothing.
        new("UIInventoryItem", "", "UIInventoryItem", null),
        new("UIInventoryTab_Equips", "", "UIInventoryTab_Equips", null),
        // The hover surface: the handler that populates the tooltip, and the text component the line
        // is appended to on its way out.
        new("HoverInfoHandler", "", "HoverInfoHandler", null),
        new("TMPro.TMP_Text", "TMPro", "TMP_Text", null),
        // The data behind a cell. `RefinableItemData` is the base both equipment and artifacts derive,
        // `StatData` is one substat line, and `StatType` names it — a filter that says `Stat Agi >= 90%`
        // is only meaningful because that enum resolved, so a MISS here has to be visible at boot.
        new("RefinableItemData", "", "RefinableItemData", null),
        new("InventoryItemData", "", "InventoryItemData", null),
        new("StatData", "", "StatData", null),
        new("StatType", "", "StatType", null),
        // The item catalog: the client's own config database, and the two `Formula` helpers that
        // answer a substat's legal range without walking a value-type dictionary. `App` is the way
        // in — a rename there costs the absolute stat form, item display names and the generated
        // reference file all at once, so it is counted rather than discovered.
        new("App", "", "App", null),
        new("GameServerRuntime", "", "GameServerRuntime", null),
        new("EquipConfig", "", "EquipConfig", null),
        new("EquipSubstatRuntime", "", "EquipSubstatRuntime", null),
        new("EquipType", "", "EquipType", null),
        new("Formula", "", "Formula", null),
        // The editor's per-frame tick, which the pickup watcher also rides. A Unity message, because
        // the engine dispatches those from OUTSIDE the assembly and they can be neither inlined away
        // nor routed around — a hook that resolved, applied and never fired is a mistake this project
        // has already paid for. Losing it costs the F8 hotkey, the live bag in the served editor and
        // every loot sound; it costs nothing that is drawn.
        new("PlayerSave.Update (editor tick)", "", "PlayerSave", "Update"),
        // The pickup path: the player's own character and the bag hanging off it. These are DATA
        // classes, read on that same tick, and they are the reason a sound can fire with the panel
        // shut. A rename here is silence, so it is counted rather than discovered.
        new("CharacterData", "", "CharacterData", null),
        new("InventoryData", "", "InventoryData", null),
    };

    public sealed record Result(string Name, bool Ok, string? Detail);

    public static List<Result> Run(Action<string> log)
    {
        var results = new List<Result>();
        foreach (var t in Targets)
        {
            string[] assemblies = t.Namespace == "TMPro" ? TextAssemblies : GameAssemblies;
            IntPtr klass = Il2CppMeta.FindClass(t.Namespace, t.Class, assemblies);
            bool ok = klass != IntPtr.Zero;
            string? detail = null;
            if (ok && t.Method is not null)
            {
                var m = Il2CppMeta.FindMethod(klass, t.Method);
                ok = m is not null;
                detail = m is null ? $"class found, method {t.Method} missing" : null;
            }
            if (ok && t.Method is null)
            {
                int methods = Il2CppMeta.Methods(klass).Count;
                detail = $"{methods} methods";
            }
            results.Add(new Result(t.Label, ok, detail));
            log($"census {(ok ? "ok " : "MISS")} {t.Label}{(detail is null ? "" : $" ({detail})")}");
        }

        // `InventoryItemsUID` is the uid -> cell map the paint pass walks, and it is declared on the
        // GENERIC base, so it cannot be censused as a Target (a class lookup by name only reaches the
        // concrete tab). Resolving it up the hierarchy here keeps it in the N/M count, because a patch
        // that renames it leaves highlighting silently painting nothing.
        IntPtr equipTab = Il2CppMeta.FindClass("", "UIInventoryTab_Equips", GameAssemblies);
        int uidMap = equipTab == IntPtr.Zero ? -1 : Il2CppMeta.FieldOffsetUp(equipTab, "InventoryItemsUID");
        results.Add(new Result(
            "UIInventoryTab.InventoryItemsUID (uid -> cell)",
            uidMap >= 0,
            uidMap >= 0 ? $"offset 0x{uidMap:x}" : "field not found on the equip tab hierarchy"));
        log($"census {(uidMap >= 0 ? "ok " : "MISS")} UIInventoryTab.InventoryItemsUID (uid -> cell)");

        // The overlay the paint pass writes. Censused as a FIELD for the same reason: it is read by name
        // at install, and a rename is the difference between highlighting and a silent no-op.
        IntPtr cell = Il2CppMeta.FindClass("", "UIInventoryItem", GameAssemblies);
        int highlight = cell == IntPtr.Zero ? -1 : Il2CppMeta.FieldOffset(cell, "Highlight");
        results.Add(new Result(
            "UIInventoryItem.Highlight (cell overlay)",
            highlight >= 0,
            highlight >= 0 ? $"offset 0x{highlight:x}" : "field not found on UIInventoryItem"));
        log($"census {(highlight >= 0 ? "ok " : "MISS")} UIInventoryItem.Highlight (cell overlay)");

        // The item's own identity, and the one field the hover path reads. `UID` is an auto-property,
        // so the FIELD is `<UID>k__BackingField` — asking for "UID" returns -1 and reads exactly like
        // "the game renamed it", which is the mistake this census exists to make impossible.
        IntPtr refinable = Il2CppMeta.FindClass("", "RefinableItemData", GameAssemblies);
        int uid = refinable == IntPtr.Zero ? -1 : Il2CppMeta.PropertyFieldOffset(refinable, "UID");
        results.Add(new Result(
            "RefinableItemData.<UID>k__BackingField (item identity)",
            uid >= 0,
            uid >= 0 ? $"offset 0x{uid:x}" : "backing field not found on RefinableItemData"));
        log($"census {(uid >= 0 ? "ok " : "MISS")} RefinableItemData.<UID>k__BackingField (item identity)");

        // What the cell holds, and what a rule reads off it. `Data` is the whole chain's first link: a
        // rename there is a filter that matches nothing at all, so it is counted rather than discovered.
        int data = cell == IntPtr.Zero ? -1 : Il2CppMeta.PropertyFieldOffset(cell, "Data");
        results.Add(new Result(
            "UIInventoryItem.Data (the item behind the cell)",
            data >= 0,
            data >= 0 ? $"offset 0x{data:x}" : "field not found on UIInventoryItem"));
        log($"census {(data >= 0 ? "ok " : "MISS")} UIInventoryItem.Data (the item behind the cell)");

        /**
         * The substat line, and the enum that names it.
         *
         * `StatData.Value` is the ROLL PERCENTAGE, not the number the game prints — that is derived from
         * it and a base cap. Every roll condition in the filter language reads this one field, so it is
         * censused by name and by offset.
         *
         * The enum is counted, not listed: its ordinal order MOVES between builds, which is exactly why
         * the reader enumerates it live instead of carrying a table. Zero members means every `Stat`
         * line in the player's filter is dead, and that must be visible at boot.
         */
        IntPtr statData = Il2CppMeta.FindClass("", "StatData", GameAssemblies);
        int roll = statData == IntPtr.Zero ? -1 : Il2CppMeta.PropertyFieldOffset(statData, "Value");
        results.Add(new Result(
            "StatData.Value (substat roll %)",
            roll >= 0,
            roll >= 0 ? $"offset 0x{roll:x}" : "field not found on StatData"));
        log($"census {(roll >= 0 ? "ok " : "MISS")} StatData.Value (substat roll %)");

        IntPtr statType = Il2CppMeta.FindClass("", "StatType", GameAssemblies);
        int statNames = statType == IntPtr.Zero ? 0 : Il2CppMeta.EnumValues(statType).Count;
        results.Add(new Result(
            "StatType members (stat names for `Stat` lines)",
            statNames > 0,
            statNames > 0 ? $"{statNames} members" : "enum did not resolve"));
        log($"census {(statNames > 0 ? "ok " : "MISS")} StatType members ({statNames})");

        /**
         * The catalog chain, field by field and method by method.
         *
         * The VALUE of `App.ServerRuntime` is deliberately not read here: the client has not loaded
         * its configs when a plugin boots, so it is legitimately null and reading it would report a
         * MISS for a thing that is about to work. What is censused is that the FIELD exists — that
         * is the part a game patch can take away, and the part whose absence is permanent.
         */
        IntPtr app = Il2CppMeta.FindClass("", "App", GameAssemblies);
        int serverRuntime = app == IntPtr.Zero ? -1 : Il2CppMeta.FieldOffset(app, "ServerRuntime");
        results.Add(new Result(
            "App.ServerRuntime (the game's own config database)",
            serverRuntime >= 0,
            serverRuntime >= 0 ? $"static offset 0x{serverRuntime:x}" : "static field not found on App"));
        log($"census {(serverRuntime >= 0 ? "ok " : "MISS")} App.ServerRuntime (the game's own config database)");

        IntPtr serverRuntimeClass = Il2CppMeta.FindClass("", "GameServerRuntime", GameAssemblies);
        int equips = serverRuntimeClass == IntPtr.Zero ? -1 : Il2CppMeta.FieldOffset(serverRuntimeClass, "Equips");
        results.Add(new Result(
            "GameServerRuntime.Equips (id -> EquipConfig)",
            equips >= 0,
            equips >= 0 ? $"offset 0x{equips:x}" : "field not found on GameServerRuntime"));
        log($"census {(equips >= 0 ? "ok " : "MISS")} GameServerRuntime.Equips (id -> EquipConfig)");

        // Declared four classes up, on `BaseConfig`, so this is the walk-up form. It is the field
        // that makes `Name "Vampiric Fang Clip"` work and the reference file worth reading.
        IntPtr equipConfig = Il2CppMeta.FindClass("", "EquipConfig", GameAssemblies);
        int displayName = equipConfig == IntPtr.Zero ? -1 : Il2CppMeta.FieldOffsetUp(equipConfig, "DisplayName");
        results.Add(new Result(
            "BaseConfig.DisplayName (the name a rule can spell)",
            displayName >= 0,
            displayName >= 0 ? $"offset 0x{displayName:x}" : "field not found on the EquipConfig hierarchy"));
        log($"census {(displayName >= 0 ? "ok " : "MISS")} BaseConfig.DisplayName (the name a rule can spell)");

        /**
         * The two static helpers that turn a roll into the number the game prints.
         *
         * Resolved by parameter TYPE, because arity is not a signature — the lesson that cost this
         * project a game process. The census asserts exactly what `ItemCatalog` binds, so a game
         * patch that changes either signature reports here rather than at the first `Stat Agi >= 3`.
         */
        IntPtr formula = Il2CppMeta.FindClass("", "Formula", GameAssemblies);
        var substatConfig = Il2CppMeta.FindOverload(formula, "GetSubstatConfig", "EquipConfig");
        results.Add(new Result(
            "Formula.GetSubstatConfig (item -> substat pool)",
            substatConfig is not null,
            substatConfig is null ? "no (EquipConfig) overload on Formula" : null));
        log($"census {(substatConfig is not null ? "ok " : "MISS")} Formula.GetSubstatConfig (item -> substat pool)");

        var substatRange = Il2CppMeta.FindMethod(formula, "GetSubstatRange", m =>
            m.ParamCount == 4 && m.ParamTypeNames[0] == "StatType" && m.ParamTypeNames[1] == "EquipSubstatRuntime");
        results.Add(new Result(
            "Formula.GetSubstatRange (the base cap behind a roll)",
            substatRange is not null,
            substatRange is null ? "no (StatType, EquipSubstatRuntime, out, out) overload on Formula" : null));
        log($"census {(substatRange is not null ? "ok " : "MISS")} Formula.GetSubstatRange (the base cap behind a roll)");

        // Counted, not listed, for the same reason as StatType: its ordinal order MOVES between
        // builds, which is why the catalog enumerates it live. Zero members means every `Type` line
        // falls back to the cell's text, which is a quieter failure than it sounds.
        IntPtr equipType = Il2CppMeta.FindClass("", "EquipType", GameAssemblies);
        int equipTypes = equipType == IntPtr.Zero ? 0 : Il2CppMeta.EnumValues(equipType).Count;
        results.Add(new Result(
            "EquipType members (type names for `Type` lines)",
            equipTypes > 0,
            equipTypes > 0 ? $"{equipTypes} members" : "enum did not resolve"));
        log($"census {(equipTypes > 0 ? "ok " : "MISS")} EquipType members ({equipTypes})");

        /**
         * The editor hotkey, read the only way this mod can read a key: the engine's own `Input`.
         *
         * Resolved by parameter TYPE — `GetKeyDown` also has a `(System.String)` overload, and
         * resolving an engine overload on arity is what took this game's process down once. `KeyCode`
         * is counted rather than listed for the same reason `StatType` is: the ordinal for `F8` is read
         * live, never hardcoded, so zero members means the configured key cannot be resolved at all.
         *
         * `Input` lives in `UnityEngine.InputLegacyModule.dll` on a modern Unity and in
         * `UnityEngine.CoreModule.dll`/`UnityEngine.dll` on older ones, so all three are tried.
         */
        IntPtr input = Il2CppMeta.FindClass("UnityEngine", "Input",
            "UnityEngine.InputLegacyModule.dll", "UnityEngine.CoreModule.dll", "UnityEngine.dll");
        var getKeyDown = Il2CppMeta.FindOverload(input, "GetKeyDown", "UnityEngine.KeyCode");
        results.Add(new Result(
            "Input.GetKeyDown(KeyCode) (the editor hotkey)",
            getKeyDown is not null,
            getKeyDown is null ? "no (KeyCode) overload on UnityEngine.Input" : null));
        log($"census {(getKeyDown is not null ? "ok " : "MISS")} Input.GetKeyDown(KeyCode) (the editor hotkey)");

        IntPtr keyCode = Il2CppMeta.FindClass("UnityEngine", "KeyCode",
            "UnityEngine.CoreModule.dll", "UnityEngine.dll", "UnityEngine.InputLegacyModule.dll");
        int keyCodes = keyCode == IntPtr.Zero ? 0 : Il2CppMeta.EnumValues(keyCode).Count;
        results.Add(new Result(
            "KeyCode members (the name you put in Editor/Hotkey)",
            keyCodes > 0,
            keyCodes > 0 ? $"{keyCodes} members" : "enum did not resolve"));
        log($"census {(keyCodes > 0 ? "ok " : "MISS")} KeyCode members ({keyCodes})");

        /**
         * The hops from the frame hook's `self` to the dictionaries the pickup watcher diffs:
         * `PlayerSave.Data` -> `CharacterData.Inventory` -> `InventoryData.Equips`/`Artifacts`/
         * `Cards`/`Gems`.
         *
         * Every one of them is a `{ get; set; }`, so the FIELD is `<Name>k__BackingField` and the
         * plain-name lookup returns -1 — indistinguishable from a rename, which is the mistake this
         * census exists to make impossible. The first three must resolve or no loot sound plays at
         * all; the last three each cost only their own kind of pickup. A player's pasted log is the
         * only evidence of which one moved.
         */
        IntPtr playerSave = Il2CppMeta.FindClass("", "PlayerSave", GameAssemblies);
        IntPtr characterData = Il2CppMeta.FindClass("", "CharacterData", GameAssemblies);
        IntPtr inventoryData = Il2CppMeta.FindClass("", "InventoryData", GameAssemblies);

        CensusField(results, log, "PlayerSave.<Data> (your live character)", playerSave, "Data");
        CensusField(results, log, "CharacterData.<Inventory> (the bag behind it)", characterData, "Inventory");
        CensusField(results, log, "InventoryData.<Equips> (uid -> equipment)", inventoryData, "Equips");
        CensusField(results, log, "InventoryData.<Artifacts> (uid -> artifact)", inventoryData, "Artifacts");
        CensusField(results, log, "InventoryData.<Cards> (id -> card stack)", inventoryData, "Cards");
        CensusField(results, log, "InventoryData.<Gems> (uid -> gem)", inventoryData, "Gems");

        return results;
    }

    /**
     * One auto-property's backing field, counted and logged in the census's own shape.
     *
     * `PropertyFieldOffset` tries the plain name first and then `<Name>k__BackingField`, so this says
     * "resolved" for either spelling and MISS only when the game really has moved it.
     */
    private static void CensusField(List<Result> results, Action<string> log, string label, IntPtr klass, string property)
    {
        int offset = klass == IntPtr.Zero ? -1 : Il2CppMeta.PropertyFieldOffset(klass, property);
        results.Add(new Result(
            label,
            offset >= 0,
            offset >= 0 ? $"offset 0x{offset:x}" : $"no {property} field on the class hierarchy"));
        log($"census {(offset >= 0 ? "ok " : "MISS")} {label}{(offset >= 0 ? $" (offset 0x{offset:x})" : "")}");
    }

    public static string ToJson(List<Result> results, string at)
    {
        return JsonSerializer.Serialize(new
        {
            kind = "census",
            resolved = results.Count(r => r.Ok),
            total = results.Count,
            hooks = results.Select(r => new { name = r.Name, ok = r.Ok, detail = r.Detail }),
            at,
        });
    }
}
