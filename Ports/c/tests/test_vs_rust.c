/*
 * This library against the C library of the Rust implementation, call by call, for every function of the shared part of the interface.
 * This library is linked in; the Rust one is loaded at run time and called through function pointers, so both run in one process on the
 * same arguments. Written by the adversarial reviewer of this library. Optional, as it needs the Rust library built:
 *
 *   cargo build --release -p singulink-steno-capi           (in Ports/rust)
 *   make vs-rust RUST_LIB=../rust/target/release/libsteno.so  (Windows: steno.dll)
 *
 * It prints every difference in status, out_len or output (a few of each kind), then counts, and fails on any difference. The two
 * libraries have the same shared interface, the code point functions and the out_len rules included, so there must be none.
 */

#include "../steno.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <windows.h>
#define LOAD(p) ((void *)LoadLibraryA(p))
#define SYM(h, n) ((void *)GetProcAddress((HMODULE)(h), n))
#else
#include <dlfcn.h>
#define LOAD(p) dlopen(p, RTLD_NOW | RTLD_LOCAL)
#define SYM(h, n) dlsym(h, n)
#endif

typedef steno_status (*enc8_t)(const uint8_t *, size_t, uint8_t *, size_t, size_t *);
typedef steno_status (*enc16_t)(const uint16_t *, size_t, uint8_t *, size_t, size_t *);
typedef steno_status (*dec8_t)(const uint8_t *, size_t, uint8_t *, size_t, size_t *);
typedef steno_status (*dec16_t)(const uint8_t *, size_t, uint16_t *, size_t, size_t *);
typedef steno_status (*len8_t)(const uint8_t *, size_t, size_t *);
typedef steno_status (*len16_t)(const uint16_t *, size_t, size_t *);
typedef size_t (*max_t)(size_t);
typedef bool (*ascii_t)(const uint8_t *, size_t);
typedef const char *(*name_t)(int);
typedef steno_status (*enc32_t)(const uint32_t *, size_t, uint8_t *, size_t, size_t *);
typedef steno_status (*dec32_t)(const uint8_t *, size_t, uint32_t *, size_t, size_t *);
typedef steno_status (*len32_t)(const uint32_t *, size_t, size_t *);

static struct {
    enc8_t encode_utf8;
    enc16_t encode_utf16;
    dec8_t decode_utf8;
    dec16_t decode_utf16;
    len8_t encoded_len_utf8;
    len16_t encoded_len_utf16;
    len8_t decoded_len_utf8;
    len8_t decoded_len_utf16;
    max_t max_e8, max_e16, max_d8, max_d16;
    ascii_t is_ascii;
    name_t name;
    enc32_t encode_cp;
    dec32_t decode_cp;
    len32_t encoded_len_cp;
    len8_t decoded_len_cp;
    max_t max_ecp, max_dcp;
} R;

static unsigned long diffs = 0, calls = 0;
static unsigned long kind_count[64];

static void diff(int kind, const char *what, const char *detail) {
    diffs++;
    if (kind_count[kind]++ < 4) printf("DIFF %s: %s\n", what, detail);
}

static uint64_t rs = 0x1234567;
static uint32_t rnd(uint32_t n) {
    rs ^= rs << 13;
    rs ^= rs >> 7;
    rs ^= rs << 17;
    return n == 0 ? 0 : (uint32_t)(rs % n);
}

static void hex(char *out, const void *p, size_t n) {
    size_t i;
    const uint8_t *b = (const uint8_t *)p;
    out[0] = 0;
    for (i = 0; i < n && i < 24; i++) sprintf(out + 2 * i, "%02X", b[i]);
}

