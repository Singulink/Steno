# Rust Reference Implementation of the Steno Encoding

The crate `singulink-steno` is the Rust reference implementation of the Steno Encoding, and the crate `singulink-steno-capi` is a C interface
over it that other languages can bind to.

The Steno Encoding is a compact encoding of Unicode text whose bytes sort like the text. It was designed to be the ideal per-value compression
encoding for databases and key-value stores, which can sort, compare, range-scan and prefix-match Steno bytes as plain bytes, without decoding
them. There is no byte below `08` inside a code point, so line feeds, tabs and spaces can be found at the byte level.

- [Steno Encoding, version 1](https://github.com/Singulink/Singulink.Steno/blob/main/FORMAT.md): the specification.
- [The main README](https://github.com/Singulink/Singulink.Steno/blob/main/README.md): how the Steno Encoding compares with other encodings.
- [The Rust reference implementation](https://www.singulink.com/Docs/Steno/articles/implementations/rust.html) and
  [Storing values](https://www.singulink.com/Docs/Steno/articles/start/storing-values.html) on the documentation site.

## Installation

> The crates are not published yet.

```toml
[dependencies]
singulink-steno = "0.1"
```

The library name is `singulink_steno`. It has two default features:

- `std` keeps a layout cache per thread. Without it, the crate is `no_std` and needs only `alloc` (see [Memory and Threads](#memory-and-threads)).
- `simd` adds vector code for x86-64 and ARM64 (see [Performance](#performance)). Without it, the crate forbids unsafe code.

## Usage

### Encode and Decode a String

```rust
let bytes = singulink_steno::encode("Москва");
assert_eq!(bytes.len(), 7); // UTF-8 takes 12

let text = singulink_steno::decode(&bytes)?;
assert_eq!(text, "Москва");
# Ok::<(), singulink_steno::DecodeError>(())
```

The bytes sort like the text. Compare them as byte slices, which Rust compares as unsigned bytes:

```rust
use singulink_steno::encode;

assert!(encode("Zug") < encode("Zürich")); // the bytes of "Zug" sort first
assert!("Zug" < "Zürich");                 // and so do the strings

// Code point order, not the order of a language: "Z" sorts before "a", and "Å" after "z".
assert!(encode("Z") < encode("a"));
assert!(encode("Å") > encode("z"));
```

Rust compares `str` by its UTF-8 bytes, which is code point order, and byte slices as unsigned bytes. So Rust's own comparisons already give
the order of the Steno Encoding, on strings in memory and on Steno bytes alike, and this crate has no comparison functions. (In languages that
compare strings by UTF-16 code units, such as Java, C# and JavaScript, the order differs where a character above U+FFFF meets U+E000..U+FFFF.)

### Write into Your Own Buffer

Every one-shot function has a form that appends to a `Vec` or `String`, and a form that writes into a slice. The maximum-length functions give
a length that is always enough, for every input of that length.

```rust
let text = "Grüße";
let mut buf = vec![0u8; singulink_steno::max_encoded_len(text.len())];
let n = singulink_steno::encode_to_slice(text, &mut buf).expect("long enough");
assert_eq!(&buf[..n], singulink_steno::encode(text));
assert_eq!(n, singulink_steno::encoded_len(text));

let mut out = vec![0u8; singulink_steno::max_decoded_len(n)];
let m = singulink_steno::decode_to_slice(&buf[..n], &mut out).expect("valid and long enough");
assert_eq!(&out[..m], text.as_bytes());

// A slice that is too short is an error, not a panic.
assert!(singulink_steno::encode_to_slice(text, &mut buf[..2]).is_err());
```

The contents of the slice after the length that a function returns are unspecified: the vector code writes whole blocks.

| Function | Bound |
|---|---|
| `max_encoded_len(n)` | `n × 2` Steno bytes for `n` UTF-8 bytes |
| `utf16::max_encoded_len(n)` | `n × 3` Steno bytes for `n` UTF-16 code units |
| `code_points::max_encoded_len(n)` | `n × 4` Steno bytes for `n` code points |
| `max_decoded_len(n)` | `n × 4` UTF-8 bytes for `n` Steno bytes |
| `utf16::max_decoded_len(n)` | `n × 2` UTF-16 code units for `n` Steno bytes |
| `code_points::max_decoded_len(n)` | `n` code points for `n` Steno bytes |

### UTF-16 and Code Points

These forms hold every value, surrogates included.

```rust
use singulink_steno::{code_points, utf16, DecodeReason};

// UTF-16 with an unpaired surrogate: encoded as a code point of its own, and decoded back as it was.
let units = [0x0061, 0xD800, 0x0062];
let bytes = utf16::encode(&units);
assert_eq!(utf16::decode(&bytes)?, units);
assert_eq!(code_points::decode(&bytes)?, [0x61, 0xD800, 0x62]);

// A str cannot hold U+D800, so decoding to a String fails instead of replacing it.
let e = singulink_steno::decode(&bytes).unwrap_err();
assert_eq!(e.reason(), DecodeReason::Surrogate);
assert_eq!(e.position(), 1);
# Ok::<(), singulink_steno::DecodeError>(())
```

### Handle Errors

```rust
use singulink_steno::{code_points, DecodeReason, EncodeErrorKind};

let e = singulink_steno::decode(&[0x48, 0x00]).unwrap_err();
assert_eq!(e.reason(), DecodeReason::ZeroByte);
assert_eq!(e.reason().as_str(), "zero-byte");
assert_eq!(e.position(), 1); // the code point that broke the rule starts at byte 1

// A high surrogate code point followed by a low one is not a value.
let e = code_points::encode(&[0xD83D, 0xDE00]).unwrap_err();
assert_eq!(e.kind(), EncodeErrorKind::NotAValue);
assert_eq!(e.position(), 1);
```

### Encode and Decode in Pieces

```rust
use singulink_steno::{Decoder, Encoder};

let mut encoder = Encoder::new();
let mut bytes = Vec::new();
encoder.encode_utf16(&[0x48, 0x69, 0x20, 0xD83D], &mut bytes)?; // ends with half of a surrogate pair
encoder.encode_utf16(&[0xDE00], &mut bytes)?;                   // the other half
encoder.finish(&mut bytes);
assert_eq!(bytes, singulink_steno::encode("Hi 😀"));

let mut decoder = Decoder::new();
let mut text = String::new();

for b in &bytes {
    decoder.decode(std::slice::from_ref(b), &mut text)?; // one byte at a time
}

decoder.finish()?;
assert_eq!(text, "Hi 😀");
# Ok::<(), Box<dyn std::error::Error>>(())
```

### Test for ASCII Text

```rust
assert!(singulink_steno::is_ascii_text(&singulink_steno::encode("Hello, world.\r\n")));
assert!(!singulink_steno::is_ascii_text(&singulink_steno::encode("Grüße")));
```

`is_ascii_text` is true if the value has only tab, line feed, carriage return and U+0020..U+007E, decided from the Steno bytes alone. Such a
value has as many UTF-8 bytes as Steno bytes. The Steno bytes are not the ASCII bytes: U+0020..U+007E are `07`..`65`.

## API Overview

| Item | What it does |
|---|---|
| `encode`, `encode_to_vec`, `encode_to_slice`, `encoded_len` | Encode a `&str` |
| `decode`, `decode_to_string`, `decode_to_slice`, `decoded_len` | Decode to a `String` or to UTF-8 bytes |
| `utf16::{encode, encode_to_vec, encode_to_slice, encoded_len}` | Encode UTF-16 code units, unpaired surrogates included |
| `utf16::{decode, decode_to_vec, decode_to_slice, decoded_len}` | Decode to UTF-16 code units |
| `code_points::{encode, encode_to_vec, decode, decode_to_vec}` | Encode and decode code points as `u32` |
| `max_encoded_len`, `max_decoded_len` and the same in `utf16` and `code_points` | True upper bounds of the lengths |
| `Encoder`, `Decoder` | Encode or decode one value in pieces |
| `is_ascii_text` | Whether the Steno bytes hold only ASCII text |
| `DecodeError`, `DecodeReason`, `EncodeError`, `EncodeErrorKind`, `BufferTooSmall`, `DecodeToSliceError` | Errors |
| `WEB_NAME` | `"x-steno"`, the name to store next to the Steno bytes |

## Text Model

A value of the Steno Encoding is a sequence of code points U+0000..U+10FFFF. Surrogate code points (U+D800..U+DFFF) are allowed on their own,
but a high surrogate code point directly followed by a low one is not a value.

- **`str` and `String`** hold Unicode scalar values, so they never hold a surrogate. Encoding a `&str` never fails. Decoding to a `String`
  (or to UTF-8 bytes) fails with `DecodeReason::Surrogate` if the value has a surrogate code point. It never replaces it.
- **UTF-16 (`&[u16]`)** holds every value. The encoder reads a high surrogate followed by a low surrogate as one code point, and every other
  code unit as the code point of the same value, so every `&[u16]` can be encoded, ill-formed or not, and decodes back unchanged.
- **Code points (`&[u32]`)** hold every value. The encoder rejects a number above 0x10FFFF and a high surrogate code point followed by a low
  one (`EncodeErrorKind::NotAValue`).

The encoder does not normalize the text and does not add or remove a byte order mark. The empty value encodes to no bytes.

## Errors

Decoding invalid Steno bytes fails with a `DecodeError`. `reason()` is one of the six rules of FORMAT.md §10.1, which the decoder checks in
the order of §9.2, so the reason is the rule that the bytes break first: `ZeroByte`, `UnusedLead`, `Truncated`, `PastRange`, `SurrogatePair`
or `InvalidTrail` (a byte `01`..`07` where a trail byte is expected). `position()` is the offset of the first byte of the code point that broke
the rule. The seventh reason, `Surrogate`, is not a defect of the bytes: it comes only from the decoders that write a `str` or UTF-8. A decoder never returns part of a value: on an error, the
output that the call appended to is left as it was.

Encoding a `str` or UTF-16 never fails. Encoding code points fails only with an `EncodeError` as described above. The functions that write
into a slice fail with `BufferTooSmall` (or `DecodeToSliceError::BufferTooSmall`) if the slice is too short, and the contents of the slice
are then unspecified.

## Streaming

`Encoder` and `Decoder` encode or decode one value given in pieces. Over any split of the input, they give exactly the Steno bytes or the text
of the one-shot functions, also when a piece ends in the middle of a surrogate pair or of the bytes of a code point.

- The encoder holds a high surrogate at the end of a UTF-16 piece until the next piece. That is all it holds.
- Pieces of a `str`, of UTF-16 and of code points can be mixed. A high surrogate and a low surrogate are joined only when both are UTF-16
  code units; once a high surrogate has been written as a code point of its own, a low surrogate right after it, in any form, is rejected
  with `NotAValue`, so every mix gives either the bytes of a value or an error.
- The decoder holds the bytes of an unfinished code point (at most 3) until the next piece.
- `finish` consumes the object and ends the value: the encoder writes a held high surrogate as an unpaired surrogate, and the decoder fails
  with `Truncated` if it holds bytes. There is no flush, and nothing but `finish` and `reset` ends a value.
- `reset` starts a new value and drops what is held.
- A call that fails leaves the object and the output as they were before the call.
- Error positions of the decoder count from the start of the value. Error positions of the encoder are indices in the slice of the call.

## Memory and Threads

The one-shot functions are safe to call from many threads at once. `Encoder` and `Decoder` are small values with no heap memory; they are
`Send` and `Sync`, but each one is meant to be used by one thread at a time (they take `&mut self`).

The encoder and the decoder work from a **layout**, a table that depends on the state (FORMAT.md §6). A layout takes under 3 KB. With the
`std` feature:

- The layouts that most values use are built once, on first use, and shared by all threads without a lock: the initial layout, the layout of
  each alphabet window below U+3400 (where an alphabet value goes after its first character), and the layouts of Chinese, Japanese and
  Korean text. There are at most 104 of them, so they take at most about 300 KB, and only for the scripts that the process meets.
- Each thread keeps a cache of at most 32 other layouts, so a thread uses under 100 KB for it. The cache is bounded (a new layout replaces an
  old one), no layout refers to another (a cache slot remembers which slots the layouts it led to were in, and checks the slot when it uses
  that), no lock is taken, and nothing in it is shared between threads.

Without the `std` feature, each call builds the layouts it needs into a cache of its own, which makes short values with characters outside
ASCII much slower.

## Before You Store Data

- **Store the name of the encoding with the data.** Steno bytes carry no header or version marker, so a reader cannot tell them from other
  bytes. Store `x-steno` (`WEB_NAME`) in the schema or next to the value.
- **Keep each value whole.** The bytes of a character depend on the characters before it, so two values cannot be joined by joining their
  bytes, and decoding must start at the first byte of a value. To keep several values in one buffer, end each with a byte `00` (which never
  occurs in Steno bytes) or store its length.
- **Expect code point order.** The bytes sort in the order of the code points, which is the order of Rust's `str` comparison. It is not the
  order of a language: "Z" sorts before "a", and "Å" after "z". Sort with a collation where people read the order.

The guide [Storing values](https://www.singulink.com/Docs/Steno/articles/start/storing-values.html) explains these points in full.

## Conformance

This implementation passes all 7,640 vectors of the specification's test vector file (valid, invalid and not-a-value), and all 145,573
vectors of the large vector file.

```sh
cargo test                                    # all tests, under a minute
STENO_LARGE_VECTORS=/path/to/steno-v1-vectors-large.txt cargo test --release --test vectors
STENO_SIMD=0 cargo test                       # the same with the vector code turned off
```

The tests read `TestVectors/steno-v1-vectors.txt` from the repository. They also check, on seeded random text, the properties of FORMAT.md §13
(round trip, byte order equals code point order, prefixes, no byte `00`), agreement with a plain codec written from the specification,
streaming over random splits, buffer bounds, random and damaged Steno bytes, concurrent use, and the vector code against the scalar code
around every block boundary. A second, independent oracle (a port of the clean-room C# codec of the repository) checks every form of the API
on random and damaged input, the memory stays bounded (`tests/memory.rs`), and `tests/worst_case.rs` bounds the time of text that changes state
at every code point. `STENO_FUZZ=1 cargo test --release` runs the fuzz tests at full size (about a minute and a half).

## Safety

- No dependencies, and no native code: the crate `singulink-steno` is Rust only.
- Without the `simd` feature, the crate forbids unsafe code (`#![forbid(unsafe_code)]`).
- With it, unsafe code is allowed only in the module `src/simd`: the loads and stores of the vector code, each on a reference to an array of
  exactly one vector; the calls of the vector code after SSSE3 was detected (and of the NEON code, which every ARM64 processor runs); and
  the one function that appends UTF-8 that the vector code wrote to a `String` without checking it again, since the code that writes it can
  only write valid UTF-8. Every unsafe block has a safety comment, and the unsafe code passes Miri.

## Performance

The default build is **vector code on x86-64 and ARM64 with a scalar fallback**:

- On x86-64, the vector code needs SSSE3, which is detected at run time (with `std`) or taken from the compile-time target features (without
  `std`). On little-endian ARM64 it uses NEON, which every ARM64 processor has. Elsewhere, big-endian ARM64 (`aarch64_be`) included, and
  without the `simd` feature, the scalar code runs alone.
- UTF-16, both directions: runs of 1-byte codes (ASCII with the main window of an alphabet, or with the kana and punctuation of Chinese and
  Japanese text) 16 code units at a time, and a run of 4 to 15 code units, the most common length of a short value, in one step with two
  overlapping partial vectors; the mixed 1- and 2-byte codes of Chinese, Japanese and Korean text (the ideographs and the Hangul syllables)
  with byte shuffles, 8 code units or 32 bytes at a time, for runs of 32 code units or more.
- UTF-8 (`&str`, `String` and the UTF-8 functions of the C interface): the text goes through UTF-16 on the stack, converted by vector code
  for sequences of 1 to 3 bytes, 16 bytes at a time, and from UTF-16 back to UTF-8 eight code units at a time. Encoding goes this way for
  alphabets in runs of 64 bytes or more, decoding for runs of 32 bytes or more.
- `is_ascii_text`, 16 bytes at a time.
- Scalar code: everything else, among it the changes of state, short UTF-8 runs, and the encoding of Chinese, Japanese and Korean text from
  a `&str`. On these, the vector code measured slower than the scalar code, so it is not used. ASCII runs go through a loop that the compiler
  vectorizes.
- The ARM64 vector code shares everything with the x86-64 code but the intrinsics. It is tested in CI on ARM64.

**Worst case.** Text that reaches a new state at almost every character (each character from another of many windows) cannot use a cached
layout. Such a state is used for its one code point from the ranges of its layout alone, which takes no memory and about 200 ns to build; the
full layout is built only when a state lasts for a second code point. Measured on one machine: about 250 ns per code point for text that hops
among 20, 104 or 400 windows, against about 1.5 ns for ordinary text, so a megabyte of such text takes about a quarter of a second. Memory
stays bounded as described in [Memory and Threads](#memory-and-threads). `tests/worst_case.rs` checks a bound.

<!-- benchmark-summary:start -->
The measured figures are in `BENCHMARKS.md` after the first benchmark run.
<!-- benchmark-summary:end -->

The benchmarks are in the crate `singulink-steno-bench` in the folder `bench`, with Criterion, on the shared workloads of the repository:

- `sh bench/run.sh` (about 10 minutes) measures encoding and decoding in the `str` and UTF-16 forms, next to the standard library's UTF-16 to
  UTF-8 conversions, writes `bench/results.csv` and `BENCHMARKS.md`, and fills in the block above.
- `sh bench/run-native.sh` (about 30 minutes) compares the Steno Encoding (this crate, and the plain C implementation of `Ports/c` with and
  without its vector code) with UTF-8, with SCSU and BOCU-1 through ICU, and with Zstd, Brotli, LZ4 and Deflate through their C libraries,
  each value on its own; it also compares two encoded values without decoding them. It
  writes `bench/native-results.csv`, `bench/native-libraries.csv` and a section of `BENCHMARKS.md`.

## C Interface

The crate `singulink-steno-capi` in the folder `capi` builds a static library and a dynamic library named `steno` (`libsteno.a`,
`libsteno.so`, `steno.dll`, and so on), with the header `capi/include/steno.h`. Its ABI is not stable before version 1.0.

The functions encode from UTF-8, UTF-16 and code points, decode to UTF-8, UTF-16 and code points, give exact and maximum lengths, and test
for ASCII text. Every declaration of `steno.h` is also in the header of the plain C implementation of the repository (`Ports/c/steno.h`), with
the same signature, status values and meaning, so a program that uses only these declarations can link with either library; that header
adds streaming objects. Each takes
pointer and length pairs, writes the produced length through an out pointer, never allocates memory that the caller must free, and is safe to
call from any thread. The status values (`STENO_OK`, the six decode reasons, `STENO_DESTINATION_TOO_SMALL`, `STENO_INVALID_UTF8`,
`STENO_SURROGATE`, `STENO_INVALID_ARGUMENT`, and `STENO_NOT_A_VALUE` and `STENO_INVALID_CODE_POINT` for code points) are stable. The header documents every function. There is no comparison function: compare
Steno bytes with `memcmp`.

`capi/tests/c/test.c` is a C program that uses every function, and is the example to start from. To build and run it on Linux (as the CI job
does):

```sh
cargo build --release -p singulink-steno-capi
cc -std=c99 -Wall -Wextra -Werror -Icapi/include capi/tests/c/test.c -Ltarget/release -lsteno -Wl,-rpath,"$PWD/target/release" -o steno-test
./steno-test
```

On Windows with the GNU toolchain of Rust, `zig cc` links it with the dynamic library (`steno.dll` must be next to the program or on the
`PATH`):

```sh
zig cc -std=c99 -Wall -Wextra -Werror -target x86_64-windows-gnu -Icapi/include capi/tests/c/test.c target/release/libsteno.dll.a -o steno-test.exe
```

## License

MIT, as the rest of the [repository](https://github.com/Singulink/Singulink.Steno/blob/main/LICENSE.md).
