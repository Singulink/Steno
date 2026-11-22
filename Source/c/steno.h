/*
 * steno.h: the C reference implementation of the Steno Encoding.
 *
 * The Steno Encoding, version 1, is a compact encoding of Unicode text whose bytes sort like the text. It is specified in FORMAT.md:
 * https://github.com/Singulink/Steno/blob/main/FORMAT.md
 *
 * The library is this header and steno.c. It is C99, needs nothing but the C standard library, and never allocates memory: the caller
 * provides every buffer. Add steno.c to a build, or build the library with the CMakeLists.txt or the Makefile next to this file.
 *
 * Copyright (c) 2025 Singulink and Contributors. MIT License: see LICENSE.md in the root of the repository.
 *
 * The first part of this header (up to "Additions") is also the C interface of the Rust reference implementation of the Steno Encoding
 * (Source/rust/capi/include/steno.h): the same functions, signatures, status values and meaning, so that a program that uses only that part
 * can link with either library. The second part adds what that interface does not have: streaming objects, a version number, and the
 * STENO_API marker for shared libraries.
 *
 * THE ABI IS NOT STABLE YET. Before version 1.0 of this library, functions and their signatures may change between versions. Rebuild
 * against the header of the library version you link with. The values of steno_status are stable already: they never change, and new
 * values are only added at the end.
 *
 * Conventions of every function:
 *
 * - Text and bytes are passed as a pointer and a length, counted in elements (bytes for UTF-8 and Steno, 16-bit code units for UTF-16,
 *   32-bit values for code points). A pointer may be NULL when its length is 0. The pointer must point to that many readable (source) or
 *   writable (destination) elements. A uint16_t or uint32_t pointer must be aligned for its type: the library reads and writes the
 *   code units and code points as uint16_t and uint32_t, and refuses a pointer that is not aligned (STENO_INVALID_ARGUMENT). The source
 *   and the destination must not overlap.
 * - The result is a steno_status. The produced length is written through out_len: the number of elements written (or, for the
 *   *_len functions, that would be written) on STENO_OK; the byte offset in src where the code point that broke a rule starts on a decode
 *   error (STENO_ZERO_BYTE to STENO_SURROGATE_PAIR, STENO_INVALID_TRAIL, and STENO_SURROGATE); the length of the valid UTF-8 at the start
 *   of src on STENO_INVALID_UTF8; the index of the element on STENO_NOT_A_VALUE and STENO_INVALID_CODE_POINT; and 0 on every other
 *   status. out_len must not be NULL, must be aligned for size_t, and must not point into the source or the destination (or, for the
 *   streaming functions, into the object): each of these fails with STENO_INVALID_ARGUMENT, and then nothing is written through it.
 *   Otherwise out_len is written once, as the last thing the call does.
 * - The contents of the destination after the produced length are unspecified, and on an error all of them are. No function writes
 *   past dst + dst_len.
 * - Each one-shot call encodes or decodes one whole value, from the initial state. The library never allocates memory; a one-shot call
 *   uses about 2 KB of stack, most of it a cache of layouts that lives only during the call.
 * - Every function is safe to call from any thread at the same time: the library has no global state that it writes. A streaming
 *   object (steno_encoder, steno_decoder) must not be used by two threads at once.
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

/* (An addition, see "Additions": the Rust header has no such marker. It expands to nothing for a static library, so the declarations
   are the same.) STENO_API marks every function of the library. For a static library or when steno.c is compiled into the program,
   define nothing.
   For a shared library: on Windows, define STENO_BUILD_SHARED when building the DLL (the functions are exported) and STENO_SHARED when
   using it (they are imported); with gcc and clang elsewhere, the functions keep default visibility even when the library is built with
   -fvisibility=hidden. Define STENO_API yourself to override all of this. */
#ifndef STENO_API
#if defined(_WIN32) || defined(__CYGWIN__)
#if defined(STENO_BUILD_SHARED)
#define STENO_API __declspec(dllexport)
#elif defined(STENO_SHARED)
#define STENO_API __declspec(dllimport)
#else
#define STENO_API
#endif
#elif defined(__GNUC__) && __GNUC__ >= 4
#define STENO_API __attribute__((visibility("default")))
#else
#define STENO_API
#endif
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
STENO_API steno_status steno_encode_utf8(const uint8_t *src, size_t src_len, uint8_t *dst, size_t dst_len, size_t *out_len);

