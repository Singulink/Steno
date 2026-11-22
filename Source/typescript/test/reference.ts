// A plain reference codec of the Steno Encoding, version 1, written from FORMAT.md and kept apart from the library. It rebuilds the layout before
// every code point, so it is slow, but each step can be checked against the text of the specification. The tests use it as an oracle.

type Window = number; // the first code point of the window, or -1 for none

interface RefState {
  ctx: 'general' | 'cjk' | 'hangul';
  main: Window;
  second: Window;
  slot: 'letters' | 'hiragana' | 'katakana';
  conf: number;
  pending: Window;
}

export interface RefRange {
  lo: number;
  hi: number;
  size: number;
  first: number;
  leads: number;
}

export function refWindow(c: number): Window {
  const block = c - (c % 0x80);
  if (c <= 0xbf) return -1;
  if (c <= 0x13f) return 0xc0;
  if (c <= 0x1bf) return 0x140;
  if (c <= 0x23f) return 0x1c0;
  if (c <= 0x2bf) return 0x240;
  if (c <= 0x2ff) return 0x2c0;
  if (c <= 0x36f) return 0x300;
  if (c <= 0x37f) return -1;
  if (c <= 0x4ff) return block;
  if (c <= 0x52f) return 0x500;
  if (c <= 0x58f) return 0x530;
  if (c <= 0x5ff) return 0x590;
  if (c <= 0x1fff) return block;
  if (c <= 0x20ff) return -1;
  if (c <= 0x2fff) return block;
  if (c <= 0x30ff) return -1;
  if (c <= 0x33ff) return block;
  if (c <= 0x9fff) return -1;
  if (c <= 0xabff) return block;
  if (c <= 0xdfff) return -1;
  if (c <= 0x1ffff) return block;
  if (c <= 0x3ffff) return -1;
  return block;
}

function refWindowEnd(w: Window): number {
  const ends: Record<number, number> = { 0x2c0: 0x2ff, 0x300: 0x36f, 0x500: 0x52f, 0x530: 0x58f, 0x590: 0x5ff };
  return ends[w] ?? w + 0x7f;
}

function initial(): RefState {
  return { ctx: 'general', main: 0xc0, second: -1, slot: 'letters', conf: 0, pending: -1 };
}

export function refLayout(s: RefState): RefRange[] {
  let segs: [number, number, number][] = [[0, 0x10ffff, 0]];

  const piece = (lo: number, hi: number, size: number): void => {
    if (lo > hi) return;
    const next: [number, number, number][] = [];

    for (const g of segs) {
      if (g[1] < lo || g[0] > hi) {
        next.push(g);
        continue;
      }

      if (g[0] < lo) next.push([g[0], lo - 1, g[2]]);
      if (g[1] > hi) next.push([hi + 1, g[1], g[2]]);
    }

    next.push([lo, hi, size]);
    next.sort((a, b) => a[0] - b[0]);
    segs = next;
  };

  const m0 = s.main;
  const m1 = refWindowEnd(s.main);

  if (s.ctx === 'general') {
    piece(Math.max(0x80, m0 - 248), m0 - 1, 2);
    if (m1 < 0x10ffff) piece(m1 + 1, Math.min(0x10ffff, m1 + 248), 2);
  }

  piece(0x00, 0x1f, 2);
  piece(0x09, 0x0a, 1);
  piece(0x0d, 0x0d, 1);

  if (s.ctx === 'cjk' && s.slot !== 'letters') {
    piece(0x20, 0x40, 1);
    piece(0x41, 0x7e, 2);
  } else {
    piece(0x20, 0x7e, 1);
  }

  piece(0x7f, 0xbf, 2);
  if (s.ctx !== 'cjk') piece(0x80, 0xeff, 2);
  piece(0x2000, 0x20cf, 2);
  if (s.second >= 0) piece(s.second, refWindowEnd(s.second), 2);

  if (s.ctx === 'general') {
    piece(m0, m1, 1);
  } else if (s.ctx === 'hangul') {
    piece(m0, m1, 2);
    piece(0x3000, 0x303f, 2);
    piece(0xac00, 0xd7a3, 2);
    piece(0xff00, 0xffff, 2);
  } else {
    piece(m0, m1, 2);
    piece(0x3000, 0x300f, 1);
    piece(0x3010, 0x30ff, 2);
    if (s.slot === 'hiragana') piece(0x3041, 0x3096, 1);
    if (s.slot === 'katakana') piece(0x30a1, 0x30fc, 1);
    piece(0x4e00, 0x9fff, 2);
    piece(0xff00, 0xffff, 2);
    if (s.slot === 'letters') piece(0xff00, 0xff1f, 1);
  }

  const sized: [number, number, number][] = [];

  for (let i = 0; i < segs.length; ) {
    const g = segs[i]!;

    if (g[2] !== 0) {
      sized.push(g);
      i++;
      continue;
    }

    const lo = g[0];
    let hi = g[1];
    i++;

    while (i < segs.length && segs[i]![2] === 0) hi = segs[i++]![1];

    let start = lo;

    // Section 6.3: the runs U+0000..U+10FBF, U+10FC0..U+1FFFF and U+20000..U+10FFFF.
    for (const bound of [0x10fc0, 0x20000]) {
      if (start < bound && hi >= bound) {
        sized.push([start, bound - 1, 0]);
        start = bound;
      }
    }

    sized.push([start, hi, 0]);
  }

  for (const p of sized) {
    if (p[2] === 0) {
      const n = p[1] - p[0] + 1;
      p[2] = n <= 248 ? 2 : n <= 248 * 248 ? 3 : 4;
    }
  }

  const ranges: RefRange[] = [];
  let lead = 1;

  for (let j = 0; j < sized.length; ) {
    const [lo, , size] = sized[j]!;
    let hi = sized[j]![1];
    j++;

    while (j < sized.length && sized[j]![2] === size && sized[j]![0] === hi + 1) hi = sized[j++]![1];

    const leads = Math.ceil((hi - lo + 1) / 248 ** (size - 1));
    ranges.push({ lo, hi, size, first: lead, leads });
    lead += leads;
  }

  if (lead - 1 > 254) throw new Error('layout needs more than 254 lead bytes');
  return ranges;
}

