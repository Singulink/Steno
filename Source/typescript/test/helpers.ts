// Helpers for the tests: a seeded random generator, a generator of mixed-script text, and conversions.

/** A small seeded random generator (mulberry32), so that every run tests the same inputs. */
export class Random {
  #s: number;

  constructor(seed: number) {
    this.#s = seed >>> 0;
  }

  /** A number in [0, 1). */
  next(): number {
    let t = (this.#s = (this.#s + 0x6d2b79f5) >>> 0);
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  }

  /** An integer in [lo, hi]. */
  int(lo: number, hi: number): number {
    return lo + Math.floor(this.next() * (hi - lo + 1));
  }

  /** One element of an array. */
  pick<T>(items: readonly T[]): T {
    return items[this.int(0, items.length - 1)]!;
  }
}

// Pools of code points, as [first, last], that text is drawn from: scripts, controls, kana, kanji, Hangul, emoji, surrogates and the edges of
// windows and ranges.
const POOLS: readonly (readonly [number, number])[] = [
  [0x20, 0x7e], // ASCII
  [0x41, 0x5a], // ASCII letters
  [0x00, 0x1f], // controls
  [0x7f, 0xbf], // DEL, C1 controls and Latin-1 symbols
  [0xc0, 0x17f], // Latin-1 letters and Latin Extended-A
  [0x180, 0x2ff], // Latin Extended-B, IPA, modifiers
  [0x300, 0x36f], // combining marks
  [0x370, 0x3ff], // Greek
  [0x400, 0x52f], // Cyrillic
  [0x530, 0x5ff], // Armenian and Hebrew
  [0x600, 0x6ff], // Arabic
  [0x900, 0x97f], // Devanagari
  [0xe00, 0xe7f], // Thai
  [0x10a0, 0x10ff], // Georgian
  [0x1200, 0x137f], // Ethiopic
  [0x1f00, 0x1fff], // Greek Extended
  [0x2000, 0x20ff], // punctuation and currency
  [0x2100, 0x2fff], // symbols
  [0x3000, 0x303f], // CJK punctuation
  [0x3040, 0x309f], // hiragana
  [0x30a0, 0x30ff], // katakana
  [0x3100, 0x33ff], // bopomofo and CJK compatibility
  [0x3400, 0x4dbf], // CJK Extension A
  [0x4e00, 0x9fff], // CJK ideographs
  [0xa000, 0xabff], // Yi and others
  [0xac00, 0xd7a3], // Hangul syllables
  [0xd7a4, 0xd7ff], // after Hangul
  [0xd800, 0xdfff], // surrogate code points
  [0xe000, 0xf8ff], // private use
  [0xfe00, 0xfeff], // variation selectors and specials
  [0xff00, 0xffef], // fullwidth forms
  [0xfff0, 0xffff], // specials
  [0x10000, 0x1ffff], // plane 1
  [0x1f300, 0x1faff], // emoji
  [0x10f80, 0x11000], // the cut at U+10FC0
  [0x1ff80, 0x20080], // the cut at U+20000
  [0x20000, 0x2a6df], // CJK Extension B
  [0x30000, 0x3ffff], // plane 3
  [0x40000, 0x10ffff], // the rest
  [0x1000, 0x107f], // the main window of the fullest layout of FORMAT.md section 11.1
  [0xfff80, 0xfffff],
  [0x10ff80, 0x10ffff], // the last window
];

/**
 * Makes random text as code points: runs of one pool, so that windows are used for a while and then change. The result is always a value: a high
 * surrogate code point is never directly followed by a low one.
 */
export function randomCodePoints(rng: Random, maxLength: number): number[] {
  const length = rng.int(0, maxLength);
  const cps: number[] = [];

  while (cps.length < length) {
    const [lo, hi] = rng.pick(POOLS);
    const run = rng.int(1, 8);

    // A small part of a pool, so that the run stays in one window or two.
    const base = rng.int(lo, hi);
    const spread = rng.next() < 0.7 ? 0x7f : hi - lo;

    for (let i = 0; i < run && cps.length < length; i++) {
      const c = Math.min(hi, Math.max(lo, base + rng.int(-spread >> 1, spread)));
      const prev = cps[cps.length - 1];
      if (prev !== undefined && prev >= 0xd800 && prev <= 0xdbff && c >= 0xdc00 && c <= 0xdfff) continue;
      cps.push(c);
    }
  }

  return cps;
}

/** Converts code points to a string (a surrogate code point becomes a code unit of its own). */
export function toStringFromCodePoints(cps: readonly number[]): string {
  let s = '';
  for (const c of cps) s += String.fromCodePoint(c);
  return s;
}

/** Converts a string to code points as FORMAT.md section 3 reads UTF-16. */
export function codePointsOf(s: string): number[] {
  const cps: number[] = [];

  for (let i = 0; i < s.length; i++) {
    const u = s.charCodeAt(i);

    if (u >= 0xd800 && u <= 0xdbff && i + 1 < s.length) {
      const v = s.charCodeAt(i + 1);

      if (v >= 0xdc00 && v <= 0xdfff) {
        cps.push(((u - 0xd800) << 10) + (v - 0xdc00) + 0x10000);
        i++;
        continue;
      }
    }

    cps.push(u);
  }

  return cps;
}

/** Writes bytes as upper-case hexadecimal pairs without separators. */
export function hex(bytes: ArrayLike<number>): string {
  let s = '';
  for (let i = 0; i < bytes.length; i++) s += bytes[i]!.toString(16).padStart(2, '0').toUpperCase();
  return s;
}

/** Reads hexadecimal pairs. */
export function fromHex(s: string): Uint8Array {
  const bytes = new Uint8Array(s.length / 2);
  for (let i = 0; i < bytes.length; i++) bytes[i] = parseInt(s.substr(i * 2, 2), 16);
  return bytes;
}

/** Joins byte arrays. */
export function concat(parts: readonly Uint8Array[]): Uint8Array {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let pos = 0;

  for (const p of parts) {
    out.set(p, pos);
    pos += p.length;
  }

  return out;
}

/** Compares code point arrays lexicographically, a proper prefix being smaller. */
export function compareArrays(a: readonly number[], b: readonly number[]): number {
  const n = Math.min(a.length, b.length);

  for (let i = 0; i < n; i++) if (a[i] !== b[i]) return a[i]! < b[i]! ? -1 : 1;

  return Math.sign(a.length - b.length);
}
