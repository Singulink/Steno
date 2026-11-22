import {
  ASCII_RUN_CHUNK,
  DecoderState,
  EncoderState,
  asciiRunScratch,
  decodeUnits,
  encodeCodePoint,
  encodeUnits,
  latin1Decoder,
  mapAsciiRun,
  unitsToString,
} from './codec.js';
import { type StenoBytes, checkText, toBytes, toDestination } from './input.js';

/**
 * The name to store next to Steno bytes, so that a reader knows how to decode them: the bytes carry no header or version marker.
 */
export const STENO_ENCODING_NAME = 'x-steno';

// The scratch buffers and states of the one-shot functions, reused between calls, are taken through acquire() and given back through release(). The
// functions call no code of the caller while they hold them: arguments are checked and turned into plain arrays first (input.ts). Iterating the
// caller's iterable in encodeCodePoints is the one place where caller code runs, and it runs before. Should caller code still run in the middle
// and call one of these functions again, the inner call finds them taken and uses fresh ones of its own.
const SCRATCH_LIMIT = 1 << 16;
let scratchBytes = new Uint8Array(1024);
let scratchUnits = new Uint16Array(1024);
const encoderState = new EncoderState();
const decoderState = new DecoderState();
let inUse = false;

/** Takes the shared scratch and states; false if they are taken, and the caller must use fresh ones. */
function acquire(): boolean {
  if (inUse) return false;
  inUse = true;
  return true;
}

function release(shared: boolean): void {
  if (shared) inUse = false;
}

function encoderFor(shared: boolean): EncoderState {
  if (!shared) return new EncoderState();
  encoderState.reset();
  return encoderState;
}

function decoderFor(shared: boolean): DecoderState {
  if (!shared) return new DecoderState();
  decoderState.reset();
  return decoderState;
}

function bytesFor(length: number, shared: boolean): Uint8Array {
  if (!shared || length > SCRATCH_LIMIT) return new Uint8Array(length);
  if (length > scratchBytes.length) scratchBytes = new Uint8Array(length);
  return scratchBytes;
}

/** Gets a buffer for decoded code units: the shared one when the caller holds the shared state and it is not too long, otherwise a new one. */
export function unitsFor(length: number, shared: boolean): Uint16Array {
  if (!shared || length > SCRATCH_LIMIT) return new Uint16Array(length);
  if (length > scratchUnits.length) scratchUnits = new Uint16Array(length);
  return scratchUnits;
}

/** For the streaming decoder: takes the shared code unit buffer if it is free. */
export { acquire as acquireShared, release as releaseShared };

function checkLength(length: number, name: string, factor: number): number {
  if (typeof length !== 'number' || !Number.isSafeInteger(length) || length < 0 || !Number.isSafeInteger(length * factor)) {
    throw new RangeError(`${name} must be a non-negative safe integer whose multiple by ${factor} is a safe integer.`);
  }

  // + 0 turns -0 into 0.
  return length * factor + 0;
}

/**
 * Gets the most bytes that {@link encode} or {@link encodeInto} can write for a string of the given length.
 *
 * @param textLength - The length of the string in UTF-16 code units (`text.length`).
 * @returns `textLength * 3`. A code unit takes at most 3 bytes, and a surrogate pair (two code units) at most 4. The bound is reached: a string of
 *   unpaired surrogates takes exactly 3 bytes per code unit.
 * @throws RangeError if `textLength` is not a non-negative safe integer, or the result would not be a safe integer.
 */
export function maxEncodedLength(textLength: number): number {
  return checkLength(textLength, 'textLength', 3);
}

/**
 * Gets the most UTF-16 code units that {@link decode} can give for the given number of bytes.
 *
 * @param byteLength - The number of Steno bytes.
 * @returns `byteLength * 2`. A code point takes at least 1 byte and at most 2 code units. The bound is reached, up to the few bytes that the first
 *   code point of the window takes, by text in a supplementary main window (such as emoji of one block), whose code points take 1 byte each.
 * @throws RangeError if `byteLength` is not a non-negative safe integer, or the result would not be a safe integer.
 */
export function maxDecodedLength(byteLength: number): number {
  return checkLength(byteLength, 'byteLength', 2);
}