function refUpdate(s: RefState, c: number, k: number): void {
  const inr = (lo: number, hi: number): boolean => lo <= c && c <= hi;

  if (c <= 0x7f) {
    if (s.ctx === 'cjk' && (inr(0x41, 0x5a) || inr(0x61, 0x7a))) s.slot = 'letters';
    return;
  }

  if (inr(0xac00, 0xd7a3)) {
    s.pending = -1;
    s.ctx = 'hangul';
    return;
  }

  if (inr(0x3040, 0x30ff) || inr(0x3400, 0x9fff) || inr(0x20000, 0x3ffff) || (inr(0x3000, 0x303f) && s.ctx !== 'hangul')) {
    if (s.ctx !== 'cjk') {
      s.ctx = 'cjk';
      s.slot = 'letters';
    }

    s.pending = -1;
    if (inr(0x3041, 0x3096)) s.slot = 'hiragana';
    else if (inr(0x30a1, 0x30fa)) s.slot = 'katakana';
    return;
  }

  if (s.ctx !== 'general' && inr(0xff00, 0xffff)) return;

  const w = refWindow(c);
  if (w < 0) return;

  if (s.ctx === 'general' && w === s.main) {
    s.conf = Math.min(s.conf + 1, 2);
    return;
  }

  if (k > 2) s.second = w;

  if (s.ctx === 'general') {
    if (s.conf > 0) {
      s.conf--;
      return;
    }
  } else if (s.pending !== w) {
    s.pending = w;
    return;
  }

  if (s.main !== w) s.second = s.main;
  else if (s.second === w) s.second = -1;
  s.main = w;
  s.ctx = 'general';
  s.pending = -1;
  s.conf = 0;
}

/** Encodes code points; throws for a sequence that is not a value. */
export function refEncode(cps: readonly number[]): number[] {
  for (let i = 0; i < cps.length; i++) {
    const c = cps[i]!;
    if (c < 0 || c > 0x10ffff) throw new Error('not a code point');
    if (i > 0 && cps[i - 1]! >= 0xd800 && cps[i - 1]! <= 0xdbff && c >= 0xdc00 && c <= 0xdfff) throw new Error('not a value');
  }

  const s = initial();
  const out: number[] = [];

  for (const c of cps) {
    const r = refLayout(s).find((x) => x.lo <= c && c <= x.hi)!;
    const o = c - r.lo;
    const per = 248 ** (r.size - 1);
    const q = Math.floor(o / per);
    let rest = o - q * per;
    out.push(r.first + q);

    for (let e = r.size - 2; e >= 0; e--) {
      const p = 248 ** e;
      const t = Math.floor(rest / p);
      rest -= t * p;
      out.push(t + 8);
    }

    refUpdate(s, c, r.size);
  }

  return out;
}

/** Decodes bytes into code points, or gives the reason of the first rule they break. */
export function refDecode(bytes: ArrayLike<number>): { cps: number[] } | { reason: string; offset: number } {
  const s = initial();
  const cps: number[] = [];
  let pos = 0;

  while (pos < bytes.length) {
    const layout = refLayout(s);
    const start = pos;
    const b = bytes[pos]!;
    if (b === 0) return { reason: 'zero-byte', offset: start };

    const r = layout.find((x) => x.first <= b && b < x.first + x.leads);
    if (r === undefined) return { reason: 'unused-lead', offset: start };

    pos++;
    let o = b - r.first;

    for (let i = 0; i < r.size - 1; i++) {
      if (pos >= bytes.length) return { reason: 'truncated', offset: start };
      const u = bytes[pos++]!;
      if (u === 0) return { reason: 'zero-byte', offset: start };
      if (u <= 7) return { reason: 'invalid-trail', offset: start };
      o = o * 248 + (u - 8);
    }

    const c = r.lo + o;
    if (c > r.hi) return { reason: 'past-range', offset: start };

    const prev = cps[cps.length - 1];
    if (prev !== undefined && prev >= 0xd800 && prev <= 0xdbff && c >= 0xdc00 && c <= 0xdfff) return { reason: 'surrogate-pair', offset: start };

    cps.push(c);
    refUpdate(s, c, pos - start);
  }

  return { cps };
}
