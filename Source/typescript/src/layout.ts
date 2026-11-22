// The state, the windows, the layouts and the state update of the Steno Encoding, version 1 (FORMAT.md sections 4 to 7).
//
// This module is internal: the package's exports map does not expose it.

/** The general context. */
export const GENERAL = 0;

/** The CJK context. */
export const CJK = 1;

/** The Hangul context. */
export const HANGUL = 2;

/** The kana slot "letters". */
export const LETTERS = 0;

/** The kana slot "hiragana". */
export const HIRAGANA = 1;

/** The kana slot "katakana". */
export const KATAKANA = 2;

/** The window start of every 16 code points below U+3000 (all window boundaries there are multiples of 16), or -1 for none. */
const LOW_WINDOWS = /* @__PURE__ */ (() => {
  const t = new Int32Array(0x300);
  for (let i = 0; i < t.length; i++) t[i] = windowStart(i << 4);
  return t;
})();

/** The largest code point. */
const MAX_CODE_POINT = 0x10ffff;

/**
 * Gets the first code point of the window of a code point (FORMAT.md section 4), or -1 if it has none.
 *
 * @param c - A code point.
 * @returns The first code point of the window, or -1.
 */
export function windowStart(c: number): number {
  if (c <= 0x00bf) return -1;
  if (c <= 0x013f) return 0x00c0;
  if (c <= 0x01bf) return 0x0140;
  if (c <= 0x023f) return 0x01c0;
  if (c <= 0x02bf) return 0x0240;
  if (c <= 0x02ff) return 0x02c0;
  if (c <= 0x036f) return 0x0300;
  if (c <= 0x037f) return -1;
  if (c <= 0x04ff) return c & ~0x7f;
  if (c <= 0x052f) return 0x0500;
  if (c <= 0x058f) return 0x0530;
  if (c <= 0x05ff) return 0x0590;
  if (c <= 0x1fff) return c & ~0x7f;
  if (c <= 0x20ff) return -1;
  if (c <= 0x2fff) return c & ~0x7f;
  if (c <= 0x30ff) return -1;
  if (c <= 0x33ff) return c & ~0x7f;
  if (c <= 0x9fff) return -1;
  if (c <= 0xabff) return c & ~0x7f;
  if (c <= 0xdfff) return -1;
  if (c <= 0x1ffff) return c & ~0x7f;
  if (c <= 0x3ffff) return -1;
  return c & ~0x7f;
}

/**
 * Gets the last code point of a window from its first code point.
 *
 * @param start - The first code point of a window.
 * @returns The last code point of the window.
 */
export function windowEnd(start: number): number {
  switch (start) {
    case 0x02c0:
      return 0x02ff;
    case 0x0300:
      return 0x036f;
    case 0x0500:
      return 0x052f;
    case 0x0530:
      return 0x058f;
    case 0x0590:
      return 0x05ff;
    default:
      return start + 0x7f;
  }
}

/** The number of transitions that a layout remembers, and of layouts that a state remembers. */
export const TRANSITIONS = 4;

/** Gets the key of a layout in the cache: a number below 2^53 made of the fields that the layout depends on (FORMAT.md section 5). */
export function layoutKey(ctx: number, slot: number, main: number, second: number): number {
  return ((ctx * 3 + (ctx === CJK ? slot : LETTERS)) * 0x110000 + main) * 0x110001 + (second + 1);
}

// Templates that the tables of a layout are copied from.
const MINUS_ONES_I32 = /* @__PURE__ */ new Int32Array(256).fill(-1);
const MINUS_ONES_I16 = /* @__PURE__ */ new Int16Array(256).fill(-1);
const ZEROS_U8 = /* @__PURE__ */ new Uint8Array(256);

/**
 * The bytes of U+0000..U+007F that take 1 byte, or 0, for the layouts whose ASCII bytes are standard (all but the CJK layouts with the kana slot
 * hiragana or katakana), and for those others. The ASCII part of a layout depends only on that (FORMAT.md section 6.2, pieces 2 and 3), so the
 * layouts share these tables.
 */
const ASCII1_STANDARD = /* @__PURE__ */ (() => {
  const t = new Uint8Array(128);
  for (let c = 0; c < 128; c++) t[c] = c === 9 || c === 10 ? c - 7 : c === 13 ? 5 : c >= 0x20 && c <= 0x7e ? c - 0x19 : 0;
  return t;
})();

