/*
 * A differential fuzz harness of the whole interface against the independent oracle (oracle.c, a port of the clean-room
 * ReferenceSteno.cs). The adversarial reviewer of this library wrote it.
 *
 * What it drives that fuzz_decode.c and fuzz_roundtrip.c do not: the code point and UTF-16 functions on generated text (window edges,
 * runs of every length, CJK / Hangul / kana changes, surrogates, values above 0x10FFFF), the exact-length functions against what the
 * encode and decode functions write, destinations of every size from 0 to the exact size with guard bytes on both sides (or exact
 * mallocs, for AddressSanitizer), the streaming objects with arbitrary splits, mixed forms, empty pieces, copies, finish in the middle,
 * failed calls followed by a retry, zero-filled objects and reset, and decoding of mutated encodings.
 *
 * libFuzzer:  clang -g -O1 -fsanitize=fuzzer,address,undefined tests/fuzz_differential.c tests/oracle.c steno.c -o fuzz_differential
 * Standalone: cc -DSTENO_FUZZ_STANDALONE -O2 tests/fuzz_differential.c tests/oracle.c steno.c -o test_differential
 *             ./test_differential [SEED [COUNT]]     (make test runs a small fixed count)
 */

#include "../steno.h"
#include "oracle.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ---------- Input source: the fuzz data, then a generator seeded from it ---------- */

typedef struct {
    const uint8_t *d;
    size_t n, i;
    uint64_t x;
} src_t;

static uint32_t next8(src_t *s) {
    if (s->i < s->n) return s->d[s->i++];
    s->x ^= s->x << 13;
    s->x ^= s->x >> 7;
    s->x ^= s->x << 17;
    return (uint32_t)(s->x >> 24) & 0xFF;
}

static uint32_t next32(src_t *s) {
    uint32_t a = next8(s), b = next8(s), c = next8(s), d = next8(s);
    return a | (b << 8) | (c << 16) | (d << 24);
}

static uint32_t below(src_t *s, uint32_t n) {
    return n == 0 ? 0 : next32(s) % n;
}

static const char *g_what = "";

static void fail_at(const char *file, int line, const char *msg) {
    fprintf(stderr, "FAIL %s:%d [%s] %s\n", file, line, g_what, msg);
    fflush(stderr);
    abort();
}

#define REQUIRE(c, msg)                                                                                                                 \
    do {                                                                                                                                \
        if (!(c)) fail_at(__FILE__, __LINE__, msg);                                                                                     \
    } while (0)

static void *xmalloc(size_t n) {
    void *p = malloc(n == 0 ? 1 : n);
    if (p == NULL) abort();
    return p;
}

/* ---------- Guarded destinations ---------- */

#define GUARD 32
static int g_exact_alloc = 0; /* 1: malloc exactly the bytes, so that AddressSanitizer sees any overflow */

typedef struct {
    uint8_t *base, *p;
    size_t bytes;
} gbuf;

static gbuf gb_new(size_t elems, size_t elem) {
    gbuf g;
    g.bytes = elems * elem;
    if (g_exact_alloc) {
        g.base = (uint8_t *)xmalloc(g.bytes);
        g.p = g.base;
    } else {
        g.base = (uint8_t *)xmalloc(g.bytes + 2 * GUARD);
        memset(g.base, 0xA5, g.bytes + 2 * GUARD);
        g.p = g.base + GUARD;
    }
    return g;
}

static void gb_done(gbuf *g) {
    size_t i;
    if (!g_exact_alloc) {
        for (i = 0; i < GUARD; i++) {
            REQUIRE(g->base[i] == 0xA5, "write before the destination");
            REQUIRE(g->base[GUARD + g->bytes + i] == 0xA5, "write past the destination");
        }
    }
    free(g->base);
}

/* A zero-length destination: NULL or a real pointer. */
static void *dst_ptr(gbuf *g, size_t elems, src_t *s) {
    if (elems == 0 && (next8(s) & 1)) return NULL;
    return g->p;
}

/* ---------- Text forms ---------- */

static size_t to_utf8(const uint32_t *c, size_t n, uint8_t *o) {
    size_t i, k = 0;
    for (i = 0; i < n; i++) {
        uint32_t x = c[i];
        if (x < 0x80) {
            o[k++] = (uint8_t)x;
        } else if (x < 0x800) {
            o[k++] = (uint8_t)(0xC0 | (x >> 6));
            o[k++] = (uint8_t)(0x80 | (x & 0x3F));
        } else if (x < 0x10000) {
            o[k++] = (uint8_t)(0xE0 | (x >> 12));
            o[k++] = (uint8_t)(0x80 | ((x >> 6) & 0x3F));
            o[k++] = (uint8_t)(0x80 | (x & 0x3F));
        } else {
            o[k++] = (uint8_t)(0xF0 | (x >> 18));
            o[k++] = (uint8_t)(0x80 | ((x >> 12) & 0x3F));
            o[k++] = (uint8_t)(0x80 | ((x >> 6) & 0x3F));
            o[k++] = (uint8_t)(0x80 | (x & 0x3F));
        }
    }
    return k;
}

static size_t utf8_len_of(uint32_t x) {
    return x < 0x80 ? 1 : x < 0x800 ? 2 : x < 0x10000 ? 3 : 4;
}

static size_t to_utf16(const uint32_t *c, size_t n, uint16_t *o) {
    size_t i, k = 0;
    for (i = 0; i < n; i++) {
        uint32_t x = c[i];
        if (x >= 0x10000) {
            o[k++] = (uint16_t)(0xD800 + ((x - 0x10000) >> 10));
            o[k++] = (uint16_t)(0xDC00 + ((x - 0x10000) & 0x3FF));
        } else {
            o[k++] = (uint16_t)x;
        }
    }
    return k;
}

static size_t from_utf16(const uint16_t *u, size_t n, uint32_t *o) {
    size_t i, k = 0;
    for (i = 0; i < n; i++) {
        uint32_t c = u[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < n && u[i + 1] >= 0xDC00 && u[i + 1] <= 0xDFFF) {
            c = 0x10000 + ((c - 0xD800) << 10) + (u[i + 1] - 0xDC00u);
            i++;
        }
        o[k++] = c;
    }
    return k;
}

