// The fast paths against the plain paths and the reference codec: around every size threshold that the fast paths introduce, with a state change
// at every offset there, and with inputs that end inside a run. Also the bound on the layouts that transitions keep alive.

import assert from 'node:assert/strict';
import { test } from 'node:test';

import {
  StenoDecodeError,
  StenoDecoder,
  StenoEncoder,
  decode,
  decodeToCodePoints,
  encode,
  encodeCodePoints,
  encodeInto,
  isAsciiText,
} from '../dist/index.js';
import { INITIAL_LAYOUT, LAYOUT_CACHE_LIMIT, TRANSITIONS, clearLayoutCache, layoutCacheSize, reachableLayouts } from '../dist/layout.js';
import { Random, codePointsOf, concat, hex, randomCodePoints, toStringFromCodePoints } from './helpers.js';
import { refDecode, refEncode } from './reference.js';

// Code points that change the state, take more than 1 byte, or end a run of ASCII text: a Cyrillic letter (switch), an ideograph and a kana
// (CJK context), a Hangul syllable, an emoji (surrogate pair), a lone surrogate, a byte order mark, a control and DEL.
const CHANGERS = ['Ж', '漢', 'ア', 'あ', '한', '😀', '\ud800', '\udc00', '﻿', '\u0001', '\u007f', '、', 'ａ'];

// The lengths around the thresholds of the fast paths: short strings (16, 32), the ASCII runs (32 + 64), the decoder pieces (128) and the
// chunks of the ASCII encoder (4,096).
const LENGTHS = [15, 16, 17, 31, 32, 33, 63, 64, 65, 95, 96, 97, 127, 128, 129, 130, 160, 161, 4095, 4096, 4097, 4160, 4161, 8193];

/** Checks one string: the fast one-shot paths against the plain code point path, the reference codec (when short), and streaming. */
function check(text: string, rng: Random): void {
  const cps = codePointsOf(text);
  const bytes = encode(text);
  const expected = encodeCodePoints(cps);

  assert.equal(hex(bytes), hex(expected), `length ${text.length}`);
  if (cps.length <= 200) assert.equal(hex(bytes), hex(refEncode(cps)));

  const into = new Uint8Array(bytes.length + 3).fill(0xee);
  assert.equal(encodeInto(text, into).written, bytes.length);
  assert.equal(hex(into.subarray(0, bytes.length)), hex(bytes));
  assert.ok(into.subarray(bytes.length).every((b) => b === 0xee), 'nothing written after the value');

  assert.equal(decode(bytes), text);
  assert.deepEqual(decodeToCodePoints(bytes), cps);

  // Streaming, at a random split.
  const cut = rng.int(0, text.length);
  const encoder = new StenoEncoder();
  assert.equal(hex(concat([encoder.encode(text.slice(0, cut), { stream: true }), encoder.encode(text.slice(cut))])), hex(bytes));

  const at = rng.int(0, bytes.length);
  const decoder = new StenoDecoder();
  assert.equal(decoder.decode(bytes.subarray(0, at), { stream: true }) + decoder.decode(bytes.subarray(at)), text);
}

function ascii(rng: Random, length: number): string {
  let s = '';
  for (let i = 0; i < length; i++) s += String.fromCharCode(rng.next() < 0.05 ? rng.pick([9, 10, 13]) : rng.int(0x20, 0x7e));
  return s;
}

test('ASCII text around every threshold, with a state change at every offset near it', () => {
  const rng = new Random(50);

  for (const length of LENGTHS) {
    const base = ascii(rng, length);
    check(base, rng);

    // A changer at every offset within 4 of each threshold that lies inside the string, and near both ends.
    const offsets = new Set<number>();

    for (const t of [0, 16, 32, 64, 96, 128, 4096, length]) {
      for (let d = -4; d <= 4; d++) if (t + d >= 0 && t + d <= length) offsets.add(t + d);
    }

    for (const p of offsets) {
      const changer = rng.pick(CHANGERS);
      check(base.slice(0, p) + changer + base.slice(p), rng);
      check(base.slice(0, p) + changer + base.slice(p, Math.max(p, length - 40)) + changer + base.slice(Math.max(p, length - 40)), rng);
    }
  }
});

