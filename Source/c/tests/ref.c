/* The plain reference codec of the tests. See ref.h. */

#include "ref.h"

#include <stdio.h>
#include <stdlib.h>

#define RADIX 248u

/* ---------- Section 4: windows ---------- */

uint32_t ref_window(uint32_t c) {
    uint32_t blk = c - c % 0x80u;

    if (c <= 0x00BF) return REF_NONE;
    if (c <= 0x013F) return 0x00C0;
    if (c <= 0x01BF) return 0x0140;
    if (c <= 0x023F) return 0x01C0;
    if (c <= 0x02BF) return 0x0240;
    if (c <= 0x02FF) return 0x02C0;
    if (c <= 0x036F) return 0x0300;
    if (c <= 0x037F) return REF_NONE;
    if (c <= 0x04FF) return blk;
    if (c <= 0x052F) return 0x0500;
    if (c <= 0x058F) return 0x0530;
    if (c <= 0x05FF) return 0x0590;
    if (c <= 0x1FFF) return blk;
    if (c <= 0x20FF) return REF_NONE;
    if (c <= 0x2FFF) return blk;
    if (c <= 0x30FF) return REF_NONE;
    if (c <= 0x33FF) return blk;
    if (c <= 0x9FFF) return REF_NONE;
    if (c <= 0xABFF) return blk;
    if (c <= 0xDFFF) return REF_NONE;
    if (c <= 0x1FFFF) return blk;
    if (c <= 0x3FFFF) return REF_NONE;
    if (c <= 0x10FFFF) return blk;
    fprintf(stderr, "ref_window: not a code point\n");
    abort();
}

uint32_t ref_window_end(uint32_t w) {
    if (w == 0x02C0) return 0x02FF;
    if (w == 0x0300) return 0x036F;
    if (w == 0x0500) return 0x052F;
    if (w == 0x0530) return 0x058F;
    if (w == 0x0590) return 0x05FF;
    return w + 0x7F;
}

void ref_initial(ref_state *s) {
    s->ctx = 0;
    s->slot = 0;
    s->main = 0x00C0;
    s->second = REF_NONE;
    s->pending = REF_NONE;
    s->conf = 0;
}

/* ---------- Section 6: layout ---------- */

/* Segments that cover U+0000..U+10FFFF in order; size 0 means that no piece covers them. */
typedef struct {
    uint32_t lo, hi;
    unsigned size;
} seg;

typedef struct {
    seg s[128];
    size_t n;
} segs;

/* Lays a piece over the segments: the code points lo..hi get the size, whatever they had. */
static void add(segs *g, uint32_t lo, uint32_t hi, unsigned size) {
    if (g->n >= sizeof g->s / sizeof g->s[0]) {
        fprintf(stderr, "ref_layout: too many segments\n");
        abort();
    }

    g->s[g->n].lo = lo;
    g->s[g->n].hi = hi;
    g->s[g->n].size = size;
    g->n++;
}

static void piece(segs *g, uint32_t lo, uint32_t hi, unsigned size) {
    segs out;
    size_t i;

    out.n = 0;

    /* What lies below the piece, the piece, then what lies above it. */
    for (i = 0; i < g->n; i++) {
        if (g->s[i].lo < lo) add(&out, g->s[i].lo, g->s[i].hi < lo - 1 ? g->s[i].hi : lo - 1, g->s[i].size);
    }

    add(&out, lo, hi, size);

    for (i = 0; i < g->n; i++) {
        if (g->s[i].hi > hi) add(&out, g->s[i].lo > hi + 1 ? g->s[i].lo : hi + 1, g->s[i].hi, g->s[i].size);
    }

    *g = out;
}

static unsigned part_size(uint32_t n) {
    if (n <= 248) return 2;
    if (n <= 61504) return 3;
    return 4;
}

static uint32_t pow248(unsigned e) {
    uint32_t r = 1;
    while (e-- > 0) r *= RADIX;
    return r;
}