/* Strict UTF-8 (Unicode table 3-7). Returns 1 and the code points, or 0 with *bad the length of the valid prefix. */
static int from_utf8(const uint8_t *b, size_t n, uint32_t *o, size_t *on, size_t *bad) {
    size_t i = 0, k = 0;
    while (i < n) {
        uint32_t b0 = b[i], c, lo = 0x80, hi = 0xBF;
        size_t len, j;
        if (b0 < 0x80) {
            o[k++] = b0;
            i++;
            continue;
        } else if (b0 >= 0xC2 && b0 <= 0xDF) {
            len = 2;
            c = b0 & 0x1F;
        } else if (b0 >= 0xE0 && b0 <= 0xEF) {
            len = 3;
            c = b0 & 0x0F;
            if (b0 == 0xE0) lo = 0xA0;
            if (b0 == 0xED) hi = 0x9F;
        } else if (b0 >= 0xF0 && b0 <= 0xF4) {
            len = 4;
            c = b0 & 0x07;
            if (b0 == 0xF0) lo = 0x90;
            if (b0 == 0xF4) hi = 0x8F;
        } else {
            *bad = i;
            return 0;
        }
        for (j = 1; j < len; j++) {
            uint32_t x;
            if (i + j >= n) {
                *bad = i;
                return 0;
            }
            x = b[i + j];
            if (j == 1 ? (x < lo || x > hi) : (x < 0x80 || x > 0xBF)) {
                *bad = i;
                return 0;
            }
            c = (c << 6) | (x & 0x3F);
        }
        o[k++] = c;
        i += len;
    }
    *on = k;
    return 1;
}

static int is_hi(uint32_t c) {
    return c >= 0xD800 && c <= 0xDBFF;
}

static int is_lo(uint32_t c) {
    return c >= 0xDC00 && c <= 0xDFFF;
}

static int is_sur(uint32_t c) {
    return c >= 0xD800 && c <= 0xDFFF;
}

/* ---------- Text generation ---------- */

static const uint32_t EDGES[] = {
    0x00,    0x08,    0x09,    0x0A,    0x0B,    0x0D,    0x1F,    0x20,    0x40,    0x41,    0x5A,    0x60,    0x7A,    0x7E,
    0x7F,    0x80,    0xBF,    0xC0,    0x13F,   0x140,   0x1BF,   0x1C0,   0x23F,   0x240,   0x2BF,   0x2C0,   0x2FF,   0x300,
    0x36F,   0x370,   0x37F,   0x380,   0x3FF,   0x400,   0x4FF,   0x500,   0x52F,   0x530,   0x58F,   0x590,   0x5FF,   0x600,
    0x7FF,   0x800,   0xE7F,   0xEFF,   0xF00,   0xFFF,   0x1000,  0x1FFF,  0x2000,  0x20CF,  0x20D0,  0x20FF,  0x2100,  0x2FFF,
    0x3000,  0x300F,  0x3010,  0x303F,  0x3040,  0x3041,  0x3096,  0x3097,  0x30A0,  0x30A1,  0x30FA,  0x30FB,  0x30FC,  0x30FD,
    0x30FF,  0x3100,  0x33FF,  0x3400,  0x4DFF,  0x4E00,  0x9FFF,  0xA000,  0xABFF,  0xAC00,  0xD7A3,  0xD7A4,  0xD7FF,  0xD800,
    0xDBFF,  0xDC00,  0xDFFF,  0xE000,  0xFEFF,  0xFF00,  0xFF1F,  0xFF20,  0xFF7F,  0xFF80,  0xFFFE,  0xFFFF,  0x10000, 0x10F7F,
    0x10F80, 0x10FBF, 0x10FC0, 0x1FFFF, 0x20000, 0x2FFFF, 0x3FFFF, 0x40000, 0x10FF7F, 0x10FF80, 0x10FFFF};

/* A random window start (FORMAT.md 4), by sampling a code point. */
static uint32_t rand_window(src_t *s) {
    int a, b;
    for (;;) {
        uint32_t c;
        switch (below(s, 6)) {
        case 0: c = 0xC0 + below(s, 0x3B0); break;
        case 1: c = 0x380 + below(s, 0x1C80); break;
        case 2: c = 0x2100 + below(s, 0x1300); break;
        case 3: c = 0xA000 + below(s, 0xC00); break;
        case 4: c = 0xE000 + below(s, 0x12000); break;
        default: c = 0x40000 + below(s, 0xD0000); break;
        }
        if (orc_window_of((int)c, &a, &b) == 0) return (uint32_t)a;
    }
}

static uint32_t window_end_of(uint32_t w) {
    int a, b;
    orc_window_of((int)w, &a, &b);
    return (uint32_t)b;
}

typedef struct {
    uint32_t wins[6];
    int allow_invalid;
} gen_t;

/* One code point of a category. */
static uint32_t gen_cp(src_t *s, gen_t *g, unsigned cat) {
    switch (cat) {
    case 0: return 0x20 + below(s, 0x5F);
    case 1: {
        static const uint32_t ctl[] = {0x09, 0x0A, 0x0D, 0x00, 0x01, 0x1F, 0x7F, 0x0B};
        return ctl[below(s, 8)];
    }
    case 2: { /* a remembered window, or its neighbours */
        uint32_t w = g->wins[below(s, 6)], e = window_end_of(w);
        uint32_t span = e - w + 1;
        int32_t off = (int32_t)below(s, span + 2 * 260) - 260;
        int64_t c = (int64_t)w + off;
        if (c < 0) c = 0;
        if (c > 0x10FFFF) c = 0x10FFFF;
        return (uint32_t)c;
    }
    case 3: { /* inside a remembered window */
        uint32_t w = g->wins[below(s, 6)];
        return w + below(s, window_end_of(w) - w + 1);
    }
    case 4: return EDGES[below(s, sizeof EDGES / sizeof EDGES[0])];
    case 5: return 0x4E00 + below(s, 0x5200);
    case 6: return 0x3041 + below(s, 0x56);
    case 7: return 0x30A1 + below(s, 0x5C);
    case 8: return 0xAC00 + below(s, 0x2BA4);
    case 9: return 0x3000 + below(s, 0x40);
    case 10: return 0xFF00 + below(s, 0x100);
    case 11: return 0xD800 + below(s, 0x800);
    case 12: return below(s, 0x110000);
    case 13: return 0x20000 + below(s, 0x20000);
    case 14: return 0x10000 + below(s, 0x10000);
    default:
        if (g->allow_invalid && below(s, 4) == 0) return 0x110000 + below(s, 0xFFFFFFFFu - 0x110000);
        return 0x80 + below(s, 0x40);
    }
}