/* Runs one call shape on both libraries with identical buffers (one per library) and compares status, out_len and output. */
#define COMPARE(kind, what, call_c, call_r, out_c, out_r, elem)                                                                         \
    do {                                                                                                                                \
        size_t olc = 0xAAAA, olr = 0xAAAA;                                                                                              \
        steno_status sc, sr;                                                                                                            \
        char det[400], h[64];                                                                                                           \
        sc = call_c;                                                                                                                    \
        sr = call_r;                                                                                                                    \
        calls++;                                                                                                                        \
        if (sc != sr || olc != olr || (sc == STENO_OK && (out_c) != NULL && memcmp((out_c), (out_r), olc * (elem)) != 0)) {             \
            hex(h, in, in_bytes);                                                                                                       \
            snprintf(det, sizeof det, "C %s/%zu, Rust %s/%zu; input %s (%zu bytes), dst_len %zu", steno_status_name(sc), olc,            \
                     steno_status_name(sr), olr, h, (size_t)in_bytes, (size_t)dlen);                                                    \
            /* Kind 40: the documented difference (README): too small before invalid UTF-8. */                                          \
            diff(sc == STENO_DESTINATION_TOO_SMALL && sr == STENO_INVALID_UTF8 ? 40 : (kind), what, det);                               \
        }                                                                                                                               \
    } while (0)

/* Random inputs of each kind. */
static size_t gen_utf8(uint8_t *b, size_t max) {
    size_t n = 0, target = rnd((uint32_t)max);
    while (n + 4 < target) {
        uint32_t k = rnd(10), c;
        if (k < 3) c = 0x20 + rnd(0x5F);
        else if (k < 5) c = 0x80 + rnd(0x780);
        else if (k < 7) c = 0x800 + rnd(0xF800);
        else if (k < 8) c = 0x10000 + rnd(0x100000);
        else c = 0xFFFFFFFF;
        if (c >= 0xD800 && c <= 0xDFFF) c = 0x41;
        if (c == 0xFFFFFFFF) {
            b[n++] = (uint8_t)rnd(256); /* a random byte: often invalid */
        } else if (c < 0x80) {
            b[n++] = (uint8_t)c;
        } else if (c < 0x800) {
            b[n++] = (uint8_t)(0xC0 | (c >> 6));
            b[n++] = (uint8_t)(0x80 | (c & 0x3F));
        } else if (c < 0x10000) {
            b[n++] = (uint8_t)(0xE0 | (c >> 12));
            b[n++] = (uint8_t)(0x80 | ((c >> 6) & 0x3F));
            b[n++] = (uint8_t)(0x80 | (c & 0x3F));
        } else {
            b[n++] = (uint8_t)(0xF0 | (c >> 18));
            b[n++] = (uint8_t)(0x80 | ((c >> 12) & 0x3F));
            b[n++] = (uint8_t)(0x80 | ((c >> 6) & 0x3F));
            b[n++] = (uint8_t)(0x80 | (c & 0x3F));
        }
    }
    if (rnd(4) == 0 && n > 0) n -= rnd((uint32_t)(n < 3 ? n : 3)); /* cut a sequence */
    return n;
}

static size_t gen_utf16(uint16_t *u, size_t max) {
    size_t n = rnd((uint32_t)max), i;
    for (i = 0; i < n; i++) {
        uint32_t k = rnd(8);
        u[i] = (uint16_t)(k < 3 ? 0x20 + rnd(0x5F) : k < 5 ? 0x400 + rnd(0x80) : k < 6 ? 0xD800 + rnd(0x800) : k < 7 ? 0x4E00 + rnd(0x100) : rnd(0x10000));
    }
    return n;
}