const ASCII1_KANA = /* @__PURE__ */ ASCII1_STANDARD.map((b, c) => (c >= 0x41 ? 0 : b));

/** The value of `Layout.decode1` that marks a code point of the main window in the general context (the update raises the confidence). */
export const DECODE1_CONFIDENCE = 0x200000;

/** The value of `Layout.decode1` that marks U+3000..U+30FF in the CJK context (the update sets the pending window to none). */
export const DECODE1_PENDING = 0x400000;

// ---------- The ranges of a state, computed without allocation ----------
//
// computeRanges writes the ranges of a state into these module arrays (JavaScript runs one thread per realm, and no caller code runs while they
// are in use). A layout copies them; a state that is used for one code point only resolves that code point from them and builds no layout.

const P_LO = new Int32Array(32);
const P_HI = new Int32Array(32);
const P_SIZE = new Int32Array(32);
let pieceCount = 0;
const EVENTS = new Int32Array(64);

/** The ranges of the last computeRanges call: first and last code point and size. */
export const R_LO = new Int32Array(64);
export const R_HI = new Int32Array(64);
export const R_SIZE = new Int32Array(64);
let rangeCount = 0;

/** The key of the state whose ranges R_LO, R_HI and R_SIZE hold, or -1. */
let rangesKey = -1;

/** Makes R_LO, R_HI and R_SIZE hold the ranges of a state, computing them only if they hold another state's. */
function ensureRanges(ctx: number, slot: number, main: number, second: number): number {
  const key = layoutKey(ctx, slot, main, second);

  if (key !== rangesKey) {
    computeRanges(ctx, slot, main, second);
    rangesKey = key;
  }

  return rangeCount;
}

function addPiece(lo: number, hi: number, size: number): void {
  P_LO[pieceCount] = lo;
  P_HI[pieceCount] = hi;
  P_SIZE[pieceCount] = size;
  pieceCount++;
}

/** Adds a run of one size, joined with the range before it if that has the same size and ends just before (FORMAT.md section 6.4). */
function addRun(lo: number, hi: number, size: number): void {
  const n = rangeCount;

  if (n > 0 && R_SIZE[n - 1] === size && R_HI[n - 1] === lo - 1) {
    R_HI[n - 1] = hi;
  } else {
    R_LO[n] = lo;
    R_HI[n] = hi;
    R_SIZE[n] = size;
    rangeCount = n + 1;
  }
}

function addPart(lo: number, hi: number): void {
  const n = hi - lo + 1;
  addRun(lo, hi, n <= 248 ? 2 : n <= 61504 ? 3 : 4);
}

/** Adds a stretch, split into parts at the cuts of FORMAT.md section 6.3: U+10FC0 (U+20000 - 61,504) and U+20000. */
function addStretch(lo: number, hi: number): void {
  let start = lo;

  if (start < 0x10fc0 && hi >= 0x10fc0) {
    addPart(start, 0x10fbf);
    start = 0x10fc0;
  }

  if (start < 0x20000 && hi >= 0x20000) {
    addPart(start, 0x1ffff);
    start = 0x20000;
  }

  addPart(start, hi);
}

/**
 * Computes the ranges of a state (FORMAT.md section 6.1, steps 1 to 3) into R_LO, R_HI and R_SIZE, in code point order.
 *
 * The pieces are listed in their order; then every elementary interval between the boundaries of the pieces takes the size of the last piece
 * that covers it (a later piece overwrites an earlier one), which is the overlay of section 6.2 without building it piece by piece.
 *
 * @returns The number of ranges.
 */