/* Generates up to max code points. With allow_invalid 0, never a high surrogate directly followed by a low one, nor a value above
   0x10FFFF. */
static size_t gen_text(src_t *s, uint32_t *out, size_t max, int allow_invalid) {
    gen_t g;
    size_t n = 0, target = below(s, (uint32_t)max + 1), i;

    g.allow_invalid = allow_invalid;
    for (i = 0; i < 6; i++) g.wins[i] = rand_window(s);

    while (n < target) {
        uint32_t op = next8(s);
        unsigned cat = op % 16;

        if ((op & 0xF0) == 0xF0) {
            /* A run of one category, of any length up to 300, maybe mixed with ASCII. */
            size_t len = below(s, 301), k;
            unsigned mix = next8(s) & 3;
            for (k = 0; k < len && n < target; k++) {
                uint32_t c = (mix == 1 && (k & 1)) || (mix == 2 && below(s, 4) == 0) ? 0x20 + below(s, 0x5F) : gen_cp(s, &g, cat);
                out[n++] = c;
            }
        } else if ((op & 0xF0) == 0xE0) {
            /* A new remembered window. */
            g.wins[below(s, 6)] = rand_window(s);
        } else {
            out[n++] = gen_cp(s, &g, cat);
        }
    }

    if (!allow_invalid) {
        for (i = 1; i < n; i++) {
            if (is_hi(out[i - 1]) && is_lo(out[i])) out[i] = 0x41;
        }
    }

    return n;
}

/* ---------- One-shot checks ---------- */

/* Sizes of destination to try for an exact size e: all from 0 to e if e is small, else a sample, and some larger. */
static size_t pick_sizes(src_t *s, size_t e, size_t *out) {
    size_t n = 0, i;
    if (e <= 40) {
        for (i = 0; i <= e; i++) out[n++] = i;
    } else {
        out[n++] = 0;
        out[n++] = 1;
        out[n++] = e - 1;
        out[n++] = e;
        for (i = 0; i < 4; i++) out[n++] = below(s, (uint32_t)e);
    }
    out[n++] = e + 1 + below(s, 9);
    return n;
}

enum { F_CP = 0, F_U16 = 1, F_U8 = 2 };

static steno_status enc_call(int form, const void *src, size_t n, uint8_t *dst, size_t dn, size_t *ol) {
    switch (form) {
    case F_CP: return steno_encode_code_points((const uint32_t *)src, n, dst, dn, ol);
    case F_U16: return steno_encode_utf16((const uint16_t *)src, n, dst, dn, ol);
    default: return steno_encode_utf8((const uint8_t *)src, n, dst, dn, ol);
    }
}

static steno_status enc_len_call(int form, const void *src, size_t n, size_t *ol) {
    switch (form) {
    case F_CP: return steno_encoded_len_code_points((const uint32_t *)src, n, ol);
    case F_U16: return steno_encoded_len_utf16((const uint16_t *)src, n, ol);
    default: return steno_encoded_len_utf8((const uint8_t *)src, n, ol);
    }
}

static size_t max_enc(int form, size_t n) {
    return form == F_CP ? steno_max_encoded_len_code_points(n) : form == F_U16 ? steno_max_encoded_len_utf16(n) : steno_max_encoded_len_utf8(n);
}

/* Encodes src (n elements of form) with every destination size. want: the oracle status; ref/rlen: its bytes; err: its position; pre: the
   bytes the encoder writes before it meets the error (rlen when want is 0). */
static void check_encode(src_t *s, int form, const void *src, size_t n, int want, const uint8_t *ref, size_t rlen, size_t err, size_t pre) {
    size_t sizes[64], ns, k, ol = 12345;
    steno_status st;

    st = enc_len_call(form, src, n, &ol);
    REQUIRE((int)st == want, "encoded_len status");
    REQUIRE(ol == (want == 0 ? rlen : err), "encoded_len out_len");
    if (want == 0) REQUIRE(rlen <= max_enc(form, n), "max_encoded_len is exceeded");

    ns = pick_sizes(s, want == 0 ? rlen : pre + 1, sizes);
    for (k = 0; k < ns; k++) {
        size_t d = sizes[k];
        gbuf g = gb_new(d, 1);
        ol = 777;
        st = enc_call(form, src, n, (uint8_t *)dst_ptr(&g, d, s), d, &ol);

        if (want == 0) {
            if (d >= rlen) {
                REQUIRE(st == STENO_OK && ol == rlen, "encode status / length");
                REQUIRE(rlen == 0 || memcmp(g.p, ref, rlen) == 0, "encode bytes differ from the oracle");
            } else {
                REQUIRE(st == STENO_DESTINATION_TOO_SMALL && ol == 0, "encode: destination too small expected");
            }
        } else {
            if (d >= pre) {
                REQUIRE((int)st == want && ol == err, "encode: error status / position");
                REQUIRE(pre == 0 || memcmp(g.p, ref, pre) == 0, "encode: bytes before the error");
            } else {
                REQUIRE(st == STENO_DESTINATION_TOO_SMALL && ol == 0, "encode: too small before the error expected");
            }
        }

        gb_done(&g);
    }
}

typedef steno_status (*dec_fn)(const uint8_t *, size_t, void *, size_t, size_t *);

static steno_status dec_call(int form, const uint8_t *src, size_t n, void *dst, size_t dn, size_t *ol) {
    switch (form) {
    case F_CP: return steno_decode_code_points(src, n, (uint32_t *)dst, dn, ol);
    case F_U16: return steno_decode_utf16(src, n, (uint16_t *)dst, dn, ol);
    default: return steno_decode_utf8(src, n, (uint8_t *)dst, dn, ol);
    }
}

