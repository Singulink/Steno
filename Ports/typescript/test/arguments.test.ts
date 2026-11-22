// The arguments of the public functions, as untyped JavaScript code passes them: never a silent wrong result. Most of these cases come from an
// adversarial review of this implementation.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { setFlagsFromString } from 'node:v8';
import { runInNewContext } from 'node:vm';

import * as S from '../dist/index.js';

// Calls that a JavaScript caller (or a TypeScript caller through `any`) can make.
const loose = S as unknown as Record<string, (...args: unknown[]) => unknown>;
const hex = (b: Uint8Array): string => Buffer.from(b.buffer, b.byteOffset, b.length).toString('hex');
const reasonOf = (f: () => unknown): string => {
  try {
    f();
    return 'accepted';
  } catch (e) {
    if (e instanceof S.StenoDecodeError) return `${e.reason}@${e.offset}`;
    if (e instanceof TypeError) return 'TypeError';
    if (e instanceof RangeError) return 'RangeError';
    throw e;
  }
};

test('the decoding functions take any BufferSource, read as its bytes', () => {
  const text = 'Hello Москва';
  const bytes = S.encode(text);
  const cps = S.decodeToCodePoints(bytes);
  const padded = new Uint8Array(bytes.length + 6);
  padded.set(bytes, 3);

  const shared = new Uint8Array(new SharedArrayBuffer(bytes.length));
  shared.set(bytes);

  const inputs: unknown[] = [
    shared,
    shared.buffer,
    bytes.slice().buffer,
    new DataView(padded.buffer, 3, bytes.length),
    new Int8Array(padded.buffer, 3, bytes.length),
    Buffer.from(bytes),
    runInNewContext('new Uint8Array(b)', { b: [...bytes] }),
  ];

  for (const input of inputs) {
    assert.equal(loose['decode']!(input), text);
    assert.deepEqual(loose['decodeToCodePoints']!(input), cps);
    assert.equal((new S.StenoDecoder() as unknown as { decode(x: unknown): string }).decode(input), text);
  }

  // A typed array of another element size is read as its bytes, in the platform's byte order: [0x2f, -1] is 2F FF (unused-lead), and a
  // Uint16Array [0x2f, 0x1ff] is 2F 00 FF 01 on a little-endian platform (zero-byte at 1).
  assert.equal(reasonOf(() => loose['decode']!(new Int8Array([0x2f, -1]))), 'unused-lead@1');
  if (new Uint8Array(new Uint16Array([1]).buffer)[0] === 1) assert.equal(reasonOf(() => loose['decode']!(new Uint16Array([0x2f, 0x1ff]))), 'zero-byte@1');
});

test('the decoding functions throw a TypeError for anything that is not bytes', () => {
  for (const input of ['abc', 123, null, {}, [0x2f], { length: 1, 0: 0x2f }, new Proxy(new Uint8Array(1), {})]) {
    assert.equal(reasonOf(() => loose['decode']!(input)), 'TypeError');
    assert.equal(reasonOf(() => loose['decodeToCodePoints']!(input)), 'TypeError');
    if (input !== undefined) assert.equal(reasonOf(() => (new S.StenoDecoder() as unknown as { decode(x: unknown): string }).decode(input)), 'TypeError');
  }

  assert.equal(reasonOf(() => loose['decode']!(undefined)), 'TypeError');
});

test('StenoDecoderStream takes ArrayBuffer chunks and errors on chunks that are not bytes', async () => {
  const bytes = S.encode('Hello Москва');
  const read = async (chunks: unknown[]): Promise<string> => {
    const source = new ReadableStream<unknown>({
      start(c): void {
        for (const chunk of chunks) c.enqueue(chunk);
        c.close();
      },
    });

    let text = '';
    for await (const s of (source as ReadableStream<Uint8Array>).pipeThrough(new S.StenoDecoderStream())) text += s;
    return text;
  };

  assert.equal(await read([bytes.slice(0, 5).buffer, new DataView(bytes.slice(5).buffer)]), 'Hello Москва');
  await assert.rejects(read(['Hello']), TypeError);
});

test('the encoding functions take only strings', () => {
  for (const input of [123, {}, null, undefined, [1], new String('a')]) {
    assert.equal(reasonOf(() => loose['encode']!(input)), 'TypeError');
    assert.equal(reasonOf(() => loose['encodeInto']!(input, new Uint8Array(10))), 'TypeError');
    if (input !== undefined) assert.equal(reasonOf(() => (new S.StenoEncoder() as unknown as { encode(x: unknown): Uint8Array }).encode(input)), 'TypeError');
  }

  // StenoEncoder.encode() without an argument is the empty string, as TextEncoder.encode() is.
  assert.equal(new S.StenoEncoder().encode().length, 0);
});

test('StenoEncoderStream errors on chunks that are not strings', async () => {
  const source = new ReadableStream<unknown>({
    start(c): void {
      c.enqueue(123);
      c.close();
    },
  });

  await assert.rejects(new Response((source as ReadableStream<string>).pipeThrough(new S.StenoEncoderStream())).arrayBuffer(), TypeError);
});

test('encodeInto takes only a Uint8Array (or a subclass) as the destination', () => {
  for (const destination of [[], new Int8Array(10), new Uint8ClampedArray(10), new ArrayBuffer(10), new DataView(new ArrayBuffer(10)), null, 'x']) {
    assert.equal(reasonOf(() => loose['encodeInto']!('Hello', destination)), 'TypeError');
  }

  const buffer = Buffer.alloc(10);
  assert.deepEqual(S.encodeInto('Hello', buffer), { read: 5, written: 5 });
  assert.equal(hex(buffer.subarray(0, 5)), hex(S.encode('Hello')));
});