export function computeRanges(ctx: number, slot: number, main: number, second: number): number {
  rangesKey = -1;
  pieceCount = 0;
  rangeCount = 0;
  const m0 = main;
  const m1 = windowEnd(main);

  // Piece 1: the neighbours of the main window.
  if (ctx === GENERAL) {
    addPiece(Math.max(0x80, m0 - 248), m0 - 1, 2);
    if (m1 < MAX_CODE_POINT) addPiece(m1 + 1, Math.min(MAX_CODE_POINT, m1 + 248), 2);
  }

  // Piece 2.
  addPiece(0x00, 0x1f, 2);
  addPiece(0x09, 0x0a, 1);
  addPiece(0x0d, 0x0d, 1);

  // Piece 3.
  if (ctx === CJK && slot !== LETTERS) {
    addPiece(0x20, 0x40, 1);
    addPiece(0x41, 0x7e, 2);
  } else {
    addPiece(0x20, 0x7e, 1);
  }

  // Pieces 4 to 7.
  addPiece(0x7f, 0xbf, 2);
  if (ctx !== CJK) addPiece(0x80, 0xeff, 2);
  addPiece(0x2000, 0x20cf, 2);
  if (second >= 0) addPiece(second, windowEnd(second), 2);

  // Piece 8.
  if (ctx === GENERAL) {
    addPiece(m0, m1, 1);
  } else if (ctx === HANGUL) {
    addPiece(m0, m1, 2);
    addPiece(0x3000, 0x303f, 2);
    addPiece(0xac00, 0xd7a3, 2);
    addPiece(0xff00, 0xffff, 2);
  } else {
    addPiece(m0, m1, 2);
    addPiece(0x3000, 0x300f, 1);
    addPiece(0x3010, 0x30ff, 2);
    if (slot === HIRAGANA) addPiece(0x3041, 0x3096, 1);
    if (slot === KATAKANA) addPiece(0x30a1, 0x30fc, 1);
    addPiece(0x4e00, 0x9fff, 2);
    addPiece(0xff00, 0xffff, 2);
    if (slot === LETTERS) addPiece(0xff00, 0xff1f, 1);
  }

  // The events where a piece starts (at its first code point) or ends (after its last), as position * 64 + piece * 2 + (1 for a start), sorted
  // by an insertion sort (there are at most about 40). Sweeping them in order with a bit set of the pieces that cover the position gives every
  // elementary interval the size of the last piece that covers it: the highest bit.
  let ne = 0;

  for (let i = 0; i < pieceCount; i++) {
    EVENTS[ne++] = P_LO[i]! * 64 + i * 2 + 1;
    EVENTS[ne++] = (P_HI[i]! + 1) * 64 + i * 2;
  }

  for (let i = 1; i < ne; i++) {
    const v = EVENTS[i]!;
    let j = i - 1;

    while (j >= 0 && EVENTS[j]! > v) {
      EVENTS[j + 1] = EVENTS[j]!;
      j--;
    }

    EVENTS[j + 1] = v;
  }

  // Sections 6.3 and 6.4: the stretches (no piece) are split into parts and sized; runs of the same size join into ranges. Piece 2 starts at
  // U+0000, so the first event is at 0.
  let stretchLo = -1;
  let mask = 0;
  let j = 0;

  while (j < ne) {
    const pos = EVENTS[j]! >> 6;

    while (j < ne && EVENTS[j]! >> 6 === pos) {
      const e = EVENTS[j]!;
      const bit = 1 << ((e >> 1) & 31);
      mask = (e & 1) === 1 ? mask | bit : mask & ~bit;
      j++;
    }

    if (pos > MAX_CODE_POINT) break;

    const hi = j < ne ? Math.min(MAX_CODE_POINT, (EVENTS[j]! >> 6) - 1) : MAX_CODE_POINT;
    const size = mask === 0 ? 0 : P_SIZE[31 - Math.clz32(mask)]!;

    if (size === 0) {
      if (stretchLo < 0) stretchLo = pos;
    } else {
      if (stretchLo >= 0) {
        addStretch(stretchLo, pos - 1);
        stretchLo = -1;
      }

      addRun(pos, hi, size);
    }
  }

  if (stretchLo >= 0) addStretch(stretchLo, MAX_CODE_POINT);
  return rangeCount;
}

/** The number of lead bytes of a range of a size and length (FORMAT.md section 6.5). */
function leadsOf(size: number, length: number): number {
  return size === 1 ? length : size === 2 ? Math.ceil(length / 248) : size === 3 ? Math.ceil(length / 61504) : 1;
}

/** The range that resolveCodePoint or resolveLead found: first and last code point, size and first lead byte. */
export let foundLo = 0;
export let foundHi = 0;
export let foundSize = 0;
export let foundLead = 0;

/**
 * Finds the range of a code point in the layout of a state without building the layout: for a state that is used for one code point.
 * The result is in foundLo, foundHi, foundSize and foundLead.
 */
export function resolveCodePoint(st: State, c: number): void {
  const n = ensureRanges(st.ctx, st.slot, st.main, st.second);
  let lead = 1;

  for (let r = 0; r < n; r++) {
    const size = R_SIZE[r]!;

    if (c <= R_HI[r]!) {
      foundLo = R_LO[r]!;
      foundHi = R_HI[r]!;
      foundSize = size;
      foundLead = lead;
      return;
    }

    lead += leadsOf(size, R_HI[r]! - R_LO[r]! + 1);
  }
}

