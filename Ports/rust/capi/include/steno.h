/*
 * steno.h: the C interface of the Rust reference implementation of the Steno Encoding.
 *
 * The Steno Encoding, version 1, is a compact encoding of Unicode text whose bytes sort like the text. It is specified in FORMAT.md:
 * https://github.com/Singulink/Singulink.Steno/blob/main/FORMAT.md
 *
 * Link with the static library (libsteno.a, steno.lib) or the dynamic library (libsteno.so, libsteno.dylib, steno.dll) that the crate
 * singulink-steno-capi builds.
 *
 * Every declaration of this header is also in the header of the C reference implementation of the Steno Encoding (Ports/c/steno.h), with
 * the same signature, status values and meaning, so that a program that uses only these declarations can link with either library. That
 * header adds streaming objects and a version number, which this interface does not have yet.
 *
 * THE ABI IS NOT STABLE YET. Before version 1.0 of this library, functions and their signatures may change between versions. Rebuild
 * against the header of the library version you link with. The values of steno_status are stable already: they never change, and new
 * values are only added at the end.
 *
 * Conventions of every function:
 *
 * - Text and bytes are passed as a pointer and a length, counted in elements (bytes for UTF-8 and Steno, 16-bit code units for UTF-16,
 *   32-bit values for code points). A pointer may be NULL when its length is 0. The pointer must point to that many readable (source) or
 *   writable (destination) elements. A uint16_t or uint32_t pointer must be aligned for its type. The source and the destination must
 *   not overlap.
 * - The result is a steno_status. The produced length is written through out_len: the number of elements written (or, for the
 *   *_len functions, that would be written) on STENO_OK; the byte offset in src where the code point that broke a rule starts on a decode
 *   error (STENO_ZERO_BYTE to STENO_SURROGATE_PAIR, STENO_INVALID_TRAIL, and STENO_SURROGATE); the length of the valid UTF-8 at the start
 *   of src on STENO_INVALID_UTF8; the index of the element on STENO_NOT_A_VALUE and STENO_INVALID_CODE_POINT; and 0 on every other
 *   status. out_len must not be NULL, and must not point into the source or the destination: either fails with STENO_INVALID_ARGUMENT,
 *   and then nothing is written through it.
 * - The contents of the destination after the produced length are unspecified, and on an error all of them are. No function writes
 *   past dst + dst_len.
 * - Each call encodes or decodes one whole value, from the initial state. The library never allocates memory that the caller must free;
 *   it keeps a few layouts in memory of its own (see the README of the Rust implementation).
 * - Every function is safe to call from any thread at the same time. Each thread keeps a small bounded cache of its own.
 * - When several problems are present, the status is the first one met, reading the source from the start. For example, a destination
 *   that is too short is reported even when the source is invalid after the point where the destination is full, and invalid input is
 *   reported when the encoding of all the input before it fits.
 *
 * Comparing Steno bytes: compare them as unsigned bytes with memcmp, and the shorter string first when one is a prefix of the other.
 * That gives the code point order of the texts. The library has no comparison function, as memcmp is all that is needed.
 *
 * The bytes carry no header or version marker: store the name "x-steno" next to them. The encoding is stateful: two encoded values cannot
 * be joined by joining their bytes, and decoding must start at the first byte of a value. The byte 00 never occurs, so it can end a value.
 */

#ifndef STENO_H
#define STENO_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* The result of a function. */
typedef enum steno_status {
    /* Success. */
    STENO_OK = 0,
    /* The Steno input has a byte 00 (FORMAT.md 10.1, rule 1, zero-byte). */
    STENO_ZERO_BYTE = 1,
    /* The Steno input has a lead byte that the layout of the current state does not use (rule 2, unused-lead). */
    STENO_UNUSED_LEAD = 2,
    /* The Steno input ends in the middle of the encoding of a code point (rule 3, truncated). */
    STENO_TRUNCATED = 3,
    /* The Steno input has a code point past the last code point of its range (rule 4, past-range). */
    STENO_PAST_RANGE = 4,
    /* The Steno input has a low surrogate code point directly after a high surrogate code point (rule 5, surrogate-pair). */
    STENO_SURROGATE_PAIR = 5,
    /* The destination is too short. The steno_max_* functions give a length that is always enough. */
    STENO_DESTINATION_TOO_SMALL = 6,
    /* The UTF-8 input is not valid UTF-8. */
    STENO_INVALID_UTF8 = 7,
    /* The Steno input is valid, but the value has a surrogate code point (U+D800..U+DFFF), which has no UTF-8 form. Decode it to UTF-16. */
    STENO_SURROGATE = 8,
    /* A pointer is NULL while its length is not 0, a uint16_t or uint32_t pointer is not aligned, the source and the destination
       overlap, out_len is NULL or points into the source or the destination, or a length is larger than any buffer can be. */
    STENO_INVALID_ARGUMENT = 9,
    /* The Steno input has a byte 01..07 where a trail byte is expected; trail bytes are 08..FF (rule 6, invalid-trail). */
    STENO_INVALID_TRAIL = 10,
    /* The code points are not a value: a high surrogate code point (U+D800..U+DBFF) directly followed by a low one (U+DC00..U+DFFF)
       (FORMAT.md 3). Combine such a pair into one code point first. */
    STENO_NOT_A_VALUE = 11,
    /* A uint32_t in the code point input is above 0x10FFFF, so it is not a code point. */
    STENO_INVALID_CODE_POINT = 12
} steno_status;

