/*
 * The examples of README.md. Each function body is the code of one example, as the README shows it; main runs them, captures what they
 * print, and compares it with the output that the README states. Keep the two in step.
 */

#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "steno.h"

static char captured[4096];
static size_t captured_len;

/* The examples print through this, so that main can compare what they print. */
static int capture(const char *fmt, ...) {
    va_list ap;
    int n;

    va_start(ap, fmt);
    n = vsnprintf(captured + captured_len, sizeof captured - captured_len, fmt, ap);
    va_end(ap);
    if (n > 0) captured_len += (size_t)n;
    return n;
}

#define printf capture

/* README: "Encode and decode UTF-8" */
static void example_utf8(void) {
    const char *text = "Москва";
    uint8_t bytes[64];
    char back[64];
    size_t n, m;
    steno_status s;

    s = steno_encode_utf8((const uint8_t *)text, strlen(text), bytes, sizeof bytes, &n);
    printf("%s: %u Steno bytes for %u UTF-8 bytes\n", steno_status_name(s), (unsigned)n, (unsigned)strlen(text));

    s = steno_decode_utf8(bytes, n, (uint8_t *)back, sizeof back, &m);
    printf("%s: %.*s\n", steno_status_name(s), (int)m, back);
}

/* README: "Size the buffers" */
static void example_sizes(void) {
    const char *text = "Grüße aus Zürich";
    size_t len = strlen(text), n;
    uint8_t *bytes = malloc(steno_max_encoded_len_utf8(len));

    if (bytes != NULL && steno_encode_utf8((const uint8_t *)text, len, bytes, steno_max_encoded_len_utf8(len), &n) == STENO_OK) {
        printf("%u bytes, at most %u\n", (unsigned)n, (unsigned)steno_max_encoded_len_utf8(len));
    }

    free(bytes);
}

/* README: "Compare Steno bytes" */
static int steno_compare(const uint8_t *a, size_t a_len, const uint8_t *b, size_t b_len) {
    int c = memcmp(a, b, a_len < b_len ? a_len : b_len);
    return c != 0 ? c : (a_len > b_len) - (a_len < b_len);
}

static void example_order(void) {
    uint8_t zug[16], zurich[16];
    size_t n1, n2;

    steno_encode_utf8((const uint8_t *)"Zug", 3, zug, sizeof zug, &n1);
    steno_encode_utf8((const uint8_t *)"Zürich", 7, zurich, sizeof zurich, &n2);
    printf("%d\n", steno_compare(zug, n1, zurich, n2) < 0); /* "Zug" < "Zürich" in code point order */
}

/* README: "UTF-16 and code points" */
static void example_utf16(void) {
    const uint16_t units[] = {0x0048, 0xD83D, 0xDE00, 0xDC00}; /* "H", U+1F600 as a pair, and a lone low surrogate */
    const uint32_t pair[] = {0xD83D, 0xDE00};
    uint8_t bytes[32], utf8[32];
    uint16_t back[8];
    size_t n, m;
    steno_status s;

    steno_encode_utf16(units, 4, bytes, sizeof bytes, &n);
    s = steno_decode_utf16(bytes, n, back, 8, &m);
    printf("%s: %u units, the same: %d\n", steno_status_name(s), (unsigned)m, memcmp(back, units, sizeof units) == 0);

    /* The lone surrogate has no UTF-8 form: an error, never a replacement. */
    s = steno_decode_utf8(bytes, n, utf8, sizeof utf8, &m);
    printf("%s at byte %u\n", steno_status_name(s), (unsigned)m);

    /* As code points, a high surrogate directly followed by a low one is not a value. */
    s = steno_encode_code_points(pair, 2, bytes, sizeof bytes, &n);
    printf("%s at index %u\n", steno_status_name(s), (unsigned)n);
}

/* README: "Errors" */
static void example_errors(void) {
    const uint8_t bad[] = {0x2F, 0x50, 0x00};
    uint8_t text[16];
    size_t n;
    steno_status s = steno_decode_utf8(bad, sizeof bad, text, sizeof text, &n);

    printf("%s at byte %u\n", steno_status_name(s), (unsigned)n);
}

/* README: "Streaming" */
static void example_streaming(void) {
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
    printf("%d\n", len == wn && memcmp(out, whole, len) == 0);
}

static void example_streaming_decode(void) {
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
}

/* README: "ASCII text" */
static void example_ascii(void) {
    uint8_t a[32], b[32];
    size_t na, nb;

    steno_encode_utf8((const uint8_t *)"Hello\tworld\n", 12, a, sizeof a, &na);
    steno_encode_utf8((const uint8_t *)"Gr\xC3\xBC\xC3\x9F" "e", 7, b, sizeof b, &nb);
    printf("%d %d\n", steno_is_ascii_text(a, na), steno_is_ascii_text(b, nb));
}

#undef printf

static int run(const char *name, void (*f)(void), const char *expected) {
    captured_len = 0;
    captured[0] = 0;
    f();

    if (strcmp(captured, expected) != 0) {
        printf("FAIL %s: printed\n%s\nexpected\n%s\n", name, captured, expected);
        return 1;
    }

    return 0;
}

int main(void) {
    int failures = 0;

    failures += run("utf8", example_utf8, "ok: 7 Steno bytes for 12 UTF-8 bytes\nok: \xD0\x9C\xD0\xBE\xD1\x81\xD0\xBA\xD0\xB2\xD0\xB0\n");
    failures += run("sizes", example_sizes, "16 bytes, at most 38\n");
    failures += run("order", example_order, "1\n");
    failures += run("utf16", example_utf16, "ok: 4 units, the same: 1\nsurrogate at byte 4\nnot-a-value at index 1\n");
    failures += run("errors", example_errors, "zero-byte at byte 2\n");
    failures += run("streaming", example_streaming, "1\n");
    failures += run("streaming_decode", example_streaming_decode, "ok: 2 units, U+6771 U+4EAC\n");
    failures += run("ascii", example_ascii, "1 0\n");

    printf(failures == 0 ? "README examples: all passed\n" : "README examples: %d failed\n", failures);
    return failures == 0 ? 0 : 1;
}
