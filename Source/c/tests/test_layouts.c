/*
 * Every layout of FORMAT.md 13.6 (5 contexts x 7,356 main windows x 7,357 second windows or none, 270,583,104 layouts), built by
 * steno.c (included) and compared range by range with the independent oracle (oracle.c), with the fast-path parameters checked against
 * the oracle's ranges, the bounds that steno.c relies on (fewer than STENO_PRIVATE_MAX_RANGES ranges, at most 254 lead bytes), and the
 * constant layouts compared field by field with a fresh build and with the oracle. Written by the adversarial reviewer of this library.
 * Optional (make exhaustive): about 40 seconds on 10 threads.
 *
 *   test_layouts [THREADS [STRIDE]]      STRIDE (default 1): check every STRIDE-th second window only, for a quick run
 */

#include "../steno.c"

#include "oracle.h"

#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>

static uint32_t W[8000];
static size_t NW;
static unsigned STRIDE = 1;
static const unsigned COMBOS[5][2] = {{CTX_GENERAL, 0}, {CTX_HANGUL, 0}, {CTX_CJK, SLOT_LETTERS}, {CTX_CJK, SLOT_HIRAGANA},
                                      {CTX_CJK, SLOT_KATAKANA}};

typedef struct {
    unsigned id, threads;
    unsigned long long built, bad;
    unsigned most_ranges, most_leads;
} job;

static pthread_mutex_t print_lock = PTHREAD_MUTEX_INITIALIZER;

static void report(job *j, const st_t *s, const char *what) {
    j->bad++;
    if (j->bad <= 5) {
        pthread_mutex_lock(&print_lock);
        printf("MISMATCH ctx %u slot %u main %X second %X: %s\n", s->ctx, s->slot, s->main, s->second, what);
        pthread_mutex_unlock(&print_lock);
    }
}

/* The oracle range that holds c. */
static const orc_range *orc_find(const orc_range *r, int nr, uint32_t c) {
    int i;
    for (i = 0; i < nr; i++) {
        if ((uint32_t)r[i].lo <= c && c <= (uint32_t)r[i].hi) return &r[i];
    }
    return NULL;
}

/* The 1-byte lead of c in the oracle layout, or -1. */
static int orc_one(const orc_range *r, int nr, uint32_t c) {
    const orc_range *x = orc_find(r, nr, c);
    return x != NULL && x->size == 1 ? (int)(x->first + (int)(c - (uint32_t)x->lo)) : -1;
}

static void check_one(job *j, const st_t *s, const layout *L) {
    orc_state o;
    orc_range r[64];
    int nr, i;
    uint32_t c;

    o.ctx = s->ctx;
    o.slot = s->slot;
    o.main_start = (int)s->main;
    o.main_end = (int)window_end(s->main);
    o.second_start = s->second == NONE ? ORC_NONE : (int)s->second;
    o.second_end = s->second == NONE ? ORC_NONE : (int)window_end(s->second);
    o.conf = 0;
    o.pending_start = ORC_NONE;
    nr = orc_layout_raw(&o, r);

    if (nr < 0) {
        report(j, s, "oracle: more than 254 lead bytes");
        return;
    }
    if ((unsigned)nr != L->nr || L->nr >= MAXR) {
        report(j, s, "range count");
        return;
    }
    for (i = 0; i < nr; i++) {
        if (L->lo[i] != (uint32_t)r[i].lo || L->lo[i + 1] != (uint32_t)r[i].hi + 1 || L->size[i] != r[i].size || L->first[i] != r[i].first ||
            L->first[i + 1] - L->first[i] != r[i].leads) {
            report(j, s, "range");
            return;
        }
    }
    if (L->lo[nr] != CP_END) report(j, s, "end");
    if (L->first[nr] != (r[nr - 1].first + r[nr - 1].leads > 255 ? 255 : r[nr - 1].first + r[nr - 1].leads)) report(j, s, "last lead");
    if ((unsigned)nr > j->most_ranges) j->most_ranges = (unsigned)nr;
    if ((unsigned)(r[nr - 1].first + r[nr - 1].leads - 1) > j->most_leads) j->most_leads = (unsigned)(r[nr - 1].first + r[nr - 1].leads - 1);

    /* Fast-path parameters. */
    for (c = 0x20; c <= 0x7E; c++) {
        int one = orc_one(r, nr, c);
        if ((c - 0x20 < L->ascii_n) != (one >= 0)) {
            report(j, s, "ascii_n");
            break;
        }
        if (one >= 0 && one != (int)(c - 0x19)) report(j, s, "ascii lead");
    }
    if (L->one_n > 0) {
        if (orc_one(r, nr, L->one_lo) != L->one_first || orc_one(r, nr, L->one_lo + L->one_n - 1) != (int)(L->one_first + L->one_n - 1)) {
            report(j, s, "one run");
        }
    }
    if (L->punct_n > 0 && orc_one(r, nr, L->punct_lo) != L->punct_first) report(j, s, "punct");
    if (L->fw_n > 0 && orc_one(r, nr, L->fw_lo) != L->fw_first) report(j, s, "fw");
    if (L->wide_n > 0) {
        const orc_range *x = orc_find(r, nr, L->wide_lo), *y = orc_find(r, nr, L->wide_lo + L->wide_n - 1);
        if (x == NULL || x != y || x->size != 2 || (uint32_t)x->lo != L->wide_r0 || x->first != L->wide_first) report(j, s, "wide");
        if (L->wfull_n > 0) {
            /* Every code point of each full lead byte is in the wide run. */
            uint32_t lo = L->wide_r0 + (uint32_t)(L->wfull_first - L->wide_first) * 248u, hi = lo + (uint32_t)L->wfull_n * 248u - 1;
            if (lo < L->wide_lo || hi >= L->wide_lo + L->wide_n) report(j, s, "wfull");
        }
    }
}

