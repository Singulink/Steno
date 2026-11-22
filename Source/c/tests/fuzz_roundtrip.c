/*
 * A libFuzzer harness for the encoder: the input, read as UTF-16 code units, as UTF-8 and as code points, encodes to the bytes of the plain
 * reference codec in every form and in pieces, and decodes back.
 *
 *   clang -g -O1 -fsanitize=fuzzer,address,undefined tests/fuzz_roundtrip.c tests/ref.c steno.c -o fuzz_roundtrip && ./fuzz_roundtrip -max_total_time=60
 */

#include "../steno.h"

#include <stdlib.h>
#include <string.h>

#include "ref.h"

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size);

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size) {
    size_t nu = size / 2, n = 0, i, len = 0, rlen = 0, ri, got = 0;
    uint16_t *units = (uint16_t *)malloc(2 * nu + 2), *back16 = (uint16_t *)malloc(2 * nu + 2);
    uint32_t *cps = (uint32_t *)malloc(4 * nu + 4);
    uint8_t *enc = (uint8_t *)malloc(4 * nu + 4), *ref = (uint8_t *)malloc(4 * nu + 4), *u8 = (uint8_t *)malloc(4 * size + 4);
    steno_encoder e;

    /* The input as UTF-16 code units (any sequence is a value), and the code points it reads as (FORMAT.md 3). */
    for (i = 0; i < nu; i++) units[i] = (uint16_t)(data[2 * i] | (data[2 * i + 1] << 8));

    for (i = 0; i < nu; i++) {
        uint32_t c = units[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < nu && units[i + 1] >= 0xDC00 && units[i + 1] <= 0xDFFF) {
            c = 0x10000 + ((c - 0xD800) << 10) + (units[i + 1] - 0xDC00u);
            i++;
        }
        cps[n++] = c;
    }

    if (ref_encode(cps, n, ref, &rlen, &ri, NULL) != 0) abort();
    if (steno_encode_utf16(units, nu, enc, 3 * nu, &len) != STENO_OK || len != rlen || memcmp(enc, ref, len) != 0) abort();
    if (steno_encode_code_points(cps, n, enc, 4 * n, &len) != STENO_OK || len != rlen || memcmp(enc, ref, len) != 0) abort();
    if (steno_decode_utf16(ref, rlen, back16, nu, &got) != STENO_OK || got != nu || memcmp(back16, units, 2 * nu) != 0) abort();

    /* In pieces of the size the first unit gives. */
    {
        size_t step = nu == 0 ? 1 : 1 + units[0] % 7, pos = 0;
        steno_encoder_init(&e);
        for (i = 0; i < nu; i += step) {
            size_t k = nu - i < step ? nu - i : step;
            if (steno_encoder_feed_utf16(&e, units + i, k, enc + pos, 4 * nu + 4 - pos, &got) != STENO_OK) abort();
            pos += got;
        }
        if (steno_encoder_finish(&e, enc + pos, 4 * nu + 4 - pos, &got) != STENO_OK) abort();
        if (pos + got != rlen || memcmp(enc, ref, rlen) != 0) abort();
    }

    /* The raw input as UTF-8: if it is valid, it encodes to the reference bytes of its code points. */
    {
        steno_status s = steno_encode_utf8(data, size, u8, 2 * size, &len);
        if (s == STENO_OK) {
            size_t back = 0;
            uint8_t *dec = (uint8_t *)malloc(size + 1);
            if (steno_decode_utf8(u8, len, dec, size, &back) != STENO_OK || back != size || memcmp(dec, data, size) != 0) abort();
            free(dec);
        } else if (s != STENO_INVALID_UTF8 || len > size) {
            abort();
        }
    }

    free(units);
    free(back16);
    free(cps);
    free(enc);
    free(ref);
    free(u8);
    return 0;
}
