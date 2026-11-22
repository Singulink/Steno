// Differential tests against the second independent oracle in oracle2.ts (a port of ReferenceSteno.cs, from an adversarial review). The random
// tests run a fixed number of rounds, so the default run is quick and deterministic; set STENO_FUZZ_ROUNDS for a longer run.

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

import * as S from '../dist/index.js';
import { refDecode, refEncode } from './oracle2.js';

const ROUNDS = Number(process.env['STENO_FUZZ_ROUNDS'] ?? '300');

class Rng {
  #s: number;
  constructor(seed: number) {
    this.#s = seed >>> 0;
  }
  next(): number {
    let t = (this.#s = (this.#s + 0x6d2b79f5) >>> 0);
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  }
  int(a: number, b: number): number {
    return a + Math.floor(this.next() * (b - a + 1));
  }
}

const hex = (b: Uint8Array): string => Buffer.from(b.buffer, b.byteOffset, b.length).toString('hex');
const str = (cps: readonly number[]): string => cps.map((c) => String.fromCodePoint(c)).join('');
const cpsOf = (s: string): number[] => Array.from(s, (ch) => ch.codePointAt(0)!);
const why = (f: () => unknown): string => {
  try {
    f();
    return 'ok';
  } catch (e) {
    if (e instanceof S.StenoDecodeError) return `${e.reason}@${e.offset}`;
    throw e;
  }
};
const refWhy = (b: Uint8Array): string => {
  const r = refDecode(b);
  return typeof r === 'string' ? r : 'ok';
};

const POOLS: readonly (readonly [number, number])[] = [
  [0x20, 0x7e], [0x20, 0x7e], [0x41, 0x5a], [0, 0x1f], [0x7f, 0xbf], [0xc0, 0x13f], [0x140, 0x2ff], [0x300, 0x3ff], [0x400, 0x5ff],
  [0x600, 0xeff], [0xef0, 0xf10], [0x1000, 0x1fff], [0x2000, 0x20ff], [0x2100, 0x2fff], [0x3000, 0x30ff], [0x3040, 0x309f], [0x30a0, 0x30ff],
  [0x3100, 0x4dff], [0x4e00, 0x9fff], [0xa000, 0xabff], [0xac00, 0xd7a3], [0xd7a0, 0xd810], [0xd800, 0xdfff], [0xdbf0, 0xdc10], [0xe000, 0xe0ff],
  [0xfef0, 0xffff], [0xff00, 0xff1f], [0x10000, 0x1ffff], [0x1f300, 0x1f6ff], [0x10f00, 0x11100], [0x1ff00, 0x20100], [0x20000, 0x2a6df],
  [0x3ff80, 0x40080], [0x40000, 0x10ffff], [0x10ff00, 0x10ffff], [0xfeff, 0xfeff], [0xfffe, 0xfffe],
];

function randomCps(rng: Rng, maxLen: number): number[] {
  const len = rng.int(0, maxLen);
  const cps: number[] = [];

  while (cps.length < len) {
    const [lo, hi] = POOLS[rng.int(0, POOLS.length - 1)]!;
    const run = rng.next() < 0.15 ? rng.int(20, 300) : rng.int(1, 10);
    const base = rng.int(lo, hi);
    const spread = rng.next() < 0.7 ? 0x7f : hi - lo;

    for (let i = 0; i < run && cps.length < len; i++) {
      const c = Math.min(hi, Math.max(lo, base + rng.int(-(spread >> 1), spread)));
      const p = cps[cps.length - 1];
      if (p !== undefined && p >= 0xd800 && p <= 0xdbff && c >= 0xdc00 && c <= 0xdfff) continue;
      cps.push(c);
    }
  }

  return cps;
}

function splits(rng: Rng, n: number): number[] {
  const p: number[] = [];
  let i = 0;

  while (i < n) {
    i += rng.next() < 0.3 ? rng.int(0, 3) : rng.int(0, 200);
    p.push(Math.min(i, n));
  }

  return p;
}

test('second oracle: the oracle agrees with the published vectors', () => {
  let n = 0;

  for (const raw of readFileSync(new URL('../../../TestVectors/steno-v1-vectors.txt', import.meta.url), 'utf8').split('\n')) {
    const line = raw.replace(/\r$/, '');
    if (line === '' || line.startsWith('#')) continue;
    const f = line.split('\t');
    const cps = (x: string): number[] => (x === '-' ? [] : x.split(' ').map((h) => parseInt(h, 16)));
    const bytes = (x: string): Uint8Array => (x === '-' ? new Uint8Array(0) : Uint8Array.from(x.match(/../g)!, (h) => parseInt(h, 16)));

    if (f[0] === 'valid') {
      assert.equal(hex(refEncode(cps(f[2]!))), hex(bytes(f[3]!)), f[1]);
    } else if (f[0] === 'invalid') {
      assert.equal(refWhy(bytes(f[2]!)).split('@')[0], f[3], f[1]);
    } else {
      assert.throws(() => refEncode(cps(f[2]!)), RangeError);
    }

    n++;
  }

  assert.equal(n, 7640);
});

test('second oracle: random values through every API agree with the oracle', () => {
  const rng = new Rng(20261121);
  let n = 0;

  for (let round = 0; round < ROUNDS; round++) {
    n++;
    const cps = randomCps(rng, rng.next() < 0.05 ? 3000 : 120);
    const s = str(cps);
    const want = refEncode(cps);
    const wh = hex(want);
    assert.equal(hex(S.encode(s)), wh);
    assert.equal(hex(S.encodeCodePoints(cps)), wh);
    assert.ok(want.length <= S.maxEncodedLength(s.length));
    assert.ok(s.length <= S.maxDecodedLength(want.length));

    const dst = new Uint8Array(want.length + rng.int(0, 3));
    assert.equal(S.encodeInto(s, dst).written, want.length);
    assert.equal(hex(dst.subarray(0, want.length)), wh);
    if (want.length > 0) assert.throws(() => S.encodeInto(s, new Uint8Array(want.length - 1)), RangeError);

    assert.equal(S.decode(want), s);
    assert.deepEqual(S.decodeToCodePoints(want), cps);

    const enc = new S.StenoEncoder();
    const parts: Uint8Array[] = [];
    let at = 0;

    for (const p of splits(rng, s.length)) {
      parts.push(enc.encode(s.slice(at, p), { stream: true }));
      at = p;
    }

    parts.push(enc.encode(s.slice(at)));
    assert.equal(parts.map(hex).join(''), wh);

    const dec = new S.StenoDecoder();
    let t = '';
    at = 0;

    for (const p of splits(rng, want.length)) {
      t += dec.decode(want.subarray(at, p), { stream: true });
      at = p;
    }

    assert.equal(t + dec.decode(want.subarray(at)), s);
  }

  assert.ok(n > 0);
});

test('second oracle: mutated encodings are rejected with the oracle reason and offset, or accepted with the oracle value', () => {
  const rng = new Rng(77);

  for (let round = 0; round < ROUNDS; round++) {
    const b = Array.from(refEncode(randomCps(rng, 40)));

    for (let k = rng.int(1, 4); k > 0; k--) {
      const op = rng.int(0, 4);
      if (op === 0 && b.length > 0) b[rng.int(0, b.length - 1)] = rng.int(0, 255);
      else if (op === 1) b.splice(rng.int(0, b.length), 0, rng.int(0, 255));
      else if (op === 2 && b.length > 0) b.splice(rng.int(0, b.length - 1), 1);
      else if (op === 3) b.length = rng.int(0, b.length);
      else if (b.length > 0) b[rng.int(0, b.length - 1)] = [0, 1, 2, 3, 4, 5, 6, 7, 8, 0xfe, 0xff, 0xf8, 0xfa, 0xfb][rng.int(0, 13)]!;
    }

    const bytes = Uint8Array.from(b);
    const want = refWhy(bytes);
    assert.equal(why(() => S.decode(bytes)), want, hex(bytes));
    assert.equal(why(() => S.decodeToCodePoints(bytes)), want, hex(bytes));

    const dec = new S.StenoDecoder();
    const streamed = why(() => {
      let at = 0;

      for (const p of splits(rng, bytes.length)) {
        dec.decode(bytes.subarray(at, p), { stream: true });
        at = p;
      }

      dec.decode(bytes.subarray(at));
    });

    assert.equal(streamed, want, hex(bytes));
    if (want === 'ok') assert.deepEqual(S.decodeToCodePoints(bytes), refDecode(bytes));
  }
});

test('second oracle: every 1- and 2-byte suffix after a few prefixes', () => {
  for (const p of ['', 'ЖЖ', '漢', 'ア', '한', '😀', '漢Ł']) {
    const pre = S.encode(p);
    const buf = new Uint8Array(pre.length + 2);
    buf.set(pre);

    for (let x = 0; x < 256; x++) {
      buf[pre.length] = x;

      for (let y = -1; y < 256; y++) {
        let b: Uint8Array;

        if (y < 0) {
          b = buf.slice(0, pre.length + 1);
        } else {
          buf[pre.length + 1] = y;
          b = buf.slice();
        }

        const want = refWhy(b);
        assert.equal(why(() => S.decode(b)), want, hex(b));
      }
    }
  }
});

test('second oracle: a state-changing character at every offset of runs around the fast-path sizes', () => {
  const prefixes = ['', 'Мо', '漢', 'あ', 'ア', '한', '😀😁', '\u{10FF00}\u{10FF01}'];
  const xs = ['é', 'Ж', 'あ', 'ア', '漢', '한', '\u0001', '\ud800', '\udc00', '😀', '﻿', '￾', 'a', '　', 'Ａ', '\u{20000}'];
  const fills = ['a', 'ж', '漢', 'あ', '한', '😀'];

  for (const pre of prefixes) {
    for (const f of fills) {
      for (const x of xs) {
        for (const L of [31, 32, 33, 64, 65, 96, 128, 129]) {
          for (let p = 0; p <= L; p += 3) {
            const s = pre + f.repeat(p) + x + f.repeat(L - p);
            const want = refEncode(cpsOf(s));
            assert.equal(hex(S.encode(s)), hex(want), JSON.stringify(s));
            assert.equal(S.decode(want), s);
          }
        }
      }
    }
  }
});

test('second oracle: a decoder call that throws leaves the decoder as it was', () => {
  const rng = new Rng(99);

  for (let round = 0; round < ROUNDS; round++) {
    const s = str(randomCps(rng, 300));
    const b = S.encode(s);
    const d = new S.StenoDecoder();
    let out = '';
    let at = 0;

    while (at < b.length) {
      const p = Math.min(b.length, at + (rng.next() < 0.5 ? rng.int(0, 4) : rng.int(0, 300)));
      out += d.decode(b.subarray(at, p), { stream: true });
      at = p;

      if (rng.next() < 0.4) {
        // The next bytes of the value followed by 00: always rejected somewhere.
        const k = rng.int(0, Math.min(200, b.length - at));
        const g = new Uint8Array(k + 1);
        g.set(b.subarray(at, at + k));
        assert.throws(() => d.decode(g, { stream: rng.next() < 0.5 }), S.StenoDecodeError);
      }
    }

    assert.equal(out + d.decode(), s);
  }
});
