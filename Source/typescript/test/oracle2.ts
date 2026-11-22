// A second independent plain codec of the Steno Encoding, version 1, from an adversarial review of this implementation. It is ported from
// Source/dotnet/Singulink.Steno.Tests/ReferenceSteno.cs (written from FORMAT.md alone). It rebuilds the layout from the pieces before every code point.
// It shares no code with src/ or test/reference.ts.
// It was checked against all 7,640 published vectors and all 145,573 large vectors.

const R = 248;
const G = 0;
const CJK = 1;
const HANGUL = 2;
const LET = 0;
const HIR = 1;
const KAT = 2;

interface RState {
  ctx: number;
  slot: number;
  main: number;
  second: number;
  conf: number;
  pending: number;
}

interface RRange {
  lo: number;
  hi: number;
  size: number;
  first: number;
  leads: number;
}

type Seg = [number, number, number];

export function refWindowOf(c: number): number {
  const blk = (x: number): number => x - (x % 0x80);
  if (c <= 0xbf) return -1;
  if (c <= 0x13f) return 0xc0;
  if (c <= 0x1bf) return 0x140;
  if (c <= 0x23f) return 0x1c0;
  if (c <= 0x2bf) return 0x240;
  if (c <= 0x2ff) return 0x2c0;
  if (c <= 0x36f) return 0x300;
  if (c <= 0x37f) return -1;
  if (c <= 0x4ff) return blk(c);
  if (c <= 0x52f) return 0x500;
  if (c <= 0x58f) return 0x530;
  if (c <= 0x5ff) return 0x590;
  if (c <= 0x1fff) return blk(c);
  if (c <= 0x20ff) return -1;
  if (c <= 0x2fff) return blk(c);
  if (c <= 0x30ff) return -1;
  if (c <= 0x33ff) return blk(c);
  if (c <= 0x9fff) return -1;
  if (c <= 0xabff) return blk(c);
  if (c <= 0xdfff) return -1;
  if (c <= 0x1ffff) return blk(c);
  if (c <= 0x3ffff) return -1;
  return blk(c);
}

function windowEndOf(s: number): number {
  switch (s) {
    case 0x2c0:
      return 0x2ff;
    case 0x300:
      return 0x36f;
    case 0x500:
      return 0x52f;
    case 0x530:
      return 0x58f;
    case 0x590:
      return 0x5ff;
    default:
      return s + 0x7f;
  }
}

const memo = new Map<string, RRange[]>();

