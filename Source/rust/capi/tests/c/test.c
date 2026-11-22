/*
 * A test of the C interface through steno.h. Build it against the static or the dynamic library and run it; it prints one line per check
 * that fails and exits with 1 if any did.
 */

#include <stdio.h>
#include <string.h>

#include "steno.h"

static int failures = 0;

#define CHECK(cond)                                                    \
    do {                                                               \
        if (!(cond)) {                                                 \
            printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond);     \
            failures++;                                                \
        }                                                              \
    } while (0)

/* A valid vector: UTF-8 text, its UTF-16 form and its Steno bytes. */
static void valid(const char *utf8, const uint16_t *utf16, size_t utf16_len, const uint8_t *bytes, size_t bytes_len) {
    uint8_t buf[256];
    uint16_t units[256];
    size_t n = 12345;
    size_t utf8_len = strlen(utf8);

    /* memcmp is only called with a length above 0: the empty vector passes NULL pointers, which memcmp must not get even with 0. */
    CHECK(steno_encode_utf8((const uint8_t *)utf8, utf8_len, buf, sizeof buf, &n) == STENO_OK);
    CHECK(n == bytes_len && (n == 0 || memcmp(buf, bytes, n) == 0));

    CHECK(steno_encode_utf16(utf16, utf16_len, buf, sizeof buf, &n) == STENO_OK);
    CHECK(n == bytes_len && (n == 0 || memcmp(buf, bytes, n) == 0));

    CHECK(steno_decode_utf8(bytes, bytes_len, buf, sizeof buf, &n) == STENO_OK);
    CHECK(n == utf8_len && (n == 0 || memcmp(buf, utf8, n) == 0));

    CHECK(steno_decode_utf16(bytes, bytes_len, units, 256, &n) == STENO_OK);
    CHECK(n == utf16_len && (n == 0 || memcmp(units, utf16, n * 2) == 0));

    CHECK(steno_encoded_len_utf8((const uint8_t *)utf8, utf8_len, &n) == STENO_OK && n == bytes_len);
    CHECK(steno_encoded_len_utf16(utf16, utf16_len, &n) == STENO_OK && n == bytes_len);
    CHECK(steno_decoded_len_utf8(bytes, bytes_len, &n) == STENO_OK && n == utf8_len);
    CHECK(steno_decoded_len_utf16(bytes, bytes_len, &n) == STENO_OK && n == utf16_len);

    /* Exact destinations work; one element short fails cleanly. */
    CHECK(steno_encode_utf8((const uint8_t *)utf8, utf8_len, buf, bytes_len, &n) == STENO_OK && n == bytes_len);

    if (bytes_len > 0) {
        CHECK(steno_encode_utf8((const uint8_t *)utf8, utf8_len, buf, bytes_len - 1, &n) == STENO_DESTINATION_TOO_SMALL && n == 0);
        CHECK(steno_decode_utf16(bytes, bytes_len, units, utf16_len - 1, &n) == STENO_DESTINATION_TOO_SMALL && n == 0);
    }

    CHECK(bytes_len <= steno_max_encoded_len_utf8(utf8_len));
    CHECK(bytes_len <= steno_max_encoded_len_utf16(utf16_len));
    CHECK(utf8_len <= steno_max_decoded_len_utf8(bytes_len));
    CHECK(utf16_len <= steno_max_decoded_len_utf16(bytes_len));
}

/* An invalid vector: the status and the position of the defect. */
static void invalid(const uint8_t *bytes, size_t len, steno_status expected, size_t position) {
    uint8_t buf[64];
    uint16_t units[64];
    size_t n = 12345;

    if (expected == STENO_SURROGATE_PAIR) {
        /* The UTF-8 decoder meets the high surrogate first, and that has no UTF-8 form. */
        CHECK(steno_decode_utf8(bytes, len, buf, sizeof buf, &n) == STENO_SURROGATE && n == 0);
    } else {
        CHECK(steno_decode_utf8(bytes, len, buf, sizeof buf, &n) == expected && n == position);
    }

    n = 12345;
    CHECK(steno_decode_utf16(bytes, len, units, 64, &n) == expected && n == position);
    n = 12345;
    CHECK(steno_decoded_len_utf16(bytes, len, &n) == expected && n == position);
}