test('ASCII runs inside other text, of lengths around the run thresholds', () => {
  const rng = new Random(51);

  for (let n = 0; n < 300; n++) {
    let text = '';

    while (text.length < 600) {
      text += toStringFromCodePoints(randomCodePoints(rng, 6));
      text += ascii(rng, rng.pick([0, 1, 31, 32, 33, 63, 64, 95, 96, 97, 100, 200]));
    }

    // Keep it a value when two pieces meet in a high and a low surrogate: as UTF-16 that is simply one supplementary code point.
    check(text, rng);
  }
});

test('long runs of ideographs, kana and Hangul syllables, entering and leaving the wide contexts', () => {
  const rng = new Random(52);
  const pools: [number, number][] = [
    [0x4e00, 0x9fff],
    [0x3041, 0x3096],
    [0x30a1, 0x30fc],
    [0x3000, 0x303f],
    [0xac00, 0xd7a3],
    [0xff00, 0xffef],
    [0x41, 0x7a],
    [0xa000, 0xa0ff],
    [0x4d80, 0x4dff],
    [0x0141, 0x017f],
  ];

  for (let n = 0; n < 400; n++) {
    const cps: number[] = [];

    while (cps.length < 120) {
      const [lo, hi] = rng.pick(pools);
      const run = rng.int(1, 30);
      for (let i = 0; i < run; i++) cps.push(rng.int(lo, hi));
    }

    const text = toStringFromCodePoints(cps);
    assert.equal(hex(encode(text)), hex(refEncode(cps)));
    check(text, rng);
  }
});

test('broken bytes on the fast decoding paths: reason and offset of the reference codec', () => {
  const rng = new Random(53);

  for (let n = 0; n < 1500; n++) {
    const cps: number[] = [];
    while (cps.length < 40) cps.push(rng.pick([rng.int(0x4e00, 0x9fff), rng.int(0xac00, 0xd7a3), rng.int(0x3041, 0x30ff), rng.int(0x20, 0x7e)]));

    const valid = encode(toStringFromCodePoints(cps));
    const bad = valid.slice(0, rng.int(1, valid.length));
    const changes = rng.int(0, 2);
    for (let i = 0; i < changes; i++) bad[rng.int(0, bad.length - 1)] = rng.pick([0, 0xff, rng.int(1, 255)]);

    const expected = refDecode(bad);

    if ('reason' in expected) {
      assert.throws(
        () => decode(bad),
        (e: unknown) => e instanceof StenoDecodeError && e.reason === expected.reason && e.offset === expected.offset,
        hex(bad),
      );
    } else {
      assert.deepEqual(decodeToCodePoints(bad), expected.cps);
      assert.equal(hex(encode(decode(bad))), hex(bad));
    }
  }
});

test('isAsciiText at the thresholds', () => {
  const rng = new Random(54);

  for (const length of LENGTHS) {
    const bytes = encode(ascii(rng, length));
    assert.equal(isAsciiText(bytes), true);

    for (const p of [0, length >> 1, length - 1]) {
      const bad = bytes.slice();
      bad[p] = rng.pick([0x01, 0x04, 0x06, 0x66, 0xff, 0x00]);
      assert.equal(isAsciiText(bad), false);
    }
  }
});

test('a one-shot call that throws leaves the next call in the initial state', () => {
  const text = 'Москва 漢字 한국 😀';
  const expected = hex(encode(text));

  // A destination that is too small, in the middle of a script change.
  assert.throws(() => encodeInto(text, new Uint8Array(5)), RangeError);
  assert.equal(hex(encode(text)), expected);
  assert.throws(() => encodeInto('漢字'.repeat(100), new Uint8Array(10)), RangeError);
  const buffer = new Uint8Array(100);
  const { written } = encodeInto(text, buffer);
  assert.equal(hex(buffer.subarray(0, written)), expected);

  // Invalid bytes after state changes, and code points that are not a value.
  const bytes = encode(text);
  assert.throws(() => decode(concat([bytes.subarray(0, 12), new Uint8Array([0xff, 0xff])])), StenoDecodeError);
  assert.equal(decode(bytes), text);
  assert.throws(() => decode(concat([bytes, new Uint8Array([0])])), StenoDecodeError);
  assert.equal(decode(bytes), text);
  assert.throws(() => encodeCodePoints([0x416, 0x4e00, 0xd800, 0xdc00]), RangeError);
  assert.equal(hex(encodeCodePoints(codePointsOf(text))), expected);
});