static steno_status dec_len_call(int form, const uint8_t *src, size_t n, size_t *ol) {
    switch (form) {
    case F_CP: return steno_decoded_len_code_points(src, n, ol);
    case F_U16: return steno_decoded_len_utf16(src, n, ol);
    default: return steno_decoded_len_utf8(src, n, ol);
    }
}

static size_t elem_of(int form) {
    return form == F_CP ? 4 : form == F_U16 ? 2 : 1;
}

static size_t max_dec(int form, size_t n) {
    return form == F_CP ? steno_max_decoded_len_code_points(n) : form == F_U16 ? steno_max_decoded_len_utf16(n) : steno_max_decoded_len_utf8(n);
}

/* Decodes bytes in one form with every destination size. The oracle's verdict: want (status), err (its position), cps/ncp (the code
   points decoded before the error, or all of them), starts (the byte offset of each). */
static void check_decode_form(src_t *s, int form, const uint8_t *b, size_t n, int want, size_t err, const uint32_t *cps, size_t ncp,
                              const size_t *starts) {
    size_t sizes[64], ns, k, ol, i, exp_n = 0, exp_err = err, ne;
    int exp = want;
    uint8_t *exp_out = (uint8_t *)xmalloc(4 * ncp + 4);
    steno_status st;

    /* What the form gives: the text of the code points, stopping at the first surrogate for UTF-8. */
    for (i = 0; i < ncp; i++) {
        uint32_t c = cps[i];
        if (form == F_U8 && is_sur(c)) {
            exp = STENO_SURROGATE;
            exp_err = starts[i];
            break;
        }
        if (form == F_CP) {
            memcpy(exp_out + 4 * exp_n, &c, 4);
            exp_n++;
        } else if (form == F_U16) {
            uint16_t u[2];
            size_t m = to_utf16(&c, 1, u);
            memcpy(exp_out + 2 * exp_n, u, 2 * m);
            exp_n += m;
        } else {
            exp_n += to_utf8(&c, 1, exp_out + exp_n);
        }
    }

    st = dec_len_call(form, b, n, &ol);
    REQUIRE((int)st == exp, "decoded_len status");
    REQUIRE(ol == (exp == 0 ? exp_n : exp_err), "decoded_len out_len");
    if (exp == 0) REQUIRE(exp_n <= max_dec(form, n), "max_decoded_len is exceeded");

    ne = exp == 0 ? exp_n : exp_n + 1;
    ns = pick_sizes(s, ne, sizes);
    for (k = 0; k < ns; k++) {
        size_t d = sizes[k];
        gbuf g = gb_new(d, elem_of(form));
        ol = 999;
        st = dec_call(form, b, n, dst_ptr(&g, d, s), d, &ol);
        if (d >= exp_n) {
            REQUIRE((int)st == exp, "decode status");
            REQUIRE(ol == (exp == 0 ? exp_n : exp_err), "decode out_len");
            REQUIRE(exp_n == 0 || memcmp(g.p, exp_out, exp_n * elem_of(form)) == 0, "decoded text differs");
        } else {
            REQUIRE(st == STENO_DESTINATION_TOO_SMALL && ol == 0, "decode: too small expected");
        }
        gb_done(&g);
    }

    free(exp_out);
}

/* ---------- Streaming encoder ---------- */

typedef struct {
    steno_encoder *e;
    uint8_t out[1 << 18];
    size_t olen;
    size_t fed; /* elements fed so far in this value */
    int held_high; /* the last element fed was a high surrogate code unit in a UTF-16 call */
} est_t;

static steno_encoder *g_enc_a, *g_enc_b;

/* One feed call with a random destination; on STENO_DESTINATION_TOO_SMALL it retries with enough room. Returns the status of the
   last call; *got the bytes written. */
static steno_status enc_feed_retry(src_t *s, est_t *t, int form, const void *p, size_t n, size_t *got) {
    size_t enough = steno_max_encoded_len_code_points(n + 3), d, ol;
    steno_status st;

    for (;;) {
        gbuf g;
        unsigned r = next8(s) & 7;
        d = r == 0 ? below(s, (uint32_t)enough + 1) : r == 1 ? 0 : enough;
        g = gb_new(d, 1);
        ol = 4242;
        switch (form) {
        case F_CP: st = steno_encoder_feed_code_points(t->e, (const uint32_t *)p, n, (uint8_t *)dst_ptr(&g, d, s), d, &ol); break;
        case F_U16: st = steno_encoder_feed_utf16(t->e, (const uint16_t *)p, n, (uint8_t *)dst_ptr(&g, d, s), d, &ol); break;
        default: st = steno_encoder_feed_utf8(t->e, (const uint8_t *)p, n, (uint8_t *)dst_ptr(&g, d, s), d, &ol); break;
        }
        if (st == STENO_OK) {
            REQUIRE(ol <= d && ol <= enough, "feed wrote more than the documented bound");
            REQUIRE(t->olen + ol <= sizeof t->out, "harness overflow");
            memcpy(t->out + t->olen, g.p, ol);
            t->olen += ol;
            t->fed += n;
            if (n > 0) t->held_high = form == F_U16 && is_hi(((const uint16_t *)p)[n - 1]);
            *got = ol;
            gb_done(&g);
            return st;
        }
        gb_done(&g);
        if (st != STENO_DESTINATION_TOO_SMALL) {
            *got = ol;
            return st;
        }
        REQUIRE(ol == 0, "too small: out_len not 0");
        REQUIRE(d < enough, "too small although the documented bound was given");
        if (n == 0) REQUIRE(0, "an empty piece failed");
    }
}

static steno_status enc_finish_retry(src_t *s, est_t *t, size_t *got) {
    steno_status st;
    size_t ol;
    for (;;) {
        size_t d = (next8(s) & 3) == 0 ? below(s, 4) : 3;
        gbuf g = gb_new(d, 1);
        ol = 31337;
        st = steno_encoder_finish(t->e, (uint8_t *)dst_ptr(&g, d, s), d, &ol);
        if (st == STENO_OK) {
            REQUIRE(ol <= d && ol <= 3, "finish wrote more than 3 bytes");
            REQUIRE(t->held_high ? ol > 0 : ol == 0, "finish: output iff a high surrogate is held");
            memcpy(t->out + t->olen, g.p, ol);
            t->olen += ol;
            t->held_high = 0;
            *got = ol;
            gb_done(&g);
            return st;
        }
        gb_done(&g);
        if (st != STENO_DESTINATION_TOO_SMALL) {
            *got = ol;
            return st;
        }
        REQUIRE(ol == 0 && d < 3, "finish too small");
    }
}