size_t ref_layout(const ref_state *s, ref_range *r) {
    segs g;
    seg sized[256];
    size_t ns = 0, i, nr = 0;
    uint32_t m0 = s->main, m1 = ref_window_end(s->main), lead = 1;

    g.n = 1;
    g.s[0].lo = 0;
    g.s[0].hi = 0x10FFFF;
    g.s[0].size = 0;

    /* 6.2, piece 1 */
    if (s->ctx == 0) {
        piece(&g, m0 >= 0x80 + 248 ? m0 - 248 : 0x80, m0 - 1, 2);
        if (m1 < 0x10FFFF) piece(&g, m1 + 1, m1 + 248 > 0x10FFFF ? 0x10FFFF : m1 + 248, 2);
    }

    /* piece 2 */
    piece(&g, 0x00, 0x1F, 2);
    piece(&g, 0x09, 0x0A, 1);
    piece(&g, 0x0D, 0x0D, 1);

    /* piece 3 */
    if (s->ctx == 1 && s->slot != 0) {
        piece(&g, 0x20, 0x40, 1);
        piece(&g, 0x41, 0x7E, 2);
    } else {
        piece(&g, 0x20, 0x7E, 1);
    }

    /* pieces 4, 5 and 6 */
    piece(&g, 0x7F, 0xBF, 2);
    if (s->ctx != 1) piece(&g, 0x80, 0xEFF, 2);
    piece(&g, 0x2000, 0x20CF, 2);

    /* piece 7 */
    if (s->second != REF_NONE) piece(&g, s->second, ref_window_end(s->second), 2);

    /* piece 8 */
    if (s->ctx == 0) {
        piece(&g, m0, m1, 1);
    } else if (s->ctx == 2) {
        piece(&g, m0, m1, 2);
        piece(&g, 0x3000, 0x303F, 2);
        piece(&g, 0xAC00, 0xD7A3, 2);
        piece(&g, 0xFF00, 0xFFFF, 2);
    } else {
        piece(&g, m0, m1, 2);
        piece(&g, 0x3000, 0x300F, 1);
        piece(&g, 0x3010, 0x30FF, 2);
        if (s->slot == 1) piece(&g, 0x3041, 0x3096, 1);
        if (s->slot == 2) piece(&g, 0x30A1, 0x30FC, 1);
        piece(&g, 0x4E00, 0x9FFF, 2);
        piece(&g, 0xFF00, 0xFFFF, 2);
        if (s->slot == 0) piece(&g, 0xFF00, 0xFF1F, 1);
    }

    /* 6.3: join the uncovered segments into stretches, cut them at U+10FC0 and U+20000, and size the parts. */
    i = 0;
    while (i < g.n) {
        uint32_t lo, hi, b[2] = {0x10FC0, 0x20000};
        unsigned j;

        if (g.s[i].size != 0) {
            sized[ns++] = g.s[i++];
            continue;
        }

        lo = g.s[i].lo;
        hi = g.s[i].hi;
        i++;

        while (i < g.n && g.s[i].size == 0) hi = g.s[i++].hi;

        for (j = 0; j < 2; j++) {
            if (lo < b[j] && hi >= b[j]) {
                sized[ns].lo = lo;
                sized[ns].hi = b[j] - 1;
                sized[ns].size = part_size(b[j] - lo);
                ns++;
                lo = b[j];
            }
        }

        sized[ns].lo = lo;
        sized[ns].hi = hi;
        sized[ns].size = part_size(hi - lo + 1);
        ns++;
    }

    /* 6.4 and 6.5: maximal runs of one size are the ranges; then the lead bytes. */
    i = 0;
    while (i < ns) {
        uint32_t lo = sized[i].lo, hi = sized[i].hi, per;
        unsigned size = sized[i].size;
        i++;

        while (i < ns && sized[i].size == size && sized[i].lo == hi + 1) hi = sized[i++].hi;

        per = pow248(size - 1);
        r[nr].lo = lo;
        r[nr].hi = hi;
        r[nr].size = size;
        r[nr].first = lead;
        r[nr].leads = (hi - lo + 1 + per - 1) / per;
        lead += r[nr].leads;
        nr++;
    }

    if (lead - 1 > 254) {
        fprintf(stderr, "ref_layout: more than 254 lead bytes\n");
        abort();
    }

    return nr;
}

/* ---------- Section 7: the state update ---------- */

static int in(uint32_t c, uint32_t lo, uint32_t hi) {
    return lo <= c && c <= hi;
}

