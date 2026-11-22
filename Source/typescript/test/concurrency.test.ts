// Concurrency. JavaScript runs one thread per realm, and each worker has its own copy of the module and its layout cache. These tests check that
// workers encoding and decoding at the same time agree with one thread, and that interleaved encoders and decoders in one thread, which share
// the layout cache, do not disturb each other.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { Worker } from 'node:worker_threads';

import { StenoDecoder, StenoEncoder, encode } from '../dist/index.js';
import { work } from './concurrency-worker.js';
import { Random, concat, hex, randomCodePoints, toStringFromCodePoints } from './helpers.js';

test('workers agree with one thread', async () => {
  const seeds = [101, 102, 103, 104];
  const count = 300;

  const results = await Promise.all(
    seeds.map(
      (seed) =>
        new Promise<string[]>((resolve, reject) => {
          const worker = new Worker(new URL('./concurrency-worker.js', import.meta.url), { workerData: { seed, count } });
          worker.once('message', (m: string[]) => {
            resolve(m);
            void worker.terminate();
          });
          worker.once('error', reject);
        }),
    ),
  );

  seeds.forEach((seed, i) => assert.deepEqual(results[i], work(seed, count)));
});

test('interleaved encoders and decoders in one thread', () => {
  const rng = new Random(105);
  const texts = Array.from({ length: 8 }, () => toStringFromCodePoints(randomCodePoints(rng, 200)));
  const encoders = texts.map(() => new StenoEncoder());
  const decoders = texts.map(() => new StenoDecoder());
  const expected = texts.map((t) => encode(t));
  const parts: Uint8Array[][] = texts.map(() => []);
  const decoded = texts.map(() => '');
  const at = texts.map(() => 0);
  const byteAt = texts.map(() => 0);

  // Feed all of them a few units or bytes at a time, round robin, so that their states change the shared cache in turn.
  let busy = true;

  while (busy) {
    busy = false;

    for (let i = 0; i < texts.length; i++) {
      const text = texts[i]!;

      if (at[i]! < text.length) {
        const next = Math.min(text.length, at[i]! + rng.int(1, 4));
        parts[i]!.push(encoders[i]!.encode(text.slice(at[i], next), { stream: true }));
        at[i] = next;
        busy = true;
      }

      const bytes = expected[i]!;

      if (byteAt[i]! < bytes.length) {
        const next = Math.min(bytes.length, byteAt[i]! + rng.int(1, 4));
        decoded[i] += decoders[i]!.decode(bytes.subarray(byteAt[i], next), { stream: true });
        byteAt[i] = next;
        busy = true;
      }
    }
  }

  for (let i = 0; i < texts.length; i++) {
    parts[i]!.push(encoders[i]!.encode());
    decoded[i] += decoders[i]!.decode();
    assert.equal(hex(concat(parts[i]!)), hex(expected[i]!));
    assert.equal(decoded[i], texts[i]);
  }
});
