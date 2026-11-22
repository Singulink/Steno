<div class="article">

# C

The C reference implementation of the Steno Encoding is the library `steno`: two files, `steno.h` and `steno.c`, in plain C99. It needs nothing but the C standard library, never allocates memory (the caller provides every buffer), and keeps no global state that it writes. It is made to be compiled into a C or C++ project, such as a SQLite or PostgreSQL extension, an embedded system or a binding for another language. It passes every test vector of the specification.

### Installation

> [!NOTE]
> Not published yet: the files come with the release of the repository tagged `Ports/c/v0.1.0`.

Copy `steno.h` and `steno.c` into your project and compile `steno.c` with the rest of your code. Any C99 compiler builds it, with no configuration:

```sh
cc -std=c99 -O2 -c steno.c
```

With CMake, add the folder and link the target `steno`:

```cmake
add_subdirectory(path/to/Ports/c)
target_link_libraries(my_program PRIVATE steno)
```

The folder also has a plain `Makefile` (`make && make install PREFIX=/usr/local`). CMake and the Makefile both build the static library `libsteno.a`.

## First Example

Every function that encodes or decodes takes the source as a pointer and a length, and the destination as a pointer and a capacity. It returns a `steno_status` and writes the length it produced through its last argument, the out length. To size the destination, `steno_encoded_len_*` and `steno_decoded_len_*` give the exact length, at the cost of a pass over the input; `steno_max_*` gives at once a length that is enough for any input of that size:

```c
const char *text = "Grüße aus Zürich";
size_t text_len = strlen(text), n, m;

/* The exact length, then a buffer of that size. */
if (steno_encoded_len_utf8((const uint8_t *)text, text_len, &n) != STENO_OK) return;
uint8_t *bytes = malloc(n);
if (bytes == NULL) return;
steno_encode_utf8((const uint8_t *)text, text_len, bytes, n, &n);
printf("%u Steno bytes for %u UTF-8 bytes\n", (unsigned)n, (unsigned)text_len); /* 16 Steno bytes for 19 UTF-8 bytes */

/* Or a buffer of the maximum length, with no pass over the input first. */
char back[64];
if (steno_max_decoded_len_utf8(n) <= sizeof back &&
    steno_decode_utf8(bytes, n, (uint8_t *)back, sizeof back, &m) == STENO_OK) {
    printf("%.*s\n", (int)m, back); /* Grüße aus Zürich */
}

free(bytes);
```

The same functions exist for UTF-16 (`uint16_t`) and for code points (`uint32_t`).

## Encoding in Pieces

A `steno_encoder` encodes one value whose text arrives in pieces, and a `steno_decoder` decodes Steno bytes that arrive in pieces. The caller owns the object, which is about 1.8 KB: on the stack, in a struct or allocated. A piece may end anywhere, even in the middle of a UTF-8 sequence or of the bytes of one code point, and the result is the same as one call on the whole value:

```c
/* The text arrives in pieces; the first piece ends in the middle of "é" (C3 A9). */
const char *pieces[] = {"Caf\xC3", "\xA9 du Commerce"};
uint8_t out[64];
size_t len = 0, n, i;
steno_encoder enc;

steno_encoder_init(&enc);

for (i = 0; i < 2; i++) {
    if (steno_encoder_feed_utf8(&enc, (const uint8_t *)pieces[i], strlen(pieces[i]), out + len, sizeof out - len, &n) != STENO_OK) return;
    len += n;
}

if (steno_encoder_finish(&enc, out + len, sizeof out - len, &n) != STENO_OK) return;
len += n;
printf("%u bytes\n", (unsigned)len); /* 16 bytes: the same as one call on the whole text */

steno_encoder_reset(&enc); /* the next value starts here */
```

