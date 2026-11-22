/*
 * Tests of the internals of steno.c, which this file includes: the layouts against the reference codec and the specification, the
 * bounds that the code relies on (at most STENO_PRIVATE_MAX_RANGES ranges, at most 254 lead bytes), the division by 248, the windows.
 *
 * Every layout with every second window, compared with the independent oracle, is the optional test test_layouts.c (make exhaustive).
 */

#include "../steno.c"

#include "harness.h"
#include "ref.h"

static const unsigned COMBOS[5][2] = {{CTX_GENERAL, 0}, {CTX_HANGUL, 0}, {CTX_CJK, SLOT_LETTERS}, {CTX_CJK, SLOT_HIRAGANA},
                                      {CTX_CJK, SLOT_KATAKANA}};

static uint32_t *all_windows(size_t *count) {
    uint32_t *w = (uint32_t *)xmalloc(8000 * sizeof *w), c = 0;
    size_t n = 0;

    while (c <= 0x10FFFF) {
        uint32_t x = window_of(c);

        if (x == NONE) {
            c++;
        } else {
            w[n++] = x;
            c = window_end(x) + 1;
        }
    }

    *count = n;
    return w;
}

static st_t make_state(unsigned ctx, unsigned slot, uint32_t main_w, uint32_t second) {
    st_t s = ST_INITIAL;
    s.ctx = (uint8_t)ctx;
    s.slot = (uint8_t)slot;
    s.main = main_w;
    s.second = second;
    return s;
}

/* The layout of steno.c against the reference codec's, range by range. Returns the number of lead bytes. */
static unsigned compare_layout(const st_t *s) {
    layout L;
    ref_state rs;
    ref_range rr[64];
    size_t nr, i;

    build_layout(&L, s, key_of(s));
    rs.ctx = s->ctx;
    rs.slot = s->slot;
    rs.main = s->main;
    rs.second = s->second;
    rs.pending = REF_NONE;
    rs.conf = 0;
    nr = ref_layout(&rs, rr);

    CHECKF(nr <= MAXR && L.nr == nr, "ctx %u slot %u main %X second %X: %u ranges, the reference %u", s->ctx, s->slot, s->main,
           s->second, L.nr, (unsigned)nr);

    for (i = 0; i < nr && i < L.nr; i++) {
        CHECKF(L.lo[i] == rr[i].lo && L.lo[i + 1] == rr[i].hi + 1 && L.size[i] == rr[i].size && L.first[i] == rr[i].first &&
                   (unsigned)(L.first[i + 1] - L.first[i]) == rr[i].leads,
               "ctx %u slot %u main %X second %X: range %u differs", s->ctx, s->slot, s->main, s->second, (unsigned)i);
    }

    return (unsigned)L.first[L.nr] - 1;
}

/* The fast-path parameters against a plain lookup in the ranges. */
static void check_fast_params(const st_t *s) {
    layout L;
    uint32_t c;

    build_layout(&L, s, key_of(s));

    for (c = 0x20; c < 0x20 + L.ascii_n; c++) {
        uint8_t b[4];
        CHECK(encode_cp(&L, c, b) == 1 && b[0] == c - 0x19);
    }

    for (c = L.one_lo; c < L.one_lo + L.one_n; c++) {
        uint8_t b[4];
        CHECK(encode_cp(&L, c, b) == 1 && b[0] == L.one_first + (c - L.one_lo));
    }

    for (c = L.punct_lo; c < L.punct_lo + L.punct_n; c++) {
        uint8_t b[4];
        CHECK(encode_cp(&L, c, b) == 1 && b[0] == L.punct_first + (c - L.punct_lo));
    }

    for (c = L.fw_lo; c < L.fw_lo + L.fw_n; c++) {
        uint8_t b[4];
        CHECK(encode_cp(&L, c, b) == 1 && b[0] == L.fw_first + (c - L.fw_lo));
    }

    for (c = L.wide_lo; c < L.wide_lo + L.wide_n; c++) {
        uint8_t b[4];
        uint32_t o = c - L.wide_r0, q = div248(o);
        CHECK(encode_cp(&L, c, b) == 2 && b[0] == L.wide_first + q && b[1] == o - q * 248 + 8);
    }

    /* Every code point of a full wide lead byte is in the wide run. */
    for (c = 0; c < L.wfull_n; c++) {
        uint32_t lead = L.wfull_first + c, t;

        for (t = 8; t <= 0xFF; t += 0xF7) {
            uint8_t b[2];
            uint32_t cp = 0;
            b[0] = (uint8_t)lead;
            b[1] = (uint8_t)t;
            CHECK(decode_cp(&L, b, 2, &cp) == 2 && cp - L.wide_lo < L.wide_n);
            CHECK(cp == L.wide_r0 + (lead - L.wide_first) * 248 + t - 8);
        }
    }

    CHECK(L.wfull_n == 0 || (L.wfull_first > 0 && L.wfull_first + L.wfull_n <= L.first[L.nr]));
}