/**
 * Encodes a string as one value into new bytes.
 *
 * @remarks
 * Every string can be encoded, unpaired surrogates included: a high surrogate followed by a low surrogate is one supplementary code point, and
 * every other code unit is the code point with its value (FORMAT.md section 3). Encoding never fails.
 *
 * @param text - The string to encode.
 * @returns The Steno bytes of the value, in a new array.
 * @throws TypeError if `text` is not a string. Unlike `TextEncoder`, the encoders do not convert other values with `String()`: a number or an
 *   object that reaches an encoder is far more often a bug than an intention.
 *
 * @example
 * ```js
 * const bytes = encode('Москва');
 * console.log(bytes.length); // 7
 * ```
 */
export function encode(text: string): Uint8Array<ArrayBuffer> {
  checkText(text, 'text');
  const shared = acquire();

  try {
    const out = bytesFor(text.length * 3, shared);
    const n = encodeUnits(encoderFor(shared), text, out, 0, true);
    return out.slice(0, n);
  } finally {
    release(shared);
  }
}

/**
 * Encodes a string as one value into the caller's buffer, in the manner of `TextEncoder.encodeInto`.
 *
 * @remarks
 * Unlike `TextEncoder.encodeInto`, a destination that is too small is an error, not a partial result. Steno is stateful: the bytes of the rest of
 * the string depend on the code points before it, so a second call for the rest would start from the initial state and write wrong bytes without
 * any error. A destination of {@link maxEncodedLength}`(text.length)` bytes is always large enough.
 *
 * @param text - The string to encode.
 * @param destination - Where to write the bytes, from index 0: a `Uint8Array` (or a subclass of it, such as Node's `Buffer`).
 * @returns `read`, always `text.length`, and `written`, the number of bytes written.
 * @throws TypeError if `text` is not a string or `destination` is not a `Uint8Array`.
 * @throws RangeError if the bytes do not fit. The destination may then hold some of them.
 */
export function encodeInto(text: string, destination: Uint8Array): { read: number; written: number } {
  checkText(text, 'text');
  const out = toDestination(destination, 'destination');
  const shared = acquire();

  try {
    // A typed array ignores writes past its end, so the encoder can write straight into the destination and the size is checked after.
    const written = encodeUnits(encoderFor(shared), text, out, 0, true);

    if (written > out.length) {
      throw new RangeError(`The destination has room for ${out.length} bytes, but the value takes ${written}.`);
    }

    return { read: text.length, written };
  } finally {
    release(shared);
  }
}

/**
 * Decodes the bytes of one value into a string.
 *
 * @remarks
 * Every valid value decodes to a string; a surrogate code point of its own becomes an unpaired surrogate. Invalid bytes throw: there is no
 * replacement character, because the state after invalid data is unknown.
 *
 * @param bytes - The Steno bytes of a whole value: a `Uint8Array`, or any other `ArrayBuffer`, `SharedArrayBuffer`, typed array or `DataView`,
 *   read as its bytes.
 * @returns The string.
 * @throws TypeError if `bytes` is none of those.
 * @throws {@link StenoDecodeError} if the bytes are not the Steno bytes of a value.
 *
 * @example
 * ```js
 * console.log(decode(encode('Москва'))); // Москва
 * ```
 */
export function decode(bytes: StenoBytes): string {
  const input = toBytes(bytes, 'bytes');
  const n = input.length;
  if (n === 0) return '';

  const shared = acquire();

  try {
    // A value that starts with ASCII text stays in the initial layout until its first other code point (FORMAT.md section 14.1). Those bytes map
    // one to one to ASCII bytes, which the platform's decoder turns into a string at native speed, a scratch buffer at a time.
    let prefix = '';
    let start = 0;

    if (n >= ASCII_DECODE_MIN && shared) {
      const latin1 = latin1Decoder();
      let j = latin1 === null ? 0 : mapAsciiRun(input, 0, n);

      if (latin1 !== null && (j >= ASCII_DECODE_MIN || j === n)) {
        for (;;) {
          prefix += latin1.decode(asciiRunScratch().subarray(0, j));
          start += j;
          if (start === n) return prefix;
          if (j < ASCII_RUN_CHUNK) break;
          j = mapAsciiRun(input, start, n);
          if (j === 0) break;
        }
      }
    }

    const out = unitsFor((n - start) * 2, shared);
    const st = decoderFor(shared);

    if (n - start < 128 || !shared) return prefix + unitsToString(out, decodeUnits(st, input, start, n, out, 0, 0, true), !st.lone);

    // Long input: long runs of ASCII text inside it are decoded by the platform's decoder too, into separate pieces.
    const pieces: string[] = [prefix];
    const o = decodeUnits(st, input, start, n, out, 0, 0, true, pieces);
    pieces.push(unitsToString(out, o, !st.lone));
    return pieces.join('');
  } finally {
    release(shared);
  }
}

