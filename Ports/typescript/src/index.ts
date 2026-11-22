/**
 * The TypeScript reference implementation of the Steno Encoding, version 1: a compact encoding of Unicode text whose bytes sort like the text.
 *
 * @remarks
 * The format is specified in {@link https://github.com/Singulink/Singulink.Steno/blob/main/FORMAT.md | FORMAT.md}. Every function of this module
 * encodes or decodes its input as one whole value from the initial state.
 *
 * @packageDocumentation
 */

export { compareBytes, compareCodePoints, isAsciiText, startsWith } from './compare.js';
export { StenoDecodeError, type StenoDecodeErrorReason } from './errors.js';
export type { StenoBytes } from './input.js';
export {
  STENO_ENCODING_NAME,
  decode,
  decodeToCodePoints,
  encode,
  encodeCodePoints,
  encodeInto,
  maxDecodedLength,
  maxEncodedLength,
} from './steno.js';
export {
  StenoDecoder,
  StenoDecoderStream,
  StenoEncoder,
  StenoEncoderStream,
  type StenoDecodeOptions,
  type StenoEncodeOptions,
} from './streaming.js';
