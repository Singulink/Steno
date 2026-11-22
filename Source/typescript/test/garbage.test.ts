// Garbage: random, cut-off and mutated bytes never crash or hang. Each is rejected with the reason and offset of the reference codec, or decodes
// to a value that encodes to the same bytes.

import assert from 'node:assert/strict';
import { test } from 'node:test';

import { StenoDecodeError, StenoDecoder, decode, decodeToCodePoints, encode, encodeCodePoints } from '../dist/index.js';
import { Random, hex, randomCodePoints, toStringFromCodePoints } from './helpers.js';
import { refDecode } from './reference.js';

function check(bytes: Uint8Array, rng: Random): 'accepted' | 'rejected' {
  const expected = refDecode(bytes);
  let text: string;

  try {
    text = decode(bytes);
  } catch (e) {
    assert.ok(e instanceof StenoDecodeError, String(e));
    assert.ok('reason' in expected, `rejected ${hex(bytes)}, but the reference accepts it`);
    assert.equal(e.reason, expected.reason, hex(bytes));
    assert.equal(e.offset, expected.offset, hex(bytes));

    // The streaming decoder, with a random split, agrees.
    const decoder = new StenoDecoder();
    const cut = rng.int(0, bytes.length);
    assert.throws(
      () => {
        decoder.decode(bytes.subarray(0, cut), { stream: true });
        decoder.decode(bytes.subarray(cut));
      },
      (s: unknown) => s instanceof StenoDecodeError && s.reason === e.reason && s.offset === e.offset,
    );

    return 'rejected';
  }

  assert.ok('cps' in expected, `accepted ${hex(bytes)}, but the reference rejects it`);
  assert.deepEqual(decodeToCodePoints(bytes), expected.cps);
  assert.equal(hex(encode(text)), hex(bytes), 'decoded text re-encodes to the same bytes');
  assert.equal(hex(encodeCodePoints(expected.cps)), hex(bytes));
  return 'accepted';
}

test('random bytes', () => {
  const rng = new Random(30);
  const counts = { accepted: 0, rejected: 0 };

  for (let n = 0; n < 4000; n++) {
    const bytes = new Uint8Array(rng.int(0, 12));

    for (let i = 0; i < bytes.length; i++) {
      const r = rng.next();
      bytes[i] = r < 0.05 ? 0 : r < 0.5 ? rng.int(0x07, 0x65) : rng.int(0, 255);
    }

    counts[check(bytes, rng)]++;
  }

  assert.ok(counts.accepted > 100 && counts.rejected > 100, JSON.stringify(counts));
});

test('cut-off and mutated encodings', () => {
  const rng = new Random(31);
  const counts = { accepted: 0, rejected: 0 };

  for (let n = 0; n < 1500; n++) {
    const valid = encode(toStringFromCodePoints(randomCodePoints(rng, 20)));
    if (valid.length === 0) continue;

    counts[check(valid.subarray(0, rng.int(0, valid.length - 1)), rng)]++;

    const mutated = valid.slice();
    const changes = rng.int(1, 3);
    for (let i = 0; i < changes; i++) mutated[rng.int(0, mutated.length - 1)] = rng.int(0, 255);
    counts[check(mutated, rng)]++;
  }

  assert.ok(counts.accepted > 100 && counts.rejected > 100, JSON.stringify(counts));
});

test('every lead byte in the initial layout, with every kind of trail', () => {
  const rng = new Random(32);

  for (let b = 0; b < 256; b++) {
    for (const trail of [[], [0], [1], [7], [8], [0xff], [0xff, 0xff], [0xff, 0xff, 0xff], [1, 1, 1], [8, 8, 8], [8, 7, 8], [0x80, 0x80, 0x80]]) {
      check(new Uint8Array([b, ...trail]), rng);
    }
  }
});
