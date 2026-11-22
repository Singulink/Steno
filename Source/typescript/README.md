# TypeScript Reference Implementation of the Steno Encoding

This package, `@singulink/steno`, is the TypeScript reference implementation of the Steno Encoding. It is one ES module with type declarations
and no runtime dependencies. It uses only standard JavaScript and `Uint8Array`, so it runs unchanged in Node.js 20 and later, Deno, Bun and
browsers.

The Steno Encoding is a compact encoding of Unicode text whose bytes sort like the text. It was designed to be the ideal per-value compression
encoding for databases and key-value stores, which can sort, compare, range-scan and prefix-match Steno bytes as plain bytes, without decoding
them. The byte 00 never occurs, and no byte below 08 occurs inside a code point, so line feeds, tabs and spaces can be found at the byte level.

- [Specification](https://github.com/Singulink/Steno/blob/main/FORMAT.md) (Steno Encoding, version 1)
- [Main README](https://github.com/Singulink/Steno#readme), with the sizes and the comparisons with other encodings
- [This implementation on the documentation site](https://www.singulink.com/Docs/Steno/articles/implementations/typescript.html)
- [Guide to storing values](https://www.singulink.com/Docs/Steno/articles/start/storing-values.html)

## Install

> Not published yet: the package below is the planned name.

```sh
npm install @singulink/steno
```

For TypeScript, the declarations need TypeScript 5.7 or later (they declare results as `Uint8Array<ArrayBuffer>`, so that they go straight into
`Blob`, `fetch` and Web Crypto under strict settings), and the `DOM` library or `@types/node`, which declare `TransformStream` and the other web
stream types that the streaming classes use.

## Usage

### Encode and Decode

```js
import { decode, encode } from '@singulink/steno';

const bytes = encode('Москва');
console.log(bytes.length); // 7
console.log(new TextEncoder().encode('Москва').length); // 12
console.log(decode(bytes)); // Москва
```

`encode` and `decode` work on one whole value. To write into your own buffer, use `encodeInto`. A destination of
`maxEncodedLength(text.length)` bytes is always large enough, and `maxDecodedLength(bytes.length)` is the most UTF-16 code units that the bytes
can decode to.

```js
import { encodeInto, maxDecodedLength, maxEncodedLength } from '@singulink/steno';

const text = 'Zürich';
const buffer = new Uint8Array(maxEncodedLength(text.length));
const { read, written } = encodeInto(text, buffer);
console.log(read, written); // 6 6
console.log(maxEncodedLength(6), maxDecodedLength(6)); // 18 12
```

Unlike `TextEncoder.encodeInto`, `encodeInto` throws a `RangeError` when the destination is too small, instead of writing part of the text. The
encoding is stateful: the bytes of the rest of the text depend on the text before it, so a second call for the rest would write wrong bytes
without any error. When it throws, the destination may hold some of the bytes.

### What the Functions Accept

A JavaScript library is often called from untyped code, so the functions check their arguments and throw a `TypeError` for a wrong one, instead
of returning an empty or wrong result:

- **Bytes** (`decode`, `decodeToCodePoints`, `StenoDecoder.decode`, the chunks of `StenoDecoderStream`): a `Uint8Array`, or any other
  `ArrayBuffer`, `SharedArrayBuffer`, typed array or `DataView`, read as the bytes of its range of the buffer, as `TextDecoder` takes them. An
  `Int16Array` is read as its bytes, not as its elements.
- **Text** (`encode`, `encodeInto`, `StenoEncoder.encode`, the chunks of `StenoEncoderStream`): a string. Unlike `TextEncoder`, the encoders do not
  convert other values with `String()`: a number or an object that reaches an encoder is far more often a bug than an intention.
- **Code points** (`encodeCodePoints`): an iterable of numbers. A number that is not a code point is a `RangeError`.
- **A destination** (`encodeInto`): a `Uint8Array`, or a subclass of it such as Node's `Buffer`.
- **The `stream` option:** any truthy value means true, as with `TextDecoder`.

### Compare and Sort

```js
import { compareBytes, compareCodePoints, encode, startsWith } from '@singulink/steno';

console.log(compareBytes(encode('Zug'), encode('Zürich'))); // -1
console.log(compareCodePoints('Zug', 'Zürich')); // -1
console.log(startsWith(encode('Zürich'), encode('Zür'))); // true
```

`compareBytes` compares bytes as unsigned bytes, which JavaScript has no built-in function for. On Steno bytes it gives the code point order of
the texts. `compareCodePoints` gives the same order for strings in memory. Use it where strings must be in the order of their Steno bytes: the
`<` operator and the default `Array.prototype.sort` compare UTF-16 code units, which is a different order, and `localeCompare` is the order of a
language. Where two strings first differ, a supplementary character (such as an emoji) and a character in U+E000..U+FFFF are in the opposite
order:

```js
import { compareBytes, compareCodePoints, encode } from '@singulink/steno';

console.log('すごい！' < 'すごい😀'); // false
console.log(compareCodePoints('すごい！', 'すごい😀')); // -1
console.log(compareBytes(encode('すごい！'), encode('すごい😀'))); // -1
```

The bytes of a prefix of a text are a prefix of the bytes of the text, so `startsWith` on Steno bytes answers a prefix query. The prefix must end
on a whole code point: a prefix that ends between the two halves of a surrogate pair ends in an unpaired surrogate, which is another code point.

### ASCII Text

```js
import { encode, isAsciiText } from '@singulink/steno';

console.log(isAsciiText(encode('Hello, world!'))); // true
console.log(isAsciiText(encode('Zürich'))); // false
```

`isAsciiText` decides from the bytes alone, without decoding, whether a value has only tab, line feed, carriage return and U+0020..U+007E. The
bytes of such a value are not ASCII bytes: U+0020..U+007E are the bytes `07`..`65`.

### Code Points

```js
import { decodeToCodePoints, encode, encodeCodePoints } from '@singulink/steno';

console.log(decodeToCodePoints(encode('a😀'))); // [ 97, 128512 ]
console.log(decodeToCodePoints(encodeCodePoints([0x61, 0xd800, 0x62]))); // [ 97, 55296, 98 ]

try {
  encodeCodePoints([0xd800, 0xdc00]);
} catch (e) {
  console.log(e instanceof RangeError); // true
}
```

`encodeCodePoints` and `decodeToCodePoints` work on code points (integers from 0 to 0x10FFFF) instead of strings. `encodeCodePoints` throws a
`RangeError` for a sequence that is not a value: a high surrogate code point directly followed by a low one.

### Streaming

```js
import { StenoDecoder, StenoEncoder, encode } from '@singulink/steno';

const encoder = new StenoEncoder();
const a = encoder.encode('Моск', { stream: true });
const b = encoder.encode('ва');
console.log([...a, ...b].join() === encode('Москва').join()); // true

const bytes = encode('Москва');
const decoder = new StenoDecoder();
let text = decoder.decode(bytes.subarray(0, 2), { stream: true });
text += decoder.decode(bytes.subarray(2));
console.log(text); // Москва
```

`StenoEncoder` and `StenoDecoder` encode and decode one value given in pieces, in the manner of `TextDecoder` with `{ stream: true }`.
`StenoEncoderStream` and `StenoDecoderStream` are `TransformStream`s, like `TextEncoderStream` and `TextDecoderStream`:

```js
import { StenoDecoderStream, StenoEncoderStream } from '@singulink/steno';

const encoded = new Blob(['Моск', 'ва']).stream().pipeThrough(new TextDecoderStream()).pipeThrough(new StenoEncoderStream());
const bytes = new Uint8Array(await new Response(encoded).arrayBuffer());
console.log(bytes.length); // 7

const decoded = new Blob([bytes]).stream().pipeThrough(new StenoDecoderStream()).pipeThrough(new TextEncoderStream());
console.log(await new Response(decoded).text()); // Москва
```

What streaming guarantees:

- Over any split of the input, the pieces give the bytes or the text of the one-shot functions for the whole value. This includes a split between
  the two halves of a surrogate pair (the encoder holds the high surrogate) and a split inside the bytes of a code point (the decoder holds them).
- A call without `{ stream: true }` ends the value, as with `TextDecoder`: the encoder writes a held high surrogate as an unpaired surrogate, the
  decoder throws the `truncated` error for held bytes of an unfinished code point, and the next call starts a new value. `reset()` also starts a
  new value.
- A decoder call that throws leaves the decoder as it was before the call.
- All the chunks of a `StenoEncoderStream` or `StenoDecoderStream` are one value.

### Errors

```js
import { StenoDecodeError, decode } from '@singulink/steno';

try {
  decode(new Uint8Array([0x2f, 0x00]));
} catch (e) {
  if (e instanceof StenoDecodeError) console.log(e.reason, e.offset); // zero-byte 1
}
```

Bytes that are not the Steno bytes of a value throw a `StenoDecodeError`. Its `reason` is the rule of the specification that the bytes broke, one of
`zero-byte`, `unused-lead`, `truncated`, `past-range`, `surrogate-pair` and `invalid-trail` (a byte 01..07 where a trail byte 08..FF is
expected), in the check order of the specification (sections 9.2 and 10.1).
Its `offset` is where the code point that broke the rule starts; for a streaming decoder it counts from the first byte of the value. There is
no replacement character and no partial result, because the state after invalid data is unknown. Encoding a string never fails.

## Text Model

A JavaScript string is UTF-16, so every string is a value and can be encoded. A high surrogate followed by a low surrogate is one supplementary
code point; every other code unit, an unpaired surrogate included, is the code point with its value. Decoding always gives a string: a surrogate
code point of its own becomes an unpaired surrogate, so every string round-trips exactly. The encoding does not normalize the text and does
not add or remove a byte order mark.

## Before You Store Data

- **Store the name of the encoding with the bytes.** Steno bytes carry no header or version marker, so nothing in them says how to read them.
  Record `x-steno` (exported as `STENO_ENCODING_NAME`) in the schema or next to the data.
- **Store each value by itself.** The bytes of a code point depend on the code points before it, so decoding must start at the first byte of a
  value, and two values cannot be joined by joining their bytes. To keep several values in one buffer, end each with a `00` byte (which never
  occurs in Steno bytes) or record its length.
- **Expect code point order, not alphabetical order.** The bytes sort in the order of the characters' Unicode numbers: "Z" sorts before "a", and
  "Å" after "z". Use it for keys, ranges and prefixes, not for lists that people read in their language's order.

The [guide to storing values](https://www.singulink.com/Docs/Steno/articles/start/storing-values.html) has more.

## Thread Safety

JavaScript runs one thread per realm, and each worker has its own copy of the module. The one-shot functions can be used from any number of
workers at once. The layouts that the codec builds are kept in a cache of at most 1,024 entries per realm; when it is full, the oldest entry is
dropped. Encoder and decoder objects hold the state of one value and are not to be shared between values that are in progress at the same time.

The one-shot functions reuse buffers and a state between calls. They call no code of the caller while they use them: arguments are checked and
read through plain views first, and no method of a caller's object (such as an overridden `set` of a `Uint8Array` subclass) is called. If caller
code still runs in the middle of a call (the iterator given to `encodeCodePoints`) and calls one of the functions again, that inner call uses a
fresh state and buffers of its own.

## Conformance

This implementation passes all 7,640 vectors of the specification's test vector file (valid, invalid and not-a-value), and the large vector file
of 145,573 vectors. To run the tests:

```sh
npm ci
npm test
```

The tests read the vector file from `../../TestVectors/steno-v1-vectors.txt`. If the environment variable `STENO_LARGE_VECTORS` names the large
vector file, the tests run it too. The other tests compare random mixed-script text with two independent plain codecs, compare the fast paths
with the plain ones around every size at which they change, and check the order and prefix properties, streaming over random splits, buffer
bounds, garbage input, wrong arguments and workers. The random tests run a fixed number of rounds; set `STENO_FUZZ_ROUNDS` for a longer run. The
examples of this README are run as tests, and `npm run test:types` compiles them as TypeScript against the declarations under strict settings.

## Performance

This is scalar JavaScript, with fast paths: no WebAssembly, no native code and no dependencies.

- Runs of ASCII text are encoded and decoded by the platform's own `TextEncoder` and `TextDecoder`, which work at native speed, with one pass
  over the bytes to map them between ASCII and Steno.
- Text in the main window, the CJK ideographs, the Hangul syllables and the kana of the kana slot take one table lookup or one short calculation
  per character in a tight loop. Other characters, such as a change of script, take one slower step each.
- Each layout is built once, when a state is used for a run of code points, and kept in a bounded cache; a layout remembers the last layouts that
  a change of script led to, and an encoder or decoder remembers the last layouts it used.
- Decoded text is built into a string by the platform's UTF-16 decoder, or for short strings with one call of `String.fromCharCode`.

A state builds its layout (the tables of the fast paths) only when it is used for a run of code points or comes back soon. A state that text
leaves after a few code points builds nothing: each of its code points is resolved from the state, by computing its ranges once. So the worst
case, text that moves to another window on every code point or every few code points, among more windows than the cache holds, costs a few
hundred nanoseconds per code point (about 300 to 350 ns on a desktop machine, against 10 to 40 ns for ordinary text), and memory stays bounded.

JavaScript has no vector instructions. A WebAssembly build of the Rust reference implementation is the route to vector speed in JavaScript; it is
not part of this package yet.

<!-- benchmarks:start -->
The measured figures are in `BENCHMARKS.md` after the first benchmark run (`npm run bench`).
<!-- benchmarks:end -->

## License

MIT, the license of the repository. See [LICENSE.md](LICENSE.md).
