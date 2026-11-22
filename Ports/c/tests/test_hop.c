/*
 * The worst case of time: text that reaches a new state at almost every code point. Each such code point builds a layout, which no cache
 * can avoid. This test measures the time per code point of such text, against ordinary Russian text, and fails if the cost is not
 * bounded: more than 2 microseconds per code point, or more than 1,000 times ordinary text, one-shot; more than 5 microseconds per call
 * when streamed one element per call.
 *
 * The shapes: hops among 3, 20, 104 and 400 block windows; every code point from a random window of all 7,356 (a switch at every code
 * point); windows far apart (3- and 4-byte code points, which also set the second window); an ideograph then two code points of a new
 * window (leaving the CJK context each time); Hangul, a window, kana and a letter in turn (every context). The last four come from the
 * adversarial review's measurements.
 *
 * It takes a few seconds. It is a test of a bound, not a benchmark: the figures it prints depend on the machine and its load.
 */

#include "../steno.h"

#include <time.h>

#include "harness.h"

/* Block windows in the general context, outside the ranges of the CJK and Hangul contexts. */
static uint32_t hop_windows[2000];
static size_t n_hop_windows;

/* Every window of FORMAT.md 4. */
static uint32_t all_windows[8000];
static size_t n_all_windows;

static void make_windows(void) {
    static const uint32_t spans[][2] = {{0x1000, 0x2000}, {0x2100, 0x3000}, {0x3100, 0x3400}, {0xA000, 0xAC00}, {0xE000, 0x20000}};
    static const uint32_t special[] = {0xC0, 0x140, 0x1C0, 0x240, 0x2C0, 0x300, 0x500, 0x530, 0x590};
    static const uint32_t blocks[][2] = {{0x380, 0x500}, {0x600, 0x2000}, {0x2100, 0x3000}, {0x3100, 0x3400}, {0xA000, 0xAC00},
                                         {0xE000, 0x20000}, {0x40000, 0x110000}};
    size_t i;
    uint32_t w;

    for (i = 0; i < sizeof spans / sizeof spans[0]; i++) {
        for (w = spans[i][0]; w < spans[i][1]; w += 0x80) hop_windows[n_hop_windows++] = w;
    }

    for (i = 0; i < sizeof special / sizeof special[0]; i++) all_windows[n_all_windows++] = special[i];

    for (i = 0; i < sizeof blocks / sizeof blocks[0]; i++) {
        for (w = blocks[i][0]; w < blocks[i][1]; w += 0x80) all_windows[n_all_windows++] = w;
    }
}

/* n code points, each from a window chosen at random among `windows` windows spread over the list. */
static size_t hop_text(uint32_t *out, size_t n, size_t windows, uint64_t seed) {
    rng_t r;
    size_t i;

    r.s = seed;
    for (i = 0; i < n; i++) out[i] = hop_windows[rng_below(&r, (uint32_t)windows) * (n_hop_windows / windows)] + rng_below(&r, 0x80);
    return n;
}

static size_t russian_text(uint32_t *out, size_t n) {
    rng_t r = {1};
    size_t i;

    for (i = 0; i < n; i++) out[i] = rng_below(&r, 7) == 0 ? 0x20 : 0x430 + rng_below(&r, 32);
    return n;
}

static size_t all_windows_text(uint32_t *out, size_t n) {
    rng_t r = {2};
    size_t i;

    for (i = 0; i < n; i++) out[i] = all_windows[rng_below(&r, (uint32_t)n_all_windows)] + 5;
    return n;
}

static size_t far_text(uint32_t *out, size_t n) {
    rng_t r = {3};
    size_t i;

    for (i = 0; i < n; i++) out[i] = (i & 1) ? 0x40000 + 0x80 * rng_below(&r, 0x1A00) : 0xE000 + 0x80 * rng_below(&r, 0x240);
    return n;
}

static size_t cjk_hop_text(uint32_t *out, size_t n) {
    rng_t r = {4};
    size_t i = 0;

    while (i + 3 <= n) {
        uint32_t w = all_windows[rng_below(&r, (uint32_t)n_all_windows)];
        out[i++] = 0x4E00 + rng_below(&r, 0x5200);
        out[i++] = w + 1;
        out[i++] = w + 2;
    }

    return i;
}