/* Sometimes moves the progress to the other object by copying it. */
static void maybe_copy(src_t *s, est_t *t) {
    if ((next8(s) & 15) == 0) {
        steno_encoder *other = t->e == g_enc_a ? g_enc_b : g_enc_a;
        memcpy(other, t->e, sizeof *other);
        memset(t->e, 0x5A, sizeof *t->e);
        t->e = other;
    }
}

/* Streams a value (cps, a value) in chunks of random forms, split at random, and compares with the oracle bytes. */
static void check_stream_encode(src_t *s, const uint32_t *cps, size_t n, const uint8_t *ref, size_t rlen) {
    static est_t t;
    size_t ci = 0, got;
    steno_status st;

    t.e = g_enc_a;
    t.olen = 0;
    t.fed = 0;
    t.held_high = 0;

    /* A zero-filled object, an initialized one, or one with junk progress that is then reset. */
    switch (next8(s) % 3) {
    case 0: memset(t.e, 0, sizeof *t.e); break;
    case 1: steno_encoder_init(t.e); break;
    default: {
        uint32_t junk[8];
        uint8_t jb[64];
        size_t jn = gen_text(s, junk, 8, 0), k = below(s, 4);
        uint16_t ju[1] = {0xD812};
        steno_encoder_init(t.e);
        (void)steno_encoder_feed_code_points(t.e, junk, jn, jb, sizeof jb, &got);
        if (k == 1) (void)steno_encoder_feed_utf16(t.e, ju, 1, jb, sizeof jb, &got);
        if (k == 2) (void)steno_encoder_feed_utf8(t.e, (const uint8_t *)"\xF0\x9F", 2, jb, sizeof jb, &got);
        steno_encoder_reset(t.e);
        break;
    }
    }

    while (ci < n) {
        size_t len = 1 + below(s, (uint32_t)(n - ci < 64 ? n - ci : 64)), k, has_sur = 0;
        int form = (int)below(s, 3);
        for (k = 0; k < len; k++) has_sur |= is_sur(cps[ci + k]);
        if (form == F_U8 && has_sur) form = F_U16;

        if (form == F_CP) {
            size_t a = 0;
            while (a < len) {
                size_t m = below(s, (uint32_t)(len - a + 1));
                st = enc_feed_retry(s, &t, F_CP, cps + ci + a, m, &got);
                REQUIRE(st == STENO_OK, "stream feed cps");
                a += m;
                maybe_copy(s, &t);
            }
        } else if (form == F_U16) {
            uint16_t u[128];
            size_t nu = to_utf16(cps + ci, len, u), a = 0;
            while (a < nu) {
                size_t m = below(s, (uint32_t)(nu - a + 1));
                st = enc_feed_retry(s, &t, F_U16, u + a, m, &got);
                REQUIRE(st == STENO_OK, "stream feed utf16");
                a += m;
                maybe_copy(s, &t);
                /* finish in the middle: only where it does not split a pair. */
                if ((next8(s) & 7) == 0 && !(a > 0 && a < nu && is_hi(u[a - 1]) && is_lo(u[a]))) {
                    size_t before = t.olen;
                    st = enc_finish_retry(s, &t, &got);
                    REQUIRE(st == STENO_OK, "finish in the middle of UTF-16");
                    REQUIRE(t.olen - before == got, "finish output");
                }
            }
        } else {
            uint8_t b[256];
            size_t nb = to_utf8(cps + ci, len, b), a = 0, held_start = 0;
            size_t *seq = (size_t *)xmalloc((nb + 1) * sizeof(size_t)); /* seq[i]: start of the sequence holding byte i */
            size_t q = 0;
            for (k = 0; k < len; k++) {
                size_t m = utf8_len_of(cps[ci + k]), j;
                for (j = 0; j < m; j++) seq[q + j] = q;
                q += m;
            }
            while (a < nb) {
                size_t m = below(s, (uint32_t)(nb - a + 1));
                size_t fed_before = t.fed;
                st = enc_feed_retry(s, &t, F_U8, b + a, m, &got);
                REQUIRE(st == STENO_OK, "stream feed utf8");
                a += m;
                (void)fed_before;
                maybe_copy(s, &t);
                if ((next8(s) & 7) == 0) {
                    size_t before = t.olen;
                    int mid = a < nb && seq[a] != a;
                    held_start = mid ? t.fed - (a - seq[a]) : 0;
                    st = enc_finish_retry(s, &t, &got);
                    if (mid) {
                        REQUIRE(st == STENO_INVALID_UTF8 && got == held_start, "finish inside a UTF-8 sequence");
                    } else {
                        REQUIRE(st == STENO_OK && t.olen - before == got, "finish between UTF-8 sequences");
                    }
                }
            }
            free(seq);
        }

        /* Empty pieces of any form, any time. */
        if ((next8(s) & 3) == 0) {
            size_t ol = 1;
            uint8_t dd[1];
            REQUIRE(steno_encoder_feed_utf8(t.e, NULL, 0, dd, 0, &ol) == STENO_OK && ol == 0, "empty utf8 piece");
            REQUIRE(steno_encoder_feed_utf16(t.e, NULL, 0, NULL, 0, &ol) == STENO_OK && ol == 0, "empty utf16 piece");
            REQUIRE(steno_encoder_feed_code_points(t.e, NULL, 0, NULL, 0, &ol) == STENO_OK && ol == 0, "empty cp piece");
        }

        ci += len;
    }

    st = enc_finish_retry(s, &t, &got);
    REQUIRE(st == STENO_OK, "final finish");
    REQUIRE(t.olen == rlen && memcmp(t.out, ref, rlen) == 0, "streamed bytes differ from the oracle");

    /* Finish again: nothing more. */
    st = enc_finish_retry(s, &t, &got);
    REQUIRE(st == STENO_OK && got == 0, "second finish");
}

/* Streams code points that are not a value (or have a value above 0x10FFFF) as code point pieces: the call with the bad element fails
   at its position, leaves the object as it was, and the valid part can then be fed. */
