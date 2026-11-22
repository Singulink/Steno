/*
 * The independent oracle of the tests: see oracle.h. A line-by-line port of ReferenceSteno.cs; slow on purpose. Keep it independent of
 * steno.c.
 */

#include "oracle.h"

#include <string.h>

#define RADIX 248
#define TRAIL_BIAS 8

typedef struct {
    int lo, hi, size;
} seg;

typedef struct {
    seg s[128];
    int n;
} segs;

static int in(int c, int lo, int hi) {
    return lo <= c && c <= hi;
}

void orc_initial(orc_state *s) {
    s->ctx = 0;
    s->slot = 0;
    s->main_start = 0xC0;
    s->main_end = 0x13F;
    s->second_start = s->second_end = ORC_NONE;
    s->conf = 0;
    s->pending_start = ORC_NONE;
}

static int block(int c, int *a, int *b) {
    *a = c - (c % 0x80);
    *b = *a + 0x7F;
    return 0;
}

#define W(a, b)                                                                                                                         \
    do {                                                                                                                                \
        *start = (a);                                                                                                                   \
        *end = (b);                                                                                                                     \
        return 0;                                                                                                                       \
    } while (0)

int orc_window_of(int c, int *start, int *end) {
    if (c <= 0x00BF) return -1;
    if (c <= 0x013F) W(0x00C0, 0x013F);
    if (c <= 0x01BF) W(0x0140, 0x01BF);
    if (c <= 0x023F) W(0x01C0, 0x023F);
    if (c <= 0x02BF) W(0x0240, 0x02BF);
    if (c <= 0x02FF) W(0x02C0, 0x02FF);
    if (c <= 0x036F) W(0x0300, 0x036F);
    if (c <= 0x037F) return -1;
    if (c <= 0x04FF) return block(c, start, end);
    if (c <= 0x052F) W(0x0500, 0x052F);
    if (c <= 0x058F) W(0x0530, 0x058F);
    if (c <= 0x05FF) W(0x0590, 0x05FF);
    if (c <= 0x1FFF) return block(c, start, end);
    if (c <= 0x20FF) return -1;
    if (c <= 0x2FFF) return block(c, start, end);
    if (c <= 0x30FF) return -1;
    if (c <= 0x33FF) return block(c, start, end);
    if (c <= 0x9FFF) return -1;
    if (c <= 0xABFF) return block(c, start, end);
    if (c <= 0xDFFF) return -1;
    if (c <= 0x1FFFF) return block(c, start, end);
    if (c <= 0x3FFFF) return -1;
    if (c <= 0x10FFFF) return block(c, start, end);
    return -1;
}

static void sort_segs(segs *g) {
    int i, j;
    for (i = 1; i < g->n; i++) {
        seg x = g->s[i];
        for (j = i - 1; j >= 0 && g->s[j].lo > x.lo; j--) g->s[j + 1] = g->s[j];
        g->s[j + 1] = x;
    }
}

static void piece(segs *g, int lo, int hi, int size) {
    segs next;
    int i;

    if (lo > hi) return;
    next.n = 0;

    for (i = 0; i < g->n; i++) {
        seg x = g->s[i];

        if (x.hi < lo || x.lo > hi) {
            next.s[next.n++] = x;
            continue;
        }

        if (x.lo < lo) {
            seg y = {x.lo, lo - 1, x.size};
            next.s[next.n++] = y;
        }

        if (x.hi > hi) {
            seg y = {hi + 1, x.hi, x.size};
            next.s[next.n++] = y;
        }
    }

    {
        seg y = {lo, hi, size};
        next.s[next.n++] = y;
    }

    sort_segs(&next);
    *g = next;
}

static long long pow_radix(int e) {
    long long r = 1;
    int i;
    for (i = 0; i < e; i++) r *= RADIX;
    return r;
}

/* A memo of layouts by the fields they depend on (FORMAT.md 5), only to make the fuzzing faster; orc_layout_raw is the port. Not thread
   safe: the oracle is used from one thread. */
int orc_layout_raw(const orc_state *s, orc_range *r);

#define MEMO 4096
static struct {
    int valid, ctx, slot, main_start, second_start, nr;
    orc_range r[32];
} memo[MEMO];

int orc_layout(const orc_state *s, orc_range *r) {
    int slot = s->ctx == 1 ? s->slot : 0;
    unsigned h = ((unsigned)s->main_start * 31u + (unsigned)(s->second_start + 1) * 131u + (unsigned)s->ctx * 7u + (unsigned)slot) % MEMO;
    if (memo[h].valid && memo[h].ctx == s->ctx && memo[h].slot == slot && memo[h].main_start == s->main_start &&
        memo[h].second_start == s->second_start) {
        memcpy(r, memo[h].r, sizeof(orc_range) * (size_t)memo[h].nr);
        return memo[h].nr;
    }
    {
        int nr = orc_layout_raw(s, r);
        if (nr > 0 && nr <= 32) {
            memo[h].valid = 1;
            memo[h].ctx = s->ctx;
            memo[h].slot = slot;
            memo[h].main_start = s->main_start;
            memo[h].second_start = s->second_start;
            memo[h].nr = nr;
            memcpy(memo[h].r, r, sizeof(orc_range) * (size_t)nr);
        }
        return nr;
    }
}