/**
 * Finds the range that a lead byte belongs to in the layout of a state without building the layout. The result is in foundLo, foundHi,
 * foundSize and foundLead.
 *
 * @returns False if the byte is not a lead byte of the layout (an unused lead byte).
 */
export function resolveLead(st: State, b: number): boolean {
  const n = ensureRanges(st.ctx, st.slot, st.main, st.second);
  let lead = 1;

  for (let r = 0; r < n; r++) {
    const size = R_SIZE[r]!;
    const leads = leadsOf(size, R_HI[r]! - R_LO[r]! + 1);

    if (b < lead + leads) {
      if (b < lead) return false;
      foundLo = R_LO[r]!;
      foundHi = R_HI[r]!;
      foundSize = size;
      foundLead = lead;
      return true;
    }

    lead += leads;
  }

  return false;
}

/**
 * The ranges of one layout (FORMAT.md section 6), with the tables that the encoder and the decoder look things up in.
 *
 * A state builds its layout only when it is used for more than one code point (see State); a state that is left after one code point resolves
 * that code point with resolveCodePoint or resolveLead. A layout holds other layouts only in its transitions, which the cache clears when it
 * drops the layout.
 */
export class Layout {
  /** The number of ranges. */
  readonly count: number;

  /** The first code point of each range. */
  readonly lo: Int32Array;

  /** The last code point of each range. */
  readonly hi: Int32Array;

  /** The size of each range: 1 to 4 bytes per code point. */
  readonly size: Uint8Array;

  /** The first lead byte of each range. */
  readonly first: Uint8Array;

  /** The number of lead bytes of each range. */
  readonly leads: Uint8Array;

  /** For each byte value, the index of the range that it is a lead byte of, or -1 if it is unused (and for 00). */
  readonly leadRange: Int16Array;

  /**
   * For each byte value that is the lead byte of a range of size 1, the code point that it encodes, plus `DECODE1_CONFIDENCE` or
   * `DECODE1_PENDING` for what the code point does to the state; -1 for every other byte value.
   */
  readonly decode1: Int32Array;

  /** For each code point U+0000..U+007F, its byte if it takes 1 byte, or 0 if it takes more. Shared between layouts. */
  readonly ascii1: Uint8Array;

  /**
   * True if `ascii1` is that of the initial layout: U+0020..U+007E are the bytes 07..65 and tab, line feed and carriage return are 02, 03 and
   * 05. It is so in every layout but the CJK layouts with the kana slot hiragana or katakana.
   */
  readonly asciiStandard: boolean;

  /** In the general context, the first code point of the main window; otherwise 0. */
  readonly mainLo: number;

  /** In the general context, the last code point of the main window minus the first; otherwise -1, so that no code point matches. */
  readonly mainSpan: number;

  /** In the general context, the first lead byte of the main window; otherwise 0. */
  readonly mainLead: number;

  /** The number of lead bytes that the layout uses. */
  readonly leadCount: number;

  /**
   * In the wide contexts, the first code point of the block that has a fast path: the ideographs U+4E00..U+9FFF (CJK) or the Hangul syllables
   * U+AC00..U+D7A3 (Hangul). Each lies in one range of size 2 (FORMAT.md section 14.3), and the state update of such a code point only sets the
   * pending window to none. In the general context 0.
   */
  readonly wideLo: number;

  /** The last code point of the fast-path block minus the first; -1 in the general context, so that no code point matches. */
  readonly wideSpan: number;

  /** The first code point of the range that holds the fast-path block. */
  readonly wideR0: number;

  /** The first lead byte of the range that holds the fast-path block. */
  readonly wideLead: number;

  /** The number of lead bytes of the range that holds the fast-path block; 0 in the general context. */
  readonly wideLeads: number;

  /**
   * For U+3000..U+30FF, the byte of the code point if it takes 1 byte in the CJK context, otherwise 0. Such a code point only sets the pending
   * window to none. All zeros (shared) in the other contexts.
   */
  readonly cjk1: Uint8Array;

  /** The key of the layout in the cache; -1 for NO_LAYOUT. */
  readonly key: number;

  /**
   * True once the cache has dropped the layout. A dropped layout stays correct for a state that still uses it, but it keeps no transitions, so
   * it holds no other layout alive.
   */
  evicted = false;