`steno_encoder_finish` and `steno_decoder_finish` end the input but do not start a new value: a later call continues the same one. Call the init or reset function of the object before the next value. The streaming objects are in the C reference implementation only, not in the C library of the Rust reference implementation ([Two Libraries, One Interface](#two-libraries-one-interface)).

## Comparing

Compare Steno bytes with `memcmp` and, when one is a prefix of the other, put the shorter first. That is the code point order of the texts, so the library needs no comparison function of its own:

```c
static int steno_compare(const uint8_t *a, size_t a_len, const uint8_t *b, size_t b_len) {
    int c = memcmp(a, b, a_len < b_len ? a_len : b_len);
    return c != 0 ? c : (a_len > b_len) - (a_len < b_len);
}

uint8_t zug[16], zurich[16];
size_t n1, n2;

steno_encode_utf8((const uint8_t *)"Zug", 3, zug, sizeof zug, &n1);
steno_encode_utf8((const uint8_t *)"Z\xC3\xBCrich", 7, zurich, sizeof zurich, &n2);
printf("%d\n", steno_compare(zug, n1, zurich, n2) < 0); /* 1: "Zug" sorts first, as in code point order */
```

A store that orders keys or `BLOB` values as unsigned bytes keeps Steno bytes in that order.

## Errors by Status

Every function that encodes or decodes returns a `steno_status`, and `steno_status_name` gives its name, such as `"zero-byte"`. On a decoding error, the out length is the byte offset where the bad code point starts:

```c
const uint8_t bad[] = {0x2F, 0x50, 0x00};
uint8_t text[16];
size_t n;

switch (steno_decode_utf8(bad, sizeof bad, text, sizeof text, &n)) {
case STENO_OK:
    printf("%.*s\n", (int)n, (const char *)text);
    break;
case STENO_DESTINATION_TOO_SMALL:
    printf("make the buffer larger\n");
    break;
default: /* a decoding error: n is where the bad code point starts */
    printf("invalid at byte %u\n", (unsigned)n); /* invalid at byte 2 */
    break;
}
```

## Two Libraries, One Interface

The C library of the [Rust reference implementation](rust.md#c-library) has the same one-shot functions as the C reference implementation: the same names, signatures, status values and meaning, and the same library name, `steno`. A program written against these functions builds with either library. Both export the same symbols, so a program links one of them, not both.

- **Choose the C reference implementation** to compile two files with nothing else: no other toolchain, no allocation, any C compiler, and the streaming objects.
- **Choose the Rust implementation's C library** if your project already builds Rust, or links Rust code that uses the crate.

The shared part is every one-shot function: encoding, decoding and the length functions for UTF-8, UTF-16 and code points, `steno_is_ascii_text` and `steno_status_name`. The streaming objects are in the C reference implementation only, in the part of `steno.h` after the "Additions" marker; a program that must link with either library uses only the part before it. The two libraries are tested against each other, call by call.

## What It Guarantees

| Guarantee | What it means |
|---|---|
| No allocation | The library never calls `malloc`; a one-shot call uses about 2 KB of stack |
| Safe from any thread | Every function can be called from many threads at once; a streaming object must not be used by two at once |
| Checked arguments | A NULL pointer with a length, a misaligned `uint16_t` or `uint32_t` pointer, a source and a destination that overlap, an out length that is NULL, misaligned or points into either, or a length no buffer can have give `STENO_INVALID_ARGUMENT` |
| Never past the capacity | No function writes past the destination capacity it is given |
| An error changes nothing | A streaming call that fails leaves the progress of the value as it was, so the caller can retry with a larger destination |
| Text forms | UTF-8 cannot hold a surrogate code point, so decoding a value that has one to UTF-8 gives `STENO_SURROGATE`; UTF-16 and code points hold every value |

The bytes carry no header, so record the name `x-steno` next to the data to say what they are.

## What Is Tested

The C reference implementation passes all the published test vectors and the large vector file, in every form: UTF-8, UTF-16, code points and the streaming objects. An independent adversarial review has checked it. It is fuzzed against a separate reference codec, and its tests run under AddressSanitizer, UndefinedBehaviorSanitizer, MemorySanitizer and ThreadSanitizer. Every build gives the same output: gcc and clang at every optimization level, 32-bit and big-endian machines, ARM under emulation, and builds with and without the vector code (`STENO_NO_SIMD`).

Not tested yet: building with MSVC, and the NEON code on real ARM hardware, which the CI will run on an ARM64 machine.

## Performance

The C reference implementation has vector code for SSE2 on x86 and NEON on little-endian ARM64, chosen at compile time, and a scalar fallback that every other machine uses; `-DSTENO_NO_SIMD` turns the vector code off. It is measured in the native benchmark of the [Rust reference implementation](rust.md#performance), next to the Rust code.

[!include[](../../benchmarks/impl-c.md)]

## More

The [README of the C reference implementation](https://github.com/Singulink/Singulink.Steno/blob/main/Ports/c/README.md) has every example, the text model, the errors, streaming, thread safety and how to run the tests.

## API Reference

The C reference implementation is documented in its header, [steno.h](https://github.com/Singulink/Singulink.Steno/blob/main/Ports/c/steno.h). [API References](../../api/index.md) lists the API reference of every implementation.

</div>
