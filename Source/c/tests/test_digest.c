/*
 * A digest of everything the library returns for a fixed, seeded set of inputs: every function, every form, valid and invalid input,
 * several destination sizes, and streaming with seeded splits. Every build, on every machine and with every flag (vector or scalar
 * code, big-endian, 32-bit, -funsigned-char, -O3 -flto, ...), must print the same digests, and the test fails if they are not the
 * expected ones below.
 *
 * Phase 1 is the digest of the adversarial review, unchanged: it printed 11a602d56a65a32d in 35 builds (gcc and clang at every
 * optimization level, sanitizers, scalar, i386, AArch64 with NEON, big-endian AArch64, ppc64 and s390x under QEMU, ARMv7, Windows).
 * Phase 2 adds long runs of one script (ASCII, Cyrillic, Greek, kana, Hangul, ideographs, emoji), so that the vector code runs on whole
 * blocks in every form.
 *
 *   test_digest            checks both digests against the expected values
 *   test_digest CASES      prints the digests over CASES cases of phase 1 (no check)
 */

#include "../steno.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static uint64_t H = UINT64_C(1469598103934665603);

static void mix(const void *p, size_t n) {
    const uint8_t *b = (const uint8_t *)p;
    size_t i;
    for (i = 0; i < n; i++) H = (H ^ b[i]) * 1099511628211ull;
}

static void mixu(uint64_t v) {
    uint8_t b[8];
    int i;
    for (i = 0; i < 8; i++) b[i] = (uint8_t)(v >> (8 * i)); /* byte order independent */
    mix(b, 8);
}

static void mix16(const uint16_t *u, size_t n) {
    size_t i;
    for (i = 0; i < n; i++) mixu(u[i]);
}

static void mix32(const uint32_t *u, size_t n) {
    size_t i;
    for (i = 0; i < n; i++) mixu(u[i]);
}

static uint64_t rs = 42;
static uint32_t rnd(uint32_t n) {
    rs ^= rs << 13;
    rs ^= rs >> 7;
    rs ^= rs << 17;
    return n == 0 ? 0 : (uint32_t)(rs % n);
}

static uint32_t gen_cp(void) {
    static uint32_t win = 0x400;
    uint32_t k = rnd(16);
    if (rnd(32) == 0) win = rnd(2) ? 0x80 * rnd(0x220) : 0x40000 + 0x80 * rnd(0x1A00);
    switch (k) {
    case 0: case 1: case 2: case 3: return 0x20 + rnd(0x5F);
    case 4: case 5: case 6: return win + rnd(0x80);
    case 7: return 0x4E00 + rnd(0x5200);
    case 8: return 0x3041 + rnd(0xBC);
    case 9: return 0xAC00 + rnd(0x2BA4);
    case 10: return 0xD800 + rnd(0x800);
    case 11: return rnd(0x110000);
    case 12: return 0x3000 + rnd(0x40);
    case 13: return 0xFF00 + rnd(0x100);
    case 14: return rnd(0x20);
    default: return 0x80 + rnd(0x780);
    }
}

#define EXPECTED_PHASE1 UINT64_C(0x11a602d56a65a32d)
#define EXPECTED_PHASE2 UINT64_C(0x267494de935b2e83)