static void *work(void *arg) {
    job *j = (job *)arg;
    size_t c, i, k;
    layout L;

    for (c = 0; c < 5; c++) {
        for (i = j->id; i < NW; i += j->threads) {
            for (k = 0; k <= NW; k += (k == NW ? 1 : STRIDE)) {
                st_t s = ST_INITIAL;
                s.ctx = (uint8_t)COMBOS[c][0];
                s.slot = (uint8_t)COMBOS[c][1];
                s.main = W[i];
                s.second = k == NW ? NONE : W[k];
                if (s.second == s.main && s.ctx == CTX_GENERAL) continue;
                build_layout(&L, &s, key_of(&s));
                check_one(j, &s, &L);
                j->built++;
            }
        }
    }
    return NULL;
}

int main(int argc, char **argv) {
    unsigned threads = argc > 1 ? (unsigned)atoi(argv[1]) : 4, t;
    pthread_t th[64];
    job jobs[64];
    unsigned long long built = 0, bad = 0;
    unsigned most_ranges = 0, most_leads = 0;
    size_t i;
    uint32_t c = 0;

    if (argc > 2) STRIDE = (unsigned)atoi(argv[2]);
    if (threads < 1 || threads > 64) threads = 4;

    while (c <= 0x10FFFF) {
        uint32_t x = window_of(c);
        if (x == NONE) {
            c++;
        } else {
            W[NW++] = x;
            c = window_end(x) + 1;
        }
    }
    printf("%zu windows\n", NW);

    /* The constant layouts: every field against a fresh build, and against the oracle. */
    for (i = 0; i < N_STATIC; i++) {
        const layout *S = &STATIC_LAYOUTS[i];
        st_t s = ST_INITIAL;
        layout L;
        job j0 = {0, 1, 0, 0, 0, 0};
        memset(&L, 0, sizeof L);
        s.ctx = (uint8_t)(S->key & 3);
        s.slot = (uint8_t)((S->key >> 2) & 3);
        s.main = (uint32_t)((S->key >> 4) & 0x1FFFFF);
        s.second = (S->key >> 25) == 0x3FFFFF ? NONE : (uint32_t)(S->key >> 25);
        build_layout(&L, &s, key_of(&s));
        if (key_of(&s) != S->key || L.nr != S->nr || memcmp(L.lo, S->lo, (L.nr + 1) * 4) != 0 || memcmp(L.first, S->first, L.nr + 1) != 0 ||
            memcmp(L.size, S->size, L.nr) != 0 || L.one_lo != S->one_lo || L.one_n != S->one_n || L.wide_lo != S->wide_lo ||
            L.wide_n != S->wide_n || L.wide_r0 != S->wide_r0 || L.punct_lo != S->punct_lo || L.punct_n != S->punct_n || L.fw_lo != S->fw_lo ||
            L.fw_n != S->fw_n || L.ascii_n != S->ascii_n || L.one_first != S->one_first || L.wide_first != S->wide_first ||
            L.wfull_first != S->wfull_first || L.wfull_n != S->wfull_n || L.punct_first != S->punct_first || L.fw_first != S->fw_first) {
            printf("MISMATCH constant layout %zu\n", i);
            bad++;
        }
        check_one(&j0, &s, S);
        bad += j0.bad;
    }
    printf("%u constant layouts checked\n", (unsigned)N_STATIC);

    for (t = 0; t < threads; t++) {
        memset(&jobs[t], 0, sizeof jobs[t]);
        jobs[t].id = t;
        jobs[t].threads = threads;
        pthread_create(&th[t], NULL, work, &jobs[t]);
    }
    for (t = 0; t < threads; t++) {
        pthread_join(th[t], NULL);
        built += jobs[t].built;
        bad += jobs[t].bad;
        if (jobs[t].most_ranges > most_ranges) most_ranges = jobs[t].most_ranges;
        if (jobs[t].most_leads > most_leads) most_leads = jobs[t].most_leads;
    }

    printf("%llu layouts compared with the oracle, %llu mismatches, at most %u ranges (MAXR %u), at most %u lead bytes\n", built, bad,
           most_ranges, (unsigned)MAXR, most_leads);
    return bad != 0;
}
