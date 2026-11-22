// The streaming encoder and decoder, and the TransformStreams: any split gives the result of the one-shot functions.

import assert from 'node:assert/strict';
import { test } from 'node:test';

import {
  StenoDecodeError,
  StenoDecoder,
  StenoDecoderStream,
  StenoEncoder,
  StenoEncoderStream,
  decode,
  encode,
} from '../dist/index.js';
import { Random, concat, hex, randomCodePoints, toStringFromCodePoints } from './helpers.js';

function streamOf<T>(items: readonly T[]): ReadableStream<T> {
  return new ReadableStream<T>({
    start(controller): void {
      for (const item of items) controller.enqueue(item);
      controller.close();
    },
  });
}

function splitPoints(rng: Random, length: number): number[] {
  const points: number[] = [];
  let i = 0;

  while (i < length) {
    i += rng.int(0, 5);
    points.push(Math.min(i, length));
  }

  return points;
}

function encodeInPieces(encoder: StenoEncoder, text: string, points: readonly number[]): Uint8Array {
  const parts: Uint8Array[] = [];
  let at = 0;

  for (const p of points) {
    parts.push(encoder.encode(text.slice(at, p), { stream: true }));
    at = p;
  }

  parts.push(encoder.encode(text.slice(at)));
  return concat(parts);
}

function decodeInPieces(decoder: StenoDecoder, bytes: Uint8Array, points: readonly number[]): string {
  let s = '';
  let at = 0;

  for (const p of points) {
    s += decoder.decode(bytes.subarray(at, p), { stream: true });
    at = p;
  }

  return s + decoder.decode(bytes.subarray(at));
}

test('random splits give the one-shot bytes and text', () => {
  const rng = new Random(10);
  const encoder = new StenoEncoder();
  const decoder = new StenoDecoder();

  for (let n = 0; n < 1000; n++) {
    const text = toStringFromCodePoints(randomCodePoints(rng, 40));
    const bytes = encode(text);

    // One encoder and one decoder for all values: a call without { stream: true } ends the value and the next call starts a new one.
    assert.equal(hex(encodeInPieces(encoder, text, splitPoints(rng, text.length))), hex(bytes));
    assert.equal(decodeInPieces(decoder, bytes, splitPoints(rng, bytes.length)), text);
  }
});

test('one code unit and one byte per call', () => {
  const rng = new Random(11);

  for (let n = 0; n < 300; n++) {
    const text = toStringFromCodePoints(randomCodePoints(rng, 30));
    const bytes = encode(text);
    const units = [...Array(text.length).keys()].map((i) => i + 1);
    const singles = [...Array(bytes.length).keys()].map((i) => i + 1);

    assert.equal(hex(encodeInPieces(new StenoEncoder(), text, units)), hex(bytes));
    assert.equal(decodeInPieces(new StenoDecoder(), bytes, singles), text);
  }
});

test('a split inside a surrogate pair and inside the bytes of a code point', () => {
  const encoder = new StenoEncoder();
  const a = encoder.encode('a\ud83d', { stream: true });
  const b = encoder.encode('\ude00b');
  assert.equal(hex(concat([a, b])), hex(encode('a😀b')));

  // A high surrogate at the end of the value is an unpaired surrogate; a non-stream call ends the value.
  const c = encoder.encode('x\ud83d');
  assert.equal(hex(c), hex(encode('x\ud83d')));
  assert.equal(hex(encoder.encode('\ude00')), hex(encode('\ude00')));

  // An empty streaming call keeps holding the high surrogate.
  const d1 = encoder.encode('\ud83d', { stream: true });
  const d2 = encoder.encode('', { stream: true });
  const d3 = encoder.encode('\ude00');
  assert.equal(hex(concat([d1, d2, d3])), hex(encode('😀')));

  const bytes = encode('😀漢字');
  const decoder = new StenoDecoder();
  assert.equal(decoder.decode(bytes.subarray(0, 1), { stream: true }), '');
  assert.equal(decoder.decode(bytes.subarray(1, 2), { stream: true }), '');
  assert.equal(decoder.decode(bytes.subarray(2), { stream: true }), '😀漢字');
  assert.equal(decoder.decode(), '');
});