/* Phase 2: long runs of one script, with a space now and then, in every form, decoded back in every form and mutated. */
static void phase2(void) {
    static const uint32_t base[8] = {0x20, 0x430, 0x3B1, 0x3041, 0x30A1, 0xAC00, 0x4E00, 0x1F600};
    static const uint32_t span[8] = {0x5F, 0x20, 0x18, 0x56, 0x5A, 0x2BA4, 0x5200, 0x50};
    static uint32_t cps[700], dcp[3000];
    static uint16_t u16[1400], d16[3000];
    static uint8_t u8[3000], enc[3000], d8[6000];
    unsigned k;

    for (k = 0; k < 2000; k++) {
        unsigned script = k % 8, gap = 5 + k % 13;
        size_t n = 1 + rnd(600), i, nu, nb, elen = 0, ol;
        steno_status s;

        for (i = 0; i < n; i++) cps[i] = i % gap == gap - 1 ? 0x20 : base[script] + rnd(span[script]);
        if (k % 5 == 0) {
            /* Sometimes a change of state inside the run. The position is drawn first: one statement with both calls would leave the
               order of the draws to the compiler. */
            size_t at = rnd((uint32_t)n);
            cps[at] = gen_cp();
        }
        for (i = 1; i < n; i++) {
            if (cps[i - 1] >= 0xD800 && cps[i - 1] <= 0xDBFF && cps[i] >= 0xDC00 && cps[i] <= 0xDFFF) cps[i] = 0x41;
        }

        s = steno_encode_code_points(cps, n, enc, sizeof enc, &elen);
        mixu(s);
        mixu(elen);
        if (s != STENO_OK) continue;
        mix(enc, elen);

        for (nu = 0, i = 0; i < n; i++) {
            if (cps[i] >= 0x10000) {
                u16[nu++] = (uint16_t)(0xD800 + ((cps[i] - 0x10000) >> 10));
                u16[nu++] = (uint16_t)(0xDC00 + ((cps[i] - 0x10000) & 0x3FF));
            } else {
                u16[nu++] = (uint16_t)cps[i];
            }
        }

        s = steno_encode_utf16(u16, nu, d8, sizeof d8, &ol);
        mixu(s);
        mixu(ol);
        mix(d8, ol);

        for (nb = 0, i = 0; i < n; i++) {
            uint32_t c = cps[i];
            if (c >= 0xD800 && c <= 0xDFFF) c = 0xFFFD;
            if (c < 0x80) {
                u8[nb++] = (uint8_t)c;
            } else if (c < 0x800) {
                u8[nb++] = (uint8_t)(0xC0 | (c >> 6));
                u8[nb++] = (uint8_t)(0x80 | (c & 0x3F));
            } else if (c < 0x10000) {
                u8[nb++] = (uint8_t)(0xE0 | (c >> 12));
                u8[nb++] = (uint8_t)(0x80 | ((c >> 6) & 0x3F));
                u8[nb++] = (uint8_t)(0x80 | (c & 0x3F));
            } else {
                u8[nb++] = (uint8_t)(0xF0 | (c >> 18));
                u8[nb++] = (uint8_t)(0x80 | ((c >> 12) & 0x3F));
                u8[nb++] = (uint8_t)(0x80 | ((c >> 6) & 0x3F));
                u8[nb++] = (uint8_t)(0x80 | (c & 0x3F));
            }
        }

        s = steno_encode_utf8(u8, nb, d8, sizeof d8, &ol);
        mixu(s);
        mixu(ol);
        mix(d8, ol);

        if (k % 3 == 0 && elen > 0) enc[rnd((uint32_t)elen)] = (uint8_t)rnd(256);
        s = steno_decode_code_points(enc, elen, dcp, sizeof dcp / 4, &ol);
        mixu(s);
        mixu(ol);
        if (s == STENO_OK) mix32(dcp, ol);
        s = steno_decode_utf16(enc, elen, d16, sizeof d16 / 2, &ol);
        mixu(s);
        mixu(ol);
        if (s == STENO_OK) mix16(d16, ol);
        s = steno_decode_utf8(enc, elen, d8, sizeof d8, &ol);
        mixu(s);
        mixu(ol);
        if (s == STENO_OK) mix(d8, ol);
        mixu(steno_is_ascii_text(enc, elen));
    }
}