int main(void) {
    /* FORMAT.md 11.3, 11.5, 11.9, 11.10 and 11.11. */
    {
        static const uint16_t u[] = {0x48, 0x69, 0x21, 0x09, 0x4F, 0x4B, 0x0D, 0x0A};
        static const uint8_t b[] = {0x2F, 0x50, 0x08, 0x02, 0x36, 0x32, 0x05, 0x03};
        valid("Hi!\tOK\r\n", u, 8, b, sizeof b);
    }
    {
        static const uint16_t u[] = {0x041C, 0x0438, 0x0440, 0x002C, 0x0020, 0x00AB, 0x0434, 0x0430, 0x00BB, 0x0021};
        static const uint8_t b[] = {0xE9, 0xF4, 0xA2, 0xAA, 0x13, 0x07, 0x66, 0x34, 0x9E, 0x9A, 0x66, 0x44, 0x08};
        valid("\xD0\x9C\xD0\xB8\xD1\x80, \xC2\xAB\xD0\xB4\xD0\xB0\xC2\xBB!", u, 10, b, sizeof b);
    }
    {
        static const uint16_t u[] = {0x6771, 0x4EAC, 0x3067, 0x30C6, 0x30B9, 0x30C8, 0x3092, 0x3059, 0x308B, 0x004F, 0x004B};
        static const uint8_t b[] = {0xF8, 0x50, 0xE9, 0x7C, 0xB4, 0x7A, 0x5F, 0x94, 0x37, 0x56, 0x65, 0x3D, 0x8A, 0x56, 0x88, 0x28, 0x16, 0x32};
        valid("\xE6\x9D\xB1\xE4\xBA\xAC\xE3\x81\xA7\xE3\x83\x86\xE3\x82\xB9\xE3\x83\x88\xE3\x82\x92\xE3\x81\x99\xE3\x82\x8BOK", u, 11, b, sizeof b);
    }
    {
        static const uint16_t u[] = {0xD55C, 0xAD6D, 0xC5B4, 0x0020, 0xBB38, 0xC7A5};
        static const uint8_t b[] = {0xF8, 0xC2, 0x64, 0x7B, 0x7D, 0x94, 0x8C, 0x07, 0x89, 0xB8, 0x96, 0x8D};
        valid("\xED\x95\x9C\xEA\xB5\xAD\xEC\x96\xB4 \xEB\xAC\xB8\xEC\x9E\xA5", u, 6, b, sizeof b);
    }
    {
        static const uint16_t u[] = {0x0048, 0x0069, 0x0020, 0xD83D, 0xDE00, 0xD83D, 0xDC4D};
        static const uint8_t b[] = {0x2F, 0x50, 0x07, 0xF9, 0xF4, 0x58, 0x78, 0xF2, 0x95};
        valid("Hi \xF0\x9F\x98\x80\xF0\x9F\x91\x8D", u, 7, b, sizeof b);
    }
    valid("", NULL, 0, NULL, 0);

    /* Lone surrogates (11.12): UTF-16 both ways; UTF-8 output fails with STENO_SURROGATE at the code point. */
    {
        static const uint16_t u[] = {0x0061, 0xD800, 0x0062, 0xDC00};
        static const uint8_t b[] = {0x48, 0xF8, 0xC5, 0x20, 0x49, 0xF8, 0xC9, 0x40};
        uint8_t buf[64];
        uint16_t units[64];
        size_t n;
        CHECK(steno_encode_utf16(u, 4, buf, sizeof buf, &n) == STENO_OK && n == sizeof b && memcmp(buf, b, n) == 0);
        CHECK(steno_decode_utf16(b, sizeof b, units, 64, &n) == STENO_OK && n == 4 && memcmp(units, u, 8) == 0);
        CHECK(steno_decode_utf8(b, sizeof b, buf, sizeof buf, &n) == STENO_SURROGATE && n == 1);
    }

    /* Invalid data: one vector of each reason. */
    {
        static const uint8_t zero[] = {0x48, 0x00};
        static const uint8_t unused[] = {0x48, 0xFF};
        static const uint8_t truncated[] = {0x48, 0xE9};
        static const uint8_t past[] = {0xF9, 0xFF, 0xFF, 0xFF};
        static const uint8_t pair[] = {0xF8, 0xC5, 0x20, 0xF8, 0xC9, 0x40};
        static const uint8_t trail[] = {0x48, 0xE9, 0x05};
        invalid(zero, 2, STENO_ZERO_BYTE, 1);
        invalid(unused, 2, STENO_UNUSED_LEAD, 1);
        invalid(truncated, 2, STENO_TRUNCATED, 1);
        invalid(past, 4, STENO_PAST_RANGE, 0);
        invalid(pair, 6, STENO_SURROGATE_PAIR, 3);
        invalid(trail, 3, STENO_INVALID_TRAIL, 1);
    }

    /* Invalid UTF-8 input: the position is the length of the valid prefix. */
    {
        uint8_t buf[16];
        size_t n;
        CHECK(steno_encode_utf8((const uint8_t *)"ab\xFF", 3, buf, sizeof buf, &n) == STENO_INVALID_UTF8 && n == 2);
        CHECK(steno_encoded_len_utf8((const uint8_t *)"\xED\xA0\x80", 3, &n) == STENO_INVALID_UTF8 && n == 0);
    }

    /* The first problem met wins: a destination that is full before the bad sequence, then the bad sequence itself. */
    {
        uint8_t buf[16];
        size_t n;
        CHECK(steno_encode_utf8((const uint8_t *)"abcÿ", 4, buf, 2, &n) == STENO_DESTINATION_TOO_SMALL && n == 0);
        CHECK(steno_encode_utf8((const uint8_t *)"abcÿ", 4, buf, 3, &n) == STENO_INVALID_UTF8 && n == 3);
    }

    /* Code points. */
    {
        static const uint32_t cps[] = {0x48, 0x69, 0x20, 0x1F600, 0x1F44D};
        static const uint8_t b[] = {0x2F, 0x50, 0x07, 0xF9, 0xF4, 0x58, 0x78, 0xF2, 0x95};
        static const uint32_t pair[] = {0x61, 0xD800, 0xDC00};
        static const uint32_t big[] = {0x61, 0x110000};
        uint8_t buf[64];
        uint32_t back[16];
        size_t n;
        CHECK(steno_encode_code_points(cps, 5, buf, sizeof buf, &n) == STENO_OK && n == sizeof b && memcmp(buf, b, n) == 0);
        CHECK(steno_decode_code_points(b, sizeof b, back, 16, &n) == STENO_OK && n == 5 && memcmp(back, cps, sizeof cps) == 0);
        CHECK(steno_encoded_len_code_points(cps, 5, &n) == STENO_OK && n == sizeof b);
        CHECK(steno_decoded_len_code_points(b, sizeof b, &n) == STENO_OK && n == 5);
        CHECK(steno_encode_code_points(pair, 3, buf, sizeof buf, &n) == STENO_NOT_A_VALUE && n == 2);
        CHECK(steno_encode_code_points(big, 2, buf, sizeof buf, &n) == STENO_INVALID_CODE_POINT && n == 1);
        CHECK(steno_encode_code_points(cps, 5, buf, 4, &n) == STENO_DESTINATION_TOO_SMALL && n == 0);
        CHECK(steno_max_encoded_len_code_points(5) == 20 && steno_max_decoded_len_code_points(9) == 9);
    }

    /* Invalid arguments. */
    {
        uint8_t buf[16];
        size_t n;
        CHECK(steno_encode_utf8(NULL, 1, buf, sizeof buf, &n) == STENO_INVALID_ARGUMENT);
        CHECK(steno_encode_utf8((const uint8_t *)"a", 1, buf, sizeof buf, NULL) == STENO_INVALID_ARGUMENT);
        CHECK(steno_decode_utf8(buf, 4, buf + 2, 4, &n) == STENO_INVALID_ARGUMENT);
        CHECK(steno_encode_utf16((const uint16_t *)(buf + 1), 2, buf + 8, 8, &n) == STENO_INVALID_ARGUMENT);
        CHECK(!steno_is_ascii_text(NULL, 3));
    }

    /* ASCII text, and the order of the bytes. */
    {
        uint8_t a[64], b[64];
        size_t na, nb;
        CHECK(steno_encode_utf8((const uint8_t *)"Zug", 3, a, sizeof a, &na) == STENO_OK);
        CHECK(steno_encode_utf8((const uint8_t *)"Z\xC3\xBCrich", 7, b, sizeof b, &nb) == STENO_OK);
        CHECK(steno_is_ascii_text(a, na));
        CHECK(!steno_is_ascii_text(b, nb));
        CHECK(memcmp(a, b, na < nb ? na : nb) < 0);
        CHECK(steno_is_ascii_text(NULL, 0));
    }

    CHECK(strcmp(steno_status_name(STENO_PAST_RANGE), "past-range") == 0);
    CHECK(strcmp(steno_status_name(STENO_INVALID_TRAIL), "invalid-trail") == 0);
    CHECK(strcmp(steno_status_name(STENO_NOT_A_VALUE), "not-a-value") == 0);
    CHECK(strcmp(steno_status_name(STENO_INVALID_CODE_POINT), "invalid-code-point") == 0);
    CHECK(strcmp(steno_status_name(42), "unknown") == 0);

    if (failures == 0) {
        printf("C interface: all checks passed\n");
    }

    return failures == 0 ? 0 : 1;
}
