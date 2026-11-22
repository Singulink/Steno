// The encoding and decoding loops (FORMAT.md sections 8 and 9), on UTF-16 code units.
//
// This module is internal: the package's exports map does not expose it.

import { StenoDecodeError } from './errors.js';
import {
  DECODE1_CONFIDENCE,
  DECODE1_PENDING,
  NO_LAYOUT,
  State,
  foundHi,
  foundLead,
  foundLo,
  foundSize,
  resolveCodePoint,
  resolveLead,
} from './layout.js';

/** The state of an encoder: the state of the format, and a high surrogate held from the end of the input before. */
export class EncoderState extends State {
  /** A high surrogate code unit at the end of the input before, or 0 for none. */
  held = 0;

  override reset(): void {
    super.reset();
    this.held = 0;
  }

  /** Copies the fields of another encoder state into this one. */
  copyEncoderFrom(other: EncoderState): void {
    this.copyFrom(other);
    this.held = other.held;
  }
}

/** The state of a decoder: the state of the format, and what a decoder that gets the bytes in pieces must carry from one piece to the next. */
export class DecoderState extends State {
  /** Whether the last code point was a high surrogate code point (for FORMAT.md section 10.1 rule 5). */
  prevHigh = false;

  /** The bytes of an unfinished code point at the end of the input before. */
  readonly held = new Uint8Array(4);

  /** The number of bytes in `held`. */
  heldLength = 0;

  /** The number of bytes of the value that came before the current input. */
  consumed = 0;

  /** Whether a surrogate code point of its own was decoded since this was last set to false. */
  lone = false;

  override reset(): void {
    super.reset();
    this.prevHigh = false;
    this.heldLength = 0;
    this.consumed = 0;
    this.lone = false;
  }

  /** Copies the fields of another decoder state into this one. */
  copyDecoderFrom(other: DecoderState): void {
    this.copyFrom(other);
    this.prevHigh = other.prevHigh;
    this.held.set(other.held);
    this.heldLength = other.heldLength;
    this.consumed = other.consumed;
    this.lone = other.lone;
  }
}

/**
 * Writes the bytes of one code point in the layout of the state, and updates the state (FORMAT.md sections 8.2 and 7).
 *
 * @returns The position after the bytes.
 */
export function encodeCodePoint(st: State, c: number, out: Uint8Array, pos: number): number {
  const layout = st.layout;
  if (layout === NO_LAYOUT) return encodeWithoutLayout(st, c, out, pos);

  const r = layout.find(c);
  const k = layout.size[r]!;
  pos = writeCode(out, pos, k, layout.first[r]!, c - layout.lo[r]!);
  st.update(c, k);
  return pos;
}

/**
 * Encodes one code point for a state that has no layout yet: the one range is resolved from the state. Kept apart, so that the common path
 * stays small.
 */
function encodeWithoutLayout(st: State, c: number, out: Uint8Array, pos: number): number {
  resolveCodePoint(st, c);
  const k = foundSize;
  pos = writeCode(out, pos, k, foundLead, c - foundLo);
  updateState(st, c, k);
  return pos;
}

/** Writes the bytes of a code point: its offset `o` in a range of size `k` with the first lead byte `lead` (FORMAT.md section 8.2). */
function writeCode(out: Uint8Array, pos: number, k: number, lead: number, o: number): number {
  // The offset in base 248, with the trail bytes 08..FF (each digit + 8). Every number here is at most 0x10FFFF.
  switch (k) {
    case 1:
      out[pos++] = lead + o;
      break;
    case 2: {
      const q = (o / 248) | 0;
      out[pos++] = lead + q;
      out[pos++] = o - q * 248 + 8;
      break;
    }
    case 3: {
      const q = (o / 61504) | 0;
      o -= q * 61504;
      const t = (o / 248) | 0;
      out[pos++] = lead + q;
      out[pos++] = t + 8;
      out[pos++] = o - t * 248 + 8;
      break;
    }
    default: {
      const q = (o / 15252992) | 0;
      o -= q * 15252992;
      const t1 = (o / 61504) | 0;
      o -= t1 * 61504;
      const t2 = (o / 248) | 0;
      out[pos++] = lead + q;
      out[pos++] = t1 + 8;
      out[pos++] = t2 + 8;
      out[pos++] = o - t2 * 248 + 8;
      break;
    }
  }

  return pos;
}

