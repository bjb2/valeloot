/**
 * Build the standalone ValeLoot rule editor: one HTML file, no external references.
 *
 *     bun run build:editor
 *
 * WHY IT IS A COMMITTED ARTIFACT. `mod/ValeLoot/editor/ValeLoot-editor.html` is checked in and the
 * csproj's `Package` target copies it into the zip. That keeps the mod buildable with the .NET SDK
 * alone — a contributor with no bun installed can still produce a plugin — at the cost of a file
 * that can go stale. So this script is the only way it is produced, and it FAILS LOUDLY: a silently
 * stale artifact would ship a page whose validation disagrees with the mod, which is the one bug
 * this whole arrangement exists to prevent.
 *
 * WHY THE PAGE IS A REAL .html FILE. Assembling it from a TypeScript template literal was the
 * obvious shape and it is a trap: a single backtick or `${` in the inline script ends the literal,
 * and every escape inside it needs doubling for the rest of the file's life. So the page lives in
 * `valeloot-editor/editor.html`, written as ordinary HTML with ordinary JavaScript, and this script
 * only substitutes one marker. The emitted script bodies are then handed to `node --check` before
 * anything is written, so a syntax error is a failed build rather than a blank page a player finds.
 */
import { spawnSync } from 'node:child_process';
import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = dirname(here);
const SOURCE = join(here, 'valeloot-editor', 'editor.html');
const ENTRY = join(here, 'valeloot-editor', 'entry.ts');
const OUT = join(root, 'mod', 'ValeLoot', 'editor', 'ValeLoot-editor.html');
const MARKER = '/*@@PARSER@@*/';

function fail(message: string): never {
  console.error(`build:editor — ${message}`);
  process.exit(1);
}

/**
 * The parser, compiled from the modules in `src/filter/` that define the language.
 *
 * Not minified: this ships to players inside a mod zip, and someone will open it to check what a
 * file dropped in their game folder actually does. A page that answers that question in a second is
 * worth more than the bytes.
 */
const built = await Bun.build({ entrypoints: [ENTRY], target: 'browser', minify: false });
if (!built.success) {
  for (const log of built.logs) console.error(String(log));
  fail('the parser bundle did not compile — the editor was NOT rewritten, so the committed file is still the last good one');
}
if (built.outputs.length !== 1) fail(`expected one bundle chunk, got ${built.outputs.length}`);
const parser = await built.outputs[0]!.text();

/**
 * A bundle that still imports something would be a page that cannot run from `file://` — no server,
 * no module resolution. Cheap to assert, and the failure mode without it is a blank page.
 */
if (/^\s*(import|export)\s/m.test(parser)) {
  fail('the bundle carries an import or export, so it is not inlinable as a classic script');
}

const page = readFileSync(SOURCE, 'utf8');
if (!page.includes(MARKER)) fail(`${SOURCE} has no ${MARKER} injection point`);

/**
 * Wrapped, because two inline classic scripts share one global scope.
 *
 * The bundle hoists every module-level binding it inlined — `unquote`, `splitList`, `bound` — into
 * that scope, and the page happens to want some of those names too. The collision is not a warning:
 * `Identifier 'unquote' has already been declared` kills the whole page script before a line of it
 * runs, and the symptom is a first-run screen that renders and then does nothing. One wrapper makes
 * `VL` the only thing the bundle contributes, permanently.
 *
 * `</script` anywhere inside an inline script closes it, whatever the JavaScript thinks. Neither
 * side contains one today; splitting it keeps that from becoming a silent corruption the day one
 * does, and the split is invisible to the parser.
 */
const safe = (code: string): string => code.replace(/<\/script/gi, '<\\/script');
const html = page.replace(MARKER, () => `(function(){\n${safe(parser)}\n})();`);

// Nothing external, ever: the page has to work with the machine offline and the folder moved.
const external = html.match(/(?:https?:|\/\/)[^\s"')]+/gi)?.filter((hit) => !hit.startsWith('//')) ?? [];
if (external.length) fail(`the page references something outside itself: ${[...new Set(external)].join(', ')}`);

/**
 * Verify the emitted scripts parse, before the file is written.
 *
 * `node --check` on each `<script>` body: the failure this catches is a marker substitution that
 * lands mid-token, and it is worth catching here because the symptom in a browser is a page that
 * loads, renders its first-run screen and then does nothing at all.
 *
 * NODE, not `process.execPath`. Under `bun run` that path is bun, and bun's `--check` RUNS the file
 * — the browser script promptly died on `document is not defined`, which is a true statement about
 * bun and says nothing at all about whether the script parses.
 */
// These are source-format delimiters, not an HTML sanitizer. Requiring the two literal tags makes a
// malformed or attribute-bearing tag fail the build instead of approximating browser parsing with a regex.
const scriptOpen = '<script>';
const scriptClose = '</script>';
const scripts = html.split(scriptOpen).slice(1).map((body, index) => {
  const end = body.indexOf(scriptClose);
  if (end < 0) fail(`inline script ${index + 1} has no exact ${scriptClose} terminator`);
  return body.slice(0, end);
});
if (scripts.length !== 2) fail(`expected two inline scripts in the emitted page, found ${scripts.length}`);
for (const [index, code] of scripts.entries()) {
  const temp = join(here, `.editor-check-${index}.js`);
  writeFileSync(temp, code);
  const checked = spawnSync('node', ['--check', temp], { encoding: 'utf8', shell: process.platform === 'win32' });
  rmSync(temp, { force: true });
  if (checked.error) fail(`could not run \`node --check\`: ${checked.error.message}`);
  if (checked.status !== 0) fail(`script ${index + 1} does not parse:\n${(checked.stderr || '').trim()}`);
}

mkdirSync(dirname(OUT), { recursive: true });
writeFileSync(OUT, html);
const bytes = Buffer.byteLength(html);
console.log(`ValeLoot editor -> ${OUT}`);
console.log(`  ${bytes.toLocaleString('en-US')} bytes · parser ${parser.length.toLocaleString('en-US')} · no external references`);
