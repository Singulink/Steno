// A worker for concurrency.test.ts: encodes and decodes random text of a seed and sends the results back.

import { parentPort, workerData } from 'node:worker_threads';

import { decode, encode } from '../dist/index.js';
import { Random, hex, randomCodePoints, toStringFromCodePoints } from './helpers.js';

export function work(seed: number, count: number): string[] {
  const rng = new Random(seed);
  const results: string[] = [];

  for (let n = 0; n < count; n++) {
    const text = toStringFromCodePoints(randomCodePoints(rng, 60));
    const bytes = encode(text);
    results.push(hex(bytes) + (decode(bytes) === text ? '' : ' MISMATCH'));
  }

  return results;
}

if (parentPort !== null) {
  const { seed, count } = workerData as { seed: number; count: number };
  parentPort.postMessage(work(seed, count));
}