static void check_stream_encode_invalid(src_t *s, const uint32_t *cps, size_t n, int want, size_t err) {
    static est_t t;
    static uint8_t ref[1 << 16];
    size_t ci = 0, got, rlen = 0, e2;
    steno_status st;

    REQUIRE(orc_encode(cps, err, ref, &rlen, &e2) == 0, "oracle prefix");
    t.e = g_enc_a;
    t.olen = 0;
    t.fed = 0;
    t.held_high = 0;
    steno_encoder_init(t.e);

    while (ci < n) {
        size_t m = 1 + below(s, (uint32_t)(n - ci));
        st = enc_feed_retry(s, &t, F_CP, cps + ci, m, &got);
        if (ci + m <= err) {
            REQUIRE(st == STENO_OK, "invalid stream: early piece failed");
            ci += m;
            continue;
        }
        REQUIRE((int)st == want && got == err, "invalid stream: status / position");
        /* The same call again fails the same way. */
        st = enc_feed_retry(s, &t, F_CP, cps + ci, m, &got);
        REQUIRE((int)st == want && got == err, "invalid stream: retry");
        /* The valid part of the piece. */
        st = enc_feed_retry(s, &t, F_CP, cps + ci, err - ci, &got);
        REQUIRE(st == STENO_OK, "invalid stream: valid part");
        st = enc_finish_retry(s, &t, &got);
        REQUIRE(st == STENO_OK, "invalid stream: finish");
        REQUIRE(t.olen == rlen && memcmp(t.out, ref, rlen) == 0, "invalid stream: bytes of the valid part");
        return;
    }

    REQUIRE(0, "invalid stream: no call failed");
}

/* ---------- Streaming decoder ---------- */

static steno_decoder *g_dec_a, *g_dec_b;

typedef struct {
    steno_decoder *d;
    uint32_t out[1 << 17];
    size_t ncp;
    size_t fed;
} dst_t;

/* Appends decoded output of a form as code points. */
static void dst_append(dst_t *t, int form, const void *p, size_t n) {
    if (form == F_CP) {
        memcpy(t->out + t->ncp, p, 4 * n);
        t->ncp += n;
    } else if (form == F_U16) {
        t->ncp += from_utf16((const uint16_t *)p, n, t->out + t->ncp);
    } else {
        size_t on = 0, bad = 0;
        REQUIRE(from_utf8((const uint8_t *)p, n, t->out + t->ncp, &on, &bad), "decoder wrote invalid UTF-8");
        t->ncp += on;
    }
}

static steno_status dec_feed_retry(src_t *s, dst_t *t, int form, const uint8_t *p, size_t n, size_t *got) {
    size_t enough = max_dec(form, n + 3), d, ol;
    steno_status st;

    for (;;) {
        gbuf g;
        unsigned r = next8(s) & 7;
        d = r == 0 ? below(s, (uint32_t)enough + 1) : r == 1 ? 0 : enough;
        g = gb_new(d, elem_of(form));
        ol = 4243;
        switch (form) {
        case F_CP: st = steno_decoder_feed_code_points(t->d, p, n, (uint32_t *)dst_ptr(&g, d, s), d, &ol); break;
        case F_U16: st = steno_decoder_feed_utf16(t->d, p, n, (uint16_t *)dst_ptr(&g, d, s), d, &ol); break;
        default: st = steno_decoder_feed_utf8(t->d, p, n, (uint8_t *)dst_ptr(&g, d, s), d, &ol); break;
        }
        if (st == STENO_OK) {
            REQUIRE(ol <= d && ol <= enough, "decoder feed wrote more than the documented bound");
            dst_append(t, form, g.p, ol);
            t->fed += n;
            *got = ol;
            gb_done(&g);
            return st;
        }
        gb_done(&g);
        if (st != STENO_DESTINATION_TOO_SMALL) {
            *got = ol;
            return st;
        }
        REQUIRE(ol == 0 && d < enough && n > 0, "decoder too small");
    }
}

static void maybe_copy_dec(src_t *s, dst_t *t) {
    if ((next8(s) & 15) == 0) {
        steno_decoder *other = t->d == g_dec_a ? g_dec_b : g_dec_a;
        memcpy(other, t->d, sizeof *other);
        memset(t->d, 0x5A, sizeof *t->d);
        t->d = other;
    }
}

/* The oracle's view of bytes: status, error position, the code points before the error (or all), and their starts. */
typedef struct {
    int st;
    size_t err, ncp;
    uint32_t *cps;
    size_t *starts;
} verdict;

static void oracle_verdict(const uint8_t *b, size_t n, verdict *v) {
    size_t on = 0, k;
    uint8_t *re;
    v->cps = (uint32_t *)xmalloc(4 * n + 4);
    v->starts = (size_t *)xmalloc(sizeof(size_t) * (n + 1));
    v->st = orc_decode(b, n, v->cps, &on, &v->err);
    REQUIRE(v->st >= 0, "oracle failed");
    if (v->st != 0) {
        /* The bytes before the error are valid. */
        int s2 = orc_decode(b, v->err, v->cps, &on, &k);
        REQUIRE(s2 == 0, "oracle prefix");
    }
    v->ncp = on;
    re = (uint8_t *)xmalloc(4 * on + 4);
    REQUIRE(orc_encode_starts(v->cps, on, re, &k, &k, v->starts) == 0, "oracle starts");
    free(re);
}

static void free_verdict(verdict *v) {
    free(v->cps);
    free(v->starts);
}

/* The start of the code point that holds byte offset p (p < end of the valid part). */
static size_t cp_start_of(const verdict *v, size_t p) {
    size_t lo = 0, hi = v->ncp;
    while (hi - lo > 1) {
        size_t mid = (lo + hi) / 2;
        if (v->starts[mid] <= p) lo = mid;
        else hi = mid;
    }
    return v->starts[lo];
}