  /** The keys of the last layouts that a state update led to from this layout, or -1. */
  readonly nextKeys: number[] = [-1, -1, -1, -1];

  /** The layouts of `nextKeys`, or null. */
  readonly nextLayouts: (Layout | null)[] = [null, null, null, null];

  /** The slot of `nextKeys` that the next new transition takes. */
  nextSlot = 0;

  /**
   * Builds the layout of a state, or, with a negative context, NO_LAYOUT: a layout with no fast path at all, which sends every code point to the
   * slow path, where the state resolves it without a layout.
   */
  constructor(ctx: number, slot: number, main: number, second: number) {
    if (ctx < 0) {
      this.key = -1;
      this.count = 0;
      this.lo = new Int32Array(0);
      this.hi = new Int32Array(0);
      this.size = new Uint8Array(0);
      this.first = new Uint8Array(0);
      this.leads = new Uint8Array(0);
      this.leadRange = MINUS_ONES_I16.slice();
      this.decode1 = MINUS_ONES_I32.slice();
      this.ascii1 = new Uint8Array(128);
      this.asciiStandard = false;
      this.mainLo = 0;
      this.mainSpan = -1;
      this.mainLead = 0;
      this.leadCount = 0;
      this.wideLo = 0;
      this.wideSpan = -1;
      this.wideR0 = 0;
      this.wideLead = 0;
      this.wideLeads = 0;
      this.cjk1 = ZEROS_U8;
      this.evicted = true;
      return;
    }

    this.key = layoutKey(ctx, slot, main, second);
    const n = ensureRanges(ctx, slot, main, second);
    const lo = R_LO.slice(0, n);
    const hi = R_HI.slice(0, n);
    const sizes = new Uint8Array(n);
    const firsts = new Uint8Array(n);
    const leadCounts = new Uint8Array(n);
    const leadRange = MINUS_ONES_I16.slice();
    const decode1 = MINUS_ONES_I32.slice();

    let lead = 1;

    for (let r = 0; r < n; r++) {
      const rlo = lo[r]!;
      const rhi = hi[r]!;
      const size = R_SIZE[r]!;
      const leads = leadsOf(size, rhi - rlo + 1);

      if (lead + leads - 1 > 0xfe) throw new Error('Steno: a layout needs more than 254 lead bytes. This is a defect of the implementation.');

      sizes[r] = size;
      firsts[r] = lead;
      leadCounts[r] = leads;
      leadRange.fill(r, lead, lead + leads);

      if (size === 1) {
        for (let c = rlo; c <= rhi; c++) {
          const effect = c < 0x80 ? 0 : ctx === GENERAL ? DECODE1_CONFIDENCE : ctx === CJK && c <= 0x30ff ? DECODE1_PENDING : 0;
          decode1[lead + (c - rlo)] = c + effect;
        }
      }

      lead += leads;
    }

    this.count = n;
    this.lo = lo;
    this.hi = hi;
    this.size = sizes;
    this.first = firsts;
    this.leads = leadCounts;
    this.leadRange = leadRange;
    this.decode1 = decode1;
    this.leadCount = lead - 1;
    this.asciiStandard = !(ctx === CJK && slot !== LETTERS);
    this.ascii1 = this.asciiStandard ? ASCII1_STANDARD : ASCII1_KANA;

    // The fast path for the main window, which in the general context is exactly one range of size 1.
    let mainLo = 0;
    let mainSpan = -1;
    let mainLead = 0;

    if (ctx === GENERAL) {
      const r = this.find(main);
      const end = windowEnd(main);

      if (lo[r] !== main || hi[r] !== end || sizes[r] !== 1) {
        throw new Error('Steno: the main window is not one range of size 1. This is a defect of the implementation.');
      }

      mainLo = main;
      mainSpan = end - main;
      mainLead = firsts[r]!;
    }

    this.mainLo = mainLo;
    this.mainSpan = mainSpan;
    this.mainLead = mainLead;

    // The fast path of the wide contexts.
    let wideLo = 0;
    let wideSpan = -1;
    let wideR0 = 0;
    let wideLead = 0;
    let wideLeads = 0;

    if (ctx !== GENERAL) {
      wideLo = ctx === CJK ? 0x4e00 : 0xac00;
      const wideHi = ctx === CJK ? 0x9fff : 0xd7a3;
      const r = this.find(wideLo);

      if (sizes[r] !== 2 || hi[r]! < wideHi) {
        throw new Error('Steno: the wide block is not in one range of size 2. This is a defect of the implementation.');
      }

      wideSpan = wideHi - wideLo;
      wideR0 = lo[r]!;
      wideLead = firsts[r]!;
      wideLeads = leadCounts[r]!;
    }

    this.wideLo = wideLo;
    this.wideSpan = wideSpan;
    this.wideR0 = wideR0;
    this.wideLead = wideLead;
    this.wideLeads = wideLeads;

    if (ctx === CJK) {
      const cjk1 = new Uint8Array(256);

      for (let c = 0x3000; c <= 0x30ff; c++) {
        const r = this.find(c);
        if (sizes[r] === 1) cjk1[c - 0x3000] = firsts[r]! + (c - lo[r]!);
      }

      this.cjk1 = cjk1;
    } else {
      this.cjk1 = ZEROS_U8;
    }
  }