/**
 * The number of code points in a row that a state without a layout handles before it builds its layout. Until then each code point is resolved
 * from the state (the ranges are computed once and reused while the state stays). So text that moves to a new state after every few code points
 * never pays for a layout: its cost per code point stays bounded at about one computation of the ranges.
 */
const RUN_TO_BUILD = 16;

/**
 * Updates the state after a code point that took the slow path, and builds the layout of a state that had none once it has handled a run of code
 * points: the fast paths need its tables.
 */
export function updateState(st: State, c: number, k: number): void {
  if (st.layout !== NO_LAYOUT) {
    st.update(c, k);
    return;
  }

  st.moved = false;
  st.update(c, k);
  if (!st.moved && ++st.lazyRun >= RUN_TO_BUILD) st.build();
}

// The intrinsic set of Uint8Array, taken once: the codec writes into arrays through it, never through a method that a subclass could override.
const u8Set = Uint8Array.prototype.set;

const ASCII_CHUNK = 4096;
const asciiScratch = new Uint8Array(ASCII_CHUNK);
const asciiEncoder = new TextEncoder();

// The Steno byte of each UTF-8 byte that is a code point taking 1 byte in a layout whose ASCII bytes are standard, or 0.
const ASCII_TO_STENO = /* @__PURE__ */ (() => {
  const t = new Uint8Array(256);
  t[0x09] = 0x02;
  t[0x0a] = 0x03;
  t[0x0d] = 0x05;
  for (let c = 0x20; c <= 0x7e; c++) t[c] = c - 0x19;
  return t;
})();

/**
 * Encodes the run of ASCII text that starts at a position, in a layout whose ASCII bytes are standard (`Layout.asciiStandard`): such code points
 * take 1 byte and do not change the state. The platform's UTF-8 encoder writes the ASCII bytes at native speed, and one pass over them maps each
 * to its Steno byte, up to the first byte that is not ASCII text.
 *
 * @returns The number of code units encoded (and bytes written).
 */
function encodeAsciiRun(s: string, i: number, n: number, out: Uint8Array, pos: number): number {
  const scratch = asciiScratch;
  const map = ASCII_TO_STENO;
  let total = 0;

  while (i < n) {
    const length = Math.min(ASCII_CHUNK, n - i);
    const written = asciiEncoder.encodeInto(s.substring(i, i + length), scratch).written;
    let j = 0;

    for (; j < written; j++) {
      const b = map[scratch[j]!]!;
      if (b === 0) break;
      scratch[j] = b;
    }

    // A destination that is too small takes what fits; the caller finds out from the position (see encodeInto).
    const room = out.length - pos - total;
    u8Set.call(out, j <= room ? scratch.subarray(0, j) : scratch.subarray(0, Math.max(0, room)), Math.min(out.length, pos + total));

    total += j;
    i += j;
    if (j < length) break;
  }

  return total;
}

/**
 * Encodes UTF-16 code units as the next piece of a value.
 *
 * @param st - The encoder state, updated in place.
 * @param s - The code units.
 * @param out - The destination. It must have room for `(s.length + 1) * 3` bytes; writes past its end are lost.
 * @param pos - Where to start writing.
 * @param flush - True if the input ends after `s`: a high surrogate at the end is then a code point of its own. False to hold it for the next
 *   call, which may start with its low surrogate.
 * @returns The position after the last byte written.
 */
