/*
 * Everything CI checks, on your machine, in one command: `bun run check`.
 *
 * The workflow in `.github/workflows/build.yml` runs exactly these, in this order. Keeping a local
 * copy is not duplication for its own sake — it is the difference between "the tree is good" and
 * "GitHub says the tree is good", and on the day this was written GitHub spent an afternoon in a
 * major outage and could not say anything at all. A gate you can only run on somebody else's
 * infrastructure is a gate that is down when their infrastructure is.
 *
 * If you change a step here, change it there. They are meant to be the same list.
 */
import { spawnSync } from 'node:child_process';

interface Step {
  readonly name: string;
  readonly argv: readonly string[];
  /** Why a failure here matters, printed when it does. */
  readonly meaning: string;
}

const steps: readonly Step[] = [
  {
    name: 'editor artifact is in sync',
    argv: ['bun', 'run', 'build:editor'],
    meaning: 'the editor page could not be rebuilt from its sources',
  },
  {
    name: 'editor artifact is committed',
    argv: ['git', 'diff', '--exit-code', '--', 'mod/ValeLoot/editor/ValeLoot-editor.html'],
    meaning:
      'the committed editor page is STALE. It is a build output of src/filter and\n' +
      '  tools/valeloot-editor, and shipping it stale means an editor whose parser\n' +
      '  disagrees with the mod. Run `bun run build:editor` and commit the result.',
  },
  {
    name: 'filter sources type-check',
    argv: [
      './node_modules/.bin/tsc', '--noEmit', '--strict',
      '--target', 'es2022', '--module', 'esnext',
      '--moduleResolution', 'bundler', '--allowImportingTsExtensions',
      'src/filter/loot-dsl.ts', 'src/filter/loot-filter.ts',
    ],
    meaning: 'the vendored parser does not type-check; nothing else would notice',
  },
  {
    name: 'filter language conformance',
    argv: ['bun', 'tools/conformance/run.ts'],
    meaning: 'the mod and the editor read the same filter file differently',
  },
  {
    name: 'plugin builds',
    argv: ['dotnet', 'build', 'mod/ValeLoot/ValeLoot.csproj', '-c', 'Release', '--nologo', '-v', 'quiet'],
    meaning: 'the mod does not compile',
  },
  {
    name: 'plugin packages',
    argv: ['dotnet', 'build', 'mod/ValeLoot/ValeLoot.csproj', '-c', 'Release', '-t:Package', '--nologo', '-v', 'quiet'],
    meaning: '-t:Package is broken, so a release cannot be cut',
  },
];

let failed = 0;
for (const step of steps) {
  const started = Date.now();
  const [command, ...args] = step.argv;
  const result = spawnSync(command!, args, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  const seconds = ((Date.now() - started) / 1000).toFixed(1);

  if (result.status === 0) {
    console.log(`  ok    ${step.name}  (${seconds}s)`);
    continue;
  }

  failed += 1;
  console.log(`  FAIL  ${step.name}  (${seconds}s)`);
  console.log(`  ${step.meaning}`);
  const output = `${result.stdout ?? ''}${result.stderr ?? ''}`.trim();
  if (output) console.log(output.split('\n').map((l) => `    ${l}`).join('\n'));
  console.log('');
}

if (failed) {
  console.log(`${failed} of ${steps.length} checks failed.`);
  process.exit(1);
}
console.log(`all ${steps.length} checks passed.`);
