/*
 * A libFuzzer harness for the decoder: any bytes either decode, in every form and in pieces, to a value whose encoding is exactly those
 * bytes, or fail with the same status and position in every form, one-shot and streaming, as the plain reference codec says.
 *
 *   clang -g -O1 -fsanitize=fuzzer,address,undefined tests/fuzz_decode.c tests/ref.c steno.c -o fuzz_decode && ./fuzz_decode -max_total_time=60
 */

#include "../steno.h"

#include <stdlib.h>
#include <string.h>

#include "ref.h"

static void fail(const char *what) {
    (void)what;
    abort();
}

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size);

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size) {
    uint32_t *cps = (uint32_t *)malloc(4 * size + 4), *ref = (uint32_t *)malloc(4 * size + 4);
    uint16_t *units = (uint16_t *)malloc(2 * size * 2 + 2);
    uint8_t *again = (uint8_t *)malloc(4 * size + 4), *u8 = (uint8_t *)malloc(4 * size + 4);
    size_t n = 0, rn = 0, rpos = 0, un = 0, len = 0, i, split = size == 0 ? 0 : data[0] % (size + 1);
    int rs;
    steno_status s, s16, s8;
    steno_decoder d;

    s = steno_decode_code_points(data, size, cps, size, &n);
    rs = ref_decode(data, size, ref, &rn, &rpos);

    if ((int)s != rs) fail("status");

    if (rs == 0) {
        if (n != rn || memcmp(cps, ref, 4 * n) != 0) fail("text");
        if (steno_encode_code_points(cps, n, again, 4 * size + 4, &len) != STENO_OK || len != size || memcmp(again, data, size) != 0) {
            fail("re-encode");
        }
    } else if (n != rpos) {
        fail("position");
    }

    /* UTF-16 agrees with the code points. */
    s16 = steno_decode_utf16(data, size, units, 2 * size, &un);
    if (s16 != s || (s != STENO_OK && un != n)) fail("UTF-16");

    /* UTF-8 agrees, or stops at a surrogate code point. */
    s8 = steno_decode_utf8(data, size, u8, 4 * size, &len);
    if (s8 != s && s8 != STENO_SURROGATE) fail("UTF-8");

    /* In two pieces, split where the first byte says. */
    steno_decoder_init(&d);
    {
        size_t a = 0, b = 0;
        steno_status p = steno_decoder_feed_code_points(&d, data, split, ref, size, &a);

        if (p == STENO_OK) p = steno_decoder_feed_code_points(&d, data + split, size - split, ref + a, size - a, &b);
        if (p == STENO_OK) p = steno_decoder_finish(&d, &b);
        else b = a + b;

        if (p != s) fail("streaming status");
        if (p == STENO_OK && memcmp(ref, cps, 4 * n) != 0) fail("streaming text");
    }

    /* is_ascii_text from the bytes alone agrees with the text. */
    {
        int ascii = rs == 0;
        for (i = 0; i < n && ascii; i++) ascii = cps[i] == 9 || cps[i] == 10 || cps[i] == 13 || (cps[i] >= 0x20 && cps[i] <= 0x7E);
        if (steno_is_ascii_text(data, size) != (ascii != 0)) fail("is_ascii_text");
    }

    free(cps);
    free(ref);
    free(units);
    free(again);
    free(u8);
    return 0;
}