export function encodeUnits(st: EncoderState, s: string, out: Uint8Array, pos: number, flush: boolean): number {
  const n = s.length;
  let i = 0;

  if (st.held !== 0) {
    const h = st.held;

    if (n > 0) {
      st.held = 0;
      const u = s.charCodeAt(0);

      if ((u & 0xfc00) === 0xdc00) {
        pos = encodeCodePoint(st, ((h - 0xd800) << 10) + (u - 0xdc00) + 0x10000, out, pos);
        i = 1;
      } else {
        pos = encodeCodePoint(st, h, out, pos);
      }
    } else if (flush) {
      st.held = 0;
      pos = encodeCodePoint(st, h, out, pos);
    }
  }

  let layout = st.layout;
  let ascii1 = layout.ascii1;
  let mainLo = layout.mainLo;
  let mainSpan = layout.mainSpan;
  let mainLead = layout.mainLead;
  let wideLo = layout.wideLo;
  let wideSpan = layout.wideSpan;
  let wideR0 = layout.wideR0;
  let wideLead = layout.wideLead;
  let cjk1 = layout.cjk1;
  let asciiStandard = layout.asciiStandard;
  let conf = st.conf;
  let pending = st.pending;
  let run = 0;

  while (i < n) {
    const u = s.charCodeAt(i);

    // Code points that take 1 byte and change at most the confidence (FORMAT.md section 14.2): ASCII and, in the general context, the main
    // window (only its BMP part here; a supplementary main window goes the slower way).
    if (u < 0x80) {
      const b = ascii1[u]!;

      if (b !== 0) {
        out[pos++] = b;
        i++;

        // After a run of 32 ASCII characters, the rest of the run goes through the platform's UTF-8 encoder (see encodeAsciiRun).
        if (++run >= 32 && asciiStandard && n - i >= 64) {
          const m = encodeAsciiRun(s, i, n, out, pos);
          i += m;
          pos += m;
          run = 0;
        }

        continue;
      }
    } else if ((u - mainLo) >>> 0 <= mainSpan) {
      out[pos++] = mainLead + (u - mainLo);
      run = 0;
      if (conf < 2) conf++;
      i++;
      continue;
    } else if ((u - wideLo) >>> 0 <= wideSpan) {
      // An ideograph in the CJK context or a Hangul syllable in the Hangul context: 2 bytes, and the update only clears the pending window.
      // ((o + 1) * 33825) >>> 23 is o / 248 rounded down, exactly, for every o below 65,536 (a test checks all of them), and the product stays
      // below 2^32. The offset of a range of size 2 is below 254 * 248.
      const o = u - wideR0;
      const q = ((o + 1) * 33825) >>> 23;
      out[pos++] = wideLead + q;
      out[pos++] = o - q * 248 + 8;
      pending = -1;
      run = 0;
      i++;
      continue;
    } else if ((u - 0x3000) >>> 0 < 0x100) {
      // In the CJK context: U+3000..U+300F and the kana of the kana slot take 1 byte, and the update only clears the pending window.
      const b = cjk1[u - 0x3000]!;

      if (b !== 0) {
        out[pos++] = b;
        pending = -1;
        run = 0;
        i++;
        continue;
      }
    }

    let c = u;
    run = 0;
    i++;

    if ((u & 0xfc00) === 0xd800) {
      if (i < n) {
        const v = s.charCodeAt(i);

        if ((v & 0xfc00) === 0xdc00) {
          c = ((u - 0xd800) << 10) + (v - 0xdc00) + 0x10000;
          i++;
        }
      } else if (!flush) {
        st.held = u;
        break;
      }
    }

    st.conf = conf;
    st.pending = pending;
    pos = encodeCodePoint(st, c, out, pos);
    conf = st.conf;
    pending = st.pending;

    if (st.layout !== layout) {
      layout = st.layout;
      ascii1 = layout.ascii1;
      mainLo = layout.mainLo;
      mainSpan = layout.mainSpan;
      mainLead = layout.mainLead;
      wideLo = layout.wideLo;
      wideSpan = layout.wideSpan;
      wideR0 = layout.wideR0;
      wideLead = layout.wideLead;
      cjk1 = layout.cjk1;
      asciiStandard = layout.asciiStandard;
    }
  }

  st.conf = conf;
  st.pending = pending;
  return pos;
}

// The ASCII byte of each Steno byte that is ASCII text in a layout whose ASCII bytes are standard, or 0.
export const STENO_TO_ASCII = /* @__PURE__ */ (() => {
  const t = new Uint8Array(256);
  t[0x02] = 0x09;
  t[0x03] = 0x0a;
  t[0x05] = 0x0d;
  for (let b = 0x07; b <= 0x65; b++) t[b] = b + 0x19;
  return t;
})();

