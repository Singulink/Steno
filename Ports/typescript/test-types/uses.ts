// Type-level checks of the declarations, compiled by `npm run test:types` under strict settings, once with the DOM library and once with
// @types/node only. Nothing here runs. The README examples are compiled next to it, from generated/.

import {
  StenoDecodeError,
  StenoDecoder,
  StenoDecoderStream,
  StenoEncoder,
  StenoEncoderStream,
  decode,
  decodeToCodePoints,
  encode,
  encodeCodePoints,
  encodeInto,
  type StenoBytes,
  type StenoDecodeErrorReason,
} from '@singulink/steno';

// The results are backed by a plain ArrayBuffer, so they go straight into the web platform's APIs under strict settings.
const bytes = encode('Москва');
const plain: Uint8Array<ArrayBuffer> = bytes;
export const blob = new Blob([plain, encodeCodePoints([0x61]), new StenoEncoder().encode('x')]);
export const digest = crypto.subtle.digest('SHA-256', encode('x'));
export const request = new Request('https://example.invalid/', { method: 'POST', body: encode('x') });

// Every BufferSource decodes.
const inputs: StenoBytes[] = [bytes, bytes.buffer, new DataView(bytes.buffer), new Int16Array(0), new SharedArrayBuffer(0)];
for (const input of inputs) decode(input) + new StenoDecoder().decode(input, { stream: true }) + decodeToCodePoints(input).length;

// The streams.
export const text: ReadableStream<string> = new ReadableStream<ArrayBuffer>().pipeThrough(new StenoDecoderStream());
export const encoded: ReadableStream<Uint8Array<ArrayBuffer>> = new ReadableStream<string>().pipeThrough(new StenoEncoderStream());

// The errors.
try {
  decode(bytes);
} catch (e) {
  if (e instanceof StenoDecodeError) {
    const reason: StenoDecodeErrorReason = e.reason;
    const offset: number = e.offset;
    void [reason, offset];
  }
}

export const written: number = encodeInto('x', new Uint8Array(3)).written;

// What the declarations reject.
// @ts-expect-error: encode takes a string.
encode(123);
// @ts-expect-error: decode takes bytes.
decode('abc');
// @ts-expect-error: the destination of encodeInto is a Uint8Array.
encodeInto('x', new Int8Array(3));
// @ts-expect-error: encodeCodePoints takes numbers.
encodeCodePoints(['a']);
// @ts-expect-error: not a reason.
export const bad: StenoDecodeErrorReason = 'zero';
