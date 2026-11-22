# C Reference Implementation of the Steno Encoding

The library `steno`, made of the two files `steno.h` and `steno.c`, is the C reference implementation of the Steno Encoding.

The Steno Encoding is a compact encoding of Unicode text whose bytes sort like the text. It was designed to be the ideal per-value compression
encoding for databases and key-value stores, which can sort, compare, range-scan and prefix-match Steno bytes as plain bytes, without decoding
them. The bytes keep code point order and prefixes, each value has exactly one encoding, and the byte `00` never occurs. No byte below `08`
occurs inside a code point, so line feeds, tabs and spaces can be found at the byte level.

- [Specification](https://github.com/Singulink/Steno/blob/main/FORMAT.md): the Steno Encoding, version 1.
- [Main README](https://github.com/Singulink/Steno/blob/main/README.md): the sizes and the comparison with other encodings.
- [The C implementation on the documentation site](https://www.singulink.com/Docs/Steno/articles/implementations/c.html).
- [Storing values](https://www.singulink.com/Docs/Steno/articles/start/storing-values.html): what to know before you store data.

The library encodes and decodes the Steno Encoding, version 1, and passes all test vectors of the specification. It is C99, needs nothing but
the C standard library, and never allocates memory: the caller provides every buffer. It is made to be dropped into the build of a C or C++
project, such as a SQLite or PostgreSQL extension, an embedded system or a binding for another language.

## Installation

> Not published yet: the files come with the release of the repository tagged `Source/c/v1.0.0`.

Copy `steno.h` and `steno.c` into your project and compile `steno.c` with the rest of your code. No build system and no configuration are
needed:

```sh
cc -std=c99 -O2 -c steno.c
```

With CMake, add the folder and link the target `steno`:

```cmake
add_subdirectory(path/to/Source/c)
target_link_libraries(my_program PRIVATE steno)
```

Or build and install the static library with CMake (`cmake -S . -B build && cmake --build build && cmake --install build`) or with the
plain `Makefile` (`make && make install PREFIX=/usr/local`). Both name the library `steno` (`libsteno.a`), as the Rust implementation's C
library does.

For a shared library, CMake takes `-DBUILD_SHARED_LIBS=ON`, and `make shared` builds `build/libsteno.so`. Every function is marked with
`STENO_API`: on Windows, define `STENO_BUILD_SHARED` when you build the DLL (the functions are exported) and `STENO_SHARED` in the
programs that use it (they are imported; the CMake target does both for you). With gcc and clang elsewhere, the functions keep default
visibility when the library is built with `-fvisibility=hidden`, so only they are exported. A static build defines nothing.

## Usage

Every example below is a function in `tests/readme_examples.c`, which the tests compile and run, checking what it prints.

### Encode and decode UTF-8

```c
const char *text = "Москва";
uint8_t bytes[64];
char back[64];
size_t n, m;
steno_status s;

s = steno_encode_utf8((const uint8_t *)text, strlen(text), bytes, sizeof bytes, &n);
printf("%s: %u Steno bytes for %u UTF-8 bytes\n", steno_status_name(s), (unsigned)n, (unsigned)strlen(text));
/* ok: 7 Steno bytes for 12 UTF-8 bytes */

s = steno_decode_utf8(bytes, n, (uint8_t *)back, sizeof back, &m);
printf("%s: %.*s\n", steno_status_name(s), (int)m, back);
/* ok: Москва */
```

Every function takes the text or bytes as a pointer and a length, and a destination as a pointer and a capacity, and writes the length it
produced through its last argument. It returns a `steno_status`.

### Size the buffers

```c
const char *text = "Grüße aus Zürich";
size_t len = strlen(text), n;
uint8_t *bytes = malloc(steno_max_encoded_len_utf8(len));

if (bytes != NULL && steno_encode_utf8((const uint8_t *)text, len, bytes, steno_max_encoded_len_utf8(len), &n) == STENO_OK) {
    printf("%u bytes, at most %u\n", (unsigned)n, (unsigned)steno_max_encoded_len_utf8(len));
    /* 16 bytes, at most 38 */
}

free(bytes);
```

The maximum lengths are true bounds for every input of the given length, tested on worst-case inputs. They saturate at `SIZE_MAX`.
`steno_encoded_len_*` and `steno_decoded_len_*` give the exact length instead, at the cost of a pass over the input.

| Function | Input | Bound |
|---|---|---|
| `steno_max_encoded_len_utf8(n)` | `n` bytes of UTF-8 | `2n` Steno bytes |
| `steno_max_encoded_len_utf16(n)` | `n` UTF-16 code units | `3n` Steno bytes |
| `steno_max_encoded_len_code_points(n)` | `n` code points | `4n` Steno bytes |
| `steno_max_decoded_len_utf8(n)` | `n` Steno bytes | `4n` bytes of UTF-8 |
| `steno_max_decoded_len_utf16(n)` | `n` Steno bytes | `2n` UTF-16 code units |
| `steno_max_decoded_len_code_points(n)` | `n` Steno bytes | `n` code points |

A destination that is too small gives `STENO_DESTINATION_TOO_SMALL`. No function writes past the capacity it is given, and on an error the
contents of the destination are unspecified.

### Compare Steno bytes

```c
static int steno_compare(const uint8_t *a, size_t a_len, const uint8_t *b, size_t b_len) {
    int c = memcmp(a, b, a_len < b_len ? a_len : b_len);
    return c != 0 ? c : (a_len > b_len) - (a_len < b_len);
}

uint8_t zug[16], zurich[16];
size_t n1, n2;

steno_encode_utf8((const uint8_t *)"Zug", 3, zug, sizeof zug, &n1);
steno_encode_utf8((const uint8_t *)"Zürich", 7, zurich, sizeof zurich, &n2);
printf("%d\n", steno_compare(zug, n1, zurich, n2) < 0); /* 1: "Zug" < "Zürich" in code point order */
```

Comparing Steno bytes with `memcmp`, and the shorter first when one is a prefix of the other, gives the code point order of the texts. That is
also the order of `memcmp` on their UTF-8 forms, so the library needs no comparison function. A store that orders keys or `BLOB` values as
unsigned bytes keeps Steno bytes in that order.

### UTF-16 and code points

```c
const uint16_t units[] = {0x0048, 0xD83D, 0xDE00, 0xDC00}; /* "H", U+1F600 as a pair, and a lone low surrogate */
const uint32_t pair[] = {0xD83D, 0xDE00};
uint8_t bytes[32], utf8[32];
uint16_t back[8];
size_t n, m;
steno_status s;

steno_encode_utf16(units, 4, bytes, sizeof bytes, &n);
s = steno_decode_utf16(bytes, n, back, 8, &m);
printf("%s: %u units, the same: %d\n", steno_status_name(s), (unsigned)m, memcmp(back, units, sizeof units) == 0);
/* ok: 4 units, the same: 1 */

/* The lone surrogate has no UTF-8 form: an error, never a replacement. */
s = steno_decode_utf8(bytes, n, utf8, sizeof utf8, &m);
printf("%s at byte %u\n", steno_status_name(s), (unsigned)m);
/* surrogate at byte 4 */

/* As code points, a high surrogate directly followed by a low one is not a value. */
s = steno_encode_code_points(pair, 2, bytes, sizeof bytes, &n);
printf("%s at index %u\n", steno_status_name(s), (unsigned)n);
/* not-a-value at index 1 */
```

### Errors

```c
const uint8_t bad[] = {0x2F, 0x50, 0x00};
uint8_t text[16];
size_t n;
steno_status s = steno_decode_utf8(bad, sizeof bad, text, sizeof text, &n);

printf("%s at byte %u\n", steno_status_name(s), (unsigned)n);
/* zero-byte at byte 2 */
```

### Streaming

```c
/* The pieces split a UTF-8 sequence: "é" is C3 A9. */
const char *pieces[] = {"caf\xC3", "\xA9 cr\xC3\xA8me"};
uint8_t out[64], whole[64];
size_t len = 0, n, wn, i;
steno_encoder enc;

steno_encoder_init(&enc);
for (i = 0; i < 2; i++) {
    if (steno_encoder_feed_utf8(&enc, (const uint8_t *)pieces[i], strlen(pieces[i]), out + len, sizeof out - len, &n) != STENO_OK) {
        return;
    }
    len += n;
}
if (steno_encoder_finish(&enc, out + len, sizeof out - len, &n) != STENO_OK) return;
len += n;

steno_encode_utf8((const uint8_t *)"caf\xC3\xA9 cr\xC3\xA8me", 12, whole, sizeof whole, &wn);
printf("%d\n", len == wn && memcmp(out, whole, len) == 0); /* 1 */
```

```c
uint8_t bytes[16];
uint16_t text[16];
size_t n, len = 0, got, i;
steno_decoder dec;

steno_encode_utf8((const uint8_t *)"\xE6\x9D\xB1\xE4\xBA\xAC", 6, bytes, sizeof bytes, &n); /* 東京 */

/* The bytes arrive one at a time. */
steno_decoder_init(&dec);
for (i = 0; i < n; i++) {
    if (steno_decoder_feed_utf16(&dec, bytes + i, 1, text + len, 16 - len, &got) != STENO_OK) return;
    len += got;
}

/* Finish reports the bytes of an unfinished code point. */
printf("%s: %u units, U+%04X U+%04X\n", steno_status_name(steno_decoder_finish(&dec, &got)), (unsigned)len, text[0], text[1]);
/* ok: 2 units, U+6771 U+4EAC */
```

### ASCII text

```c
uint8_t a[32], b[32];
size_t na, nb;

steno_encode_utf8((const uint8_t *)"Hello\tworld\n", 12, a, sizeof a, &na);
steno_encode_utf8((const uint8_t *)"Gr\xC3\xBC\xC3\x9F" "e", 7, b, sizeof b, &nb);
printf("%d %d\n", steno_is_ascii_text(a, na), steno_is_ascii_text(b, nb)); /* 1 0 */
```

`steno_is_ascii_text` decides from the bytes alone whether the value has only tab, line feed, carriage return and U+0020..U+007E. The Steno
bytes of such text are not ASCII bytes: U+0020..U+007E are `07`..`65`.

## The Same Interface as the Rust Implementation's C Library

The first part of `steno.h` is also the C interface of the Rust reference implementation (`Source/rust/capi/include/steno.h`): the same
functions (UTF-8, UTF-16 and code points), signatures, status values and meaning, the rules for `out_len` and for the order of problems
included. A program that uses only that part can be compiled against either header and linked with either library, which are both called
`steno`. The tests compile the Rust interface's own test program, `Source/rust/capi/tests/c/test.c`, against this library and run it, and
`make vs-rust` calls both libraries in one process on millions of the same arguments (abusive ones included) and fails on any difference.

The second part of the header adds what the Rust interface does not have, marked as such: the streaming objects `steno_encoder` and
`steno_decoder`, a version number, and the `STENO_API` marker for shared libraries (which expands to nothing for a static build).

## Text Model

A value of the Steno Encoding is a sequence of code points U+0000..U+10FFFF. Surrogate code points (U+D800..U+DFFF) are allowed as code
points of their own, but a high surrogate code point directly followed by a low one is not a value. The library has three forms of text:

- **UTF-8** (`uint8_t`). Encoding needs valid UTF-8. Anything else, also the UTF-8 form of a surrogate (`ED A0 80`), overlong forms and
  values above U+10FFFF, gives `STENO_INVALID_UTF8` at the first bad sequence. Nothing is replaced with U+FFFD. Decoding a value that has a
  surrogate code point gives `STENO_SURROGATE` at that code point. A byte order mark is the code point U+FEFF, encoded like any other.
- **UTF-16** (`uint16_t`). A high surrogate followed by a low surrogate is one code point, and every other code unit is the code point with
  its value. So every sequence of code units is a value, encoding UTF-16 never fails, and decoding gives back exactly the code units that
  were encoded.
- **Code points** (`uint32_t`). A value in U+D800..U+DFFF is a surrogate code point. A high surrogate directly followed by a low one gives
  `STENO_NOT_A_VALUE` at the low one: combine such a pair into one code point first. A value above 0x10FFFF gives
  `STENO_INVALID_CODE_POINT`.

UTF-16 and code points have full fidelity: every valid value decodes in these forms.

## Errors

Every function returns a `steno_status`; `steno_status_name` gives its name. Decoding fails with the reasons of the specification (§10.1),
and when bytes break several rules, the status is the first one that a decoder meets in the order of §9.2. `out_len` is then the byte offset
where the encoding of the code point that broke the rule starts.

| Status | Meaning |
|---|---|
| `STENO_ZERO_BYTE` | a byte `00`, as a lead byte or a trail byte (`zero-byte`) |
| `STENO_UNUSED_LEAD` | a lead byte that the layout of the current state does not use (`unused-lead`) |
| `STENO_TRUNCATED` | an end in the middle of the encoding of a code point (`truncated`) |
| `STENO_PAST_RANGE` | a code point beyond the last code point of its range, which includes every value above U+10FFFF (`past-range`) |
| `STENO_SURROGATE_PAIR` | a low surrogate code point directly after a high surrogate code point (`surrogate-pair`) |
| `STENO_INVALID_TRAIL` | a byte `01`..`07` where a trail byte is expected (`invalid-trail`) |
| `STENO_SURROGATE` | not a rule of the format: a surrogate code point, which the functions that decode to UTF-8 cannot write |
| `STENO_INVALID_UTF8` | UTF-8 input that is not valid; `out_len` is the length of the valid UTF-8 before it |
| `STENO_NOT_A_VALUE` | code points with a high surrogate directly followed by a low one; `out_len` is the index of the low one |
| `STENO_INVALID_CODE_POINT` | a `uint32_t` above 0x10FFFF; `out_len` is its index |
| `STENO_DESTINATION_TOO_SMALL` | the destination is too short; `out_len` is 0 |
| `STENO_INVALID_ARGUMENT` | a NULL pointer with a length that is not 0, a pointer that is not aligned for its type, a source and a destination that overlap, an `out_len` that is NULL, not aligned, or inside the source, the destination or the streaming object (nothing is then written through it), or a length that no buffer can have |

When several problems are present, the status is the first one met, reading the source from the start: a destination that is too short is
reported even when the source is invalid after the point where the destination is full. Decoding to UTF-8 stops at the first surrogate code
point, so for bytes that also break a rule after it, the status is `STENO_SURROGATE`. Decode to code points to see every reason. Encoding a
valid value never fails.

## Streaming

A `steno_encoder` takes a value in pieces (`steno_encoder_feed_utf8`, `_utf16` and `_code_points`, which may be mixed) and writes Steno
bytes. A `steno_decoder` takes the Steno bytes in pieces and writes UTF-8, UTF-16 or code points (`steno_decoder_feed_utf8`, `_utf16` and
`_code_points`). The caller owns the object, which is about 1.8 KB: on the stack, in a struct or allocated. The library keeps no pointer
to it between calls.

- **Any split gives the same result.** Over any split of the input, also inside a UTF-8 sequence, a surrogate pair or the bytes of one code
  point, the pieces give exactly the bytes or text of the one-shot functions, or the same error at the same position. What a piece ends in
  is held until the next call: up to 3 bytes of a UTF-8 sequence or one high surrogate code unit in an encoder, and up to 3 bytes of the
  encoding of a code point in a decoder.
- **Surrogates are never joined by accident.** A high surrogate code unit and a low one are joined into one code point only when both
  come through `steno_encoder_feed_utf16`, also across two calls. A high surrogate and a low one in any other way (as code points, or as a
  code point and a code unit) are not a value: `STENO_NOT_A_VALUE`.
- **Finish does not start a new value.** `steno_encoder_finish` writes a held high surrogate as a code point of its own; with the start of a
  UTF-8 sequence held, it fails with `STENO_INVALID_UTF8`. `steno_decoder_finish` fails with `STENO_TRUNCATED` if it holds the bytes of an
  unfinished code point. After either, the next call goes on with the same value.
- **Init or reset starts a new value.** Call `steno_encoder_init` or `steno_decoder_init` first. An object filled with zero bytes
  (`memset`, static storage, or `steno_encoder enc = {0};` in C) is also ready for a new value; in C++, `= {0}` draws a warning about
  missing field initializers, so call the init function there.
- **An error changes nothing.** A call that fails leaves the progress of the value as it was, so the caller can retry with a larger
  destination. The positions of errors count the input elements from the start of the value.
- **Destination sizes.** A feed call writes at most `steno_max_encoded_len_code_points(src_len + 3)` bytes (encoder) or
  `steno_max_decoded_len_*(src_len + 3)` elements (decoder); `steno_encoder_finish` writes at most 3 bytes.

## Before You Store Data

- **Record that the bytes are Steno bytes.** The bytes have no header or version marker. Store the name `x-steno` next to the data or in
  its schema.
- **Do not join encoded values.** The encoding is stateful: the bytes of a code point depend on the code points before it. Decoding starts
  at the first byte of a value, and the bytes of two values one after the other decode to other text. To keep several values in one buffer,
  end each with a `00` byte (it never occurs in Steno bytes) or record its length.
- **The order is code point order.** It is the order of `memcmp` on UTF-8, not the order of any language: "Z" sorts before "a", and "Å"
  after "z". Use a collation, such as ICU's, to sort for people.

The guide [Storing values](https://www.singulink.com/Docs/Steno/articles/start/storing-values.html) explains each point with an example.

## Thread Safety

Every function is safe to call from any number of threads at once. The library has no global or static state that it writes, and needs no
threads library, locks or thread-local storage: a one-shot call keeps what it needs on its stack (about 2 KB), and a streaming object keeps
its own small cache of layouts. A `steno_encoder` or `steno_decoder` must not be used by two threads at once; give each thread its own.

## Safety

- No dynamic memory: the library never calls `malloc`. It reads only the input it is given and writes only within the capacity it is
  given, and it never writes a byte of a wide store that does not belong to the result.
- No dependencies but the C standard library (`<stdint.h>`, `<stddef.h>`, `<stdbool.h>`, `<string.h>`, `<limits.h>`), and the compiler's
  own SSE2 or NEON intrinsics header where those are used.
- Checks on every call: NULL pointers with a length, pointers not aligned for their type, overlapping source and destination (also with a
  streaming object itself), an `out_len` inside any of them, and lengths larger than any object can be give `STENO_INVALID_ARGUMENT` rather
  than undefined behaviour. `out_len` is written once, as the last thing a call does. What the library cannot check, that a pointer points
  to as many elements as its length says, is the caller's duty.
- The tests run under AddressSanitizer and UndefinedBehaviorSanitizer with gcc and clang, and the threads test under ThreadSanitizer. Three
  libFuzzer harnesses check the decoder and the round trip against the plain reference codec (`tests/fuzz_decode.c`,
  `tests/fuzz_roundtrip.c`) and the whole interface against the independent oracle (`tests/fuzz_differential.c`).

## Conformance

This implementation passes all 7,640 vectors of the specification's test vector file (valid, invalid and not-a-value), in every form: UTF-8,
UTF-16, code points and the streaming objects, with the exact status and position for every invalid vector. It also passes the 145,573
vectors of the large vector file.

Run the tests from this folder with `make test` (or `ctest` after a CMake build). The default run takes about half a minute. The library is
checked against two plain codecs that share no code with it: `tests/ref.c`, written by the author from the specification, and
`tests/oracle.c`, a port of the clean-room codec of the .NET tests (`ReferenceSteno.cs`) made by the library's adversarial reviewer. The
programs:

| Test | What it checks |
|---|---|
| `test_internal` | the layouts against the reference and the specification (every layout without a second window, a sample with one, the constant layouts), the bounds the code relies on, the division by 248 over its whole range, the windows |
| `test_steno` | the vector file in every form; seeded random text against the reference, with the properties of the specification (round trip, byte order equals code point order, prefixes, no `00` byte, no byte below `08` inside a code point, accepted bytes are the encoding of their value); every byte value at every position of short encodings; invalid UTF-8 and state changes at every offset around the block sizes of the fast paths; destinations of exact size and too small, with guard bytes; streaming in random pieces and mixed forms; worst-case bounds; values of a million code points; every byte string of 1 and 2 bytes against the oracle |
| `test_oracle_vectors` | the vector file against the oracle, and the library against the oracle on every vector |
| `test_api` | the abuse of the C interface: NULL and misaligned pointers, buffers that wrap the address space, overlap, `out_len` inside a buffer, held input followed by another form, failed calls and retries, objects of zero bytes |
| `test_digest` | a digest of every result on a seeded corpus, which must be the same on every machine and build (vector or scalar, 32- or 64-bit, any byte order) |
| `test_differential` | 5,000 cases of the differential fuzzer (`tests/fuzz_differential.c`) against the oracle: every destination size, streaming with arbitrary splits, copies, mutations |
| `test_sweep` | a deterministic sweep of every fast path, at offsets and lengths around the block sizes, broken by every kind of code point, through every check of the differential fuzzer |
| `test_rust_capi` | the Rust interface's own test program, against this library |
| `test_readme` | the examples of this README |
| `test_threads` | several threads at once, one-shot and streaming, on text that builds layouts at every call |
| `test_hop` | the bound on the worst case of time (below) |

To run the large vector file too, set `STENO_LARGE_VECTORS` to its path. Other targets:

```sh
make test-scalar                 # the tests with the vector code turned off
make test-shared                 # the tests that use only steno.h, against the shared library
make sanitize                    # AddressSanitizer and UndefinedBehaviorSanitizer
make fuzz FUZZ_TIME=60           # the three libFuzzer harnesses, with clang
make exhaustive THREADS=8        # every layout against the oracle (about 40 s on 10 threads), every 3-byte string, the full sweep
make vs-rust RUST_LIB=../rust/target/release/libsteno.so   # this library against the Rust implementation's C library
```

## Performance

This is portable C with vector code for runs of 1-byte codes and a scalar fallback. What uses vector code, chosen at compile time from the
compiler's predefined macros:

- SSE2 on x86-64 and on 32-bit x86 with SSE2; NEON on little-endian ARM64. Every other machine, big-endian ARM64 included, uses the scalar
  code, which is always compiled. `-DSTENO_NO_SIMD` turns the vector code off. The tests check that both give the same output.
- Printable ASCII, 16 bytes at a time: encoding and decoding UTF-8, and `steno_is_ascii_text`.
- In UTF-16, runs of the main window mixed with printable ASCII (Cyrillic, Greek, Arabic and other alphabets) and of kana, 16 code units at
  a time.
- Without vector code, printable ASCII goes 8 bytes at a time in a 64-bit word.
- A run shorter than 4 codes (a space between ideographs, a few kana) is converted one code at a time without starting the wide code, so
  that text of short runs is not slower with vector code than without it.

The NEON code has run the whole test suite, and given the same bytes as the scalar code, under user-mode QEMU (AArch64), not yet on
ARM64 hardware; the CI job on an ARM64 runner runs it on hardware. MSVC has not compiled the library yet: the code is meant to build
with it (the SSE2 path is selected by `_M_X64`), but that is untested.

The other code points that never change the layout (the main window, kana, and the ideographs and Hangul syllables of the Chinese, Japanese
and Korean contexts) are encoded and decoded in tight loops without a state update, the 2-byte ones with an exact division by 248 that
needs no divide instruction. A change of script takes the general path, one code point at a time. The layouts that values reach first are
constants, so that a short value in one script needs no layout built.

The worst case is text that reaches a new state at almost every code point, which no cache can hold: for example, every character from a
window chosen at random among 104 windows. Each such code point builds a layout: about 20 ranges in a small array, without lookup tables,
which costs about as much as encoding a few dozen ordinary code points. So such text costs a bounded time per code point.
`tests/test_hop.c` measures text that hops among 3, 20, 104 and 400 windows, among all 7,356 windows, among windows far apart, in and out
of the Chinese and Korean contexts, one-shot and streamed one element per call. It fails if a one-shot call takes more than 2 µs per code
point or more than 1,000 times as long as ordinary Russian text, or a streamed call more than 5 µs.

The C reference implementation is measured in the benchmark of the Rust reference implementation, next to the Rust code, ICU and the
compressors. The figures are in its `BENCHMARKS.md` after the first benchmark run.

## License

MIT, the license of the repository. See [LICENSE.md](https://github.com/Singulink/Steno/blob/main/LICENSE.md).