/** The most bytes of ASCII text that mapAsciiRun maps at a time: its buffer is never larger. */
export const ASCII_RUN_CHUNK = 1 << 16;

let runScratch = new Uint8Array(1024);

/** Gets the buffer that mapAsciiRun writes into. */
export function asciiRunScratch(): Uint8Array {
  return runScratch;
}

/**
 * Maps the run of Steno bytes of ASCII text that starts at a position to ASCII bytes, into the buffer of asciiRunScratch(), up to
 * ASCII_RUN_CHUNK bytes at a time.
 *
 * @returns The number of bytes mapped: the length of the run, or ASCII_RUN_CHUNK if the run is longer.
 */
export function mapAsciiRun(bytes: Uint8Array, i: number, end: number): number {
  const limit = Math.min(end - i, ASCII_RUN_CHUNK);
  const map = STENO_TO_ASCII;
  let tmp = runScratch;
  let j = 0;

  for (;;) {
    const stop = Math.min(limit, tmp.length);

    for (; j < stop; j++) {
      const c = map[bytes[i + j]!]!;
      if (c === 0) return j;
      tmp[j] = c;
    }

    if (j >= limit) return j;

    // The buffer is full and the run goes on: grow it, up to ASCII_RUN_CHUNK, so that one large value does not keep a large buffer alive.
    const bigger = new Uint8Array(Math.min(ASCII_RUN_CHUNK, tmp.length * 2));
    bigger.set(tmp.subarray(0, j));
    runScratch = tmp = bigger;
  }
}

// The decoder for 'latin1' is windows-1252 in the Encoding Standard, which is exact for the bytes 00..7F, the only ones given to it here.
let latin1: TextDecoder | null | undefined;

/** Gets a decoder for ASCII bytes, or null if the platform has none. */
export function latin1Decoder(): TextDecoder | null {
  if (latin1 === undefined) {
    try {
      latin1 = new TextDecoder('latin1');
    } catch {
      latin1 = null;
    }
  }

  return latin1;
}

/**
 * Decodes bytes as the next piece of a value into UTF-16 code units.
 *
 * @param st - The decoder state, updated in place. On an error it is left in an unknown state.
 * @param bytes - The bytes.
 * @param start - The index of the first byte to decode.
 * @param end - The index after the last byte to decode.
 * @param out - The destination. It must have room for `(end - start) * 2` code units.
 * @param o - Where to start writing.
 * @param base - The position in the value of `bytes[0]`, for the offsets of errors.
 * @param flush - True if the input ends at `end`: an unfinished code point is then an error. False to hold its bytes for the next call.
 * @param pieces - Null, or a list that takes the text decoded so far as strings, when a long run of ASCII text is decoded by the platform's
 *   decoder. The code units in `out` then come after the pieces, from index 0.
 * @returns The position after the last code unit written.
 */
