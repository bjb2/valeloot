using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ValeLoot;

/**
 * The mod's own parser, pointed at a folder of filter files, emitting one canonical JSON document.
 *
 * Its opposite number is `parse-ts.ts`, which does the same with the editor's parser. `run.ts`
 * compares the two byte for byte. Nothing here is clever on purpose: the moment this harness starts
 * interpreting, it stops being evidence about the parser and starts being evidence about itself.
 *
 * ## What `ItemCatalog` is doing here
 *
 * `LootFilter.Matches` consults the catalog to answer `Type` and `Stat <name> >= <value>` against a
 * live game. Nothing in a PARSE touches it, but the file will not compile without the symbol, and
 * linking the real one would drag in il2cpp and BepInEx — the whole reason this harness links three
 * files instead of referencing the plugin. So it is stubbed to the four members the parser and the
 * evaluator name, and it is never called: this corpus compares PARSES, not matches.
 */
internal static class ItemCatalog
{
    public const string ReferenceFileName = "valeloot-items.txt";

    public static string? TypeName(string itemId) => null;
    public static string? DisplayName(string itemId) => null;

    public static bool TryScaledValue(string itemId, int statType, int rollPct, out int value)
    {
        value = 0;
        return false;
    }
}

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: Conformance <cases-directory>");
            return 2;
        }

        string directory = args[0];
        if (!Directory.Exists(directory))
        {
            Console.Error.WriteLine($"no such directory: {directory}");
            return 2;
        }

        string[] files = Directory.GetFiles(directory, "*.txt");
        Array.Sort(files, StringComparer.Ordinal);

        var json = new StringBuilder();
        json.Append("[\n");
        for (int i = 0; i < files.Length; i++)
        {
            if (i > 0) json.Append(",\n");
            Emit(json, Path.GetFileNameWithoutExtension(files[i]), File.ReadAllText(files[i]));
        }
        json.Append("\n]\n");

        Console.Out.Write(json.ToString());
        return 0;
    }

    private static void Emit(StringBuilder json, string name, string text)
    {
        FilterParser.ParsedFilter parsed = FilterParser.Parse(text);

        json.Append("  {\"case\": ");
        Str(json, name);
        json.Append(", \"threshold\": ").Append(parsed.Threshold.ToString(CultureInfo.InvariantCulture));

        json.Append(", \"pinned\": ");
        Strings(json, parsed.Pinned);
        json.Append(", \"muted\": ");
        Strings(json, parsed.Muted);

        // LINES only. The two implementations word their messages for their own readers and always
        // will; what must never differ is which lines they refuse. Sorted NUMERICALLY — sorting the
        // rendered strings puts line 9 after line 30 and invents a mismatch out of nothing.
        var lines = new List<int>(parsed.Errors.Length);
        foreach (FilterParser.FilterError error in parsed.Errors) lines.Add(error.Line);
        lines.Sort();
        json.Append(", \"errorLines\": [").Append(string.Join(", ", lines)).Append(']');

        json.Append(", \"rules\": [");
        for (int i = 0; i < parsed.Rules.Length; i++)
        {
            if (i > 0) json.Append(", ");
            Rule(json, parsed.Rules[i]);
        }
        json.Append("]}");
    }

    private static void Rule(StringBuilder json, LootFilter.LootRule rule)
    {
        json.Append("{\"name\": ");
        Str(json, rule.Name);
        json.Append(", \"color\": ");
        // Lower-cased on both sides: the file may spell a hex any way it likes and the two parsers
        // are not required to agree about its case, only about the colour.
        Str(json, rule.Color.ToLowerInvariant());
        json.Append(", \"label\": ");
        Str(json, rule.Label);
        json.Append(", \"level\": ");
        Str(json, LootFilter.LevelName(rule.Level));
        json.Append(", \"sound\": ");
        if (rule.Sound is null) json.Append("null"); else Str(json, rule.Sound);
        json.Append(", \"mute\": ").Append(rule.Mute ? "true" : "false");

        LootFilter.LootCondition when = rule.When;
        json.Append(", \"when\": {\"names\": ");
        Strings(json, when.Names);
        json.Append(", \"types\": ");
        Strings(json, when.Types);
        json.Append(", \"minRefine\": ").Append(Int(when.MinRefine))
            .Append(", \"minTopRolls\": ").Append(Int(when.MinTopRolls))
            .Append(", \"maxTopRolls\": ").Append(Int(when.MaxTopRolls))
            .Append(", \"minAvgRoll\": ").Append(Int(when.MinAvgRoll))
            .Append(", \"maxAvgRoll\": ").Append(Int(when.MaxAvgRoll))
            .Append(", \"statsAll\": ").Append(when.StatsAll ? "true" : "false")
            .Append(", \"hasChaos\": ").Append(Bool(when.HasChaos))
            .Append(", \"favorite\": ").Append(Bool(when.Favorite))
            .Append(", \"overRoll\": ").Append(Bool(when.OverRoll));

        json.Append(", \"stats\": [");
        if (when.Stats is not null)
        {
            for (int i = 0; i < when.Stats.Length; i++)
            {
                if (i > 0) json.Append(", ");
                LootFilter.StatCondition stat = when.Stats[i];
                json.Append("{\"stat\": ");
                Str(json, stat.Stat);
                json.Append(", \"minRollPct\": ").Append(Int(stat.MinRollPct))
                    .Append(", \"minValue\": ").Append(Int(stat.MinValue))
                    .Append('}');
            }
        }
        json.Append("]}}");
    }

    private static string Int(int? value)
        => value is int number ? number.ToString(CultureInfo.InvariantCulture) : "null";

    private static string Bool(bool? value)
        => value is bool flag ? (flag ? "true" : "false") : "null";

    private static void Strings(StringBuilder json, string[]? values)
    {
        if (values is null) { json.Append("null"); return; }
        json.Append('[');
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0) json.Append(", ");
            Str(json, values[i]);
        }
        json.Append(']');
    }

    private static void Str(StringBuilder json, string value)
    {
        json.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                case '\n': json.Append("\\n"); break;
                case '\r': json.Append("\\r"); break;
                case '\t': json.Append("\\t"); break;
                default:
                    if (c < ' ') json.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else json.Append(c);
                    break;
            }
        }
        json.Append('"');
    }
}
