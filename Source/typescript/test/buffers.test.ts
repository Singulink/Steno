// Destinations and the maximum-size helpers.

import assert from 'node:assert/strict';
import { test } from 'node:test';

import { decode, encode, encodeInto, maxDecodedLength, maxEncodedLength } from '../dist/index.js';
import { Random, hex, randomCodePoints, toStringFromCodePoints } from './helpers.js';

test('encodeInto: an exact-size destination works, a smaller one throws', () => {
  const rng = new Random(20);

  for (let n = 0; n < 500; n++) {
    const text = toStringFromCodePoints(randomCodePoints(rng, 30));
    const expected = encode(text);

    const exact = new Uint8Array(expected.length);
    assert.deepEqual(encodeInto(text, exact), { read: text.length, written: expected.length });
    assert.equal(hex(exact), hex(expected));

    const big = new Uint8Array(maxEncodedLength(text.length) + 5).fill(0xaa);
    assert.equal(encodeInto(text, big).written, expected.length);
    assert.equal(hex(big.subarray(0, expected.length)), hex(expected));
    assert.ok(big.subarray(expected.length).every((b) => b === 0xaa), 'nothing written after the bytes');

    if (expected.length > 0) {
      const small = new Uint8Array(expected.length - 1);
      assert.throws(() => encodeInto(text, small), RangeError);
    }
  }
});

test('encodeInto writes from index 0 of a subarray and not outside it', () => {
  const backing = new Uint8Array(20).fill(0xaa);
  const view = backing.subarray(5, 10);
  assert.throws(() => encodeInto('abcdefg', view), RangeError);
  assert.ok(backing.subarray(0, 5).every((b) => b === 0xaa));
  assert.ok(backing.subarray(10).every((b) => b === 0xaa));
  assert.equal(encodeInto('abc', view).written, 3);
});

test('maxEncodedLength holds and is reached', () => {
  // Unpaired surrogates take 3 bytes each: the bound is reached.
  const lone = '\ud800'.repeat(100);
  assert.equal(encode(lone).length, maxEncodedLength(lone.length));

  // Other worst cases: controls, scripts that switch on every code point, supplementary code points far apart.
  const cases = [
    '\u0001'.repeat(50),
    'Жሀ'.repeat(50),
    '\u{10FFFF}\u{20000}\u{40000}'.repeat(30),
    '\udfff\ud800'.repeat(50),
    '漢Ł'.repeat(50),
  ];

  for (const text of cases) assert.ok(encode(text).length <= maxEncodedLength(text.length), text.slice(0, 4));

  const rng = new Random(21);

  for (let n = 0; n < 1000; n++) {
    const text = toStringFromCodePoints(randomCodePoints(rng, 50));
    assert.ok(encode(text).length <= maxEncodedLength(text.length));
  }

  assert.equal(maxEncodedLength(0), 0);
  assert.throws(() => maxEncodedLength(-1), RangeError);
  assert.throws(() => maxEncodedLength(1.5), RangeError);
});

test('maxDecodedLength holds and is nearly reached', () => {
  // A supplementary main window: 1 byte and 2 code units per code point, after the first.
  const emoji = String.fromCodePoint(...Array.from({ length: 100 }, (_, i) => 0x1f600 + (i % 64)));
  const bytes = encode(emoji);
  assert.equal(bytes.length, 102);
  assert.ok(decode(bytes).length <= maxDecodedLength(bytes.length));
  assert.equal(decode(bytes).length, 200);

  const rng = new Random(22);

  for (let n = 0; n < 1000; n++) {
    const b = encode(toStringFromCodePoints(randomCodePoints(rng, 50)));
    assert.ok(decode(b).length <= maxDecodedLength(b.length));
  }

  assert.throws(() => maxDecodedLength(-1), RangeError);
});

test('long values go past the scratch buffers', () => {
  const text = 'Москва, 東京 and 서울. '.repeat(20000);
  const bytes = encode(text);
  assert.equal(decode(bytes), text);
  assert.ok(bytes.length < text.length * 2);
});

test('long output with unpaired surrogates keeps them', () => {
  const text = ('abc\ud800def\udfffghi😀'.repeat(50) + '\ud83d').repeat(3);
  assert.equal(decode(encode(text)), text);
});

test('a byte order mark at the start of a long value is kept', () => {
  for (const text of ['﻿' + 'Москва '.repeat(20), '﻿'.repeat(3) + 'abc'.repeat(30), '﻿﻿', 'x'.repeat(40) + '﻿']) {
    assert.equal(decode(encode(text)), text);
  }
});
