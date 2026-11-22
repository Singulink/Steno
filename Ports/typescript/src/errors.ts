/**
 * The rule of the specification (FORMAT.md section 10.1) that bytes broke when they were rejected.
 *
 * - `zero-byte`: a byte 00, where a lead byte or a trail byte is expected.
 * - `unused-lead`: a lead byte that the layout of the current state does not use.
 * - `truncated`: the bytes end in the middle of the encoding of a code point.
 * - `past-range`: a code point beyond the last code point of its range (this includes every number above U+10FFFF).
 * - `surrogate-pair`: a low surrogate code point directly after a high surrogate code point.
 * - `invalid-trail`: a byte 01..07 where a trail byte is expected (trail bytes are 08..FF).
 *
 * For each code point the checks come in the order of FORMAT.md section 9.2: the lead byte (`zero-byte`, `unused-lead`), then each trail byte as
 * it is read (`truncated` before it, then `zero-byte`, then `invalid-trail`), then `past-range`, and last `surrogate-pair`.
 */
export type StenoDecodeErrorReason = 'zero-byte' | 'unused-lead' | 'truncated' | 'past-range' | 'surrogate-pair' | 'invalid-trail';

/**
 * The error that decoding throws for bytes that are not the Steno bytes of a value.
 *
 * @remarks
 * A program can test {@link StenoDecodeError.reason}. The message is meant for people, and its wording is not part of the contract. Decoding
 * gives no partial result: the state after invalid data is unknown, so nothing can replace the bad bytes and go on.
 *
 * @example
 * ```js
 * try {
 *   decode(new Uint8Array([0x2f, 0x00]));
 * } catch (e) {
 *   if (e instanceof StenoDecodeError) console.log(e.reason, e.offset); // zero-byte 1
 * }
 * ```
 */
export class StenoDecodeError extends Error {
  /** The rule of the specification that the bytes broke. */
  readonly reason: StenoDecodeErrorReason;

  /**
   * The position in the bytes of the value where the code point that broke the rule starts (its lead byte). For a streaming decoder it counts
   * from the first byte of the value, over all calls.
   */
  readonly offset: number;

  /**
   * Creates the error.
   *
   * @param reason - The rule of the specification that the bytes broke.
   * @param offset - Where the code point that broke the rule starts.
   */
  constructor(reason: StenoDecodeErrorReason, offset: number) {
    super(`The bytes are not the Steno bytes of a value (${reason}) at the code point that starts at byte ${offset}.`);
    this.name = 'StenoDecodeError';
    this.reason = reason;
    this.offset = offset;
  }
}
