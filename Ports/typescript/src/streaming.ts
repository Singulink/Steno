import { DecoderState, EncoderState, decodePiece, encodeUnits, unitsToString } from './codec.js';
import { type StenoBytes, checkText, streamOption, toBytes } from './input.js';
import { STENO_ENCODING_NAME, acquireShared, releaseShared, unitsFor } from './steno.js';

/** The options of {@link StenoEncoder.encode}. */
export interface StenoEncodeOptions {
  /** True if more of the same value follows in a later call. False or missing if this call ends the value. Any truthy value counts as true. */
  stream?: boolean;
}

/** The options of {@link StenoDecoder.decode}. */
export interface StenoDecodeOptions {
  /**
   * True if more bytes of the same value follow in a later call. False or missing if this call ends the value. Any truthy value counts as true,
   * as with `TextDecoder`.
   */
  stream?: boolean;
}

const EMPTY = new Uint8Array(0);

/**
 * Encodes one value given in pieces, in the manner of `TextDecoder` with `{ stream: true }`.
 *
 * @remarks
 * Over any split of a string, the pieces give the bytes that {@link encode} gives for the whole string, also when a split falls between the two
 * halves of a surrogate pair: a high surrogate at the end of a piece is held until the next call. A call without `{ stream: true }` ends the
 * value: a held high surrogate is written as an unpaired surrogate, and the next call starts a new value, as with `TextDecoder`. {@link reset}
 * also starts a new value.
 *
 * Not to be shared: an encoder holds the state of one value.
 *
 * @example
 * ```js
 * const encoder = new StenoEncoder();
 * const a = encoder.encode('Моск', { stream: true });
 * const b = encoder.encode('ва');
 * console.log([...a, ...b].join() === encode('Москва').join()); // true
 * ```
 */
export class StenoEncoder {
  /** The name of the encoding, `x-steno`. */
  readonly encoding: string = STENO_ENCODING_NAME;

  readonly #state = new EncoderState();

  /**
   * Encodes the next piece of the value.
   *
   * @param input - The next piece of the string. Missing means the empty string.
   * @param options - `{ stream: true }` if more of the value follows.
   * @returns The bytes of this piece, in a new array. They can be fewer than the piece needs (a held high surrogate is written by the next call).
   * @throws TypeError if `input` is not a string (no conversion with `String()`, unlike `TextEncoder`).
   */
  encode(input = '', options?: StenoEncodeOptions): Uint8Array<ArrayBuffer> {
    checkText(input, 'input');
    const stream = streamOption(options);
    const st = this.#state;
    const out = new Uint8Array((input.length + 1) * 3);
    const n = encodeUnits(st, input, out, 0, !stream);

    if (!stream) st.reset();
    return out.slice(0, n);
  }

  /** Starts a new value: forgets the state and any held high surrogate. */
  reset(): void {
    this.#state.reset();
  }
}

/**
 * Decodes one value given in pieces, in the manner of `TextDecoder` with `{ stream: true }`.
 *
 * @remarks
 * Over any split of the bytes, the pieces give the string that {@link decode} gives for the whole value, also when a split falls inside the bytes
 * of a code point: they are held until the next call completes them. A call without `{ stream: true }` ends the value: held bytes of an
 * unfinished code point are then the `truncated` error, and the next call starts a new value, as with `TextDecoder`. {@link reset} also starts a
 * new value.
 *
 * Invalid bytes throw a {@link StenoDecodeError} whose offset counts from the first byte of the value. A call that throws leaves the decoder as it
 * was before the call. Not to be shared: a decoder holds the state of one value.
 *
 * @example
 * ```js
 * const bytes = encode('Москва');
 * const decoder = new StenoDecoder();
 * let text = decoder.decode(bytes.subarray(0, 2), { stream: true });
 * text += decoder.decode(bytes.subarray(2));
 * console.log(text); // Москва
 * ```
 */
export class StenoDecoder {
  /** The name of the encoding, `x-steno`. */
  readonly encoding: string = STENO_ENCODING_NAME;

  readonly #state = new DecoderState();
  readonly #backup = new DecoderState();