int main(void) {
    size_t nw, i, j;
    uint32_t *w = all_windows(&nw);
    unsigned most = 0, most_ranges = 0;
    rng_t r = {7};

    CHECKF(nw == 7356, "%u windows", (unsigned)nw);

    /* Windows against the reference, at every code point. */
    {
        uint32_t c;
        for (c = 0; c <= 0x10FFFF; c++) {
            if (window_of(c) != ref_window(c)) {
                CHECKF(0, "window of %X", c);
                break;
            }
        }
        CHECK(window_of(0x110000) == NONE);
    }

    /* The division by 248 of the 2-byte fast paths, over every value it may meet and more. */
    {
        uint32_t o;
        for (o = 0; o < 65536; o++) {
            if (div248(o) != o / 248) {
                CHECKF(0, "div248(%u)", o);
                break;
            }
        }
    }

    /* The layout of the initial state that steno.c keeps as a constant (6.6). */
    {
        layout L;
        st_t s = ST_INITIAL;
        build_layout(&L, &s, key_of(&s));
        CHECK(L.key == KEY_INITIAL && INITIAL_LAYOUT.key == KEY_INITIAL);
        CHECK(L.nr == INITIAL_LAYOUT.nr && L.nr == 13);
        CHECK(memcmp(L.lo, INITIAL_LAYOUT.lo, sizeof L.lo) == 0 || memcmp(L.lo, INITIAL_LAYOUT.lo, (L.nr + 1) * sizeof L.lo[0]) == 0);
        CHECK(memcmp(L.first, INITIAL_LAYOUT.first, L.nr + 1u) == 0 && memcmp(L.size, INITIAL_LAYOUT.size, L.nr) == 0);
        CHECK(L.ascii_n == INITIAL_LAYOUT.ascii_n && L.one_lo == INITIAL_LAYOUT.one_lo && L.one_n == INITIAL_LAYOUT.one_n &&
              L.one_first == INITIAL_LAYOUT.one_first && L.wide_n == 0 && INITIAL_LAYOUT.wide_n == 0 && L.punct_n == 0 && L.fw_n == 0 &&
              L.wfull_n == 0 && INITIAL_LAYOUT.wfull_n == 0 && INITIAL_LAYOUT.punct_n == 0 && INITIAL_LAYOUT.fw_n == 0);
        CHECK(L.first[L.nr] - 1 == 250);
    }

    /* Every constant layout is the layout that build_layout gives for its key. */
    for (i = 0; i < N_STATIC; i++) {
        const layout *S = &STATIC_LAYOUTS[i];
        uint64_t k = S->key;
        st_t s = ST_INITIAL;
        layout L;

        s.ctx = (uint8_t)(k & 3);
        s.slot = (uint8_t)((k >> 2) & 3);
        s.main = (uint32_t)((k >> 4) & 0x1FFFFF);
        s.second = (k >> 25) == 0x3FFFFF ? NONE : (uint32_t)(k >> 25);
        CHECK(key_of(&s) == k);
        build_layout(&L, &s, k);
        CHECKF(L.nr == S->nr && memcmp(L.lo, S->lo, (L.nr + 1u) * sizeof L.lo[0]) == 0 && memcmp(L.first, S->first, L.nr + 1u) == 0 &&
                   memcmp(L.size, S->size, L.nr) == 0,
               "constant layout %u: ranges", (unsigned)i);
        CHECKF(L.one_lo == S->one_lo && L.one_n == S->one_n && L.wide_lo == S->wide_lo && L.wide_n == S->wide_n && L.wide_r0 == S->wide_r0 &&
                   L.punct_lo == S->punct_lo && L.punct_n == S->punct_n && L.fw_lo == S->fw_lo && L.fw_n == S->fw_n &&
                   L.ascii_n == S->ascii_n && L.one_first == S->one_first && L.wide_first == S->wide_first &&
                   L.wfull_first == S->wfull_first && L.wfull_n == S->wfull_n && L.punct_first == S->punct_first &&
                   L.fw_first == S->fw_first,
               "constant layout %u: fast-path parameters", (unsigned)i);
        (void)compare_layout(&s);

        for (j = 0; j < i; j++) CHECK(STATIC_LAYOUTS[j].key != k);
    }

    CHECK(N_STATIC == 20);

    /* The layouts of 11.1, by their lead byte counts and a few ranges. */
    {
        layout L;
        st_t s = make_state(CTX_GENERAL, 0, 0x400, 0x1200);
        build_layout(&L, &s, key_of(&s));
        CHECK(L.nr == 15 && L.lo[6] == 0x7F && L.first[6] == 0x66 && L.lo[13] == 0x20D0 && L.first[13] == 0xF9 && L.first[14] == 0xFB);

        s = make_state(CTX_CJK, SLOT_HIRAGANA, 0xC0, NONE);
        build_layout(&L, &s, key_of(&s));
        CHECK(L.nr == 20 && L.lo[15] == 0x4E00 && L.first[15] == 0x96 && L.first[16] == 0xEB && L.first[19] == 0xF0);

        s = make_state(CTX_HANGUL, 0, 0xC0, NONE);
        build_layout(&L, &s, key_of(&s));
        CHECK(L.nr == 17 && L.lo[12] == 0xAC00 && L.first[12] == 0x7A && L.first[16] == 0xAD);

        s = make_state(CTX_GENERAL, 0, 0x1000, 0x1200);
        build_layout(&L, &s, key_of(&s));
        CHECK(L.first[L.nr] - 1 == 254 && L.lo[7] == 0x1000 && L.first[7] == 0x77);

        s = make_state(CTX_GENERAL, 0, 0x11200, 0x10E80);
        CHECK(compare_layout(&s) == 254);
    }

    /* 13.6, fact 1: every layout without a second window, against the reference; at most 252 lead bytes. */
    for (i = 0; i < nw; i++) {
        for (j = 0; j < 5; j++) {
            st_t s = make_state(COMBOS[j][0], COMBOS[j][1], w[i], NONE);
            layout L;
            unsigned leads = compare_layout(&s);

            build_layout(&L, &s, key_of(&s));
            if (leads > most) most = leads;
            if (L.nr > most_ranges) most_ranges = L.nr;
            if (i % 97 == 0) check_fast_params(&s);
        }
    }

    CHECKF(most == 252, "without a second window, the most lead bytes are %u", most);
    printf("without a second window: at most %u lead bytes, %u ranges\n", most, most_ranges);

    /* 13.6, fact 2, on a sample: random second windows, often next to the main window, against the reference. */
    most = 0;
    for (i = 0; i < 60000; i++) {
        const unsigned *combo = COMBOS[rng_below(&r, 5)];
        size_t a = rng_below(&r, (uint32_t)nw), b;
        st_t s;
        unsigned leads;
        layout L;

        b = rng_below(&r, 2) == 0 ? (a + nw - 3 + rng_below(&r, 7)) % nw : rng_below(&r, (uint32_t)nw);
        if (combo[0] == CTX_GENERAL && a == b) continue;

        s = make_state(combo[0], combo[1], w[a], w[b]);
        leads = compare_layout(&s);
        build_layout(&L, &s, key_of(&s));
        if (leads > most) most = leads;
        if (L.nr > most_ranges) most_ranges = L.nr;
        if (i % 50 == 0) check_fast_params(&s);
    }

    CHECKF(most <= 254, "with a second window, %u lead bytes", most);
    printf("sample with a second window: at most %u lead bytes, %u ranges\n", most, most_ranges);

    free(w);
    return harness_report("internal");
}
