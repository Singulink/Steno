// The test vectors of the specification (FORMAT.md section 12). They decide whether the implementation may call itself conforming.

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

import { StenoDecodeError, decode, decodeToCodePoints, encode, encodeCodePoints, StenoDecoder } from '../dist/index.js';
import { fromHex, hex, toStringFromCodePoints } from './helpers.js';

interface Counts {
  valid: number;
  invalid: number;
  notAValue: number;
}

function parseCodePoints(field: string): number[] {
  return field === '-' ? [] : field.split(' ').map((x) => parseInt(x, 16));
}

function parseBytes(field: string): Uint8Array {
  return field === '-' ? new Uint8Array(0) : fromHex(field);
}

function reasonOf(f: () => unknown): string {
  try {
    f();
  } catch (e) {
    if (e instanceof StenoDecodeError) return e.reason;
    throw e;
  }

  return 'accepted';
}

/** Runs every vector of a file and returns the counts by kind. */
function runVectors(path: string | URL, streamingEvery: number): Counts {
  const counts: Counts = { valid: 0, invalid: 0, notAValue: 0 };
  const failures: string[] = [];
  const lines = readFileSync(path, 'utf8').split('\n');
  let index = 0;

  for (const raw of lines) {
    const line = raw.replace(/\r$/, '');
    if (line === '' || line.startsWith('#')) continue;

    const f = line.split('\t');
    const kind = f[0];
    const name = f[1] ?? '?';
    index++;

    if (kind === 'valid') {
      counts.valid++;
      const cps = parseCodePoints(f[2]!);
      const bytes = parseBytes(f[3]!);
      const expected = hex(bytes);

      const fromCps = hex(encodeCodePoints(cps));
      if (fromCps !== expected) failures.push(`${name}: encodeCodePoints gave ${fromCps}, expected ${expected}`);

      // Every valid value can be a JavaScript string: no valid vector has a high surrogate code point directly followed by a low one.
      const text = toStringFromCodePoints(cps);
      const fromText = hex(encode(text));
      if (fromText !== expected) failures.push(`${name}: encode gave ${fromText}, expected ${expected}`);

      const decoded = decodeToCodePoints(bytes);
      if (decoded.join() !== cps.join()) failures.push(`${name}: decodeToCodePoints gave ${decoded.map((c) => c.toString(16)).join(' ')}`);
      if (decode(bytes) !== text) failures.push(`${name}: decode gave another string`);

      // A sample also through the streaming decoder, one byte per call.
      if (index % streamingEvery === 0) {
        const decoder = new StenoDecoder();
        let s = '';
        for (let i = 0; i < bytes.length; i++) s += decoder.decode(bytes.subarray(i, i + 1), { stream: true });
        s += decoder.decode();
        if (s !== text) failures.push(`${name}: the streaming decoder gave another string`);
      }
    } else if (kind === 'invalid') {
      counts.invalid++;
      const bytes = parseBytes(f[2]!);
      const reason = f[3]!;

      const r1 = reasonOf(() => decode(bytes));
      const r2 = reasonOf(() => decodeToCodePoints(bytes));
      if (r1 !== reason) failures.push(`${name}: decode gave ${r1}, expected ${reason}`);
      if (r2 !== reason) failures.push(`${name}: decodeToCodePoints gave ${r2}, expected ${reason}`);

      if (index % streamingEvery === 0) {
        const decoder = new StenoDecoder();
        const r3 = reasonOf(() => {
          for (let i = 0; i < bytes.length; i++) decoder.decode(bytes.subarray(i, i + 1), { stream: true });
          decoder.decode();
        });
        if (r3 !== reason) failures.push(`${name}: the streaming decoder gave ${r3}, expected ${reason}`);
      }
    } else if (kind === 'not-a-value') {
      counts.notAValue++;
      const cps = parseCodePoints(f[2]!);
      assert.throws(() => encodeCodePoints(cps), RangeError, name);
    } else {
      failures.push(`unknown kind of vector: ${line}`);
    }

    if (failures.length > 20) break;
  }

  assert.deepEqual(failures, []);
  return counts;
}

test('the published test vectors', () => {
  const counts = runVectors(new URL('../../../TestVectors/steno-v1-vectors.txt', import.meta.url), 1);

  // The header of the file: 6798 valid, 829 invalid and 13 not-a-value vectors.
  assert.deepEqual(counts, { valid: 6798, invalid: 829, notAValue: 13 });
  console.log(`published vectors: ${counts.valid} valid, ${counts.invalid} invalid, ${counts.notAValue} not-a-value`);
});

const large = process.env['STENO_LARGE_VECTORS'];

test('the large test vector file (STENO_LARGE_VECTORS)', { skip: large === undefined || large === '' ? 'STENO_LARGE_VECTORS is not set' : false }, () => {
  const counts = runVectors(large!, 50);
  const total = counts.valid + counts.invalid + counts.notAValue;
  console.log(`large vectors: ${total} (${counts.valid} valid, ${counts.invalid} invalid, ${counts.notAValue} not-a-value)`);
  assert.ok(total > 0);
});
