import { type StenoBytes, checkText, toBytes } from './input.js';

/**
 * Compares two byte arrays as unsigned bytes, lexicographically, a proper prefix being smaller.
 *
 * @remarks
 * For Steno bytes this is the order of the values in code point order (see {@link compareCodePoints}), so a store can sort and compare them
 * without decoding them. It can be passed to `Array.prototype.sort`.
 *
 * @param a - The first bytes: a `Uint8Array`, or any other `ArrayBuffer`, `SharedArrayBuffer`, typed array or `DataView`, read as its bytes.
 * @param b - The second bytes, of the same kinds.
 * @returns -1 if `a` comes first, 0 if they are equal, 1 if `b` comes first.
 * @throws TypeError if `a` or `b` is none of those kinds.
 *
 * @example
 * ```js
 * console.log(compareBytes(encode('Zug'), encode('Zürich'))); // -1
 * ```
 */
export function compareBytes(a: StenoBytes, b: StenoBytes): number {
  const u = toBytes(a, 'a');
  const v = toBytes(b, 'b');
  const n = Math.min(u.length, v.length);

  for (let i = 0; i < n; i++) {
    const x = u[i]!;
    const y = v[i]!;
    if (x !== y) return x < y ? -1 : 1;
  }

  return u.length < v.length ? -1 : u.length > v.length ? 1 : 0;
}

/**
 * Determines whether a byte array starts with another, for example whether Steno bytes start with the Steno bytes of a prefix.
 *
 * @remarks
 * The Steno bytes of a prefix of a value are a prefix of the Steno bytes of the value, when the prefix is taken in code points. A prefix of a
 * string that ends between the two halves of a surrogate pair is not such a prefix: it ends in an unpaired high surrogate, which is another code
 * point.
 *
 * @param bytes - The bytes to look at, of the kinds that {@link compareBytes} takes.
 * @param prefix - The bytes to look for at the start, of the same kinds.
 * @returns True if `bytes` starts with `prefix`.
 * @throws TypeError if `bytes` or `prefix` is not bytes.
 *
 * @example
 * ```js
 * console.log(startsWith(encode('Zürich'), encode('Zür'))); // true
 * ```
 */
export function startsWith(bytes: StenoBytes, prefix: StenoBytes): boolean {
  const u = toBytes(bytes, 'bytes');
  const p = toBytes(prefix, 'prefix');
  const n = p.length;
  if (n > u.length) return false;

  for (let i = 0; i < n; i++) if (u[i] !== p[i]) return false;

  return true;
}

/**
 * Compares two strings in code point order, the order in which their Steno bytes sort.
 *
 * @remarks
 * Code point order compares the strings code point by code point: a surrogate pair as the one supplementary code point (U+10000..U+10FFFF) that it
 * stands for, and an unpaired surrogate as a code point of its own (U+D800..U+DFFF). For strings without unpaired surrogates it is also the order
 * of their UTF-8 bytes.
 *
 * JavaScript's `<`, `>` and the default `Array.prototype.sort` compare UTF-16 code units, which is not code point order, and `localeCompare` is a
 * language's order, which is neither. In strings without unpaired surrogates, code unit order differs from code point order in one case: where
 * the strings first differ, one has a supplementary character (a surrogate pair) and the other has a character in U+E000..U+FFFF. Code unit order
 * puts the supplementary character first, because its first code unit is in 0xD800..0xDBFF; code point order puts it last. For example
 * `'すごい！'` (with U+FF01) comes after `'すごい😀'` (with U+1F600) with `<`, but before it in code point order.
 *
 * @param a - The first string.
 * @param b - The second string.
 * @returns -1 if `a` comes first, 0 if they are equal, 1 if `b` comes first.
 * @throws TypeError if `a` or `b` is not a string.
 *
 * @example
 * ```js
 * console.log('すごい！' < 'すごい😀'); // false
 * console.log(compareCodePoints('すごい！', 'すごい😀')); // -1
 * ```
 */
export function compareCodePoints(a: string, b: string): number {
  checkText(a, 'a');
  checkText(b, 'b');
  if (a === b) return 0;

  const n = Math.min(a.length, b.length);
  let i = 0;

  while (i < n && a.charCodeAt(i) === b.charCodeAt(i)) i++;

  // One string is a prefix of the other in code units: the shorter comes first. That is code point order too, also if the shorter one ends in a
  // high surrogate that the longer one pairs (as a code point of its own, a high surrogate is below every supplementary code point).
  if (i === a.length || i === b.length) return a.length < b.length ? -1 : 1;

  // If both strings share a high surrogate just before i and either pairs it with a low surrogate at i, the code points that differ start at
  // that high surrogate.
  if (i > 0 && isHigh(a.charCodeAt(i - 1)) && (isLow(a.charCodeAt(i)) || isLow(b.charCodeAt(i)))) i--;

  return codePointAt(a, i) < codePointAt(b, i) ? -1 : 1;
}

function isHigh(u: number): boolean {
  return (u & 0xfc00) === 0xd800;
}

function isLow(u: number): boolean {
  return (u & 0xfc00) === 0xdc00;
}

function codePointAt(s: string, i: number): number {
  const u = s.charCodeAt(i);

  if (isHigh(u) && i + 1 < s.length) {
    const v = s.charCodeAt(i + 1);
    if (isLow(v)) return ((u - 0xd800) << 10) + (v - 0xdc00) + 0x10000;
  }

  return u;
}

// For each byte value, 1 if it is the Steno byte of tab, line feed, carriage return or U+0020..U+007E in the initial layout.
const ASCII_TEXT = /* @__PURE__ */ (() => {
  const t = new Uint8Array(256);
  t[0x02] = 1;
  t[0x03] = 1;
  t[0x05] = 1;
  for (let b = 0x07; b <= 0x65; b++) t[b] = 1;
  return t;
})();

/**
 * Determines from the bytes alone, without decoding, whether a value has only ASCII text: tab, line feed, carriage return and U+0020..U+007E.
 *
 * @remarks
 * Such a value is encoded in the initial layout throughout, so its bytes are exactly the bytes `02`, `03`, `05` and `07`..`65` (FORMAT.md section
 * 14.1), and bytes that are all of these are valid. The bytes are not ASCII bytes: U+0020..U+007E are `07`..`65` (the code point minus 0x19).
 *
 * @param bytes - The Steno bytes of a whole value, of the kinds that {@link compareBytes} takes.
 * @throws TypeError if `bytes` is not bytes.
 * @returns True if the value has only those characters (the empty value included). False otherwise, also for bytes that are not valid Steno
 *   data.
 *
 * @example
 * ```js
 * console.log(isAsciiText(encode('Hello, world!'))); // true
 * console.log(isAsciiText(encode('Zürich'))); // false
 * ```
 */
export function isAsciiText(bytes: StenoBytes): boolean {
  const u = toBytes(bytes, 'bytes');
  const t = ASCII_TEXT;
  for (let i = 0; i < u.length; i++) if (t[u[i]!] === 0) return false;
  return true;
}