test('the layouts kept alive by the cache and its transitions stay bounded', () => {
  clearLayoutCache();
  const rng = new Random(55);

  // Many windows in turn, so that the cache fills and drops layouts many times, while transitions are remembered.
  for (let round = 0; round < 3; round++) {
    const cps: number[] = [];

    for (let i = 0; i < 4 * LAYOUT_CACHE_LIMIT; i++) {
      const w = rng.next() < 0.5 ? 0xe000 + rng.int(0, 0x23f) * 0x80 : 0x40000 + rng.int(0, 0x1000) * 0x80;
      // A run of 20 code points in each window, so that each state builds its layout (a state builds one after a run of 16).
      for (let k = 0; k < 20; k++) cps.push(w + 5 + k);
      if (rng.next() < 0.2) cps.push(rng.int(0x4e00, 0x9fff), rng.int(0xac00, 0xd7a3));
    }

    const text = toStringFromCodePoints(cps);
    assert.equal(decode(encode(text)), text);
  }

  assert.equal(layoutCacheSize(), LAYOUT_CACHE_LIMIT);

  const alive = reachableLayouts();
  assert.ok(alive.size <= (TRANSITIONS + 1) * LAYOUT_CACHE_LIMIT + TRANSITIONS + 1, `${alive.size} layouts alive`);

  // A dropped layout remembers nothing, so it keeps no other layout alive.
  let dropped = 0;

  for (const layout of alive) {
    if (!layout.evicted) continue;
    dropped++;
    assert.ok(layout.nextLayouts.every((l) => l === null), 'a dropped layout remembers no transition');
  }

  // Transitions are remembered only towards layouts that were in the cache, so a dropped one is seldom still reachable; when it is, it keeps
  // nothing else alive.
  void dropped;
  assert.equal(INITIAL_LAYOUT.evicted, false);
});

/** The best time per code point, over several rounds, of encoding and decoding a text. */
function bestTime(text: string, codePoints: number): number {
  const bytes = encode(text);
  let best = Infinity;

  for (let round = 0; round < 7; round++) {
    const start = performance.now();
    encode(text);
    decode(bytes);
    best = Math.min(best, ((performance.now() - start) * 1e6) / (2 * codePoints));
  }

  return best;
}

/** Text that cycles through `windows` windows, `run` code points in each. */
function hopping(windows: number, run: number, total: number): { text: string; count: number } {
  const cps: number[] = [];
  for (let i = 0; cps.length < total; i++) for (let k = 0; k < run; k++) cps.push(0x40000 + (i % windows) * 0x80 + 1 + k);
  return { text: toStringFromCodePoints(cps), count: cps.length };
}

test('text that hops across more windows than the cache holds costs a bounded multiple of ordinary slow-path text', () => {
  // The worst cases of the layout cache: every code point (or every short run) in a new window, cycling through 2,000 windows, more than the
  // 1,024 layouts that the cache holds. A state that is left after one code point builds no layout (it resolves the code point from the state),
  // and a state builds its layout after a run of 16, so the cost per code point is bounded at about one computation of the ranges. README.md
  // gives the figures. The reference is text that changes state on every code point between two windows, whose layouts are cached.
  const ordinary = hopping(2, 1, 20000);
  const reference = bestTime(ordinary.text, ordinary.count);

  for (const run of [1, 2, 17]) {
    const hop = hopping(2000, run, 40000);
    assert.equal(decode(encode(hop.text)), hop.text);

    const time = bestTime(hop.text, hop.count);
    assert.ok(time < 40 * reference + 200, `run ${run}: ${time.toFixed(0)} ns per code point, against ${reference.toFixed(0)} for ordinary text`);
  }

  // Code points that each move to a new state build (almost) no layouts: see SEEN_SLOTS in layout.ts.
  clearLayoutCache();
  const hop = hopping(2000, 1, 20000);
  encode(hop.text);
  assert.ok(layoutCacheSize() < 200, `${layoutCacheSize()} layouts built`);
  assert.ok(layoutCacheSize() <= LAYOUT_CACHE_LIMIT);
});