  /**
   * Decodes the next piece of the value.
   *
   * @param input - The next piece of the bytes: a `Uint8Array`, or any other `ArrayBuffer`, `SharedArrayBuffer`, typed array or `DataView`, read
   *   as its bytes, as `TextDecoder` takes them. Missing means no bytes.
   * @param options - `{ stream: true }` if more bytes of the value follow.
   * @returns The text of the code points that this piece completes.
   * @throws TypeError if `input` is none of those.
   * @throws {@link StenoDecodeError} if the bytes are not the Steno bytes of a value, or if a call without `{ stream: true }` ends in the middle
   *   of a code point (`truncated`).
   */
  decode(input?: StenoBytes, options?: StenoDecodeOptions): string {
    const bytes = input === undefined ? EMPTY : toBytes(input, 'input');
    const stream = streamOption(options);
    const st = this.#state;
    this.#backup.copyDecoderFrom(st);
    const shared = acquireShared();

    try {
      // The bytes held from the call before complete a code point only with at least one byte of this call, so 2 units per byte are enough.
      const out = unitsFor(bytes.length * 2, shared);
      st.lone = false;
      // Long input: long runs of ASCII text are decoded by the platform's decoder, into separate pieces.
      const pieces = bytes.length >= 128 ? [] : null;
      const o = decodePiece(st, bytes, out, !stream, pieces);
      const tail = unitsToString(out, o, !st.lone);
      const text = pieces === null || pieces.length === 0 ? tail : pieces.join('') + tail;

      if (!stream) st.reset();
      return text;
    } catch (e) {
      st.copyDecoderFrom(this.#backup);
      throw e;
    } finally {
      releaseShared(shared);
    }
  }

  /** Starts a new value: forgets the state and any held bytes. */
  reset(): void {
    this.#state.reset();
  }
}

/**
 * A `TransformStream` that encodes the strings written to it as one value, like `TextEncoderStream`.
 *
 * @remarks
 * All the chunks of the stream are one value. The bytes are the bytes of {@link encode} for the strings joined, whatever the chunks are. A high
 * surrogate at the end of a chunk waits for the next chunk; at the end of the stream it is written as an unpaired surrogate. A chunk that is not a
 * string errors the stream with a `TypeError` (`TextEncoderStream` would convert it with `String()`).
 *
 * @example
 * ```js
 * const stream = new Blob(['Моск', 'ва']).stream().pipeThrough(new TextDecoderStream()).pipeThrough(new StenoEncoderStream());
 * const bytes = new Uint8Array(await new Response(stream).arrayBuffer());
 * console.log(bytes.length); // 7
 * ```
 */
export class StenoEncoderStream extends TransformStream<string, Uint8Array<ArrayBuffer>> {
  /** The name of the encoding, `x-steno`. */
  readonly encoding: string = STENO_ENCODING_NAME;

  /** Creates the stream. */
  constructor() {
    const encoder = new StenoEncoder();

    super({
      transform(chunk, controller): void {
        const bytes = encoder.encode(chunk, { stream: true });
        if (bytes.length > 0) controller.enqueue(bytes);
      },
      flush(controller): void {
        const bytes = encoder.encode();
        if (bytes.length > 0) controller.enqueue(bytes);
      },
    });
  }
}

/**
 * A `TransformStream` that decodes the bytes written to it as one value, like `TextDecoderStream` with `fatal: true`.
 *
 * @remarks
 * All the chunks of the stream are one value. The strings joined are the string of {@link decode} for the bytes joined, whatever the chunks are.
 * A chunk can be a `Uint8Array`, or any other `ArrayBuffer`, `SharedArrayBuffer`, typed array or `DataView`, read as its bytes, as
 * `TextDecoderStream` takes them; anything else errors the stream with a `TypeError`. Invalid bytes, or bytes that end in the middle of a code
 * point, error the stream with a {@link StenoDecodeError}.
 *
 * @example
 * ```js
 * const stream = new Blob([encode('Москва')]).stream().pipeThrough(new StenoDecoderStream());
 * console.log(await new Response(stream.pipeThrough(new TextEncoderStream())).text()); // Москва
 * ```
 */
export class StenoDecoderStream extends TransformStream<StenoBytes, string> {
  /** The name of the encoding, `x-steno`. */
  readonly encoding: string = STENO_ENCODING_NAME;

  /** Creates the stream. */
  constructor() {
    const decoder = new StenoDecoder();

    super({
      transform(chunk, controller): void {
        const text = decoder.decode(chunk, { stream: true });
        if (text.length > 0) controller.enqueue(text);
      },
      flush(controller): void {
        const text = decoder.decode();
        if (text.length > 0) controller.enqueue(text);
      },
    });
  }
}