int main(int argc, char **argv) {
    unsigned long cases = argc > 1 ? strtoul(argv[1], NULL, 10) : 20000, t;
    static uint32_t cps[4096], dcp[8192];
    static uint16_t u16[8192], d16[8192];
    static uint8_t u8[16384], enc[16384], d8[32768];
    size_t ol;

    for (t = 0; t < cases; t++) {
        size_t n = rnd(t % 50 == 0 ? 3000 : 200), i, nu = 0, nb = 0, elen = 0, k;
        steno_status s;
        int has_sur = 0;
        size_t runlen = rnd(4) == 0 ? rnd(300) : 0;

        for (i = 0; i < n; i++) {
            if (runlen > 0 && i < runlen) cps[i] = (i & 3) ? 0x430 + rnd(32) : 0x20 + rnd(0x5F);
            else cps[i] = gen_cp();
            if (i > 0 && cps[i - 1] >= 0xD800 && cps[i - 1] <= 0xDBFF && cps[i] >= 0xDC00 && cps[i] <= 0xDFFF) cps[i] = 0x41;
            if (cps[i] >= 0xD800 && cps[i] <= 0xDFFF) has_sur = 1;
        }
        if (rnd(20) == 0 && n > 0) {
            /* Sometimes not a value. The value is drawn before the position: the order that gcc and clang chose for the review's
               one-statement form, so that the digest stays the one of the review. */
            uint32_t v = rnd(2) ? 0x110000 + rnd(1000) : 0xDC00;
            cps[rnd((uint32_t)n)] = v;
        }

        /* Code points, all destination size classes. */
        s = steno_encode_code_points(cps, n, enc, sizeof enc, &elen);
        mixu(s);
        mixu(elen);
        if (s == STENO_OK) mix(enc, elen);
        s = steno_encoded_len_code_points(cps, n, &ol);
        mixu(s);
        mixu(ol);
        if (elen > 0) {
            s = steno_encode_code_points(cps, n, enc + 8192, rnd((uint32_t)elen), &ol);
            mixu(s);
            mixu(ol);
        }
        if (s != STENO_OK && elen == 0) continue;
        s = steno_encode_code_points(cps, n, enc, sizeof enc, &elen);
        if (s != STENO_OK) {
            mixu(s);
            mixu(elen);
            continue;
        }

        /* UTF-16 and UTF-8. */
        for (i = 0; i < n; i++) {
            if (cps[i] >= 0x10000) {
                u16[nu++] = (uint16_t)(0xD800 + ((cps[i] - 0x10000) >> 10));
                u16[nu++] = (uint16_t)(0xDC00 + ((cps[i] - 0x10000) & 0x3FF));
            } else {
                u16[nu++] = (uint16_t)cps[i];
            }
        }
        s = steno_encode_utf16(u16, nu, d8, sizeof d8, &ol);
        mixu(s);
        mixu(ol);
        mix(d8, ol);
        s = steno_encoded_len_utf16(u16, nu, &ol);
        mixu(s);
        mixu(ol);
        if (!has_sur) {
            for (i = 0; i < n; i++) {
                uint32_t c = cps[i];
                if (c < 0x80) u8[nb++] = (uint8_t)c;
                else if (c < 0x800) {
                    u8[nb++] = (uint8_t)(0xC0 | (c >> 6));
                    u8[nb++] = (uint8_t)(0x80 | (c & 0x3F));
                } else if (c < 0x10000) {
                    u8[nb++] = (uint8_t)(0xE0 | (c >> 12));
                    u8[nb++] = (uint8_t)(0x80 | ((c >> 6) & 0x3F));
                    u8[nb++] = (uint8_t)(0x80 | (c & 0x3F));
                } else {
                    u8[nb++] = (uint8_t)(0xF0 | (c >> 18));
                    u8[nb++] = (uint8_t)(0x80 | ((c >> 12) & 0x3F));
                    u8[nb++] = (uint8_t)(0x80 | ((c >> 6) & 0x3F));
                    u8[nb++] = (uint8_t)(0x80 | (c & 0x3F));
                }
            }
            if (rnd(8) == 0 && nb > 0) u8[rnd((uint32_t)nb)] = (uint8_t)rnd(256); /* sometimes invalid UTF-8 */
            s = steno_encode_utf8(u8, nb, d8, sizeof d8, &ol);
            mixu(s);
            mixu(ol);
            if (s == STENO_OK) mix(d8, ol);
            s = steno_encoded_len_utf8(u8, nb, &ol);
            mixu(s);
            mixu(ol);
        }

        /* Decode the encoding, maybe mutated, in every form. */
        if (rnd(3) == 0 && elen > 0) {
            for (k = rnd(3) + 1; k > 0; k--) {
                uint32_t how = rnd(3), p = rnd((uint32_t)elen);
                if (how == 0) enc[p] = (uint8_t)rnd(256);
                else if (how == 1) elen = p;
                else enc[p] = (uint8_t)rnd(8);
                if (elen == 0) break;
            }
        }
        s = steno_decode_code_points(enc, elen, dcp, sizeof dcp / 4, &ol);
        mixu(s);
        mixu(ol);
        if (s == STENO_OK) mix32(dcp, ol);
        s = steno_decode_utf16(enc, elen, d16, sizeof d16 / 2, &ol);
        mixu(s);
        mixu(ol);
        if (s == STENO_OK) mix16(d16, ol);
        s = steno_decode_utf8(enc, elen, d8, sizeof d8, &ol);
        mixu(s);
        mixu(ol);
        if (s == STENO_OK) mix(d8, ol);
        s = steno_decoded_len_utf8(enc, elen, &ol);
        mixu(s);
        mixu(ol);
        s = steno_decoded_len_utf16(enc, elen, &ol);
        mixu(s);
        mixu(ol);
        s = steno_decoded_len_code_points(enc, elen, &ol);
        mixu(s);
        mixu(ol);
        mixu(steno_is_ascii_text(enc, elen));
        if (elen > 0) {
            s = steno_decode_utf16(enc, elen, d16, rnd((uint32_t)elen), &ol);
            mixu(s);
            mixu(ol);
        }

        /* Streaming: decode in seeded pieces, as code points, and encode the UTF-16 in seeded pieces. */
        {
            steno_decoder d;
            steno_encoder e;
            size_t a = 0, pos = 0;
            steno_decoder_init(&d);
            while (a < elen) {
                size_t m = 1 + rnd(7);
                if (m > elen - a) m = elen - a;
                s = steno_decoder_feed_code_points(&d, enc + a, m, dcp + pos, sizeof dcp / 4 - pos, &ol);
                mixu(s);
                mixu(ol);
                if (s != STENO_OK) break;
                pos += ol;
                a += m;
            }
            s = steno_decoder_finish(&d, &ol);
            mixu(s);
            mixu(ol);
            mix32(dcp, pos);

            steno_encoder_init(&e);
            a = 0;
            pos = 0;
            while (a < nu) {
                size_t m = 1 + rnd(5);
                if (m > nu - a) m = nu - a;
                s = steno_encoder_feed_utf16(&e, u16 + a, m, d8 + pos, sizeof d8 - pos, &ol);
                mixu(s);
                mixu(ol);
                pos += ol;
                a += m;
            }
            s = steno_encoder_finish(&e, d8 + pos, 3, &ol);
            mixu(s);
            mixu(ol);
            mix(d8, pos + ol);
        }
    }

    {
        int k;
        for (k = -1; k < 14; k++) mix(steno_status_name(k), strlen(steno_status_name(k)));
        mixu(steno_max_encoded_len_utf8(12345));
    }

    {
        uint64_t h1 = H;
        int bad;

        H = UINT64_C(1469598103934665603);
        phase2();
        printf("digest: phase 1 %016llx over %lu cases, phase 2 %016llx\n", (unsigned long long)h1, cases, (unsigned long long)H);
        if (argc > 1) return 0;
        bad = h1 != EXPECTED_PHASE1 || H != EXPECTED_PHASE2;
        printf(bad ? "digest: NOT the expected %016llx and %016llx\n" : "digest: as expected\n", (unsigned long long)EXPECTED_PHASE1,
               (unsigned long long)EXPECTED_PHASE2);
        return bad;
    }
}