void ref_update(ref_state *s, uint32_t c, unsigned k) {
    uint32_t w;

    /* Step 1 */
    if (c <= 0x7F) {
        if (s->ctx == 1 && (in(c, 0x41, 0x5A) || in(c, 0x61, 0x7A))) s->slot = 0;
        return;
    }

    /* Step 2 */
    if (in(c, 0xAC00, 0xD7A3)) {
        s->pending = REF_NONE;
        s->ctx = 2;
        return;
    }

    /* Step 3 */
    if (in(c, 0x3040, 0x30FF) || in(c, 0x3400, 0x9FFF) || in(c, 0x20000, 0x3FFFF) || (in(c, 0x3000, 0x303F) && s->ctx != 2)) {
        if (s->ctx != 1) {
            s->ctx = 1;
            s->slot = 0;
        }
        s->pending = REF_NONE;
        if (in(c, 0x3041, 0x3096)) s->slot = 1;
        else if (in(c, 0x30A1, 0x30FA)) s->slot = 2;
        return;
    }

    /* Step 4 */
    if (s->ctx != 0 && in(c, 0xFF00, 0xFFFF)) return;

    /* Step 5 */
    w = ref_window(c);
    if (w == REF_NONE) return;

    /* Step 6 */
    if (s->ctx == 0 && w == s->main) {
        s->conf = s->conf + 1 > 2 ? 2 : s->conf + 1;
        return;
    }

    /* Step 7 */
    if (k == 3 || k == 4) s->second = w;

    /* Step 8 */
    if (s->ctx == 0) {
        if (s->conf > 0) {
            s->conf--;
            return;
        }
    } else if (s->pending != w) {
        s->pending = w;
        return;
    }

    /* Step 9 */
    if (s->main != w) s->second = s->main;
    else if (s->second == w) s->second = REF_NONE;
    s->main = w;
    s->ctx = 0;
    s->pending = REF_NONE;
    s->conf = 0;
}

/* ---------- Sections 8 and 9 ---------- */

int ref_encode(const uint32_t *cps, size_t n, uint8_t *out, size_t *out_len, size_t *err_index, size_t *starts) {
    ref_state s;
    ref_range r[64];
    size_t i, len = 0;

    *out_len = 0;

    for (i = 0; i < n; i++) {
        if (cps[i] > 0x10FFFF) {
            *err_index = i;
            return 12;
        }

        if (i > 0 && in(cps[i - 1], 0xD800, 0xDBFF) && in(cps[i], 0xDC00, 0xDFFF)) {
            *err_index = i;
            return 11;
        }
    }

    ref_initial(&s);

    for (i = 0; i < n; i++) {
        uint32_t c = cps[i], o, q, rest, p;
        size_t nr = ref_layout(&s, r), j;
        const ref_range *g = NULL;
        unsigned t;

        for (j = 0; j < nr; j++) {
            if (r[j].lo <= c && c <= r[j].hi) g = &r[j];
        }

        if (starts != NULL) starts[i] = len;
        o = c - g->lo;
        p = pow248(g->size - 1);
        q = o / p;
        rest = o - q * p;
        out[len++] = (uint8_t)(g->first + q);

        for (t = g->size - 1; t > 0; t--) {
            p = pow248(t - 1);
            out[len++] = (uint8_t)(rest / p + 8);
            rest %= p;
        }

        ref_update(&s, c, g->size);
    }

    *out_len = len;
    return 0;
}

int ref_decode(const uint8_t *bytes, size_t n, uint32_t *out, size_t *out_n, size_t *err_pos) {
    ref_state s;
    ref_range r[64];
    size_t pos = 0, count = 0;

    ref_initial(&s);
    *out_n = 0;

    while (pos < n) {
        size_t nr = ref_layout(&s, r), j, start = pos;
        unsigned b = bytes[pos], t;
        const ref_range *g = NULL;
        uint64_t o, c;

        *err_pos = start;
        if (b == 0) return 1;

        for (j = 0; j < nr; j++) {
            if (r[j].first <= b && b < r[j].first + r[j].leads) g = &r[j];
        }

        if (g == NULL) return 2;

        pos++;
        o = b - g->first;

        for (t = 1; t < g->size; t++) {
            unsigned u;
            if (pos >= n) return 3;
            u = bytes[pos];
            if (u == 0) return 1;
            if (u < 8) return 10;
            o = o * RADIX + (u - 8);
            pos++;
        }

        c = g->lo + o;
        if (c > g->hi) return 4;
        if (count > 0 && in(out[count - 1], 0xD800, 0xDBFF) && in((uint32_t)c, 0xDC00, 0xDFFF)) return 5;

        out[count++] = (uint32_t)c;
        ref_update(&s, (uint32_t)c, g->size);
    }

    *out_n = count;
    return 0;
}