function buildLayout(s: RState): RRange[] {
  const key = `${s.ctx}|${s.ctx === CJK ? s.slot : 0}|${s.main}|${s.second}`;
  const cached = memo.get(key);
  if (cached !== undefined) return cached;

  let segs: Seg[] = [[0, 0x10ffff, 0]];

  const piece = (lo: number, hi: number, size: number): void => {
    if (lo > hi) return;
    const next: Seg[] = [];

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
  const m1 = windowEndOf(s.main);

  if (s.ctx === G) {
    piece(Math.max(0x80, m0 - R), m0 - 1, 2);
    if (m1 < 0x10ffff) piece(m1 + 1, Math.min(0x10ffff, m1 + R), 2);
  }

  piece(0, 0x1f, 2);
  piece(9, 0xa, 1);
  piece(0xd, 0xd, 1);

  if (s.ctx === CJK && s.slot !== LET) {
    piece(0x20, 0x40, 1);
    piece(0x41, 0x7e, 2);
  } else {
    piece(0x20, 0x7e, 1);
  }

  piece(0x7f, 0xbf, 2);
  if (s.ctx !== CJK) piece(0x80, 0xeff, 2);
  piece(0x2000, 0x20cf, 2);
  if (s.second >= 0) piece(s.second, windowEndOf(s.second), 2);

  if (s.ctx === G) {
    piece(m0, m1, 1);
  } else if (s.ctx === HANGUL) {
    piece(m0, m1, 2);
    piece(0x3000, 0x303f, 2);
    piece(0xac00, 0xd7a3, 2);
    piece(0xff00, 0xffff, 2);
  } else {
    piece(m0, m1, 2);
    piece(0x3000, 0x300f, 1);
    piece(0x3010, 0x30ff, 2);
    if (s.slot === HIR) piece(0x3041, 0x3096, 1);
    if (s.slot === KAT) piece(0x30a1, 0x30fc, 1);
    piece(0x4e00, 0x9fff, 2);
    piece(0xff00, 0xffff, 2);
    if (s.slot === LET) piece(0xff00, 0xff1f, 1);
  }

  const part = (lo: number, hi: number): Seg => {
    const n = hi - lo + 1;
    return [lo, hi, n <= R ? 2 : n <= R * R ? 3 : 4];
  };

  const sized: Seg[] = [];

  for (let i = 0; i < segs.length; ) {
    const seg = segs[i]!;

    if (seg[2] !== 0) {
      sized.push(seg);
      i++;
      continue;
    }

    const lo = seg[0];
    let hi = seg[1];
    i++;

    while (i < segs.length && segs[i]![2] === 0) {
      hi = segs[i]![1];
      i++;
    }

    let start = lo;

    for (const b of [0x10fc0, 0x20000]) {
      if (start < b && hi >= b) {
        sized.push(part(start, b - 1));
        start = b;
      }
    }

    sized.push(part(start, hi));
  }

  const ranges: RRange[] = [];
  let lead = 1;

  for (let j = 0; j < sized.length; ) {
    const [lo, hi0, size] = sized[j]!;
    let hi = hi0;
    j++;

    while (j < sized.length && sized[j]![2] === size && sized[j]![0] === hi + 1) {
      hi = sized[j]![1];
      j++;
    }

    const per = R ** (size - 1);
    const leads = Math.ceil((hi - lo + 1) / per);
    ranges.push({ lo, hi, size, first: lead, leads });
    lead += leads;
  }

  if (lead - 1 > 254) throw new Error('more than 254 lead bytes');
  memo.set(key, ranges);
  if (memo.size > 20000) memo.clear();
  return ranges;
}

const inr = (c: number, a: number, b: number): boolean => a <= c && c <= b;

function update(s: RState, c: number, n: number): void {
  if (c <= 0x7f) {
    if (s.ctx === CJK && (inr(c, 0x41, 0x5a) || inr(c, 0x61, 0x7a))) s.slot = LET;
    return;
  }

  if (inr(c, 0xac00, 0xd7a3)) {
    s.pending = -1;
    s.ctx = HANGUL;
    return;
  }

  if (inr(c, 0x3040, 0x30ff) || inr(c, 0x3400, 0x9fff) || inr(c, 0x20000, 0x3ffff) || (inr(c, 0x3000, 0x303f) && s.ctx !== HANGUL)) {
    if (s.ctx !== CJK) {
      s.ctx = CJK;
      s.slot = LET;
    }

    s.pending = -1;
    if (inr(c, 0x3041, 0x3096)) s.slot = HIR;
    else if (inr(c, 0x30a1, 0x30fa)) s.slot = KAT;
    return;
  }

  if (s.ctx !== G && inr(c, 0xff00, 0xffff)) return;

  const w = refWindowOf(c);
  if (w < 0) return;

  if (s.ctx === G && w === s.main) {
    s.conf = Math.min(s.conf + 1, 2);
    return;
  }

  if (n > 2) s.second = w;

  if (s.ctx === G) {
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
  s.ctx = G;
  s.pending = -1;
  s.conf = 0;
}

const fresh = (): RState => ({ ctx: G, slot: LET, main: 0xc0, second: -1, conf: 0, pending: -1 });

/** Encodes code points; throws RangeError for a sequence that is not a value. */
export function refEncode(cps: readonly number[]): Uint8Array {
  for (let i = 0; i < cps.length; i++) {
    const c = cps[i]!;
    if (!Number.isInteger(c) || c < 0 || c > 0x10ffff) throw new RangeError('not a code point');
    if (i > 0 && inr(cps[i - 1]!, 0xd800, 0xdbff) && inr(c, 0xdc00, 0xdfff)) throw new RangeError('not a value');
  }

  const s = fresh();
  const out: number[] = [];

  for (const c of cps) {
    const r = buildLayout(s).find((x) => x.lo <= c && c <= x.hi)!;
    const k = r.size;
    const p = R ** (k - 1);
    const o = c - r.lo;
    const q = Math.floor(o / p);
    let rest = o - q * p;
    out.push(r.first + q);

    for (let i = k - 2; i >= 0; i--) {
      const pp = R ** i;
      const t = Math.floor(rest / pp);
      rest -= t * pp;
      out.push(t + 8);
    }

    update(s, c, k);
  }

  return Uint8Array.from(out);
}

/** Decodes bytes; returns the code points, or the reason and the offset of the code point where the input is rejected. */
export function refDecode(bytes: Uint8Array): number[] | string {
  const s = fresh();
  const res: number[] = [];
  let pos = 0;

  while (pos < bytes.length) {
    const b = bytes[pos]!;
    if (b === 0) return `zero-byte@${pos}`;
    const rg = buildLayout(s).find((r) => r.first <= b && b < r.first + r.leads);
    if (rg === undefined) return `unused-lead@${pos}`;
    const start = pos++;
    let o = b - rg.first;

    for (let i = 0; i < rg.size - 1; i++) {
      if (pos >= bytes.length) return `truncated@${start}`;
      const u = bytes[pos]!;
      if (u === 0) return `zero-byte@${start}`;
      if (u < 8) return `invalid-trail@${start}`;
      o = o * R + (u - 8);
      pos++;
    }

    const c = rg.lo + o;
    if (c > rg.hi) return `past-range@${start}`;
    if (res.length > 0 && inr(res[res.length - 1]!, 0xd800, 0xdbff) && inr(c, 0xdc00, 0xdfff)) return `surrogate-pair@${start}`;
    res.push(c);
    update(s, c, pos - start);
  }

  return res;
}