/* Encodes UTF-16 text as one value. Unpaired surrogates are encoded as code points of their own, so every input is valid. */
STENO_API steno_status steno_encode_utf16(const uint16_t *src, size_t src_len, uint8_t *dst, size_t dst_len, size_t *out_len);

/* Decodes one value to UTF-8. Fails with STENO_SURROGATE if the value has a surrogate code point. */
STENO_API steno_status steno_decode_utf8(const uint8_t *src, size_t src_len, uint8_t *dst, size_t dst_len, size_t *out_len);

/* Decodes one value to UTF-16. Every valid value can be decoded: a surrogate code point becomes an unpaired surrogate. */
STENO_API steno_status steno_decode_utf16(const uint8_t *src, size_t src_len, uint16_t *dst, size_t dst_len, size_t *out_len);

/* The exact number of Steno bytes of UTF-8 or UTF-16 text, through out_len. */
STENO_API steno_status steno_encoded_len_utf8(const uint8_t *src, size_t src_len, size_t *out_len);
STENO_API steno_status steno_encoded_len_utf16(const uint16_t *src, size_t src_len, size_t *out_len);

/* The exact number of UTF-8 bytes or UTF-16 code units of the value in Steno bytes, through out_len. */
STENO_API steno_status steno_decoded_len_utf8(const uint8_t *src, size_t src_len, size_t *out_len);
STENO_API steno_status steno_decoded_len_utf16(const uint8_t *src, size_t src_len, size_t *out_len);

/* Maximum lengths, true bounds for every input of the given length. They saturate at SIZE_MAX.
   steno_max_encoded_len_utf8(n)   = n * 2 Steno bytes for n UTF-8 bytes;
   steno_max_encoded_len_utf16(n)  = n * 3 Steno bytes for n UTF-16 code units;
   steno_max_decoded_len_utf8(n)   = n * 4 UTF-8 bytes for n Steno bytes;
   steno_max_decoded_len_utf16(n)  = n * 2 UTF-16 code units for n Steno bytes. */
STENO_API size_t steno_max_encoded_len_utf8(size_t utf8_len);
STENO_API size_t steno_max_encoded_len_utf16(size_t utf16_len);
STENO_API size_t steno_max_decoded_len_utf8(size_t steno_len);
STENO_API size_t steno_max_decoded_len_utf16(size_t steno_len);

/* True if the value in the Steno bytes has only tab, line feed, carriage return and U+0020..U+007E (the empty value included), decided
   from the bytes alone. False for bytes that are not valid Steno bytes, and for a NULL src with a length that is not 0. */
STENO_API bool steno_is_ascii_text(const uint8_t *src, size_t src_len);

/* The name of a status, as a static string: "ok", "zero-byte", "unused-lead", "truncated", "past-range", "surrogate-pair",
   "destination-too-small", "invalid-utf8", "surrogate", "invalid-argument", "invalid-trail", "not-a-value", "invalid-code-point", or
   "unknown" for another value. Never NULL. */
STENO_API const char *steno_status_name(int status);

/* ---------- Code points: every value, with full fidelity ----------

   A code point is a uint32_t from 0 to 0x10FFFF. A surrogate code point (0xD800..0xDFFF) is a code point of its own, but a high
   surrogate code point directly followed by a low one is not a value (FORMAT.md 3). These functions follow the conventions above. */

/* Encodes code points as one value. Fails with STENO_NOT_A_VALUE at the index of the low surrogate of a high surrogate code point
   directly followed by a low one, and with STENO_INVALID_CODE_POINT at the index of a value above 0x10FFFF. */
STENO_API steno_status steno_encode_code_points(const uint32_t *src, size_t src_len, uint8_t *dst, size_t dst_len, size_t *out_len);

/* Decodes one value to code points. Every valid value can be decoded. */
STENO_API steno_status steno_decode_code_points(const uint8_t *src, size_t src_len, uint32_t *dst, size_t dst_len, size_t *out_len);

/* The exact number of Steno bytes of the code points, or of code points of the value in Steno bytes, through out_len. */
STENO_API steno_status steno_encoded_len_code_points(const uint32_t *src, size_t src_len, size_t *out_len);
STENO_API steno_status steno_decoded_len_code_points(const uint8_t *src, size_t src_len, size_t *out_len);

/* Maximum lengths, true bounds that saturate at SIZE_MAX:
   steno_max_encoded_len_code_points(n) = n * 4 Steno bytes for n code points;
   steno_max_decoded_len_code_points(n) = n code points for n Steno bytes. */