test('encodeCodePoints takes an iterable of numbers', () => {
  assert.equal(reasonOf(() => loose['encodeCodePoints']!(5)), 'TypeError');
  assert.equal(reasonOf(() => loose['encodeCodePoints']!({ length: 0 })), 'TypeError');
  assert.equal(reasonOf(() => loose['encodeCodePoints']!(null)), 'TypeError');
  assert.equal(reasonOf(() => loose['encodeCodePoints']!(['a'])), 'TypeError');
  assert.equal(reasonOf(() => loose['encodeCodePoints']!([0x110000])), 'RangeError');
  assert.equal(hex(S.encodeCodePoints(new Uint32Array([0x48, 0x69]))), hex(S.encode('Hi')));
  assert.equal(hex(S.encodeCodePoints((function* () { yield 0x48; yield 0x69; })())), hex(S.encode('Hi')));
  assert.equal(reasonOf(() => loose['encodeCodePoints']!('Hi')), 'TypeError'); // a string iterates as strings, not numbers
});

test('any truthy stream option streams, as with TextDecoder', () => {
  const bytes = S.encode('漢');
  const d = new S.StenoDecoder();
  const options = { stream: 1 } as unknown as { stream: boolean };
  assert.equal(d.decode(bytes.subarray(0, 2), options) + d.decode(bytes.subarray(2)), '漢');

  const e = new S.StenoEncoder();
  assert.equal(hex(Buffer.concat([e.encode('\ud83d', { stream: 'yes' } as unknown as { stream: boolean }), e.encode('\ude00')])), hex(S.encode('😀')));
});

test('a Uint8Array subclass whose species re-enters decode does not disturb StenoDecoder', () => {
  class Evil extends Uint8Array {
    static get [Symbol.species](): Uint8ArrayConstructor {
      S.decode(S.encode('Z'.repeat(20)));
      return Uint8Array;
    }
  }

  S.decode(S.encode('ж'.repeat(600)));

  const text = 'Москва漢';
  const full = S.encode(text);
  const cut = full.length - 1; // inside 漢, so the decoder holds bytes
  const ev = new Evil(cut);
  ev.set(full.subarray(0, cut));
  const d = new S.StenoDecoder();
  const a = d.decode(ev, { stream: true });
  assert.equal(a + d.decode(full.subarray(cut)), text);
});

test('a destination whose set() re-enters encode does not disturb encodeInto', () => {
  let calls = 0;

  class Hooked extends Uint8Array {
    override set(src: ArrayLike<number>, offset?: number): void {
      calls++;
      S.encode('漢字');
      super.set(src, offset);
    }
  }

  const text = 'Ж' + 'a'.repeat(100) + 'éé';
  const dst = new Hooked(1000);
  const { written } = S.encodeInto(text, dst);
  assert.equal(hex(dst.subarray(0, written)), hex(S.encode(text)));
  assert.equal(calls, 0, 'the codec never calls methods of the destination');
});

test('an iterable that re-enters the functions does not disturb encodeCodePoints, and nested calls get their own state', () => {
  const inner = (function* () {
    yield 0x416;
    assert.equal(S.decode(S.encode('漢字 Москва')), '漢字 Москва');
    yield 0x4e00;
    yield 0x61;
  })();

  assert.equal(hex(S.encodeCodePoints(inner)), hex(S.encode('Ж一a')));
});

test('one large decode keeps no scratch buffer of its size alive', () => {
  setFlagsFromString('--expose-gc');
  const gc = runInNewContext('gc') as () => void;
  const used = (): number => {
    gc();
    gc();
    return process.memoryUsage().arrayBuffers;
  };

  const before = used();
  // In functions of their own, so that no frame of the test keeps the inputs alive.
  const once = (): number => S.decode(S.encode('ж' + 'a'.repeat(4_000_000))).length;
  const ascii = (): number => S.decode(S.encode('a'.repeat(3_000_000))).length;
  assert.equal(once(), 4_000_001);
  assert.equal(ascii(), 3_000_000);

  const kept = used() - before;
  assert.ok(kept < 1_000_000, `${kept} bytes of array buffers are still alive after the call`);
});

test('the maximum-length helpers reject what they cannot answer exactly', () => {
  assert.equal(Object.is(S.maxEncodedLength(-0), 0), true);
  assert.equal(Object.is(S.maxDecodedLength(-0), 0), true);

  for (const f of [S.maxEncodedLength, S.maxDecodedLength]) {
    for (const bad of [-1, 1.5, Number.NaN, Infinity, Number.MAX_SAFE_INTEGER, '3' as unknown as number]) {
      assert.throws(() => f(bad), RangeError);
    }
  }

  assert.equal(S.maxEncodedLength(Math.floor(Number.MAX_SAFE_INTEGER / 3)), Math.floor(Number.MAX_SAFE_INTEGER / 3) * 3);
});

test('the helpers on bytes take any BufferSource and throw a TypeError for anything else', () => {
  const a = S.encode('Zug');
  const b = S.encode('Zürich');

  assert.equal(S.compareBytes(a.slice().buffer, new DataView(b.slice().buffer)), -1);
  assert.equal(S.startsWith(Buffer.from(b), b.subarray(0, 2).slice().buffer), true);
  assert.equal(S.isAsciiText(a.slice().buffer), true);

  for (const bad of ['Zug', 1, null, [1, 2]]) {
    assert.equal(reasonOf(() => loose['compareBytes']!(bad, a)), 'TypeError');
    assert.equal(reasonOf(() => loose['startsWith']!(a, bad)), 'TypeError');
    assert.equal(reasonOf(() => loose['isAsciiText']!(bad)), 'TypeError');
  }

  assert.equal(reasonOf(() => loose['compareCodePoints']!('a', 1)), 'TypeError');
});