export function decodeUnits(
  st: DecoderState,
  bytes: Uint8Array,
  start: number,
  end: number,
  out: Uint16Array,
  o: number,
  base: number,
  flush: boolean,
  pieces: string[] | null = null,
): number {
  let o0 = o;
  let run = 0;
  let i = start;
  let layout = st.layout;
  let decode1 = layout.decode1;
  let wideLo = layout.wideLo;
  let wideSpan = layout.wideSpan;
  let wideR0 = layout.wideR0;
  let wideLead = layout.wideLead;
  let wideLeads = layout.wideLeads;
  let conf = st.conf;
  let pending = st.pending;

  while (i < end) {
    const b = bytes[i]!;
    const d1 = decode1[b]!;

    // A lead byte of a range of size 1: such a code point never changes the layout (FORMAT.md section 14.2). The table holds the code point and
    // what it does to the state.
    if (d1 >= 0) {
      const c1 = d1 & 0x1fffff;

      if (c1 < 0x10000) {
        out[o++] = c1;
      } else {
        out[o++] = 0xd7c0 + (c1 >> 10);
        out[o++] = 0xdc00 + (c1 & 0x3ff);
      }

      if (d1 >= DECODE1_PENDING) {
        pending = -1;
      } else if (d1 >= DECODE1_CONFIDENCE) {
        if (conf < 2) conf++;
      }

      i++;

      // After a run of 32 ASCII characters, in a layout whose ASCII bytes are standard, the rest of the run goes through the platform's
      // decoder (see decodeAsciiRun). Only when the caller collects the text in pieces.
      if (c1 >= 0x80) {
        run = 0;
      } else if (++run >= 32 && pieces !== null && layout.asciiStandard && end - i >= 64) {
        const latin1 = latin1Decoder();

        if (latin1 !== null) {
          let j = mapAsciiRun(bytes, i, end);

          if (j > 0) {
            if (o > o0) pieces.push(unitsToString(out, o, !st.lone));
            st.lone = false;
            st.prevHigh = false;
            o = o0 = 0;

            // The scratch buffer holds at most ASCII_RUN_CHUNK bytes; a longer run goes in several pieces.
            for (;;) {
              pieces.push(latin1.decode(runScratch.subarray(0, j)));
              i += j;
              if (j < ASCII_RUN_CHUNK) break;
              j = mapAsciiRun(bytes, i, end);
              if (j === 0) break;
            }
          }
        }

        run = 0;
      }

      continue;
    }

    // A lead byte of the range of the ideographs (CJK) or Hangul syllables (Hangul), with a valid trail byte (08..FF): if the code point
    // is in that block, its update only clears the pending window. Anything else, errors included, takes the general path below.
    if ((b - wideLead) >>> 0 < wideLeads && i + 1 < end) {
      const t = bytes[i + 1]!;

      if (t >= 8) {
        const c = wideR0 + (b - wideLead) * 248 + (t - 8);

        if ((c - wideLo) >>> 0 <= wideSpan) {
          out[o++] = c;
          pending = -1;
          run = 0;
          i += 2;
          continue;
        }
      }
    }

    run = 0;
    if (b === 0) throw new StenoDecodeError('zero-byte', base + i);

    let k: number;
    let first: number;
    let r0: number;
    let r1: number;

    if (layout !== NO_LAYOUT) {
      const r = layout.leadRange[b]!;
      if (r < 0) throw new StenoDecodeError('unused-lead', base + i);
      k = layout.size[r]!;
      first = layout.first[r]!;
      r0 = layout.lo[r]!;
      r1 = layout.hi[r]!;
    } else {
      // A state without a layout yet: resolve the one range from the state.
      if (!resolveLead(st, b)) throw new StenoDecodeError('unused-lead', base + i);
      k = foundSize;
      first = foundLead;
      r0 = foundLo;
      r1 = foundHi;
    }

    if (i + k > end) {
      // The input ends in the middle of the code point. The trail bytes that are there are checked first, as they are read (FORMAT.md
      // section 9.2): 00 is zero-byte, 01..07 is invalid-trail.
      for (let j = i + 1; j < end; j++) {
        const t = bytes[j]!;
        if (t === 0) throw new StenoDecodeError('zero-byte', base + i);
        if (t < 8) throw new StenoDecodeError('invalid-trail', base + i);
      }

      if (flush) throw new StenoDecodeError('truncated', base + i);

      for (let j = i; j < end; j++) st.held[j - i] = bytes[j]!;
      st.heldLength = end - i;
      st.consumed = base + i;
      i = end;
      break;
    }

    let v = b - first;

    for (let j = 1; j < k; j++) {
      const t = bytes[i + j]!;
      if (t < 8) throw new StenoDecodeError(t === 0 ? 'zero-byte' : 'invalid-trail', base + i);
      v = v * 248 + (t - 8);
    }

    const c = r0 + v;
    if (c > r1) throw new StenoDecodeError('past-range', base + i);

    if (c < 0x10000) {
      if ((c & 0xf800) === 0xd800) st.lone = true;

      // Rule 5: the output ends in a high surrogate code unit only after a high surrogate code point of its own (FORMAT.md section 14.4).
      if ((c & 0xfc00) === 0xdc00 && (o > o0 ? (out[o - 1]! & 0xfc00) === 0xd800 : st.prevHigh)) {
        throw new StenoDecodeError('surrogate-pair', base + i);
      }

      out[o++] = c;
    } else {
      out[o++] = 0xd7c0 + (c >> 10);
      out[o++] = 0xdc00 + (c & 0x3ff);
    }

    i += k;
    st.conf = conf;
    st.pending = pending;
    updateState(st, c, k);
    conf = st.conf;
    pending = st.pending;

    if (st.layout !== layout) {
      layout = st.layout;
      decode1 = layout.decode1;
      wideLo = layout.wideLo;
      wideSpan = layout.wideSpan;
      wideR0 = layout.wideR0;
      wideLead = layout.wideLead;
      wideLeads = layout.wideLeads;
    }
  }

  st.conf = conf;
  st.pending = pending;
  if (o > o0) st.prevHigh = (out[o - 1]! & 0xfc00) === 0xd800;
  return o;
}