// The shortest value, and the shortest run of ASCII text at its start, that decode() hands to the platform's decoder.
const ASCII_DECODE_MIN = 32;

/**
 * Encodes a sequence of code points as one value, with full fidelity: every value of the specification can be given, surrogate code points
 * included.
 *
 * @param codePoints - The code points, each an integer from 0 to 0x10FFFF, in any iterable (an array, a typed array, a generator).
 * @returns The Steno bytes of the value, in a new array.
 * @throws TypeError if `codePoints` is not iterable or an element is not a number.
 * @throws RangeError if an element is not an integer from 0 to 0x10FFFF, or if a high surrogate code point (0xD800..0xDBFF) is directly
 *   followed by a low surrogate code point (0xDC00..0xDFFF): such a sequence is not a value (FORMAT.md section 3).
 *
 * @example
 * ```js
 * console.log(encodeCodePoints([0x61, 0xd800, 0x62]).length); // 5
 * ```
 */
export function encodeCodePoints(codePoints: Iterable<number>): Uint8Array<ArrayBuffer> {
  if (codePoints === null || codePoints === undefined || typeof (codePoints as { [Symbol.iterator]?: unknown })[Symbol.iterator] !== 'function') {
    throw new TypeError('codePoints must be an iterable of numbers.');
  }

  // The caller's iterator runs here, before any shared state is taken.
  const cps: unknown[] = Array.from(codePoints);
  let prev = -1;

  for (let i = 0; i < cps.length; i++) {
    const c = cps[i];

    if (typeof c !== 'number') throw new TypeError(`The element at index ${i} is not a number.`);
    if (!Number.isInteger(c) || c < 0 || c > 0x10ffff) throw new RangeError(`The element at index ${i} is not a code point: ${c}.`);

    if (c >= 0xdc00 && c <= 0xdfff && prev >= 0xd800 && prev <= 0xdbff) {
      throw new RangeError(`Not a value: a high surrogate code point is directly followed by a low one at index ${i}.`);
    }

    prev = c;
  }

  const shared = acquire();

  try {
    const out = bytesFor(cps.length * 4, shared);
    const st = encoderFor(shared);
    let pos = 0;

    for (let i = 0; i < cps.length; i++) pos = encodeCodePoint(st, cps[i] as number, out, pos);

    return out.slice(0, pos);
  } finally {
    release(shared);
  }
}

/**
 * Decodes the bytes of one value into its code points, with full fidelity: a surrogate code point is a number of its own.
 *
 * @param bytes - The Steno bytes of a whole value, of the kinds that {@link decode} takes.
 * @returns The code points.
 * @throws TypeError if `bytes` is not an `ArrayBuffer`, a `SharedArrayBuffer`, a typed array or a `DataView`.
 * @throws {@link StenoDecodeError} if the bytes are not the Steno bytes of a value.
 *
 * @example
 * ```js
 * console.log(decodeToCodePoints(encode('a😀'))); // [ 97, 128512 ]
 * ```
 */
export function decodeToCodePoints(bytes: StenoBytes): number[] {
  const input = toBytes(bytes, 'bytes');
  const n = input.length;
  const result: number[] = [];
  if (n === 0) return result;

  const shared = acquire();
  let out: Uint16Array;
  let o: number;

  try {
    out = unitsFor(n * 2, shared);
    o = decodeUnits(decoderFor(shared), input, 0, n, out, 0, 0, true);
  } catch (e) {
    release(shared);
    throw e;
  }

  // A decoded value never has a high surrogate code point directly followed by a low one (FORMAT.md section 10.1 rule 5), so every pair of
  // code units here is one supplementary code point.
  for (let i = 0; i < o; i++) {
    const u = out[i]!;

    if ((u & 0xfc00) === 0xd800 && i + 1 < o && (out[i + 1]! & 0xfc00) === 0xdc00) {
      result.push(((u - 0xd800) << 10) + (out[i + 1]! - 0xdc00) + 0x10000);
      i++;
    } else {
      result.push(u);
    }
  }

  release(shared);
  return result;
}