test('finishing early is the truncated error, and a call that throws leaves the decoder as it was', () => {
  const bytes = encode('Ж漢');
  const decoder = new StenoDecoder();
  const first = decoder.decode(bytes.subarray(0, bytes.length - 1), { stream: true });

  assert.throws(
    () => decoder.decode(),
    (e: unknown) => e instanceof StenoDecodeError && e.reason === 'truncated' && e.offset === 2,
  );

  // The decoder still holds the unfinished code point, so the last byte completes it.
  assert.equal(first + decoder.decode(bytes.subarray(bytes.length - 1)), 'Ж漢');

  // An error inside the bytes of a later piece reports the offset from the start of the value.
  const bad = concat([encode('abc'), new Uint8Array([0xff])]);
  decoder.decode(bad.subarray(0, 2), { stream: true });
  assert.throws(
    () => decoder.decode(bad.subarray(2), { stream: true }),
    (e: unknown) => e instanceof StenoDecodeError && e.reason === 'unused-lead' && e.offset === 3,
  );
});

test('the surrogate-pair rule holds across calls', () => {
  // U+D800 then U+DC00 as two code points is not a value; build its bytes by hand from the two lone-surrogate encodings in the initial layout.
  const high = encode('\ud800');
  const low = encode('\udc00');
  const bytes = concat([high, low]);
  assert.throws(() => decode(bytes), (e: unknown) => e instanceof StenoDecodeError && e.reason === 'surrogate-pair' && e.offset === 3);

  const decoder = new StenoDecoder();
  assert.equal(decoder.decode(high, { stream: true }), '\ud800');
  assert.throws(() => decoder.decode(low), (e: unknown) => e instanceof StenoDecodeError && e.reason === 'surrogate-pair' && e.offset === 3);
});

test('reset starts a new value', () => {
  const encoder = new StenoEncoder();
  encoder.encode('Москва\ud83d', { stream: true });
  encoder.reset();
  assert.equal(hex(encoder.encode('Москва')), hex(encode('Москва')));

  const decoder = new StenoDecoder();
  const bytes = encode('Москва');
  decoder.decode(bytes.subarray(0, 1), { stream: true });
  decoder.reset();
  assert.equal(decoder.decode(bytes), 'Москва');
});

test('the TransformStreams give the one-shot bytes and text', async () => {
  const rng = new Random(12);
  const text = toStringFromCodePoints(randomCodePoints(rng, 400));
  const units = splitPoints(rng, text.length);
  const pieces: string[] = [];
  let at = 0;

  for (const p of units) {
    pieces.push(text.slice(at, p));
    at = p;
  }

  pieces.push(text.slice(at));

  const encoded = new Uint8Array(await new Response(streamOf(pieces).pipeThrough(new StenoEncoderStream())).arrayBuffer());
  assert.equal(hex(encoded), hex(encode(text)));

  const chunks: Uint8Array[] = [];
  at = 0;

  for (const p of splitPoints(rng, encoded.length)) {
    chunks.push(encoded.subarray(at, p));
    at = p;
  }

  chunks.push(encoded.subarray(at));

  let decoded = '';
  for await (const s of streamOf(chunks).pipeThrough(new StenoDecoderStream())) decoded += s;
  assert.equal(decoded, text);

  // Bytes that end in the middle of a code point error the stream.
  const cutBytes = encode('Ж漢');
  const cut = streamOf([cutBytes.subarray(0, cutBytes.length - 1)]).pipeThrough(new StenoDecoderStream());
  await assert.rejects(async () => {
    for await (const s of cut) void s;
  }, StenoDecodeError);
});
