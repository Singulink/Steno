// Properties of the encoding on seeded random text (FORMAT.md section 13), and agreement with the plain reference codec in reference.ts.

import assert from 'node:assert/strict';
import { test } from 'node:test';

import { compareBytes, compareCodePoints, decode, decodeToCodePoints, encode, encodeCodePoints, startsWith } from '../dist/index.js';
import { Random, codePointsOf, compareArrays, hex, randomCodePoints, toStringFromCodePoints } from './helpers.js';
import { refEncode } from './reference.js';

test('random text: the bytes are those of the reference codec, and decode back', () => {
  const rng = new Random(1);

  for (let n = 0; n < 1500; n++) {
    const cps = randomCodePoints(rng, 40);
    const text = toStringFromCodePoints(cps);
    const bytes = encode(text);
    const expected = hex(refEncode(cps));

    assert.equal(hex(bytes), expected, `value ${n}: ${cps.map((c) => c.toString(16)).join(' ')}`);
    assert.equal(hex(encodeCodePoints(cps)), expected);
    assert.equal(decode(bytes), text);
    assert.deepEqual(decodeToCodePoints(bytes), cps);
    assert.ok(!bytes.includes(0), 'no zero byte');
  }
});

/** Joins two values, leaving out a low surrogate code point after a high one, so that the result is a value. */
function join(a: readonly number[], b: readonly number[]): number[] {
  const last = a[a.length - 1];
  const first = b[0];
  const pair = last !== undefined && first !== undefined && last >= 0xd800 && last <= 0xdbff && first >= 0xdc00 && first <= 0xdfff;
  return a.concat(pair ? b.slice(1) : b);
}

test('random text: the bytes sort like the code points', () => {
  const rng = new Random(2);

  for (let n = 0; n < 3000; n++) {
    // Two values with a common start, so that they differ after the state has moved.
    const common = randomCodePoints(rng, 12);
    const a = join(common, randomCodePoints(rng, 6));
    const b = rng.next() < 0.1 ? a.slice() : join(common, randomCodePoints(rng, 6));
    const sa = toStringFromCodePoints(a);
    const sb = toStringFromCodePoints(b);
    const expected = compareArrays(a, b);

    assert.equal(compareBytes(encode(sa), encode(sb)), expected);
    assert.equal(compareCodePoints(sa, sb), expected);
    assert.equal(compareCodePoints(sb, sa), expected === 0 ? 0 : -expected);
  }
});

test('random text: the encoding of a prefix is a prefix', () => {
  const rng = new Random(3);

  for (let n = 0; n < 300; n++) {
    const cps = randomCodePoints(rng, 30);
    const bytes = encode(toStringFromCodePoints(cps));

    for (let k = 0; k <= cps.length; k++) {
      const prefix = encodeCodePoints(cps.slice(0, k));
      assert.ok(startsWith(bytes, prefix), `prefix of ${k} code points`);
    }
  }
});

test('random strings of code units, lone surrogates included, round trip', () => {
  const rng = new Random(4);

  for (let n = 0; n < 2000; n++) {
    const units: number[] = [];
    const length = rng.int(0, 30);

    for (let i = 0; i < length; i++) {
      const r = rng.next();
      units.push(r < 0.4 ? rng.int(0xd800, 0xdfff) : r < 0.6 ? rng.int(0x20, 0x7e) : r < 0.8 ? rng.int(0xe000, 0xffff) : rng.int(0, 0xffff));
    }

    const text = String.fromCharCode(...units);
    const bytes = encode(text);

    assert.equal(decode(bytes), text);
    assert.equal(hex(bytes), hex(refEncode(codePointsOf(text))));
  }
});

test('code unit order and code point order differ for supplementary characters against U+E000..U+FFFF', () => {
  // The example of the documentation.
  assert.equal('すごい！' < 'すごい😀', false);
  assert.equal(compareCodePoints('すごい！', 'すごい😀'), -1);
  assert.equal(compareBytes(encode('すごい！'), encode('すごい😀')), -1);

  // A lone high surrogate comes before every supplementary code point, also before the pair that starts with the same code unit.
  assert.equal(compareCodePoints('\ud83d', '😀'), -1);
  assert.equal(compareCodePoints('\ud83d￿', '😀'), -1);
  assert.equal(compareBytes(encode('\ud83d￿'), encode('😀')), -1);
  assert.equal(compareCodePoints('a', 'a'), 0);
  assert.equal(compareCodePoints('', 'a'), -1);
});

test('no byte below 08 inside a code point: each is the first byte of a C0 control or the space', () => {
  const rng = new Random(5);

  for (let n = 0; n < 1000; n++) {
    const cps = randomCodePoints(rng, 40);
    const bytes = encodeCodePoints(cps);

    // The bytes of code point k are those from the end of the encoding of the first k code points (a prefix) to the end of the next one.
    let start = 0;

    for (let k = 0; k < cps.length; k++) {
      const end = encodeCodePoints(cps.slice(0, k + 1)).length;
      const c = cps[k]!;

      for (let i = start + 1; i < end; i++) assert.ok(bytes[i]! >= 8, `trail byte ${bytes[i]} of U+${c.toString(16)}`);
      assert.equal(bytes[start]! < 8, c <= 0x20, `first byte ${bytes[start]} of U+${c.toString(16)}`);
      start = end;
    }
  }

  // The first byte of each C0 control and the space, in every context.
  const expected = (c: number): number => (c <= 8 ? 1 : c <= 0xa ? c - 7 : c <= 0xc ? 4 : c === 0xd ? 5 : c <= 0x1f ? 6 : 7);

  for (const prefix of ['', 'Ж', '漢あ', '漢ア', '한', '😀😀', 'ŁŁ']) {
    for (let c = 0; c <= 0x20; c++) {
      const head = encode(prefix);
      assert.equal(encode(prefix + String.fromCharCode(c))[head.length], expected(c), `U+${c.toString(16)} after ${prefix}`);
    }
  }
});

test('splitting the bytes on 03 finds the line feeds', () => {
  const rng = new Random(6);

  for (let n = 0; n < 300; n++) {
    const lines = Array.from({ length: rng.int(1, 6) }, () => toStringFromCodePoints(randomCodePoints(rng, 12).filter((c) => c !== 0x0a)));
    const text = lines.join('\n');
    const bytes = encode(text);

    // The byte positions of the line feeds, from encoding the text up to each of them.
    const expected: number[] = [];
    let at = -1;

    while ((at = text.indexOf('\n', at + 1)) >= 0) {
      // A line that ends in a high surrogate before the line feed: the prefix is still a prefix in code points, because \n is not a low surrogate.
      expected.push(encode(text.slice(0, at)).length);
    }

    const found: number[] = [];
    bytes.forEach((b, i) => {
      if (b === 0x03) found.push(i);
    });

    assert.deepEqual(found, expected);
  }
});