int main(int argc, char **argv) {
    void *h;
    unsigned long cases = argc > 2 ? strtoul(argv[2], NULL, 10) : 200000, t;
    static uint8_t in8[4096], dc[16384], dr[16384], enc[16384];
    static uint16_t in16[2048], d16c[8192], d16r[8192];
    static uint32_t cps32[2048], dc32c[16384], dc32r[16384];
    size_t in_bytes, dlen;
    const void *in;

    if (argc < 2 || (h = LOAD(argv[1])) == NULL) {
        printf("usage: test_vs_rust PATH_TO_RUST_LIBRARY [CASES]\n");
        return 2;
    }
    R.encode_utf8 = (enc8_t)SYM(h, "steno_encode_utf8");
    R.encode_utf16 = (enc16_t)SYM(h, "steno_encode_utf16");
    R.decode_utf8 = (dec8_t)SYM(h, "steno_decode_utf8");
    R.decode_utf16 = (dec16_t)SYM(h, "steno_decode_utf16");
    R.encoded_len_utf8 = (len8_t)SYM(h, "steno_encoded_len_utf8");
    R.encoded_len_utf16 = (len16_t)SYM(h, "steno_encoded_len_utf16");
    R.decoded_len_utf8 = (len8_t)SYM(h, "steno_decoded_len_utf8");
    R.decoded_len_utf16 = (len8_t)SYM(h, "steno_decoded_len_utf16");
    R.max_e8 = (max_t)SYM(h, "steno_max_encoded_len_utf8");
    R.max_e16 = (max_t)SYM(h, "steno_max_encoded_len_utf16");
    R.max_d8 = (max_t)SYM(h, "steno_max_decoded_len_utf8");
    R.max_d16 = (max_t)SYM(h, "steno_max_decoded_len_utf16");
    R.is_ascii = (ascii_t)SYM(h, "steno_is_ascii_text");
    R.name = (name_t)SYM(h, "steno_status_name");
    R.encode_cp = (enc32_t)SYM(h, "steno_encode_code_points");
    R.decode_cp = (dec32_t)SYM(h, "steno_decode_code_points");
    R.encoded_len_cp = (len32_t)SYM(h, "steno_encoded_len_code_points");
    R.decoded_len_cp = (len8_t)SYM(h, "steno_decoded_len_code_points");
    R.max_ecp = (max_t)SYM(h, "steno_max_encoded_len_code_points");
    R.max_dcp = (max_t)SYM(h, "steno_max_decoded_len_code_points");
    if (!R.encode_utf8 || !R.name || !R.is_ascii || !R.max_d16 || !R.decoded_len_utf16 || !R.encode_cp || !R.decode_cp ||
        !R.encoded_len_cp || !R.decoded_len_cp || !R.max_ecp || !R.max_dcp) {
        printf("missing symbols\n");
        return 2;
    }

    /* ---- Argument edge cases ---- */
    {
        static const size_t lens[] = {0, 1, 3, 7};
        size_t big[] = {SIZE_MAX, SIZE_MAX / 2, SIZE_MAX / 2 + 1, SIZE_MAX / 4, SIZE_MAX / 4 + 1, (size_t)PTRDIFF_MAX, (size_t)PTRDIFF_MAX - 1};
        size_t i, j;
        uint8_t *buf8 = (uint8_t *)malloc(256);
        uint16_t *buf16 = (uint16_t *)malloc(256);
        memcpy(buf8, "Hello \xC3\xA9", 8);
        for (i = 0; i < 64; i++) buf16[i] = (uint16_t)(0x41 + i);
        in = buf8;
        in_bytes = 8;

        for (i = 0; i < 4; i++) {
            for (j = 0; j < 4; j++) {
                size_t sl = lens[i];
                dlen = lens[j];
                /* NULL src or dst with these lengths. */
                COMPARE(0, "encode_utf8 NULL src", steno_encode_utf8(NULL, sl, dc, dlen, &olc), R.encode_utf8(NULL, sl, dr, dlen, &olr), dc, dr, 1);
                COMPARE(1, "encode_utf8 NULL dst", steno_encode_utf8(buf8, sl, NULL, dlen, &olc), R.encode_utf8(buf8, sl, NULL, dlen, &olr), NULL, NULL, 1);
                COMPARE(2, "encode_utf16 NULL src", steno_encode_utf16(NULL, sl, dc, dlen, &olc), R.encode_utf16(NULL, sl, dr, dlen, &olr), dc, dr, 1);
                COMPARE(3, "decode_utf8 NULL src", steno_decode_utf8(NULL, sl, dc, dlen, &olc), R.decode_utf8(NULL, sl, dr, dlen, &olr), dc, dr, 1);
                COMPARE(4, "decode_utf16 NULL dst", steno_decode_utf16(buf8, sl, NULL, dlen, &olc), R.decode_utf16(buf8, sl, NULL, dlen, &olr), NULL, NULL, 2);
                COMPARE(5, "decoded_len_utf8 NULL", steno_decoded_len_utf8(NULL, sl, &olc), R.decoded_len_utf8(NULL, sl, &olr), NULL, NULL, 1);
                COMPARE(6, "encoded_len_utf16 NULL", steno_encoded_len_utf16(NULL, sl, &olc), R.encoded_len_utf16(NULL, sl, &olr), NULL, NULL, 1);
                /* Misaligned UTF-16 source and destination. */
                COMPARE(7, "encode_utf16 misaligned src", steno_encode_utf16((const uint16_t *)(const void *)(buf8 + 1), sl, dc, dlen, &olc),
                        R.encode_utf16((const uint16_t *)(const void *)(buf8 + 1), sl, dr, dlen, &olr), dc, dr, 1);
                COMPARE(8, "decode_utf16 misaligned dst", steno_decode_utf16(buf8, sl, (uint16_t *)(void *)(dc + 1), dlen, &olc),
                        R.decode_utf16(buf8, sl, (uint16_t *)(void *)(dr + 1), dlen, &olr), NULL, NULL, 2);
                COMPARE(9, "encoded_len_utf16 misaligned", steno_encoded_len_utf16((const uint16_t *)(const void *)(buf8 + 1), sl, &olc),
                        R.encoded_len_utf16((const uint16_t *)(const void *)(buf8 + 1), sl, &olr), NULL, NULL, 1);
            }
        }

        /* Huge lengths with real pointers (each must be rejected before any access). */
        for (i = 0; i < sizeof big / sizeof big[0]; i++) {
            size_t L = big[i];
            dlen = 4;
            if (L > (size_t)PTRDIFF_MAX / 2) {
                COMPARE(10, "encode_utf16 huge src_len", steno_encode_utf16(buf16, L, dc, 4, &olc), R.encode_utf16(buf16, L, dr, 4, &olr), NULL, NULL, 1);
                COMPARE(11, "encoded_len_utf16 huge", steno_encoded_len_utf16(buf16, L, &olc), R.encoded_len_utf16(buf16, L, &olr), NULL, NULL, 1);
                COMPARE(12, "decode_utf16 huge dst_len", steno_decode_utf16(buf8, 2, d16c, L, &olc), R.decode_utf16(buf8, 2, d16r, L, &olr), NULL, NULL, 2);
            }
            if (L > (size_t)PTRDIFF_MAX) {
                COMPARE(13, "encode_utf8 huge src_len", steno_encode_utf8(buf8, L, dc, 4, &olc), R.encode_utf8(buf8, L, dr, 4, &olr), NULL, NULL, 1);
                COMPARE(14, "decoded_len_utf8 huge", steno_decoded_len_utf8(buf8, L, &olc), R.decoded_len_utf8(buf8, L, &olr), NULL, NULL, 1);
                COMPARE(15, "encode_utf8 huge dst_len", steno_encode_utf8(buf8, 2, dc, L, &olc), R.encode_utf8(buf8, 2, dr, L, &olr), NULL, NULL, 1);
            }
            /* dst_len huge but valid as a size: only as much is written as needed. */
            if (L <= (size_t)PTRDIFF_MAX) {
                COMPARE(16, "encode_utf8 dst_len PTRDIFF_MAX", steno_encode_utf8(buf8, 8, dc, L, &olc), R.encode_utf8(buf8, 8, dr, L, &olr), dc, dr, 1);
            }
        }

        /* Overlap: the same buffer, a partial overlap, adjacent (no overlap), and a destination that ends where the source starts. */
        {
            uint8_t *b = (uint8_t *)malloc(64), *b2 = (uint8_t *)malloc(64);
            size_t off;
            for (off = 0; off < 12; off++) {
                memcpy(b, "Hello world, hello!", 20);
                memcpy(b2, "Hello world, hello!", 20);
                dlen = 8;
                COMPARE(17, "encode_utf8 overlap (dst = src + off)", steno_encode_utf8(b, 8, b + off, 8, &olc), R.encode_utf8(b2, 8, b2 + off, 8, &olr), NULL, NULL, 1);
                memcpy(b, "Hello world, hello!", 20);
                memcpy(b2, "Hello world, hello!", 20);
                COMPARE(18, "encode_utf8 overlap (src = dst + off)", steno_encode_utf8(b + off, 8, b, 8, &olc), R.encode_utf8(b2 + off, 8, b2, 8, &olr), NULL, NULL, 1);
            }
            free(b);
            free(b2);
        }

        /* out_len NULL and misaligned. */
        {
            size_t store[3];
            steno_status sc = steno_encode_utf8(buf8, 3, dc, 10, NULL), sr = R.encode_utf8(buf8, 3, dr, 10, NULL);
            calls++;
            if (sc != sr) diff(19, "out_len NULL", "status");
            sc = steno_encode_utf8(buf8, 3, dc, 10, (size_t *)(void *)((uint8_t *)store + 1));
            sr = R.encode_utf8(buf8, 3, dr, 10, (size_t *)(void *)((uint8_t *)store + 1));
            calls++;
            if (sc != sr) diff(19, "out_len misaligned", "status");
            sc = steno_decoded_len_utf16(buf8, 3, NULL);
            sr = R.decoded_len_utf16(buf8, 3, NULL);
            calls++;
            if (sc != sr) diff(19, "decoded_len out_len NULL", "status");
        }

        /* out_len inside the source or the destination, at every aligned offset, in every form: both libraries refuse it and write
           nothing through it. Each library gets its own copy of the buffers, so that a write by either would show. */
        {
            union {
                size_t a[16];
                uint8_t b[128];
            } sc, sr, dc_, dr_;
            static const char hello[] = "Hello, world! 1234567890 abcdefghijklmnopqrstuvwxyz";
            size_t off;

            for (off = 0; off + sizeof(size_t) <= 64; off += sizeof(size_t)) {
                size_t k;

                for (k = 0; k < 10; k++) {
                    steno_status s1 = STENO_OK, s2 = STENO_OK;
                    size_t *oc, *or_;
                    int in_dst = k >= 5;
                    char d[200];

                    memset(sc.b, 0, sizeof sc.b);
                    memset(sr.b, 0, sizeof sr.b);
                    memcpy(sc.b, hello, 52);
                    memcpy(sr.b, hello, 52);
                    memset(dc_.b, 0x5A, sizeof dc_.b);
                    memset(dr_.b, 0x5A, sizeof dr_.b);
                    if (k % 5 == 2 || k % 5 == 4) {
                        /* Steno bytes as the source. */
                        size_t el;
                        steno_encode_utf8((const uint8_t *)hello, 52, sc.b, 64, &el);
                        memcpy(sr.b, sc.b, 64);
                    }
                    oc = (size_t *)(void *)((in_dst ? dc_.b : sc.b) + off);
                    or_ = (size_t *)(void *)((in_dst ? dr_.b : sr.b) + off);

                    switch (k % 5) {
                    case 0:
                        s1 = steno_encode_utf8(sc.b, 52, dc_.b, 64, oc);
                        s2 = R.encode_utf8(sr.b, 52, dr_.b, 64, or_);
                        break;
                    case 1:
                        s1 = steno_encode_utf16((const uint16_t *)(const void *)sc.b, 26, dc_.b, 64, oc);
                        s2 = R.encode_utf16((const uint16_t *)(const void *)sr.b, 26, dr_.b, 64, or_);
                        break;
                    case 2:
                        s1 = steno_decode_utf16(sc.b, 52, (uint16_t *)(void *)dc_.b, 64, oc);
                        s2 = R.decode_utf16(sr.b, 52, (uint16_t *)(void *)dr_.b, 64, or_);
                        break;
                    case 3:
                        s1 = steno_encode_code_points((const uint32_t *)(const void *)sc.b, 13, dc_.b, 64, oc);
                        s2 = R.encode_cp((const uint32_t *)(const void *)sr.b, 13, dr_.b, 64, or_);
                        break;
                    default:
                        s1 = in_dst ? steno_decode_code_points(sc.b, 52, (uint32_t *)(void *)dc_.b, 32, oc) : steno_decoded_len_utf8(sc.b, 52, oc);
                        s2 = in_dst ? R.decode_cp(sr.b, 52, (uint32_t *)(void *)dr_.b, 32, or_) : R.decoded_len_utf8(sr.b, 52, or_);
                        break;
                    }

                    calls++;
                    if (s1 != s2 || *oc != *or_ || memcmp(sc.b, sr.b, 64) != 0) {
                        snprintf(d, sizeof d, "case %u, out_len at %s + %u: C %s/%zu, Rust %s/%zu", (unsigned)(k % 5), in_dst ? "dst" : "src",
                                 (unsigned)off, steno_status_name(s1), *oc, steno_status_name(s2), *or_);
                        diff(23, "out_len inside a buffer", d);
                    }
                    if (s1 != STENO_INVALID_ARGUMENT && (in_dst ? off < 64 : off + sizeof(size_t) <= (k % 5 == 3 ? 52u : 52u))) {
                        snprintf(d, sizeof d, "case %u, out_len at %s + %u: %s, not invalid-argument", (unsigned)(k % 5), in_dst ? "dst" : "src",
                                 (unsigned)off, steno_status_name(s1));
                        diff(24, "out_len inside a buffer accepted", d);
                    }
                }
            }
        }

        /* is_ascii_text, status names, max lengths. */
        {
            int k;
            if (steno_is_ascii_text(NULL, 0) != R.is_ascii(NULL, 0)) diff(20, "is_ascii_text NULL 0", "");
            if (steno_is_ascii_text(NULL, 5) != R.is_ascii(NULL, 5)) diff(20, "is_ascii_text NULL 5", "");
            for (k = -3; k < 20; k++) {
                if (strcmp(steno_status_name(k), R.name(k)) != 0) {
                    char d[100];
                    snprintf(d, sizeof d, "%d: C \"%s\", Rust \"%s\"", k, steno_status_name(k), R.name(k));
                    diff(21, "status_name", d);
                }
            }
            for (i = 0; i < sizeof big / sizeof big[0] + 4; i++) {
                size_t L = i < sizeof big / sizeof big[0] ? big[i] : i * 1000 + 3;
                if (steno_max_encoded_len_utf8(L) != R.max_e8(L) || steno_max_encoded_len_utf16(L) != R.max_e16(L) ||
                    steno_max_decoded_len_utf8(L) != R.max_d8(L) || steno_max_decoded_len_utf16(L) != R.max_d16(L) ||
                    steno_max_encoded_len_code_points(L) != R.max_ecp(L) || steno_max_decoded_len_code_points(L) != R.max_dcp(L)) {
                    char d[100];
                    snprintf(d, sizeof d, "n = %zu", L);
                    diff(22, "max lengths", d);
                }
            }
        }
        free(buf8);
        free(buf16);
    }

    /* ---- Random differential ---- */
    for (t = 0; t < cases; t++) {
        uint32_t kind = rnd(4);
        size_t n, elen = 0, m;

        if (kind == 0) {
            /* UTF-8 input (valid or not), every destination size class. */
            n = gen_utf8(in8, 300);
            in = in8;
            in_bytes = n;
            dlen = rnd(3) == 0 ? rnd((uint32_t)(2 * n + 2)) : 2 * n;
            COMPARE(30, "encode_utf8", steno_encode_utf8(in8, n, dc, dlen, &olc), R.encode_utf8(in8, n, dr, dlen, &olr), dc, dr, 1);
            COMPARE(31, "encoded_len_utf8", steno_encoded_len_utf8(in8, n, &olc), R.encoded_len_utf8(in8, n, &olr), NULL, NULL, 1);
        } else if (kind == 1) {
            n = gen_utf16(in16, 300);
            in = in16;
            in_bytes = 2 * n;
            dlen = rnd(3) == 0 ? rnd((uint32_t)(3 * n + 2)) : 3 * n;
            COMPARE(32, "encode_utf16", steno_encode_utf16(in16, n, dc, dlen, &olc), R.encode_utf16(in16, n, dr, dlen, &olr), dc, dr, 1);
            COMPARE(33, "encoded_len_utf16", steno_encoded_len_utf16(in16, n, &olc), R.encoded_len_utf16(in16, n, &olr), NULL, NULL, 1);
        } else {
            /* Steno bytes: a valid encoding, mutated or cut, or random bytes. */
            n = gen_utf16(in16, 200);
            steno_encode_utf16(in16, n, enc, sizeof enc, &elen);
            if (kind == 3) {
                size_t k, muts = rnd(3);
                for (k = 0; k < muts && elen > 0; k++) {
                    uint32_t how = rnd(4);
                    if (how == 0) enc[rnd((uint32_t)elen)] = (uint8_t)rnd(256);
                    else if (how == 1) elen = rnd((uint32_t)elen);
                    else if (how == 2) enc[rnd((uint32_t)elen)] = (uint8_t)rnd(8);
                    else enc[rnd((uint32_t)elen)] = (uint8_t)(0xF0 + rnd(16));
                }
            }
            in = enc;
            in_bytes = elen;
            m = rnd(4);
            dlen = m == 0 ? rnd((uint32_t)(4 * elen + 2)) : 4 * elen;
            COMPARE(34, "decode_utf8", steno_decode_utf8(enc, elen, dc, dlen, &olc), R.decode_utf8(enc, elen, dr, dlen, &olr), dc, dr, 1);
            dlen = m == 0 ? rnd((uint32_t)(2 * elen + 2)) : 2 * elen;
            COMPARE(35, "decode_utf16", steno_decode_utf16(enc, elen, d16c, dlen, &olc), R.decode_utf16(enc, elen, d16r, dlen, &olr), d16c, d16r, 2);
            COMPARE(36, "decoded_len_utf8", steno_decoded_len_utf8(enc, elen, &olc), R.decoded_len_utf8(enc, elen, &olr), NULL, NULL, 1);
            COMPARE(37, "decoded_len_utf16", steno_decoded_len_utf16(enc, elen, &olc), R.decoded_len_utf16(enc, elen, &olr), NULL, NULL, 1);
            dlen = m == 0 ? rnd((uint32_t)(elen + 2)) : elen;
            COMPARE(41, "decode_code_points", steno_decode_code_points(enc, elen, (uint32_t *)(void *)dc32c, dlen, &olc),
                    R.decode_cp(enc, elen, (uint32_t *)(void *)dc32r, dlen, &olr), dc32c, dc32r, 4);
            COMPARE(42, "decoded_len_code_points", steno_decoded_len_code_points(enc, elen, &olc), R.decoded_len_cp(enc, elen, &olr), NULL,
                    NULL, 1);
            {
                /* Code points: the UTF-16 text read as code points, sometimes made not a value or not a code point. */
                size_t i, ncp = 0;
                for (i = 0; i < n; i++) cps32[ncp++] = in16[i];
                if (ncp > 0 && rnd(4) == 0) cps32[rnd((uint32_t)ncp)] = rnd(2) ? 0x110000u + rnd(1000) : 0xDC00u;
                in = cps32;
                in_bytes = 4 * ncp;
                dlen = m == 0 ? rnd((uint32_t)(4 * ncp + 2)) : 4 * ncp;
                COMPARE(43, "encode_code_points", steno_encode_code_points(cps32, ncp, dc, dlen, &olc), R.encode_cp(cps32, ncp, dr, dlen, &olr),
                        dc, dr, 1);
                COMPARE(44, "encoded_len_code_points", steno_encoded_len_code_points(cps32, ncp, &olc), R.encoded_len_cp(cps32, ncp, &olr),
                        NULL, NULL, 1);
            }
            if (steno_is_ascii_text(enc, elen) != R.is_ascii(enc, elen)) diff(38, "is_ascii_text", "");
        }
    }

    {
        int k;
        for (k = 0; k < 64; k++) {
            if (kind_count[k] != 0) printf("  kind %d: %lu differences\n", k, kind_count[k]);
        }
    }
    printf("%lu calls compared, %lu differences\n", calls, diffs);
    return diffs != 0;
}