int orc_layout_raw(const orc_state *s, orc_range *r) {
    segs g;
    seg sized[128];
    int nsized = 0, i, j, lead, nr = 0;
    int m0 = s->main_start, m1 = s->main_end;

    g.n = 1;
    g.s[0].lo = 0;
    g.s[0].hi = 0x10FFFF;
    g.s[0].size = 0;

    if (s->ctx == 0) {
        piece(&g, m0 - RADIX > 0x80 ? m0 - RADIX : 0x80, m0 - 1, 2);
        if (m1 < 0x10FFFF) piece(&g, m1 + 1, m1 + RADIX < 0x10FFFF ? m1 + RADIX : 0x10FFFF, 2);
    }

    piece(&g, 0x00, 0x1F, 2);
    piece(&g, 0x09, 0x0A, 1);
    piece(&g, 0x0D, 0x0D, 1);

    if (s->ctx == 1 && s->slot != 0) {
        piece(&g, 0x20, 0x40, 1);
        piece(&g, 0x41, 0x7E, 2);
    } else {
        piece(&g, 0x20, 0x7E, 1);
    }

    piece(&g, 0x7F, 0xBF, 2);
    if (s->ctx != 1) piece(&g, 0x80, 0xEFF, 2);
    piece(&g, 0x2000, 0x20CF, 2);
    if (s->second_start != ORC_NONE) piece(&g, s->second_start, s->second_end, 2);

    switch (s->ctx) {
    case 0: piece(&g, m0, m1, 1); break;
    case 2:
        piece(&g, m0, m1, 2);
        piece(&g, 0x3000, 0x303F, 2);
        piece(&g, 0xAC00, 0xD7A3, 2);
        piece(&g, 0xFF00, 0xFFFF, 2);
        break;
    default:
        piece(&g, m0, m1, 2);
        piece(&g, 0x3000, 0x300F, 1);
        piece(&g, 0x3010, 0x30FF, 2);
        if (s->slot == 1) piece(&g, 0x3041, 0x3096, 1);
        if (s->slot == 2) piece(&g, 0x30A1, 0x30FC, 1);
        piece(&g, 0x4E00, 0x9FFF, 2);
        piece(&g, 0xFF00, 0xFFFF, 2);
        if (s->slot == 0) piece(&g, 0xFF00, 0xFF1F, 1);
        break;
    }

    /* 6.3 */
    i = 0;
    while (i < g.n) {
        int lo, hi, start, k;
        static const int bounds[2] = {0x10FC0, 0x20000};

        if (g.s[i].size != 0) {
            sized[nsized++] = g.s[i];
            i++;
            continue;
        }

        lo = g.s[i].lo;
        hi = g.s[i].hi;
        i++;

        while (i < g.n && g.s[i].size == 0) {
            hi = g.s[i].hi;
            i++;
        }

        start = lo;
        for (k = 0; k < 2; k++) {
            int b = bounds[k];
            if (start < b && hi >= b) {
                long long n = (long long)(b - 1) - start + 1;
                seg y = {start, b - 1, n <= RADIX ? 2 : n <= (long long)RADIX * RADIX ? 3 : 4};
                sized[nsized++] = y;
                start = b;
            }
        }

        {
            long long n = (long long)hi - start + 1;
            seg y = {start, hi, n <= RADIX ? 2 : n <= (long long)RADIX * RADIX ? 3 : 4};
            sized[nsized++] = y;
        }
    }

    lead = 1;
    j = 0;
    while (j < nsized) {
        int lo = sized[j].lo, hi = sized[j].hi, size = sized[j].size;
        long long n, per;
        int leads;
        j++;

        while (j < nsized && sized[j].size == size && sized[j].lo == hi + 1) {
            hi = sized[j].hi;
            j++;
        }

        n = (long long)hi - lo + 1;
        per = pow_radix(size - 1);
        leads = (int)((n + per - 1) / per);
        r[nr].lo = lo;
        r[nr].hi = hi;
        r[nr].size = size;
        r[nr].first = lead;
        r[nr].leads = leads;
        nr++;
        lead += leads;
    }

    if (lead - 1 > 254) return -1;
    return nr;
}