static size_t context_mix_text(uint32_t *out, size_t n) {
    rng_t r = {5};
    size_t i = 0;

    while (i + 6 <= n) {
        uint32_t w = all_windows[rng_below(&r, (uint32_t)n_all_windows)];
        out[i++] = 0xAC00 + rng_below(&r, 0x2BA4);
        out[i++] = w + 3;
        out[i++] = w + 4;
        out[i++] = 0x3041 + rng_below(&r, 0x56);
        out[i++] = 0x30A1 + rng_below(&r, 0x5A);
        out[i++] = 0x41 + rng_below(&r, 26);
    }

    return i;
}

enum { N = 50000 };

static uint8_t enc[4 * N + 8];
static uint32_t back[N + 8];

/* The best time per code point over rounds of at least 20 ms each: one-shot encode (0) or decode (1), or streamed one element per
   call, encode (2) or decode (3). */
static double per_cp(const uint32_t *text, size_t n, int how) {
    double best = 1e18;
    size_t len = 0, got, i, pos;
    int round;

    CHECK(steno_encode_code_points(text, n, enc, sizeof enc, &len) == STENO_OK);

    for (round = 0; round < 3; round++) {
        clock_t start = clock(), now;
        long calls = 0;

        do {
            if (how == 0) {
                CHECK(steno_encode_code_points(text, n, enc, sizeof enc, &got) == STENO_OK);
            } else if (how == 1) {
                CHECK(steno_decode_code_points(enc, len, back, n, &got) == STENO_OK && got == n);
            } else if (how == 2) {
                steno_encoder e;
                steno_encoder_init(&e);
                for (pos = 0, i = 0; i < n; i++) {
                    CHECK(steno_encoder_feed_code_points(&e, text + i, 1, enc + pos, sizeof enc - pos, &got) == STENO_OK);
                    pos += got;
                }
                CHECK(pos == len);
            } else {
                steno_decoder d;
                steno_decoder_init(&d);
                for (pos = 0, i = 0; i < len; i++) {
                    CHECK(steno_decoder_feed_code_points(&d, enc + i, 1, back + pos, n - pos, &got) == STENO_OK);
                    pos += got;
                }
                CHECK(pos == n);
            }

            calls++;
            now = clock();
        } while ((double)(now - start) < 0.02 * CLOCKS_PER_SEC);

        {
            double ns = (double)(now - start) / CLOCKS_PER_SEC * 1e9 / ((double)calls * (double)n);
            if (ns < best) best = ns;
        }
    }

    return best;
}

/* Measures one text one-shot (and streamed, if stream is set), prints the figures and checks the bounds. */
static void measure(const char *name, const uint32_t *text, size_t n, double base_e, double base_d, int stream) {
    double e = per_cp(text, n, 0), d = per_cp(text, n, 1);

    printf("  %-34s encode %6.1f  decode %6.1f", name, e, d);
    CHECKF(e < 2000 && d < 2000, "%s: %.0f / %.0f ns per code point", name, e, d);
    CHECKF(e < 1000 * (base_e > 1 ? base_e : 1) && d < 1000 * (base_d > 1 ? base_d : 1), "%s: %.0f / %.0f ns per code point", name, e, d);

    if (stream) {
        double se = per_cp(text, n, 2), sd = per_cp(text, n, 3);
        printf("  streamed %6.1f / %6.1f", se, sd);
        CHECKF(se < 5000 && sd < 5000, "%s streamed: %.0f / %.0f ns per call", name, se, sd);
    }

    printf(" ns per code point\n");
}

int main(void) {
    static uint32_t text[N];
    static const size_t windows[] = {3, 20, 104, 400};
    double base_e, base_d;
    size_t i, n;
    char name[64];

    make_windows();
    n = russian_text(text, N);
    base_e = per_cp(text, n, 0);
    base_d = per_cp(text, n, 1);
    printf("ns per code point (Russian: encode %.1f, decode %.1f):\n", base_e, base_d);

    for (i = 0; i < sizeof windows / sizeof windows[0]; i++) {
        n = hop_text(text, N, windows[i], 10 + i);
        sprintf(name, "hops among %u windows", (unsigned)windows[i]);
        measure(name, text, n, base_e, base_d, 0);
    }

    n = all_windows_text(text, N);
    measure("every code point a new window", text, n, base_e, base_d, 1);
    n = far_text(text, N);
    measure("windows far apart", text, n, base_e, base_d, 1);
    n = cjk_hop_text(text, N);
    measure("ideograph, then a new window x2", text, n, base_e, base_d, 1);
    n = context_mix_text(text, N);
    measure("Hangul, window, kana, letter", text, n, base_e, base_d, 1);

    return harness_report("hop");
}
