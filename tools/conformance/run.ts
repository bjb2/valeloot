/*
 * Run both parsers over the same corpus and fail if they disagree.
 *
 * ## Why this exists
 *
 * The filter language has TWO implementations: `mod/ValeLoot/FilterParser.cs`, which decides what
 * your bag actually looks like, and `src/filter/loot-dsl.ts`, which the editor bundles to tell you
 * what it will look like. A third copy — the page in `mod/ValeLoot/editor/` — is generated from the
 * second, and CI already fails if it is stale.
 *
 * Nothing kept those two honest. When `Name` grew from a single string into a comma-separated list,
 * all three had to be edited by hand in lockstep, and the failure mode if one is missed is silent:
 * `Name "a", "b"` parses in one as two names and in the other as ONE name spelled `"a", "b"`, which
 * matches nothing. No error, no log line — the editor shows a bag the mod does not paint.
 *
 * So: a corpus of filter files, parsed by both, normalised to one canonical JSON document, compared
 * byte for byte.
 *
 * ## What is compared, and what is not
 *
 * Everything the two implementations both claim to understand. Not error MESSAGES — they are worded
 * for different readers and always will be — but the LINES they reject, because a block one accepts
 * and the other refuses is a real divergence. Not the overlay-only condition fields, which the mod
 * cannot express by design.
 *
 * A disagreement here is not a test that needs updating. It is two programs that read the same file
 * differently, which is the bug.
 */
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, '..', '..');
const cases = join(here, 'cases');

/**
 * The shipped default filter, extracted from the C# verbatim string that produces it.
 *
 * It earns its place as a case because it is the ONE filter file guaranteed to exist on every
 * install: every player has it before they have written anything. If the two parsers disagree about
 * the file the mod itself writes, they disagree for everybody.
 *
 * Read out of `FilterFile.cs` rather than copied, so it cannot drift from what is actually shipped.
 * C# escapes a quote inside a verbatim string by doubling it.
 */
function extractDefaultFilter(): string {
  const source = readFileSync(join(repo, 'mod', 'ValeLoot', 'FilterFile.cs'), 'utf8');
  const start = source.indexOf('DefaultFilter = @"');
  if (start < 0) throw new Error('could not find DefaultFilter in FilterFile.cs — has it been renamed?');

  let at = start + 'DefaultFilter = @"'.length;
  let out = '';
  for (;;) {
    if (at >= source.length) throw new Error('unterminated DefaultFilter verbatim string');
    const c = source[at];
    if (c === '"') {
      if (source[at + 1] === '"') { out += '"'; at += 2; continue; }
      break;
    }
    out += c;
    at += 1;
  }
  return out;
}

const staging = mkdtempSync(join(tmpdir(), 'valeloot-conformance-'));
writeFileSync(join(staging, '00-shipped-default.txt'), extractDefaultFilter());
for (const file of new Bun.Glob('*.txt').scanSync(cases)) {
  writeFileSync(join(staging, file), readFileSync(join(cases, file)));
}

const csharp = spawnSync(
  'dotnet',
  ['run', '--project', join(here, 'Conformance.csproj'), '-v', 'quiet', '--', staging],
  { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 },
);
if (csharp.status !== 0) {
  console.error('the C# harness failed:\n' + (csharp.stderr || csharp.stdout));
  process.exit(1);
}

const typescript = spawnSync('bun', [join(here, 'parse-ts.ts'), staging], {
  encoding: 'utf8',
  maxBuffer: 64 * 1024 * 1024,
});
if (typescript.status !== 0) {
  console.error('the TypeScript harness failed:\n' + (typescript.stderr || typescript.stdout));
  process.exit(1);
}

const mod = JSON.parse(csharp.stdout);
const editor = JSON.parse(typescript.stdout);

/**
 * Walk both documents together and report the PATH of every leaf that differs.
 *
 * A line diff is useless here: one case is a single 4 KB line, so "line 3 differs" means "somewhere
 * in this rule set, good luck". The path is the whole value of the tool — `03-types-and-stats /
 * rules[4] / when.stats[0].minRollPct` says what to go and read.
 */
function compare(a: unknown, b: unknown, path: string, out: string[]): void {
  if (Array.isArray(a) && Array.isArray(b)) {
    if (a.length !== b.length) {
      out.push(`${path}: mod has ${a.length} item(s), editor has ${b.length}`);
      return;
    }
    a.forEach((item, i) => compare(item, b[i], `${path}[${i}]`, out));
    return;
  }
  if (a && b && typeof a === 'object' && typeof b === 'object') {
    const keys = new Set([...Object.keys(a), ...Object.keys(b)]);
    for (const key of keys) {
      compare(
        (a as Record<string, unknown>)[key],
        (b as Record<string, unknown>)[key],
        path ? `${path}.${key}` : key,
        out,
      );
    }
    return;
  }
  if (a !== b) out.push(`${path}: mod ${JSON.stringify(a)}, editor ${JSON.stringify(b)}`);
}

const problems: string[] = [];
for (let i = 0; i < Math.max(mod.length, editor.length); i++) {
  const name = mod[i]?.case ?? editor[i]?.case ?? `#${i}`;
  compare(mod[i], editor[i], name, problems);
}

if (problems.length) {
  console.error(
    `the two parsers disagree in ${problems.length} place(s):\n\n` +
    problems.map((p) => `  ${p}`).join('\n') +
    '\n\nThis is not a test to update. The mod and the editor read the same filter file\n' +
    'differently, so one of them is showing a player something that is not true.\n',
  );
  process.exit(1);
}

console.log(`filter conformance: ${mod.length} case(s), the mod and the editor agree`);