/* Encodes UTF-8 text as one value. A byte order mark at the start is the character U+FEFF and is encoded as such. Text that is not
   valid UTF-8 (also the UTF-8 form of a surrogate, ED A0 80, and anything else that is not a Unicode scalar value) fails with
   STENO_INVALID_UTF8 at the first bad sequence; nothing is replaced. */
steno_status steno_encode_utf8(const uint8_t *src, size_t src_len, uint8_t *dst, size_t dst_len, size_t *out_len);

/* Encodes UTF-16 text as one value. Unpaired surrogates are encoded as code points of their own, so every input is valid. */
steno_status steno_encode_utf16(const uint16_t *src, size_t src_len, uint8_t *dst, size_t dst_len, size_t *out_len);

/* Decodes one value to UTF-8. Fails with STENO_SURROGATE if the value has a surrogate code point. */
steno_status steno_decode_utf8(const uint8_t *src, size_t src_len, uint8_t *dst, size_t dst_len, size_t *out_len);

/* Decodes one value to UTF-16. Every valid value can be decoded: a surrogate code point becomes an unpaired surrogate. */
steno_status steno_decode_utf16(const uint8_t *src, size_t src_len, uint16_t *dst, size_t dst_len, size_t *out_len);

/* The exact number of Steno bytes of UTF-8 or UTF-16 text, through out_len. */
steno_status steno_encoded_len_utf8(const uint8_t *src, size_t src_len, size_t *out_len);
steno_status steno_encoded_len_utf16(const uint16_t *src, size_t src_len, size_t *out_len);

/* The exact number of UTF-8 bytes or UTF-16 code units of the value in Steno bytes, through out_len. */
steno_status steno_decoded_len_utf8(const uint8_t *src, size_t src_len, size_t *out_len);
steno_status steno_decoded_len_utf16(const uint8_t *src, size_t src_len, size_t *out_len);

/* Maximum lengths, true bounds for every input of the given length. They saturate at SIZE_MAX.
   steno_max_encoded_len_utf8(n)   = n * 2 Steno bytes for n UTF-8 bytes;
   steno_max_encoded_len_utf16(n)  = n * 3 Steno bytes for n UTF-16 code units;
   steno_max_decoded_len_utf8(n)   = n * 4 UTF-8 bytes for n Steno bytes;
   steno_max_decoded_len_utf16(n)  = n * 2 UTF-16 code units for n Steno bytes. */
size_t steno_max_encoded_len_utf8(size_t utf8_len);
size_t steno_max_encoded_len_utf16(size_t utf16_len);
size_t steno_max_decoded_len_utf8(size_t steno_len);
size_t steno_max_decoded_len_utf16(size_t steno_len);

/* True if the value in the Steno bytes has only tab, line feed, carriage return and U+0020..U+007E (the empty value included), decided
   from the bytes alone. False for bytes that are not valid Steno bytes, and for a NULL src with a length that is not 0. */
bool steno_is_ascii_text(const uint8_t *src, size_t src_len);

/* The name of a status, as a static string: "ok", "zero-byte", "unused-lead", "truncated", "past-range", "surrogate-pair",
   "destination-too-small", "invalid-utf8", "surrogate", "invalid-argument", "invalid-trail", "not-a-value", "invalid-code-point", or
   "unknown" for another value. Never NULL. */
const char *steno_status_name(int status);

/* ---------- Code points: every value, with full fidelity ----------

   A code point is a uint32_t from 0 to 0x10FFFF. A surrogate code point (0xD800..0xDFFF) is a code point of its own, but a high
   surrogate code point directly followed by a low one is not a value (FORMAT.md 3). These functions follow the conventions above. */

/* Encodes code points as one value. Fails with STENO_NOT_A_VALUE at the index of the low surrogate of a high surrogate code point
   directly followed by a low one, and with STENO_INVALID_CODE_POINT at the index of a value above 0x10FFFF. */
steno_status steno_encode_code_points(const uint32_t *src, size_t src_len, uint8_t *dst, size_t dst_len, size_t *out_len);

/* Decodes one value to code points. Every valid value can be decoded. */
steno_status steno_decode_code_points(const uint8_t *src, size_t src_len, uint32_t *dst, size_t dst_len, size_t *out_len);

/* The exact number of Steno bytes of the code points, or of code points of the value in Steno bytes, through out_len. */
steno_status steno_encoded_len_code_points(const uint32_t *src, size_t src_len, size_t *out_len);
steno_status steno_decoded_len_code_points(const uint8_t *src, size_t src_len, size_t *out_len);

/* Maximum lengths, true bounds that saturate at SIZE_MAX:
   steno_max_encoded_len_code_points(n) = n * 4 Steno bytes for n code points;
   steno_max_decoded_len_code_points(n) = n code points for n Steno bytes. */
size_t steno_max_encoded_len_code_points(size_t code_points);
size_t steno_max_decoded_len_code_points(size_t steno_len);

#ifdef __cplusplus
}
#endif

#endif /* STENO_H */
