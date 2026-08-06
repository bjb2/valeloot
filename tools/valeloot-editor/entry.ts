/**
 * The parser bundle the standalone editor runs on — the SAME modules the mod's language is defined by.
 *
 * There are already two implementations of this language (this one, and the mod's C# in
 * `mod/ValeLoot/FilterParser.cs`) and the drift between them is a standing, documented cost. A third
 * one hand-written inside the editor page would make every grammar change a three-way merge, so the
 * page gets these bytes COMPILED rather than a lookalike. When the editor and the mod disagree about
 * a line, that disagreement is now a fact about two files, not three.
 *
 * Nothing here is exported on purpose: `Bun.build` emits an ES module, and a module with no exports
 * is also a valid classic script — which is what lets the whole bundle be inlined into one
 * `<script>` on a `file://` page with no import machinery. The page reaches it through the global.
 */
import { formatLootFilter, parseLootFilter } from '../../src/filter/loot-dsl.ts';
import {
  BUILTIN_LOOT_SOUNDS, LOOT_HIGHLIGHTS, matchLoot, matchesCondition, normalizeSoundName,
} from '../../src/filter/loot-filter.ts';

(globalThis as unknown as { VL: unknown }).VL = {
  parseLootFilter,
  formatLootFilter,
  matchLoot,
  matchesCondition,
  normalizeSoundName,
  LOOT_HIGHLIGHTS,
  BUILTIN_LOOT_SOUNDS,
};
