/**
 * The types `loot-dsl.ts` and `loot-filter.ts` borrow from their siblings, declared locally.
 *
 * VENDORED SHAPES, NOT THE ORIGINALS. In the project these two parser modules were written for they
 * import these from `inventory.ts`, `upgrades.ts` and `loot-actions.ts` — modules that carry a whole
 * inventory pipeline this repository has no use for. Every one of those imports is an `import type`,
 * erased before a single byte reaches the bundle, so dragging the modules in would add code that
 * cannot run and a dependency that cannot be justified.
 *
 * So they are re-declared here, deliberately as narrow as the two files' actual usage: `OwnedGear`
 * and `RollLine` list the fields the matcher READS and nothing else. TypeScript is structural, so a
 * caller holding a fuller item still satisfies these — the narrowing costs a caller nothing and
 * costs a reader of this repository three modules they would otherwise have to be handed.
 *
 * Every hand-written type is a place this can drift from the code it was copied from, which is the
 * argument for keeping the list short rather than the argument for copying more.
 */

/** How an item compared to the piece you are wearing. Set by the caller, not by this parser. */
export type Verdict = 'upgrade' | 'better-rolls' | 'sidegrade' | 'worse';

/**
 * Per-item overrides, keyed by ITEM ID.
 *
 * `pin` beats `mute` when an id is somehow in both: a contradictory config resolves towards showing
 * the player something rather than towards silence, because a missing highlight is invisible and
 * therefore undebuggable.
 */
export interface ItemOverrides {
  /** Always highlight, whatever the rules say. */
  pin: readonly string[];
  /** Never highlight and never make a sound. */
  mute: readonly string[];
}

/** One substat line on an item — only the fields the matcher tests. */
export interface RollLine {
  stat: string;
  /** Scaled value as the game shows it. */
  base: number;
  /** 0..100 where the roll sits in this stat's legal range; null when it cannot be placed. */
  rollPct: number | null;
  /** The chaos slot (last index, equipment only). */
  isChaos: boolean;
  /** Value above the pool maximum — only reachable by a Chaos widen. */
  over: boolean;
}

/** An owned item — only the fields the matcher tests, plus the two identity fields it indexes on. */
export interface OwnedGear {
  uid: string | null;
  itemId: string;
  name: string;
  /** Item's own slot type (`Chest`, `Pistol`, …). */
  slotType: string;
  refine: number;
  lines: RollLine[];
  /** Lines at or above the caller's threshold. "Triple top roll" = 3. */
  topRolls: number;
  /** Mean roll percentage across placeable lines; null when none could be placed. */
  avgRoll: number | null;
  favorite: boolean;
  /** Set when the caller's catalog has no such item (unreleased, or renamed). */
  unknown?: true;
}