static void check_stream_decode(src_t *s, const uint8_t *b, size_t n, const verdict *v) {
    static dst_t t;
    size_t a = 0, got;
    steno_status st;
    size_t valid_end = v->st == 0 ? n : v->err;

    t.d = g_dec_a;
    t.ncp = 0;
    t.fed = 0;
    if (next8(s) & 1) memset(t.d, 0, sizeof *t.d);
    else steno_decoder_init(t.d);

    while (a < n) {
        size_t m = below(s, (uint32_t)(n - a < 20 ? n - a + 1 : (next8(s) & 1) ? 6 : n - a + 1));
        int form = (int)below(s, 3);
        size_t ncp_before = t.ncp;

        if (m > n - a) m = n - a;
        st = dec_feed_retry(s, &t, form, b + a, m, &got);

        if (st == STENO_SURROGATE) {
            /* The value has a surrogate code point: the object is unchanged; the same piece in UTF-16 goes on. */
            REQUIRE(form == F_U8, "surrogate status from a non-UTF-8 form");
            REQUIRE(t.ncp == ncp_before, "harness");
            {
                size_t j, found = 0, from = a < valid_end ? cp_start_of(v, a) : a;
                for (j = 0; j < v->ncp; j++) {
                    if (is_sur(v->cps[j]) && v->starts[j] >= from) {
                        found = 1;
                        REQUIRE(v->starts[j] == got, "surrogate position is not the first surrogate code point");
                        break;
                    }
                }
                REQUIRE(found, "surrogate status without a surrogate");
            }
            st = dec_feed_retry(s, &t, F_U16, b + a, m, &got);
        }

        if (st != STENO_OK) {
            REQUIRE(v->st != 0, "streaming decode failed on valid bytes");
            REQUIRE((int)st == v->st && got == v->err, "streaming decode: status / position");
            /* Nothing changed: the same call fails again, and the decoded code points are those before the error. */
            {
                size_t got2;
                steno_status st2 = dec_feed_retry(s, &t, F_CP, b + a, m, &got2);
                REQUIRE(st2 == st && got2 == got, "streaming decode: retry");
            }
            REQUIRE(t.ncp <= v->ncp && memcmp(t.out, v->cps, 4 * t.ncp) == 0, "streaming decode: text before the error");
            return;
        }

        a += m;
        maybe_copy_dec(s, &t);

        if ((next8(s) & 7) == 0) {
            /* finish in the middle: truncated iff the bytes so far end inside a code point. */
            size_t ol = 5;
            st = steno_decoder_finish(t.d, &ol);
            if (a < valid_end && cp_start_of(v, a) != a) {
                REQUIRE(st == STENO_TRUNCATED && ol == cp_start_of(v, a), "finish in the middle of a code point");
            } else if (a <= valid_end) {
                REQUIRE(st == STENO_OK && ol == 0, "finish between code points");
            }
        }
    }

    {
        size_t ol = 5;
        st = steno_decoder_finish(t.d, &ol);
        if (v->st == 0) {
            REQUIRE(st == STENO_OK && ol == 0, "final decoder finish");
            REQUIRE(t.ncp == v->ncp && memcmp(t.out, v->cps, 4 * t.ncp) == 0, "streamed text differs from the oracle");
        } else {
            REQUIRE((int)st == v->st && ol == v->err, "streaming decode: the error at finish");
            REQUIRE(t.ncp == v->ncp && memcmp(t.out, v->cps, 4 * t.ncp) == 0, "streaming decode: text before the truncation");
        }
    }
}

/* ---------- Top-level checks ---------- */

static void check_bytes(src_t *s, const uint8_t *b, size_t n) {
    verdict v;
    int f;
    g_what = "bytes";
    oracle_verdict(b, n, &v);

    for (f = 0; f < 3; f++) check_decode_form(s, f, b, n, v.st, v.err, v.cps, v.ncp, v.starts);

    if (v.st == 0) {
        size_t ol;
        uint8_t *re = (uint8_t *)xmalloc(4 * v.ncp + 4);
        REQUIRE(steno_encode_code_points(v.cps, v.ncp, re, 4 * v.ncp, &ol) == STENO_OK && ol == n && memcmp(re, b, n) == 0,
                "accepted bytes re-encode differently");
        free(re);
    }

    {
        int ascii = v.st == 0;
        size_t i;
        for (i = 0; i < v.ncp && ascii; i++) ascii = v.cps[i] == 9 || v.cps[i] == 10 || v.cps[i] == 13 || (v.cps[i] >= 0x20 && v.cps[i] <= 0x7E);
        REQUIRE(steno_is_ascii_text(b, n) == (ascii != 0), "is_ascii_text");
    }

    g_what = "stream decode";
    check_stream_decode(s, b, n, &v);
    free_verdict(&v);
}

static void check_text(src_t *s, const uint32_t *cps, size_t n) {
    uint8_t *ref = (uint8_t *)xmalloc(4 * n + 4);
    size_t rlen = 0, err = 0, pre = 0;
    int os = orc_encode(cps, n, ref, &rlen, &err), i;
    REQUIRE(os >= 0, "oracle encode failed");

    if (os != 0) {
        size_t e2;
        g_what = "invalid code points";
        REQUIRE(orc_encode(cps, err, ref, &pre, &e2) == 0, "oracle prefix");
        check_encode(s, F_CP, cps, n, os, ref, pre, err, pre);
        g_what = "invalid code point stream";
        check_stream_encode_invalid(s, cps, n, os, err);
        free(ref);
        return;
    }

    g_what = "encode cps";
    check_encode(s, F_CP, cps, n, 0, ref, rlen, 0, rlen);

    {
        uint16_t *u = (uint16_t *)xmalloc(4 * n + 2);
        size_t nu = to_utf16(cps, n, u);
        g_what = "encode utf16";
        check_encode(s, F_U16, u, nu, 0, ref, rlen, 0, rlen);
        free(u);
    }

    {
        size_t k;
        int sur = 0;
        for (k = 0; k < n; k++) sur |= is_sur(cps[k]);
        if (!sur) {
            uint8_t *b8 = (uint8_t *)xmalloc(4 * n + 1);
            size_t nb = to_utf8(cps, n, b8);
            g_what = "encode utf8";
            check_encode(s, F_U8, b8, nb, 0, ref, rlen, 0, rlen);
            free(b8);
        }
    }

    g_what = "stream encode";
    check_stream_encode(s, cps, n, ref, rlen);

    /* Decode the encoding in every form and in pieces. */
    check_bytes(s, ref, rlen);

    /* Mutations of the encoding. */
    for (i = 0; i < 3 && rlen > 0; i++) {
        uint8_t *m = (uint8_t *)xmalloc(rlen + 4);
        size_t mn = rlen, p = below(s, (uint32_t)rlen);
        memcpy(m, ref, rlen);
        switch (next8(s) % 5) {
        case 0: m[p] = (uint8_t)next8(s); break;
        case 1: m[p] = (uint8_t)(next8(s) & 7); break;
        case 2: mn = p; break;
        case 3: m[p] ^= (uint8_t)(1u << (next8(s) & 7)); break;
        default: m[p] = (uint8_t)(0xF0 + (next8(s) & 15)); break;
        }
        g_what = "mutated bytes";
        check_bytes(s, m, mn);
        free(m);
    }

    free(ref);
}