/**
 * Decodes the next piece of a value given in pieces: first completes a code point held from the call before, then decodes the rest.
 *
 * @param st - The decoder state, updated in place. On an error it is left in an unknown state.
 * @param bytes - The next piece of the bytes.
 * @param out - The destination. It must have room for `bytes.length * 2` code units.
 * @param flush - True if the value ends after this piece.
 * @param pieces - Null, or a list for text decoded in pieces (see decodeUnits).
 * @returns The number of code units written.
 */
export function decodePiece(st: DecoderState, bytes: Uint8Array, out: Uint16Array, flush: boolean, pieces: string[] | null = null): number {
  let o = 0;
  let i = 0;
  const n = bytes.length;
  let base = st.consumed;

  if (st.heldLength > 0) {
    // The held bytes start a code point; its lead byte gives how many bytes it needs.
    const held = st.held;
    const layout = st.layout;
    const k = layout !== NO_LAYOUT ? layout.size[layout.leadRange[held[0]!]!]! : (resolveLead(st, held[0]!), foundSize);
    const take = Math.min(k - st.heldLength, n);

    for (let j = 0; j < take; j++) held[st.heldLength + j] = bytes[j]!;
    const length = st.heldLength + take;
    const heldStart = st.consumed;
    st.heldLength = 0;
    i = take;

    o = decodeUnits(st, held, 0, length, out, 0, heldStart, flush);

    if (st.heldLength > 0) {
      // Still unfinished: all of this piece went into it. decodeUnits has set consumed to heldStart, where the code point starts.
      return o;
    }

    base = heldStart + length - take;
  }

  o = decodeUnits(st, bytes, i, n, out, o, base, flush, pieces);
  if (st.heldLength === 0) st.consumed = base + n;
  return o;
}

// TextDecoder is much faster than String.fromCharCode for long strings, but it replaces unpaired surrogates, so it is used only for well-formed
// output. It reads the code units in the byte order of the platform, so it is used only where that is little-endian.
let utf16: TextDecoder | null | undefined;

function utf16Decoder(): TextDecoder | null {
  if (utf16 === undefined) {
    try {
      utf16 = new Uint8Array(new Uint16Array([1]).buffer)[0] === 1 ? new TextDecoder('utf-16le', { ignoreBOM: true }) : null;
    } catch {
      utf16 = null;
    }
  }

  return utf16;
}

/**
 * Converts up to 16 code units to a string with one call of `String.fromCharCode` with the code units as arguments, which engines run much faster
 * than `String.fromCharCode.apply` on a typed array for short strings.
 */