STENO_API size_t steno_max_encoded_len_code_points(size_t code_points);
STENO_API size_t steno_max_decoded_len_code_points(size_t steno_len);

/* ======================================================================================================================================
 * Additions
 *
 * Everything below is part of the C reference implementation only, not of the C interface of the Rust implementation: the version
 * number and the streaming objects (and STENO_API, defined at the top). A program that must link with either library uses only the
 * declarations above.
 * ====================================================================================================================================== */

/* The version of this library: 1.0.0. */
#define STENO_VERSION_MAJOR 1
#define STENO_VERSION_MINOR 0
#define STENO_VERSION_PATCH 0
#define STENO_VERSION_STRING "1.0.0"

/* ---------- Streaming ----------

   A steno_encoder takes one value in pieces and writes its Steno bytes; a steno_decoder takes the Steno bytes of one value in pieces
   and writes its text. The caller owns the object (on the stack, in a struct, or allocated): the library keeps no pointer to it between
   calls and keeps a small cache of layouts inside it. The object may be copied with memcpy or assignment, which copies the progress.

   - Init: steno_encoder_init or steno_decoder_init prepares an object for a new value. An object filled with zero bytes (for example
     memset, or static storage) is also ready for a new value: the first call initializes it. ("steno_encoder enc = {0};" does that in
     C, but C++ compilers warn about the fields it leaves out; call the init function there.)
   - Any split gives the one-shot result. Over any split of the input, also inside a UTF-8 sequence, a surrogate pair or the bytes of
     one code point, the pieces give exactly the bytes or text of the one-shot functions, or the same error. What a piece ends in is held
     until the next call: up to 3 bytes of a UTF-8 sequence or one high surrogate code unit in an encoder, up to 3 bytes of the encoding
     of a code point in a decoder.
   - Surrogates. A high surrogate code unit and a low one are joined into one code point only when both come in UTF-16 calls
     (steno_encoder_feed_utf16), also across two calls. A high surrogate code point and a low one in any other way (code points, or a
     code point and a code unit) are not a value: STENO_NOT_A_VALUE. Never other bytes.
   - Finish: steno_encoder_finish writes a held high surrogate as a code point of its own, and fails with STENO_INVALID_UTF8 if it holds
     the start of a UTF-8 sequence. steno_decoder_finish fails with STENO_TRUNCATED if it holds the start of the encoding of a code
     point. Finishing does NOT start a new value: a later call continues the same value. Call the init or reset function before the
     next value.
   - An error changes nothing. A call that fails leaves the progress of the value as it was before the call, so the caller can retry
     with a larger destination, or reset. (Only the object's cache of layouts may differ, which changes no result; and an object of
     zero bytes is initialized even by a call that fails.) The destination's contents are unspecified after a failed call.
   - An empty piece (src_len 0) writes nothing and changes nothing. With valid arguments (an out_len, an initialized object) it is
     never an error, whatever the object holds; a NULL or misaligned out_len or an object that is not one still gives
     STENO_INVALID_ARGUMENT.
   - Positions. On an error, out_len is the position of the problem counted in input elements (bytes, code units or code points, as
     each call took them) from the start of the value: the first element after the last init or reset. It saturates at SIZE_MAX.
   - Destination size. A feed call writes at most steno_max_encoded_len_code_points(src_len + 3) bytes (encoder; 4 bytes per element
     covers every form) or steno_max_decoded_len_utf8(src_len + 3) UTF-8 bytes, steno_max_decoded_len_utf16(src_len + 3) code units or
     src_len + 3 code points (decoder), and steno_encoder_finish at most 3 bytes. */

/* Private: the parts of the streaming objects. Their fields may change in any version: use only the functions. */
#define STENO_PRIVATE_MAX_RANGES 26
#define STENO_PRIVATE_LAYOUT_CACHE 8

typedef struct steno_private_layout {
    uint64_t key;
    uint32_t lo[STENO_PRIVATE_MAX_RANGES + 1];
    uint32_t one_lo, one_n, wide_lo, wide_n, wide_r0, punct_lo, punct_n, fw_lo, fw_n;
    uint8_t first[STENO_PRIVATE_MAX_RANGES + 1];
    uint8_t size[STENO_PRIVATE_MAX_RANGES];
    uint8_t nr, ascii_n, one_first, wide_first, wfull_first, wfull_n, punct_first, fw_first;
} steno_private_layout;