void orc_update(orc_state *s, int c, int n) {
    int ws, we;

    if (c <= 0x7F) {
        if (s->ctx == 1 && (in(c, 0x41, 0x5A) || in(c, 0x61, 0x7A))) s->slot = 0;
        return;
    }

    if (in(c, 0xAC00, 0xD7A3)) {
        s->pending_start = ORC_NONE;
        s->ctx = 2;
        return;
    }

    if (in(c, 0x3040, 0x30FF) || in(c, 0x3400, 0x9FFF) || in(c, 0x20000, 0x3FFFF) || (in(c, 0x3000, 0x303F) && s->ctx != 2)) {
        if (s->ctx != 1) {
            s->ctx = 1;
            s->slot = 0;
        }

        s->pending_start = ORC_NONE;
        if (in(c, 0x3041, 0x3096)) s->slot = 1;
        else if (in(c, 0x30A1, 0x30FA)) s->slot = 2;
        return;
    }

    if (s->ctx != 0 && in(c, 0xFF00, 0xFFFF)) return;

    if (orc_window_of(c, &ws, &we) != 0) return;

    if (s->ctx == 0 && ws == s->main_start) {
        s->conf = s->conf + 1 < 2 ? s->conf + 1 : 2;
        return;
    }

    if (n > 2) {
        s->second_start = ws;
        s->second_end = we;
    }

    if (s->ctx == 0) {
        if (s->conf > 0) {
            s->conf--;
            return;
        }
    } else {
        if (s->pending_start == ORC_NONE || s->pending_start != ws) {
            s->pending_start = ws;
            return;
        }
    }

    if (s->main_start != ws) {
        s->second_start = s->main_start;
        s->second_end = s->main_end;
    } else if (s->second_start != ORC_NONE && s->second_start == ws) {
        s->second_start = s->second_end = ORC_NONE;
    }

    s->main_start = ws;
    s->main_end = we;
    s->ctx = 0;
    s->pending_start = ORC_NONE;
    s->conf = 0;
}

static int encode_one(const orc_range *L, int nr, int c, uint8_t *out) {
    int i, k, n = 0;
    const orc_range *r = NULL;
    long long o, q, rest;

    for (i = 0; i < nr; i++) {
        if (L[i].lo <= c && c <= L[i].hi) {
            r = &L[i];
            break;
        }
    }

    if (r == NULL) return -1;
    o = c - r->lo;
    k = r->size;
    q = o / pow_radix(k - 1);
    rest = o - q * pow_radix(k - 1);
    if (r->first + q > 255) return -1;
    out[n++] = (uint8_t)(r->first + q);

    for (i = k - 2; i >= 0; i--) {
        long long p = pow_radix(i), t = rest / p;
        rest -= t * p;
        out[n++] = (uint8_t)(t + TRAIL_BIAS);
    }

    return n;
}

int orc_encode(const uint32_t *cps, size_t n, uint8_t *out, size_t *out_len, size_t *err) {
    return orc_encode_starts(cps, n, out, out_len, err, NULL);
}

int orc_encode_starts(const uint32_t *cps, size_t n, uint8_t *out, size_t *out_len, size_t *err, size_t *starts) {
    size_t i, len = 0;
    orc_state s;
    orc_range L[64];

    *out_len = 0;
    *err = 0;

    for (i = 0; i < n; i++) {
        if (cps[i] > 0x10FFFF) {
            *err = i;
            return 12;
        }

        if (i > 0 && in((int)cps[i - 1], 0xD800, 0xDBFF) && in((int)cps[i], 0xDC00, 0xDFFF)) {
            *err = i;
            return 11;
        }
    }

    orc_initial(&s);

    for (i = 0; i < n; i++) {
        int nr = orc_layout(&s, L), k;
        if (nr < 0) return -1;
        if (starts != NULL) starts[i] = len;
        k = encode_one(L, nr, (int)cps[i], out + len);
        if (k < 0) return -1;
        len += (size_t)k;
        orc_update(&s, (int)cps[i], k);
    }

    *out_len = len;
    return 0;
}

int orc_decode(const uint8_t *bytes, size_t n, uint32_t *out, size_t *out_n, size_t *err) {
    orc_state s;
    orc_range L[64];
    size_t pos = 0, count = 0;

    *out_n = 0;
    *err = 0;
    orc_initial(&s);

    while (pos < n) {
        int nr = orc_layout(&s, L), i, b = bytes[pos];
        const orc_range *found = NULL;
        size_t start;
        long long o, c;

        if (nr < 0) return -1;
        *err = pos;
        if (b == 0) return 1;

        for (i = 0; i < nr; i++) {
            if (L[i].first <= b && b < L[i].first + L[i].leads) {
                found = &L[i];
                break;
            }
        }

        if (found == NULL) return 2;
        start = pos;
        pos++;
        o = b - found->first;

        for (i = 0; i < found->size - 1; i++) {
            int u;
            if (pos >= n) return 3;
            u = bytes[pos];
            if (u == 0) return 1;
            if (u < TRAIL_BIAS) return 10;
            o = o * RADIX + (u - TRAIL_BIAS);
            pos++;
        }

        c = found->lo + o;
        if (c > found->hi) return 4;

        if (count > 0 && in((int)out[count - 1], 0xD800, 0xDBFF) && in((int)c, 0xDC00, 0xDFFF)) return 5;

        out[count++] = (uint32_t)c;
        orc_update(&s, (int)c, (int)(pos - start));
    }

    *out_n = count;
    return 0;
}