  /**
   * Finds the range that holds a code point.
   *
   * @param c - A code point.
   * @returns The index of the range.
   */
  find(c: number): number {
    const lo = this.lo;
    let a = 0;
    let b = this.count - 1;

    // The last range whose first code point is at most c. The first range starts at U+0000, so there is one.
    while (a < b) {
      const m = (a + b + 1) >> 1;
      if (lo[m]! <= c) a = m;
      else b = m - 1;
    }

    return a;
  }
}

/** The layout of a state that has no layout yet: no fast path, so that every code point takes the slow path. */
export const NO_LAYOUT = /* @__PURE__ */ new Layout(-1, 0, 0, -1);

/** The most layouts that the cache keeps. A layout takes about 2 KB. */
export const LAYOUT_CACHE_LIMIT = 1024;

const cache = new Map<number, Layout>();

/** The layout of the initial state. It is kept outside the cache, so it is never evicted. */
export const INITIAL_LAYOUT = new Layout(GENERAL, LETTERS, 0x00c0, -1);

/**
 * Gets the layout of a state from the cache, or builds it and adds it to the cache.
 *
 * The cache is bounded: when it is full, the layout that was added first is dropped, and its transitions are cleared. JavaScript runs one thread
 * per realm (a worker has its own module instance), so no locking is needed.
 */
export function getLayout(ctx: number, slot: number, main: number, second: number): Layout {
  return getLayoutByKey(layoutKey(ctx, slot, main, second), ctx, slot, main, second);
}

function getLayoutByKey(key: number, ctx: number, slot: number, main: number, second: number): Layout {
  if (key === INITIAL_LAYOUT.key) return INITIAL_LAYOUT;

  let layout = cache.get(key);

  if (layout === undefined) {
    layout = new Layout(ctx, ctx === CJK ? slot : LETTERS, main, second);

    if (cache.size >= LAYOUT_CACHE_LIMIT) {
      const oldest = cache.keys().next();

      if (oldest.done !== true) {
        const dropped = cache.get(oldest.value)!;
        cache.delete(oldest.value);
        dropped.evicted = true;
        dropped.nextKeys.fill(-1);
        dropped.nextLayouts.fill(null);
      }
    }

    cache.set(key, layout);
  }

  return layout;
}

/**
 * The states gone through lately without a layout, in a direct-mapped table of SEEN_SLOTS keys: a state lands in the slot of its hash and
 * overwrites whatever was there. A state that comes back while its key is still in its slot gets its layout built. The table is half the size of
 * the cache, so text that cycles through many more states than that keeps overwriting the slots and builds (almost) nothing, while text that
 * cycles through a few states builds each layout once. A lost key only means that a layout is built one visit later; it never changes a result.
 * The table costs a few arithmetic operations per state change, with no allocation and no eviction work.
 */
export const SEEN_SLOTS = 512;

const seenKeys = new Float64Array(SEEN_SLOTS).fill(-1);

/**
 * Tells whether a state was gone through lately without a layout, and records it if not.
 *
 * @returns True if the state was seen before (its slot is cleared, since its layout is about to be built).
 */
function seenOnce(key: number): boolean {
  const h = Math.imul((key >>> 0) ^ ((key / 4294967296) | 0), 0x9e3779b1) >>> 23;

  if (seenKeys[h] === key) {
    seenKeys[h] = -1;
    return true;
  }

  seenKeys[h] = key;
  return false;
}

