/* A tiny test harness: checks, a seeded random generator, text generators and conversions that do not use the library. */

#ifndef STENO_TEST_HARNESS_H
#define STENO_TEST_HARNESS_H

#include <stdarg.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static long harness_failures = 0;
static long harness_checks = 0;

/* Reports a failed check with a message; stops after 50 failures so that one bug does not flood the output. */
static inline void harness_fail(const char *file, int line, const char *fmt, ...) {
    va_list ap;

    harness_failures++;
    printf("FAIL %s:%d: ", file, line);
    va_start(ap, fmt);
    vprintf(fmt, ap);
    va_end(ap);
    printf("\n");

    if (harness_failures >= 50) {
        printf("too many failures\n");
        exit(1);
    }
}

#define CHECK(cond)                                                                                                                     \
    do {                                                                                                                                \
        harness_checks++;                                                                                                               \
        if (!(cond)) harness_fail(__FILE__, __LINE__, "%s", #cond);                                                                     \
    } while (0)

#define CHECKF(cond, ...)                                                                                                               \
    do {                                                                                                                                \
        harness_checks++;                                                                                                               \
        if (!(cond)) harness_fail(__FILE__, __LINE__, __VA_ARGS__);                                                                     \
    } while (0)

static inline void *xmalloc(size_t n) {
    void *p = malloc(n == 0 ? 1 : n);

    if (p == NULL) {
        printf("out of memory\n");
        exit(2);
    }

    return p;
}

/* ---------- Random numbers (splitmix64) ---------- */

typedef struct {
    uint64_t s;
} rng_t;

static inline uint64_t rng_next(rng_t *r) {
    uint64_t z = (r->s += UINT64_C(0x9E3779B97F4A7C15));
    z = (z ^ (z >> 30)) * UINT64_C(0xBF58476D1CE4E5B9);
    z = (z ^ (z >> 27)) * UINT64_C(0x94D049BB133111EB);
    return z ^ (z >> 31);
}

/* A number in 0..n-1 (n > 0). */
static inline uint32_t rng_below(rng_t *r, uint32_t n) {
    return (uint32_t)(rng_next(r) % n);
}

static inline uint32_t rng_between(rng_t *r, uint32_t lo, uint32_t hi) {
    return lo + rng_below(r, hi - lo + 1);
}

/* ---------- Text ---------- */

static inline int is_high(uint32_t c) {
    return c >= 0xD800 && c <= 0xDBFF;
}

static inline int is_low(uint32_t c) {
    return c >= 0xDC00 && c <= 0xDFFF;
}

static inline int is_surrogate(uint32_t c) {
    return c >= 0xD800 && c <= 0xDFFF;
}

/* Code points to UTF-8 (no surrogates). out must hold 4 * n bytes. */
static inline size_t to_utf8(const uint32_t *c, size_t n, uint8_t *out) {
    size_t i, k = 0;

    for (i = 0; i < n; i++) {
        uint32_t x = c[i];

        if (x < 0x80) {
            out[k++] = (uint8_t)x;
        } else if (x < 0x800) {
            out[k++] = (uint8_t)(0xC0 | (x >> 6));
            out[k++] = (uint8_t)(0x80 | (x & 0x3F));
        } else if (x < 0x10000) {
            out[k++] = (uint8_t)(0xE0 | (x >> 12));
            out[k++] = (uint8_t)(0x80 | ((x >> 6) & 0x3F));
            out[k++] = (uint8_t)(0x80 | (x & 0x3F));
        } else {
            out[k++] = (uint8_t)(0xF0 | (x >> 18));
            out[k++] = (uint8_t)(0x80 | ((x >> 12) & 0x3F));
            out[k++] = (uint8_t)(0x80 | ((x >> 6) & 0x3F));
            out[k++] = (uint8_t)(0x80 | (x & 0x3F));
        }
    }

    return k;
}

/* Code points to UTF-16 (a value: no high surrogate code point directly followed by a low one). out must hold 2 * n units. */
static inline size_t to_utf16(const uint32_t *c, size_t n, uint16_t *out) {
    size_t i, k = 0;

    for (i = 0; i < n; i++) {
        uint32_t x = c[i];

        if (x < 0x10000) {
            out[k++] = (uint16_t)x;
        } else {
            out[k++] = (uint16_t)(0xD800 + ((x - 0x10000) >> 10));
            out[k++] = (uint16_t)(0xDC00 + ((x - 0x10000) & 0x3FF));
        }
    }

    return k;
}

static inline int has_surrogate(const uint32_t *c, size_t n) {
    size_t i;

    for (i = 0; i < n; i++) {
        if (is_surrogate(c[i])) return 1;
    }

    return 0;
}

/* The categories of the random text: runs of code points from one of these, with the edges of the format among them. */
typedef struct {
    uint32_t lo, hi;
} cp_span;

static const cp_span CATEGORIES[] = {
    {0x0020, 0x007E},   /* printable ASCII */
    {0x0041, 0x005A},   /* ASCII letters */
    {0x0000, 0x001F},   /* C0 controls */
    {0x0009, 0x000D},   /* tab, line feed, carriage return and the two between */
    {0x007F, 0x00BF},   /* DEL and Latin-1 punctuation */
    {0x00C0, 0x017F},   /* Latin-1 letters, Latin Extended-A */
    {0x0180, 0x02FF},   /* Latin Extended-B, IPA, spacing modifiers */
    {0x0300, 0x037F},   /* combining marks, the code points before Greek */
    {0x0380, 0x03FF},   /* Greek */
    {0x0400, 0x04FF},   /* Cyrillic */
    {0x0500, 0x05FF},   /* Cyrillic Supplement, Armenian, Hebrew */
    {0x0600, 0x06FF},   /* Arabic */
    {0x0900, 0x097F},   /* Devanagari */
    {0x0E00, 0x0F7F},   /* Thai, Lao, Tibetan: across U+0EFF/U+0F00 */
    {0x1200, 0x137F},   /* Ethiopic */
    {0x1E00, 0x1FFF},   /* Latin Extended Additional, Greek Extended */
    {0x2000, 0x20FF},   /* punctuation, currency */
    {0x2100, 0x2FFF},   /* symbols */
    {0x3000, 0x303F},   /* CJK punctuation */
    {0x3041, 0x3096},   /* hiragana */
    {0x30A1, 0x30FF},   /* katakana */
    {0x3040, 0x30FF},   /* kana block */
    {0x3100, 0x33FF},   /* Bopomofo, compatibility */
    {0x3400, 0x4DBF},   /* CJK extension A */
    {0x4E00, 0x9FFF},   /* CJK ideographs */
    {0xA000, 0xABFF},   /* Yi and others */
    {0xAC00, 0xD7A3},   /* Hangul syllables */
    {0xD7A4, 0xD7FF},   /* after Hangul */
    {0xD800, 0xDFFF},   /* surrogates */
    {0xE000, 0xF8FF},   /* private use */
    {0xFE00, 0xFFFF},   /* specials, fullwidth */
    {0xFF00, 0xFF1F},   /* fullwidth punctuation */
    {0x10000, 0x10FFF}, /* plane 1 start */
    {0x10F80, 0x1104F}, /* around U+10FC0 */
    {0x1F300, 0x1FAFF}, /* emoji */
    {0x1FF80, 0x2007F}, /* around U+20000 */
    {0x20000, 0x2FFFF}, /* CJK extension B and later */
    {0x30000, 0x3FFFF}, /* plane 3 */
    {0x3FF80, 0x4007F}, /* around U+40000 */
    {0x40000, 0x10FFFF},/* planes 4 to 16 */
    {0x10FF80, 0x10FFFF},
};

#define N_CATEGORIES (sizeof CATEGORIES / sizeof CATEGORIES[0])

/* Edges of the format, picked one at a time. */
static const uint32_t EDGES[] = {0x0000, 0x0008, 0x0009, 0x000A, 0x000B, 0x000C, 0x000D, 0x000E, 0x001F, 0x0020, 0x0040, 0x0041,
                                 0x005A, 0x0061, 0x007A, 0x007E, 0x007F, 0x0080, 0x00BF, 0x00C0, 0x013F, 0x0140, 0x02BF, 0x02C0,
                                 0x02FF, 0x0300, 0x036F, 0x0370, 0x037F, 0x0380, 0x04FF, 0x0500, 0x052F, 0x0530, 0x058F, 0x0590,
                                 0x05FF, 0x0600, 0x0EFF, 0x0F00, 0x1FFF, 0x2000, 0x20CF, 0x20D0, 0x20FF, 0x2100, 0x2FFF, 0x3000,
                                 0x300F, 0x3010, 0x303F, 0x3040, 0x3041, 0x3096, 0x3097, 0x30A0, 0x30A1, 0x30FA, 0x30FB, 0x30FC,
                                 0x30FD, 0x30FF, 0x3100, 0x33FF, 0x3400, 0x4DFF, 0x4E00, 0x9FFF, 0xA000, 0xABFF, 0xAC00, 0xD7A3,
                                 0xD7A4, 0xD7FF, 0xD800, 0xDBFF, 0xDC00, 0xDFFF, 0xE000, 0xFEFF, 0xFF00, 0xFF1F, 0xFF20, 0xFFFE,
                                 0xFFFF, 0x10000, 0x10FBF, 0x10FC0, 0x1F600, 0x1FFFF, 0x20000, 0x3FFFF, 0x40000, 0x10FFFF};

#define N_EDGES (sizeof EDGES / sizeof EDGES[0])

/* Random text of at most max code points (max > 0): runs from the categories, spaces, single hops and edges. With surrogates 0, no
   surrogate code points (for UTF-8); otherwise lone surrogates may occur, but never a high one directly followed by a low one. */
static inline size_t random_text(rng_t *r, uint32_t *out, size_t max, int surrogates) {
    size_t n = 0, len = rng_below(r, (uint32_t)max + 1);

    while (n < len) {
        uint32_t kind = rng_below(r, 10), c;
        size_t run = 1, k;
        const cp_span *cat = &CATEGORIES[rng_below(r, (uint32_t)N_CATEGORIES)];

        if (kind < 6) run = rng_between(r, 1, 24);

        for (k = 0; k < run && n < len; k++) {
            if (kind == 9) c = EDGES[rng_below(r, (uint32_t)N_EDGES)];
            else if (kind == 8) c = 0x20;
            else c = rng_between(r, cat->lo, cat->hi);

            if (is_surrogate(c) && !surrogates) c = 0xFFFD;
            if (n > 0 && is_high(out[n - 1]) && is_low(c)) c = 0x41;
            out[n++] = c;
        }
    }

    return n;
}

/* Compares two code point sequences in code point order. */
static inline int cmp_cps(const uint32_t *a, size_t na, const uint32_t *b, size_t nb) {
    size_t i;

    for (i = 0; i < na && i < nb; i++) {
        if (a[i] != b[i]) return a[i] < b[i] ? -1 : 1;
    }

    return na == nb ? 0 : na < nb ? -1 : 1;
}

/* Compares two byte strings as unsigned bytes, a proper prefix first. */
static inline int cmp_bytes(const uint8_t *a, size_t na, const uint8_t *b, size_t nb) {
    size_t n = na < nb ? na : nb;
    int c = n == 0 ? 0 : memcmp(a, b, n);

    if (c != 0) return c < 0 ? -1 : 1;
    return na == nb ? 0 : na < nb ? -1 : 1;
}

static inline void print_hex(const char *label, const uint8_t *b, size_t n) {
    size_t i;

    printf("%s", label);
    for (i = 0; i < n && i < 64; i++) printf("%02X", b[i]);
    if (n > 64) printf("...");
    printf("\n");
}

static inline void print_cps(const char *label, const uint32_t *c, size_t n) {
    size_t i;

    printf("%s", label);
    for (i = 0; i < n && i < 32; i++) printf(" %04X", (unsigned)c[i]);
    if (n > 32) printf(" ...");
    printf("\n");
}

static inline int harness_report(const char *name) {
    if (harness_failures == 0) {
        printf("%s: all %ld checks passed\n", name, harness_checks);
        return 0;
    }

    printf("%s: %ld of %ld checks failed\n", name, harness_failures, harness_checks);
    return 1;
}

#endif