function shortString(u: Uint16Array, i: number, n: number): string {
  const f = String.fromCharCode;

  switch (n) {
    case 1:
      return f(u[i]!);
    case 2:
      return f(u[i]!, u[i + 1]!);
    case 3:
      return f(u[i]!, u[i + 1]!, u[i + 2]!);
    case 4:
      return f(u[i]!, u[i + 1]!, u[i + 2]!, u[i + 3]!);
    case 5:
      return f(u[i]!, u[i + 1]!, u[i + 2]!, u[i + 3]!, u[i + 4]!);
    case 6:
      return f(u[i]!, u[i + 1]!, u[i + 2]!, u[i + 3]!, u[i + 4]!, u[i + 5]!);
    case 7:
      return f(u[i]!, u[i + 1]!, u[i + 2]!, u[i + 3]!, u[i + 4]!, u[i + 5]!, u[i + 6]!);
    case 8:
      return f(u[i]!, u[i + 1]!, u[i + 2]!, u[i + 3]!, u[i + 4]!, u[i + 5]!, u[i + 6]!, u[i + 7]!);
    case 9:
      return f(u[i]!, u[i + 1]!, u[i + 2]!, u[i + 3]!, u[i + 4]!, u[i + 5]!, u[i + 6]!, u[i + 7]!, u[i + 8]!);
    case 10:
      return f(u[i]!, u[i + 1]!, u[i + 2]!, u[i + 3]!, u[i + 4]!, u[i + 5]!, u[i + 6]!, u[i + 7]!, u[i + 8]!, u[i + 9]!);
    case 11:
      return f(u[i]!, u[i + 1]!, u[i + 2]!, u[i + 3]!, u[i + 4]!, u[i + 5]!, u[i + 6]!, u[i + 7]!, u[i + 8]!, u[i + 9]!, u[i + 10]!);
    case 12:
      return f(u[i]!, u[i + 1]!, u[i + 2]!, u[i + 3]!, u[i + 4]!, u[i + 5]!, u[i + 6]!, u[i + 7]!, u[i + 8]!, u[i + 9]!, u[i + 10]!, u[i + 11]!);
    case 13:
      return f(u[i]!, u[i + 1]!, u[i + 2]!, u[i + 3]!, u[i + 4]!, u[i + 5]!, u[i + 6]!, u[i + 7]!, u[i + 8]!, u[i + 9]!, u[i + 10]!, u[i + 11]!, u[i + 12]!);
    case 14:
      return f(u[i]!, u[i + 1]!, u[i + 2]!, u[i + 3]!, u[i + 4]!, u[i + 5]!, u[i + 6]!, u[i + 7]!, u[i + 8]!, u[i + 9]!, u[i + 10]!, u[i + 11]!, u[i + 12]!, u[i + 13]!);
    case 15:
      return f(u[i]!, u[i + 1]!, u[i + 2]!, u[i + 3]!, u[i + 4]!, u[i + 5]!, u[i + 6]!, u[i + 7]!, u[i + 8]!, u[i + 9]!, u[i + 10]!, u[i + 11]!, u[i + 12]!, u[i + 13]!, u[i + 14]!);
    case 16:
      return f(u[i]!, u[i + 1]!, u[i + 2]!, u[i + 3]!, u[i + 4]!, u[i + 5]!, u[i + 6]!, u[i + 7]!, u[i + 8]!, u[i + 9]!, u[i + 10]!, u[i + 11]!, u[i + 12]!, u[i + 13]!, u[i + 14]!, u[i + 15]!);
    default:
      return '';
  }
}

/**
 * Converts UTF-16 code units to a string.
 *
 * @param units - The code units.
 * @param length - The number of code units to convert.
 * @param wellFormed - True if the code units have no unpaired surrogate.
 * @returns The string.
 */
export function unitsToString(units: Uint16Array, length: number, wellFormed: boolean): string {
  const CHUNK = 8192;

  if (length <= 16) return shortString(units, 0, length);
  if (length <= 32) return shortString(units, 0, 16) + shortString(units, 16, length - 16);

  if (!wellFormed) {
    if (length <= CHUNK) return String.fromCharCode.apply(null, units.subarray(0, length) as unknown as number[]);
  } else {
    const decoder = utf16Decoder();
    if (decoder !== null) return decoder.decode(units.subarray(0, length));
  }

  let s = '';

  for (let i = 0; i < length; i += CHUNK) {
    s += String.fromCharCode.apply(null, units.subarray(i, Math.min(length, i + CHUNK)) as unknown as number[]);
  }

  return s;
}