/** Gets every layout that the module keeps alive: the cached ones, the initial one, and the ones they remember. For tests. */
export function reachableLayouts(): Set<Layout> {
  const all = new Set<Layout>([INITIAL_LAYOUT, ...cache.values()]);

  for (const layout of [...all]) {
    for (const next of layout.nextLayouts) if (next !== null) all.add(next);
  }

  return all;
}

/** Gets the number of layouts in the cache. For tests. */
export function layoutCacheSize(): number {
  return cache.size;
}

/** Empties the layout cache. For tests. */
export function clearLayoutCache(): void {
  cache.clear();
  seenKeys.fill(-1);
}

/**
 * The state of FORMAT.md section 5, with the layout that belongs to it.
 */
export class State {
  /** The context: GENERAL, CJK or HANGUL. */
  ctx = GENERAL;

  /** The kana slot: LETTERS, HIRAGANA or KATAKANA. */
  slot = LETTERS;

  /** The first code point of the main window. */
  main = 0x00c0;

  /** The first code point of the second window, or -1 for none. */
  second = -1;

  /** The confidence: 0, 1 or 2. */
  conf = 0;

  /** The first code point of the pending window, or -1 for none. */
  pending = -1;

  /** The layout of the state. */
  layout: Layout = INITIAL_LAYOUT;

  /** Sets the state back to the initial state. */
  reset(): void {
    this.ctx = GENERAL;
    this.slot = LETTERS;
    this.main = 0x00c0;
    this.second = -1;
    this.conf = 0;
    this.pending = -1;
    this.layout = INITIAL_LAYOUT;
  }

  /** Copies the fields of another state into this one. */
  copyFrom(other: State): void {
    this.ctx = other.ctx;
    this.slot = other.slot;
    this.main = other.main;
    this.second = other.second;
    this.conf = other.conf;
    this.pending = other.pending;
    this.layout = other.layout;
    this.lazyKey = other.lazyKey;
  }

  /**
   * Updates the state after a code point (FORMAT.md section 7.1), and the layout if the update changed it.
   *
   * @param c - The code point.
   * @param k - The number of bytes that the code point took in the layout before the update.
   */
  update(c: number, k: number): void {
    // Step 1.
    if (c <= 0x7f) {
      if (this.ctx === CJK && this.slot !== LETTERS && ((c >= 0x41 && c <= 0x5a) || (c >= 0x61 && c <= 0x7a))) {
        this.slot = LETTERS;
        this.relayout();
      }

      return;
    }

    // Below U+3000 no code point is a Hangul syllable or a CJK code point, so steps 2 to 4 do nothing there, and the window comes from a table.
    // This is the path of most text in the general context that leaves the main window.
    if (c < 0x3000) {
      this.windowStep(LOW_WINDOWS[c >> 4]!, k);
      return;
    }

    // Step 2.
    if (c >= 0xac00 && c <= 0xd7a3) {
      this.pending = -1;

      if (this.ctx !== HANGUL) {
        this.ctx = HANGUL;
        this.relayout();
      }

      return;
    }

    // Step 3.
    if (
      (c >= 0x3040 && c <= 0x30ff) ||
      (c >= 0x3400 && c <= 0x9fff) ||
      (c >= 0x20000 && c <= 0x3ffff) ||
      (c >= 0x3000 && c <= 0x303f && this.ctx !== HANGUL)
    ) {
      let changed = false;

      if (this.ctx !== CJK) {
        this.ctx = CJK;
        this.slot = LETTERS;
        changed = true;
      }

      this.pending = -1;

      if (c >= 0x3041 && c <= 0x3096) {
        if (this.slot !== HIRAGANA) {
          this.slot = HIRAGANA;
          changed = true;
        }
      } else if (c >= 0x30a1 && c <= 0x30fa) {
        if (this.slot !== KATAKANA) {
          this.slot = KATAKANA;
          changed = true;
        }
      }

      if (changed) this.relayout();
      return;
    }

    // Step 4.
    if (this.ctx !== GENERAL && c >= 0xff00 && c <= 0xffff) return;

    // Step 5 and on.
    this.windowStep(windowStart(c), k);
  }