/* Raw bytes as UTF-8 input, one-shot and streamed. */
static void check_utf8_input(src_t *s, const uint8_t *b, size_t n) {
    uint32_t *cps = (uint32_t *)xmalloc(4 * n + 4);
    size_t ncp = 0, bad = 0, ol, rlen = 0, err;
    int valid = from_utf8(b, n, cps, &ncp, &bad);
    uint8_t *ref = (uint8_t *)xmalloc(4 * n + 4);
    steno_status st;

    g_what = "utf8 input";
    if (valid) {
        REQUIRE(orc_encode(cps, ncp, ref, &rlen, &err) == 0, "oracle");
        check_encode(s, F_U8, b, n, 0, ref, rlen, 0, rlen);
    } else {
        size_t pn = 0, pre = 0;
        from_utf8(b, bad, cps, &pn, &err);
        REQUIRE(orc_encode(cps, pn, ref, &pre, &err) == 0, "oracle prefix");
        check_encode(s, F_U8, b, n, STENO_INVALID_UTF8, ref, pre, bad, pre);
        rlen = pre;
    }

    /* Streamed in random pieces: the same bytes, or the same error at the same position (a sequence cut at the end fails at finish). */
    {
        static est_t t;
        size_t a = 0, got;
        t.e = g_enc_a;
        t.olen = 0;
        t.fed = 0;
        t.held_high = 0;
        steno_encoder_init(t.e);
        g_what = "utf8 input stream";
        while (a < n) {
            size_t m = below(s, (uint32_t)(n - a + 1));
            st = enc_feed_retry(s, &t, F_U8, b + a, m, &got);
            if (st != STENO_OK) {
                REQUIRE(!valid && st == STENO_INVALID_UTF8 && got == bad, "utf8 stream: error / position");
                REQUIRE(t.olen <= rlen && memcmp(t.out, ref, t.olen) == 0, "utf8 stream: bytes before the error");
                st = steno_encoder_finish(t.e, NULL, 0, &ol);
                free(cps);
                free(ref);
                return;
            }
            a += m;
        }
        st = enc_finish_retry(s, &t, &got);
        if (valid) {
            REQUIRE(st == STENO_OK && t.olen == rlen && memcmp(t.out, ref, rlen) == 0, "utf8 stream: bytes");
        } else {
            REQUIRE(st == STENO_INVALID_UTF8 && got == bad, "utf8 stream: error at finish");
            REQUIRE(t.olen == rlen && memcmp(t.out, ref, rlen) == 0, "utf8 stream: bytes before a cut sequence");
        }
    }

    free(cps);
    free(ref);
}

static void run_one(const uint8_t *data, size_t size) {
    src_t s;
    uint32_t mode;
    size_t i;
    uint64_t h = 1469598103934665603ull;

    for (i = 0; i < size; i++) h = (h ^ data[i]) * 1099511628211ull;
    s.d = data;
    s.n = size;
    s.i = 0;
    s.x = h | 1;

    if (g_enc_a == NULL) {
        g_enc_a = (steno_encoder *)xmalloc(sizeof *g_enc_a);
        g_enc_b = (steno_encoder *)xmalloc(sizeof *g_enc_b);
        g_dec_a = (steno_decoder *)xmalloc(sizeof *g_dec_a);
        g_dec_b = (steno_decoder *)xmalloc(sizeof *g_dec_b);
    }

    mode = next8(&s);
    g_exact_alloc = (mode >> 7) & 1;

    switch (mode % 6) {
    case 0:
    case 1:
    case 2: {
        uint32_t *cps = (uint32_t *)xmalloc(4 * 2048);
        size_t n = gen_text(&s, cps, (mode % 3) == 0 ? 2000 : 300, 0);
        check_text(&s, cps, n);
        free(cps);
        break;
    }
    case 3: {
        uint32_t *cps = (uint32_t *)xmalloc(4 * 512);
        size_t n = gen_text(&s, cps, 200, 1);
        check_text(&s, cps, n);
        free(cps);
        break;
    }
    case 4: check_bytes(&s, data + s.i, size - s.i); break;
    default: check_utf8_input(&s, data + s.i, size - s.i); break;
    }
}

#ifdef STENO_FUZZ_STANDALONE
int main(int argc, char **argv) {
    unsigned long long seed = argc > 1 ? strtoull(argv[1], NULL, 0) : 1, count = argc > 2 ? strtoull(argv[2], NULL, 0) : 1000, k;
    uint8_t buf[600];
    uint64_t x = seed * 0x9E3779B97F4A7C15ull + 1;

    for (k = 0; k < count; k++) {
        size_t n = 1 + (size_t)((x >> 33) % sizeof buf), i;
        for (i = 0; i < n; i++) {
            x ^= x << 13;
            x ^= x >> 7;
            x ^= x << 17;
            buf[i] = (uint8_t)(x >> 29);
        }
        /* Byte strings for the decoder: mostly bytes from a few interesting values. */
        if (buf[0] % 6 == 4 && (x & 1)) {
            for (i = 1; i < n; i++) buf[i] = (uint8_t)((x >> (i & 31)) & 1 ? buf[i] : (0x60 + (buf[i] & 0x9F)));
        }
        run_one(buf, n);
    }

    printf("differential: %llu cases from seed %llu, no failure\n", count, seed);
    return 0;
}
#else
int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size);
int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size) {
    run_one(data, size);
    return 0;
}
#endif