typedef struct steno_private_cache {
    steno_private_layout entry[STENO_PRIVATE_LAYOUT_CACHE];
    uint32_t next;
} steno_private_cache;

typedef struct steno_private_state {
    uint32_t main, second, pending;
    uint8_t ctx, slot, conf, last_high;
} steno_private_state;

typedef struct steno_private_progress {
    steno_private_state st;
    size_t fed;
    uint8_t held_n, held_kind, held[4];
    uint16_t held_unit;
} steno_private_progress;

/* An encoder of one value in pieces. About 1.8 KB (most of it the cache of 8 layouts); see "Streaming" above. */
typedef struct steno_encoder {
    uint32_t magic_;
    steno_private_progress progress_;
    steno_private_cache cache_;
} steno_encoder;

/* A decoder of one value in pieces. About 1.8 KB (most of it the cache of 8 layouts); see "Streaming" above. */
typedef struct steno_decoder {
    uint32_t magic_;
    steno_private_progress progress_;
    steno_private_cache cache_;
} steno_decoder;

/* Prepares an encoder for a new value. Does nothing if enc is NULL. */
STENO_API void steno_encoder_init(steno_encoder *enc);

/* Starts a new value: the same as steno_encoder_init. */
STENO_API void steno_encoder_reset(steno_encoder *enc);

/* Encodes the next piece of the value, given as UTF-8 bytes, and writes the Steno bytes of the code points it completes. out_len is the
   number of bytes written. Invalid UTF-8 fails with STENO_INVALID_UTF8 and the position of the bad sequence in the value. */
STENO_API steno_status steno_encoder_feed_utf8(steno_encoder *enc, const uint8_t *src, size_t src_len, uint8_t *dst, size_t dst_len,
                                     size_t *out_len);

/* Encodes the next piece of the value, given as UTF-16 code units. A high surrogate at the end of the piece is held until the next call,
   where a low surrogate code unit at the start of a UTF-16 piece completes the pair. */
STENO_API steno_status steno_encoder_feed_utf16(steno_encoder *enc, const uint16_t *src, size_t src_len, uint8_t *dst, size_t dst_len,
                                      size_t *out_len);

/* Encodes the next piece of the value, given as code points. */
STENO_API steno_status steno_encoder_feed_code_points(steno_encoder *enc, const uint32_t *src, size_t src_len, uint8_t *dst, size_t dst_len,
                                            size_t *out_len);

/* Ends the input: writes a held high surrogate code unit as a code point of its own (at most 3 bytes), or fails with STENO_INVALID_UTF8
   if the encoder holds the start of a UTF-8 sequence. It does not start a new value. */
STENO_API steno_status steno_encoder_finish(steno_encoder *enc, uint8_t *dst, size_t dst_len, size_t *out_len);

/* Prepares a decoder for a new value. Does nothing if dec is NULL. */
STENO_API void steno_decoder_init(steno_decoder *dec);

/* Starts a new value: the same as steno_decoder_init. */
STENO_API void steno_decoder_reset(steno_decoder *dec);

/* Decodes the next piece of the Steno bytes of the value and writes the text of the code points it completes, as UTF-8 bytes, UTF-16
   code units or code points. out_len is the number of elements written. A decoding error gives the position, in the value, of the code
   point that broke the rule. The UTF-8 form fails with STENO_SURROGATE on a surrogate code point. */
STENO_API steno_status steno_decoder_feed_utf8(steno_decoder *dec, const uint8_t *src, size_t src_len, uint8_t *dst, size_t dst_len,
                                     size_t *out_len);
STENO_API steno_status steno_decoder_feed_utf16(steno_decoder *dec, const uint8_t *src, size_t src_len, uint16_t *dst, size_t dst_len,
                                      size_t *out_len);
STENO_API steno_status steno_decoder_feed_code_points(steno_decoder *dec, const uint8_t *src, size_t src_len, uint32_t *dst, size_t dst_len,
                                            size_t *out_len);

/* Ends the input: STENO_OK (out_len 0) if the bytes so far end at the end of a code point, or STENO_TRUNCATED with the position of the
   unfinished code point. It changes nothing and does not start a new value. */
STENO_API steno_status steno_decoder_finish(const steno_decoder *dec, size_t *out_len);

#ifdef __cplusplus
}
#endif

#endif /* STENO_H */