  /**
   * Steps 5 to 9 of the state update (FORMAT.md section 7.1), for a code point whose steps 1 to 4 did nothing.
   *
   * @param w - The first code point of the window of the code point, or -1 for none.
   * @param k - The number of bytes that the code point took.
   */
  private windowStep(w: number, k: number): void {
    // Step 5.
    if (w < 0) return;

    // Step 6.
    if (this.ctx === GENERAL && w === this.main) {
      if (this.conf < 2) this.conf++;
      return;
    }

    // Step 7.
    let changed = false;

    if (k >= 3 && this.second !== w) {
      this.second = w;
      changed = true;
    }

    // Step 8.
    if (this.ctx === GENERAL) {
      if (this.conf > 0) {
        this.conf--;
        if (changed) this.relayout();
        return;
      }
    } else if (this.pending !== w) {
      this.pending = w;
      if (changed) this.relayout();
      return;
    }

    // Step 9.
    if (this.main !== w) this.second = this.main;
    else if (this.second === w) this.second = -1;

    this.main = w;
    this.ctx = GENERAL;
    this.pending = -1;
    this.conf = 0;
    this.relayout();
  }

  /**
   * The keys of the last layouts that this state used, or -1: a state that moves among a few layouts finds them here even when the cache has
   * dropped them because other text filled it.
   */
  private readonly recentKeys: number[] = [-1, -1, -1, -1];

  /** The layouts of `recentKeys`. */
  private readonly recentLayouts: (Layout | null)[] = [null, null, null, null];

  private recentSlot = 0;

  /** The key of the layout of the state while `layout` is NO_LAYOUT. */
  private lazyKey = -1;

  /** The number of code points that the state has handled in a row without a layout. */
  lazyRun = 0;

  /** Set by every change of the layout fields; the codec clears it before an update to see whether the state stayed. */
  moved = false;

  /**
   * Gives the state the layout of its fields after they changed: one that the old layout led to before, one that the state used lately, or one
   * in the cache. Otherwise it builds nothing yet (see relayoutSlow). So text that changes to a new state on every code point builds no layouts,
   * while text that stays in a state for a run, or moves among a few states, builds each layout once.
   */
  private relayout(): void {
    // The common cases, kept small so that the engine inlines them: a transition that the current layout remembers, or a layout that this state
    // used lately.
    this.moved = true;
    const key = layoutKey(this.ctx, this.slot, this.main, this.second);
    const from = this.layout;
    const next = from.nextKeys;

    for (let j = 0; j < TRANSITIONS; j++) {
      if (next[j] === key) {
        const l = from.nextLayouts[j]!;

        if (!l.evicted) {
          this.layout = l;
          return;
        }

        break;
      }
    }

    const keys = this.recentKeys;

    for (let j = 0; j < TRANSITIONS; j++) {
      if (keys[j] === key) {
        this.layout = this.recentLayouts[j]!;
        return;
      }
    }

    this.relayoutSlow(from, key);
  }

  /**
   * Gives the state a layout from the cache, or none yet (see relayout): the layout is NO_LAYOUT, the next code point is resolved without a
   * layout, and the layout is built once the state has handled a run of code points (see build) or when it comes back soon (see seenOnce).
   */
  private relayoutSlow(from: Layout, key: number): void {
    let next: Layout | undefined = key === INITIAL_LAYOUT.key ? INITIAL_LAYOUT : cache.get(key);

    if (next === undefined) {
      // Not built yet. A state that text has gone through lately without a layout is coming back: build it now. Otherwise wait.
      if (!seenOnce(key)) {
        this.layout = NO_LAYOUT;
        this.lazyKey = key;
        this.lazyRun = 0;
        return;
      }

      next = getLayoutByKey(key, this.ctx, this.slot, this.main, this.second);
    }

    this.remember(from, key, next);
  }

  /** Builds (or takes from the cache) the layout of a state that has handled a run of code points without one. */
  build(): void {
    const key = this.lazyKey;
    this.remember(NO_LAYOUT, key, getLayoutByKey(key, this.ctx, this.slot, this.main, this.second));
  }

  /** Makes a layout the state's layout, and remembers it with the state and as a transition of the layout before it. */
  private remember(from: Layout, key: number, next: Layout): void {
    if (!from.evicted) {
      const keys = from.nextKeys;
      let j = 0;

      while (j < TRANSITIONS && keys[j] !== key) j++;

      if (j === TRANSITIONS) {
        j = from.nextSlot;
        from.nextSlot = (j + 1) % TRANSITIONS;
      }

      keys[j] = key;
      from.nextLayouts[j] = next;
    }

    const j = this.recentSlot;
    this.recentKeys[j] = key;
    this.recentLayouts[j] = next;
    this.recentSlot = (j + 1) % TRANSITIONS;
    this.layout = next;
  }
}
