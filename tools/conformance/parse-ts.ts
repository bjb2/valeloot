/*
 * The EDITOR's parser, pointed at the same folder of filter files, emitting the same canonical JSON
 * as `Program.cs`.
 *
 * Byte-for-byte identical output is the whole contract, so this file is deliberately dull: same key
 * order, same defaults, same null-vs-empty choices. Anywhere it has to translate — `slotTypes` to
 * `types`, `statMode` to `statsAll`, `highlight` to `level` — the translation is named, because a
 * silent one would hide exactly the drift this corpus exists to find.
 *
 * Fields the overlay has and the mod does not (`verdicts`, `unknown`, `minSharedStats`) are left
 * out. They are not part of the shared language: the mod cannot express them and never will, so
 * comparing them would fail forever for no reason.
 */
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { parseLootFilter } from '../../src/filter/loot-dsl.ts';
import type { LootRule } from '../../src/filter/loot-filter.ts';

const directory = process.argv[2];
if (!directory) {
  console.error('usage: bun parse-ts.ts <cases-directory>');
  process.exit(2);
}

/** `JSON.stringify` for one string, which is exactly what the C# side hand-rolls. */
const str = (value: string): string => JSON.stringify(value);

const nullableStrings = (values: readonly string[] | undefined): string =>
  values === undefined ? 'null' : `[${values.map(str).join(', ')}]`;

const num = (value: number | undefined): string => (value === undefined ? 'null' : String(value));
const bool = (value: boolean | undefined): string => (value === undefined ? 'null' : String(value));

function rule(r: LootRule): string {
  const w = r.when ?? {};
  const stats = (w.stats ?? []).map(
    (s) => `{"stat": ${str(s.stat)}, "minRollPct": ${num(s.minRollPct)}, "minValue": ${num(s.minValue)}}`,
  );

  return (
    `{"name": ${str(r.name ?? '')}` +
    // Lower-cased on both sides: a file may spell a hex any way it likes, and the parsers are not
    // required to agree about its case — only about the colour.
    `, "color": ${str((r.color ?? '').toLowerCase())}` +
    `, "label": ${str(r.label ?? '')}` +
    // `highlight` here, `Level` (an int) there. Both render to the same three words.
    `, "level": ${str(r.highlight ?? 'dot')}` +
    `, "sound": ${r.sound === undefined ? 'null' : str(r.sound)}` +
    `, "mute": ${r.mute === true}` +
    `, "when": {"names": ${nullableStrings(w.names)}` +
    // `slotTypes` here, `Types` there. The same list of item types under two names.
    `, "types": ${nullableStrings(w.slotTypes)}` +
    `, "minRefine": ${num(w.minRefine)}` +
    `, "minTopRolls": ${num(w.minTopRolls)}` +
    `, "maxTopRolls": ${num(w.maxTopRolls)}` +
    `, "minAvgRoll": ${num(w.minAvgRoll)}` +
    `, "maxAvgRoll": ${num(w.maxAvgRoll)}` +
    // `statMode: 'any' | 'all'` here, a `StatsAll` boolean there. Absent means all, on both sides.
    `, "statsAll": ${w.statMode !== 'any'}` +
    `, "hasChaos": ${bool(w.hasChaos)}` +
    `, "favorite": ${bool(w.favorite)}` +
    `, "overRoll": ${bool(w.overRoll)}` +
    `, "stats": [${stats.join(', ')}]}}`
  );
}

const files = readdirSync(directory).filter((f) => f.endsWith('.txt')).sort();
const documents = files.map((file) => {
  const name = file.replace(/\.txt$/, '');
  const parsed = parseLootFilter(readFileSync(join(directory, file), 'utf8'));

  const lines = parsed.errors.map((e) => e.line).sort((a, b) => a - b);

  return (
    `  {"case": ${str(name)}` +
    `, "threshold": ${parsed.threshold ?? 90}` +
    `, "pinned": ${nullableStrings(parsed.overrides.pin)}` +
    `, "muted": ${nullableStrings(parsed.overrides.mute)}` +
    `, "errorLines": [${lines.join(', ')}]` +
    `, "rules": [${parsed.rules.map(rule).join(', ')}]}`
  );
});

process.stdout.write(`[\n${documents.join(',\n')}\n]\n`);
