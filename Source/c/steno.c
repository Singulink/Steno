/*
 * steno.c: the C reference implementation of the Steno Encoding, version 1. See steno.h for the interface and FORMAT.md for the format.
 *
 * Copyright (c) 2025 Singulink and Contributors. MIT License: see LICENSE.md in the root of the repository.
 *
 * How it works, in short:
 *
 * - The state (FORMAT.md 5) is a small struct. The layout of a state (6) depends only on the context, the kana slot, the main window and
 *   the second window. It is built as a short array of ranges (at most STENO_PRIVATE_MAX_RANGES), with a few parameters for the fast
 *   paths. Building one costs about as much as encoding a few dozen code points and needs no memory but the struct, so text that reaches
 *   a new state at every code point costs a bounded time per code point. There are no larger lookup tables: a code point or a lead byte
 *   is found in the ranges by a binary search.
 * - Each call keeps up to STENO_PRIVATE_LAYOUT_CACHE layouts: on its stack for the one-shot functions, in the object for the streaming
 *   ones. The library has no global state that it writes, so it needs no locks and no thread-local storage. The layouts that values
 *   reach first (the initial state, the CJK and Hangul contexts, and the first switch to the window of a common script) are constants,
 *   so that a short value in one script needs no layout built.
 * - The code points that take 1 byte (printable ASCII, the main window in the general context, the kana and U+3000..U+300F in the CJK
 *   context) and the ideographs and Hangul syllables of the wide contexts never change the layout (14.2), and are encoded and decoded in
 *   tight loops without a state update. Runs of printable ASCII use vector code (SSE2 on x86, NEON on little-endian AArch64) or 8 bytes
 *   at a time in a 64-bit word; so do runs of the main window mixed with printable ASCII in UTF-16. The ideographs and syllables use an
 *   exact division by 248 without a divide instruction. Everything else takes the general path, one code point at a time.
 *
 * Compile-time options: STENO_NO_SIMD turns off the vector code (the scalar code is used on every other machine anyway).
 */

#include "steno.h"

#include <limits.h>
#include <string.h>

/* ---------- Compiler and machine ---------- */

#if !defined(STENO_NO_SIMD)
#if defined(__SSE2__) || defined(_M_X64) || defined(_M_AMD64) || (defined(_M_IX86_FP) && _M_IX86_FP >= 2)
#define STENO_SSE2 1
#include <emmintrin.h>
#elif (defined(__aarch64__) || defined(_M_ARM64)) && (defined(__ARM_NEON) || defined(_M_ARM64)) && !defined(__ARM_BIG_ENDIAN) &&       \
    !(defined(__BYTE_ORDER__) && defined(__ORDER_BIG_ENDIAN__) && __BYTE_ORDER__ == __ORDER_BIG_ENDIAN__)
/* NEON on little-endian AArch64 only: the lane order of the reinterpretations below would differ on big-endian. */
#define STENO_NEON 1
#include <arm_neon.h>
#endif
#endif

#if defined(_MSC_VER)
#define STENO_INLINE static __forceinline
#define STENO_NOINLINE static __declspec(noinline)
#elif defined(__GNUC__) || defined(__clang__)
#define STENO_INLINE static inline __attribute__((always_inline))
#define STENO_NOINLINE static __attribute__((noinline))
#else
#define STENO_INLINE static inline
#define STENO_NOINLINE static
#endif

/* A compile-time check: a negative array size if the condition is false. */
#define STENO_STATIC_ASSERT(cond, name) typedef char steno_static_assert_##name[(cond) ? 1 : -1]

typedef steno_private_layout layout;
typedef steno_private_cache lcache;
typedef steno_private_state st_t;
typedef steno_private_progress progress;

#define MAXR STENO_PRIVATE_MAX_RANGES
#define NCACHE STENO_PRIVATE_LAYOUT_CACHE
#define NONE 0xFFFFFFFFu
#define CP_END 0x110000u
#define KEY_NONE UINT64_MAX

enum { CTX_GENERAL = 0, CTX_CJK = 1, CTX_HANGUL = 2 };
enum { SLOT_LETTERS = 0, SLOT_HIRAGANA = 1, SLOT_KATAKANA = 2 };

STENO_STATIC_ASSERT(NCACHE >= 1, cache_has_an_entry);

/* ---------- Windows (FORMAT.md 4) ---------- */

/* The first code point of the window of c, or NONE. */
static uint32_t window_of(uint32_t c) {
    const uint32_t block = c & ~(uint32_t)0x7F;

    if (c < 0x00C0u) return NONE;
    if (c < 0x02C0u) return ((c - 0x00C0u) & ~(uint32_t)0x7F) + 0x00C0u;
    if (c < 0x0300u) return 0x02C0u;
    if (c < 0x0370u) return 0x0300u;
    if (c < 0x0380u) return NONE;
    if (c < 0x0500u) return block;
    if (c < 0x0530u) return 0x0500u;
    if (c < 0x0590u) return 0x0530u;
    if (c < 0x0600u) return 0x0590u;
    if (c < 0x2000u) return block;
    if (c < 0x2100u) return NONE;
    if (c < 0x3000u) return block;
    if (c < 0x3100u) return NONE;
    if (c < 0x3400u) return block;
    if (c < 0xA000u) return NONE;
    if (c < 0xAC00u) return block;
    if (c < 0xE000u) return NONE;
    if (c < 0x20000u) return block;
    if (c < 0x40000u) return NONE;
    if (c < CP_END) return block;
    return NONE;
}

/* The last code point of the window that starts at w. */
static uint32_t window_end(uint32_t w) {
    switch (w) {
    case 0x02C0u: return 0x02FFu;
    case 0x0300u: return 0x036Fu;
    case 0x0500u: return 0x052Fu;
    case 0x0530u: return 0x058Fu;
    case 0x0590u: return 0x05FFu;
    default: return w + 0x7Fu;
    }
}

/* ---------- State (5) and state update (7) ---------- */

static const st_t ST_INITIAL = {0x00C0u, NONE, NONE, CTX_GENERAL, SLOT_LETTERS, 0, 0};

/* The key of the layout of a state: context, kana slot (CJK only), main window and second window. Every key is below 2^47. */
STENO_INLINE uint64_t key_of(const st_t *s) {
    uint64_t second = s->second == NONE ? 0x3FFFFFu : s->second;
    uint64_t slot = s->ctx == CTX_CJK ? s->slot : 0;
    return (uint64_t)s->ctx | (slot << 2) | ((uint64_t)s->main << 4) | (second << 25);
}

#define KEY_INITIAL (((uint64_t)0x00C0u << 4) | ((uint64_t)0x3FFFFFu << 25))

/* Updates the state after code point c, which took k bytes (FORMAT.md 7.1). */
static void update(st_t *s, uint32_t c, unsigned k) {
    uint32_t w;

    /* Step 1. */
    if (c < 0x80u) {
        if (s->ctx == CTX_CJK && ((c - 0x41u) < 26u || (c - 0x61u) < 26u)) s->slot = SLOT_LETTERS;
        return;
    }

    /* Step 2: a Hangul syllable. */
    if (c - 0xAC00u <= 0xD7A3u - 0xAC00u) {
        s->pending = NONE;
        s->ctx = CTX_HANGUL;
        return;
    }

    /* Step 3: a CJK code point. */
    if (c - 0x3040u <= 0x30FFu - 0x3040u || c - 0x3400u <= 0x9FFFu - 0x3400u || c - 0x20000u <= 0x3FFFFu - 0x20000u ||
        (c - 0x3000u <= 0x303Fu - 0x3000u && s->ctx != CTX_HANGUL)) {
        if (s->ctx != CTX_CJK) {
            s->ctx = CTX_CJK;
            s->slot = SLOT_LETTERS;
        }

        s->pending = NONE;

        if (c - 0x3041u <= 0x3096u - 0x3041u) s->slot = SLOT_HIRAGANA;
        else if (c - 0x30A1u <= 0x30FAu - 0x30A1u) s->slot = SLOT_KATAKANA;

        return;
    }

    /* Step 4. */
    if (s->ctx != CTX_GENERAL && c - 0xFF00u <= 0xFFu) return;

    /* Step 5. */
    w = window_of(c);
    if (w == NONE) return;

    /* Step 6. */
    if (s->ctx == CTX_GENERAL && w == s->main) {
        if (s->conf < 2) s->conf++;
        return;
    }

    /* Step 7. */
    if (k >= 3) s->second = w;

    /* Step 8. */
    if (s->ctx == CTX_GENERAL) {
        if (s->conf > 0) {
            s->conf--;
            return;
        }
    } else if (s->pending != w) {
        s->pending = w;
        return;
    }

    /* Step 9: a switch to w. */
    if (s->main != w) s->second = s->main;
    else if (s->second == w) s->second = NONE;

    s->main = w;
    s->ctx = CTX_GENERAL;
    s->pending = NONE;
    s->conf = 0;
}

/* ---------- Layout (6) ---------- */

/* A step function over the code points: from `at` up to the next step, the size is `size` (0: no piece covers it). Each list starts at 0
   and ends with a step at CP_END. */
typedef struct {
    uint32_t at, size;
} step_t;

#define STEP_END {CP_END, 0}

/* Pieces 2 to 6, which lie under the second window and the main window, by context and kana slot. The cuts of 6.3 at U+10FC0 and
   U+20000 are steps of their own, so that a stretch is never one interval across them. */
static const step_t B_GENERAL[] = {{0x0000, 2}, {0x0009, 1}, {0x000B, 2}, {0x000D, 1},  {0x000E, 2},  {0x0020, 1},  {0x007F, 2},
                                   {0x0F00, 0}, {0x2000, 2}, {0x20D0, 0}, {0x10FC0, 0}, {0x20000, 0}, STEP_END};
static const step_t B_CJK_LETTERS[] = {{0x0000, 2}, {0x0009, 1}, {0x000B, 2}, {0x000D, 1},  {0x000E, 2},  {0x0020, 1},  {0x007F, 2},
                                       {0x00C0, 0}, {0x2000, 2}, {0x20D0, 0}, {0x10FC0, 0}, {0x20000, 0}, STEP_END};
static const step_t B_CJK_KANA[] = {{0x0000, 2}, {0x0009, 1}, {0x000B, 2}, {0x000D, 1}, {0x000E, 2},  {0x0020, 1},  {0x0041, 2},
                                    {0x00C0, 0}, {0x2000, 2}, {0x20D0, 0}, {0x10FC0, 0}, {0x20000, 0}, STEP_END};

/* The pieces of 8 that lie over the main window, by context and kana slot. */
static const step_t T_GENERAL[] = {{0, 0}, STEP_END};
static const step_t T_HANGUL[] = {{0, 0}, {0x3000, 2}, {0x3040, 0}, {0xAC00, 2}, {0xD7A4, 0}, {0xFF00, 2}, {0x10000, 0}, STEP_END};
static const step_t T_CJK_LETTERS[] = {{0, 0},      {0x3000, 1}, {0x3010, 2}, {0x3100, 0},  {0x4E00, 2},
                                       {0xA000, 0}, {0xFF00, 1}, {0xFF20, 2}, {0x10000, 0}, STEP_END};
static const step_t T_CJK_HIRAGANA[] = {{0, 0},      {0x3000, 1}, {0x3010, 2}, {0x3041, 1}, {0x3097, 2}, {0x3100, 0},
                                        {0x4E00, 2}, {0xA000, 0}, {0xFF00, 2}, {0x10000, 0}, STEP_END};
static const step_t T_CJK_KATAKANA[] = {{0, 0},      {0x3000, 1}, {0x3010, 2}, {0x30A1, 1}, {0x30FD, 2}, {0x3100, 0},
                                        {0x4E00, 2}, {0xA000, 0}, {0xFF00, 2}, {0x10000, 0}, STEP_END};

static void add_step(step_t *a, unsigned *n, uint32_t at, uint32_t size) {
    if (*n > 0 && a[*n - 1].at == at) {
        a[*n - 1].size = size;
    } else {
        a[*n].at = at;
        a[*n].size = size;
        (*n)++;
    }
}

/* Which of the three runs of 6.3 holds c. */
STENO_INLINE unsigned run_of(uint32_t c) {
    return (unsigned)(c >= 0x10FC0u) + (unsigned)(c >= 0x20000u);
}

/* Adds a range of size `size` from `lo` (to the start of the next one), joining it with the range before if that has the same size. */
STENO_INLINE void push_range(layout *L, uint32_t lo, uint32_t size) {
    if (L->nr > 0 && L->size[L->nr - 1] == size) return;

    /* Never more than MAXR (see the tests, which build every layout without a second window and the fullest ones with one). */
    if (L->nr < MAXR) {
        L->lo[L->nr] = lo;
        L->size[L->nr] = (uint8_t)size;
        L->nr++;
    }
}

/* Adds a part of a stretch (6.3), sized by its length. */
STENO_INLINE void push_part(layout *L, uint32_t lo, uint32_t hi) {
    uint32_t n = hi - lo + 1;
    push_range(L, lo, n <= 248u ? 2u : n <= 61504u ? 3u : 4u);
}

/* The index of the range that holds c (c <= 0x10FFFF): the last range whose first code point is at most c. */
STENO_INLINE unsigned range_of(const layout *L, uint32_t c) {
    unsigned i = 0, n = L->nr;

    while (n > 1) {
        unsigned half = n / 2;
        if (L->lo[i + half] <= c) i += half;
        n -= half;
    }

    return i;
}

/* The index of the range that has lead byte b, which must be at least 1 and below L->first[L->nr]. */
STENO_INLINE unsigned range_of_lead(const layout *L, uint32_t b) {
    unsigned i = 0, n = L->nr;

    while (n > 1) {
        unsigned half = n / 2;
        if (L->first[i + half] <= b) i += half;
        n -= half;
    }

    return i;
}

/* The lead byte of code point c of a range of size 1. */
static uint8_t one_byte(const layout *L, uint32_t c) {
    unsigned i = range_of(L, c);
    return (uint8_t)(L->first[i] + (c - L->lo[i]));
}

/* Builds the layout of state s (6.1 to 6.5), and the parameters of the fast paths. */
STENO_NOINLINE void build_layout(layout *L, const st_t *s, uint64_t key) {
    const unsigned ctx = s->ctx, slot = ctx == CTX_CJK ? s->slot : SLOT_LETTERS;
    const uint32_t m0 = s->main, m1 = window_end(m0), msize = ctx == CTX_GENERAL ? 1u : 2u;
    const step_t *pb, *pt, *pu, *pn;
    step_t U[6], N[6];
    unsigned nu = 0, nn = 0, i;
    uint32_t c = 0, lead = 1, plo = 0, phi = 0;
    int has_part = 0;

    if (ctx == CTX_GENERAL) {
        pb = B_GENERAL;
        pt = T_GENERAL;
    } else if (ctx == CTX_HANGUL) {
        pb = B_GENERAL;
        pt = T_HANGUL;
    } else {
        pb = slot == SLOT_LETTERS ? B_CJK_LETTERS : B_CJK_KANA;
        pt = slot == SLOT_LETTERS ? T_CJK_LETTERS : slot == SLOT_HIRAGANA ? T_CJK_HIRAGANA : T_CJK_KATAKANA;
    }

    /* Pieces 7 and 8 (the main window): the second window and the main window, which never overlap. */
    add_step(U, &nu, 0, 0);

    if (s->second != NONE && s->second < m0) {
        add_step(U, &nu, s->second, 2);
        add_step(U, &nu, window_end(s->second) + 1, 0);
    }

    add_step(U, &nu, m0, msize);
    add_step(U, &nu, m1 + 1, 0);

    if (s->second != NONE && s->second > m0) {
        add_step(U, &nu, s->second, 2);
        add_step(U, &nu, window_end(s->second) + 1, 0);
    }

    add_step(U, &nu, CP_END, 0);

    /* Piece 1: the neighbours, in the general context. */
    add_step(N, &nn, 0, 0);

    if (ctx == CTX_GENERAL) {
        add_step(N, &nn, m0 >= 0x80u + 248u ? m0 - 248u : 0x80u, 2);
        add_step(N, &nn, m0, 0);

        if (m1 < 0x10FFFFu) {
            add_step(N, &nn, m1 + 1, 2);
            add_step(N, &nn, (m1 + 248u > 0x10FFFFu ? 0x10FFFFu : m1 + 248u) + 1, 0);
        }
    }

    add_step(N, &nn, CP_END, 0);
    pu = U;
    pn = N;
    L->nr = 0;

    /* Walk the intervals between the steps of the four lists. Over each, the size is that of the last piece that covers it: the pieces
       of 8 over the main window, then the main window, the second window, pieces 2 to 6, and the neighbours. */
    while (c < CP_END) {
        uint32_t next = pb[1].at, size;

        if (pt[1].at < next) next = pt[1].at;
        if (pu[1].at < next) next = pu[1].at;
        if (pn[1].at < next) next = pn[1].at;

        size = pt->size ? pt->size : pu->size ? pu->size : pb->size ? pb->size : pn->size;

        if (size == 0) {
            /* An interval of a stretch: it joins the part before it if it is in the same run of 6.3. */
            if (has_part && run_of(plo) == run_of(c)) {
                phi = next - 1;
            } else {
                if (has_part) push_part(L, plo, phi);
                plo = c;
                phi = next - 1;
                has_part = 1;
            }
        } else {
            if (has_part) push_part(L, plo, phi);
            has_part = 0;
            push_range(L, c, size);
        }

        c = next;
        if (pb[1].at == c) pb++;
        if (pt[1].at == c) pt++;
        if (pu[1].at == c) pu++;
        if (pn[1].at == c) pn++;
    }

    if (has_part) push_part(L, plo, phi);

    L->lo[L->nr] = CP_END;

    /* 6.5: the lead bytes, ceil(n / 248^(size - 1)) per range, with constant divisors. At most 254 (13.6), so they fit in a byte. */
    for (i = 0; i < L->nr; i++) {
        uint32_t n = L->lo[i + 1] - L->lo[i];
        L->first[i] = (uint8_t)lead;

        switch (L->size[i]) {
        case 1: lead += n; break;
        case 2: lead += (n + 247u) / 248u; break;
        case 3: lead += (n + 61503u) / 61504u; break;
        default: lead += 1; break; /* fewer than 248^3 code points */
        }
    }

    L->first[L->nr] = (uint8_t)(lead > 0xFFu ? 0xFFu : lead);
    L->key = key;

    /* The fast paths. A run with n 0 is empty. */
    L->ascii_n = (uint8_t)(ctx == CTX_CJK && slot != SLOT_LETTERS ? 0x21u : 0x5Fu);
    L->one_lo = L->one_n = L->wide_lo = L->wide_n = L->wide_r0 = L->punct_lo = L->punct_n = L->fw_lo = L->fw_n = 0;
    L->one_first = L->wide_first = L->wfull_first = L->wfull_n = L->punct_first = L->fw_first = 0;

    if (ctx == CTX_GENERAL) {
        L->one_lo = m0;
        L->one_n = m1 - m0 + 1;
    } else if (ctx == CTX_CJK) {
        if (slot == SLOT_HIRAGANA) {
            L->one_lo = 0x3041u;
            L->one_n = 0x3096u - 0x3041u + 1;
        } else if (slot == SLOT_KATAKANA) {
            L->one_lo = 0x30A1u;
            L->one_n = 0x30FCu - 0x30A1u + 1;
        } else {
            L->fw_lo = 0xFF00u;
            L->fw_n = 0x20u;
            L->fw_first = one_byte(L, 0xFF00u);
        }

        L->punct_lo = 0x3000u;
        L->punct_n = 0x10u;
        L->punct_first = one_byte(L, 0x3000u);
        L->wide_lo = 0x4E00u;
        L->wide_n = 0x9FFFu - 0x4E00u + 1;
    } else {
        L->wide_lo = 0xAC00u;
        L->wide_n = 0xD7A3u - 0xAC00u + 1;
    }

    if (L->one_n > 0) L->one_first = one_byte(L, L->one_lo);

    if (L->wide_n > 0) {
        /* The ideographs or syllables lie in one range of size 2 (14.3); its first code point may be before them. The full lead bytes are
           those all of whose 248 code points are ideographs or syllables: the decoder takes them in the loop. */
        uint32_t r = range_of(L, L->wide_lo);
        uint32_t leads = (uint32_t)L->first[r + 1] - L->first[r];
        uint32_t jmin = (L->wide_lo - L->lo[r] + 247u) / 248u, jend = (L->wide_lo + L->wide_n - L->lo[r]) / 248u;

        if (jend > leads) jend = leads;
        L->wide_r0 = L->lo[r];
        L->wide_first = L->first[r];

        if (jend > jmin) {
            L->wfull_first = (uint8_t)(L->first[r] + jmin);
            L->wfull_n = (uint8_t)(jend - jmin);
        }
    }
}

/* Constant layouts of the states that values reach first, so that a call needs no layout built for text that stays in one of them: the
   initial state (6.6), the CJK and Hangul contexts after the first ideograph, kana or syllable, and the general context after a switch
   from the initial state to the window of a common script (then the second window is U+00C0..U+013F). A program wrote them with
   build_layout; the tests check every one against it. */
static const layout STATIC_LAYOUTS[] = {
    /* The initial state (FORMAT.md 6.6). */
    {UINT64_C(0x7FFFFE000C00),
     {0x0, 0x9, 0xB, 0xD, 0xE, 0x20, 0x7F, 0xC0, 0x140, 0xF00, 0x2000, 0x20D0, 0x20000, 0x110000},
     0xC0, 0x80, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0,
     {0x01, 0x02, 0x04, 0x05, 0x06, 0x07, 0x66, 0x67, 0xE7, 0xF6, 0xF7, 0xF8, 0xFA, 0xFB},
     {2, 1, 2, 1, 2, 1, 2, 1, 2, 3, 2, 3, 4},
     13, 0x5F, 0x67, 0x00, 0x00, 0, 0x00, 0x00},
    /* CJK, kana slot letters: after the first ideograph of a value. */
    {UINT64_C(0x7FFFFE000C01),
     {0x0, 0x9, 0xB, 0xD, 0xE, 0x20, 0x7F, 0x140, 0x2000, 0x20D0, 0x3000, 0x3010, 0x3100, 0x4E00, 0xA000, 0xFF00, 0xFF20, 0x10000, 0x20000, 0x110000},
     0x0, 0x0, 0x4E00, 0x5200, 0x4E00, 0x3000, 0x10, 0xFF00, 0x20,
     {0x01, 0x02, 0x04, 0x05, 0x06, 0x07, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x7A, 0x7B, 0x7C, 0xD1, 0xD2, 0xF2, 0xF3, 0xF5, 0xF6},
     {2, 1, 2, 1, 2, 1, 2, 3, 2, 3, 1, 2, 3, 2, 3, 1, 2, 3, 4},
     19, 0x5F, 0x00, 0x7C, 0x7C, 84, 0x6A, 0xD2},
    /* CJK, kana slot hiragana. */
    {UINT64_C(0x7FFFFE000C05),
     {0x0, 0x9, 0xB, 0xD, 0xE, 0x20, 0x41, 0x140, 0x2000, 0x20D0, 0x3000, 0x3010, 0x3041, 0x3097, 0x3100, 0x4E00, 0xA000, 0xFF00, 0x10000, 0x20000, 0x110000},
     0x3041, 0x56, 0x4E00, 0x5200, 0x4E00, 0x3000, 0x10, 0x0, 0x0,
     {0x01, 0x02, 0x04, 0x05, 0x06, 0x07, 0x28, 0x2A, 0x2B, 0x2C, 0x2D, 0x3D, 0x3E, 0x94, 0x95, 0x96, 0xEB, 0xEC, 0xEE, 0xF0, 0xF1},
     {2, 1, 2, 1, 2, 1, 2, 3, 2, 3, 1, 2, 1, 2, 3, 2, 3, 2, 3, 4},
     20, 0x21, 0x3E, 0x96, 0x96, 84, 0x2D, 0x00},
    /* CJK, kana slot katakana. */
    {UINT64_C(0x7FFFFE000C09),
     {0x0, 0x9, 0xB, 0xD, 0xE, 0x20, 0x41, 0x140, 0x2000, 0x20D0, 0x3000, 0x3010, 0x30A1, 0x30FD, 0x3100, 0x4E00, 0xA000, 0xFF00, 0x10000, 0x20000, 0x110000},
     0x30A1, 0x5C, 0x4E00, 0x5200, 0x4E00, 0x3000, 0x10, 0x0, 0x0,
     {0x01, 0x02, 0x04, 0x05, 0x06, 0x07, 0x28, 0x2A, 0x2B, 0x2C, 0x2D, 0x3D, 0x3E, 0x9A, 0x9B, 0x9C, 0xF1, 0xF2, 0xF4, 0xF6, 0xF7},
     {2, 1, 2, 1, 2, 1, 2, 3, 2, 3, 1, 2, 1, 2, 3, 2, 3, 2, 3, 4},
     20, 0x21, 0x3E, 0x9C, 0x9C, 84, 0x2D, 0x00},
    /* Hangul: after the first syllable of a value. */
    {UINT64_C(0x7FFFFE000C02),
     {0x0, 0x9, 0xB, 0xD, 0xE, 0x20, 0x7F, 0xF00, 0x2000, 0x20D0, 0x3000, 0x3040, 0xAC00, 0xD7A4, 0xFF00, 0x10000, 0x20000, 0x110000},
     0x0, 0x0, 0xAC00, 0x2BA4, 0xAC00, 0x0, 0x0, 0x0, 0x0,
     {0x01, 0x02, 0x04, 0x05, 0x06, 0x07, 0x66, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0xA8, 0xA9, 0xAB, 0xAD, 0xAE},
     {2, 1, 2, 1, 2, 1, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 4},
     17, 0x5F, 0x00, 0x7A, 0x7A, 45, 0x00, 0x00},
    /* General, main window U+0140 (Latin Extended-A (Central European)), second window U+00C0: after a switch from the initial state. */
    {UINT64_C(0x180001400),
     {0x0, 0x9, 0xB, 0xD, 0xE, 0x20, 0x7F, 0x140, 0x1C0, 0xF00, 0x2000, 0x20D0, 0x20000, 0x110000},
     0x140, 0x80, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0,
     {0x01, 0x02, 0x04, 0x05, 0x06, 0x07, 0x66, 0x67, 0xE7, 0xF5, 0xF6, 0xF7, 0xF9, 0xFA},
     {2, 1, 2, 1, 2, 1, 2, 1, 2, 3, 2, 3, 4},
     13, 0x5F, 0x67, 0x00, 0x00, 0, 0x00, 0x00},
    /* General, main window U+0180 (Latin Extended-B (Vietnamese)), second window U+00C0: after a switch from the initial state. */
    {UINT64_C(0x180001800),
     {0x0, 0x9, 0xB, 0xD, 0xE, 0x20, 0x7F, 0x180, 0x200, 0xF00, 0x2000, 0x20D0, 0x20000, 0x110000},
     0x180, 0x80, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0,
     {0x01, 0x02, 0x04, 0x05, 0x06, 0x07, 0x66, 0x68, 0xE8, 0xF6, 0xF7, 0xF8, 0xFA, 0xFB},
     {2, 1, 2, 1, 2, 1, 2, 1, 2, 3, 2, 3, 4},
     13, 0x5F, 0x68, 0x00, 0x00, 0, 0x00, 0x00},
    /* General, main window U+0380 (Greek), second window U+00C0: after a switch from the initial state. */
    {UINT64_C(0x180003800),
     {0x0, 0x9, 0xB, 0xD, 0xE, 0x20, 0x7F, 0x380, 0x400, 0xF00, 0x2000, 0x20D0, 0x20000, 0x110000},
     0x380, 0x80, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0,
     {0x01, 0x02, 0x04, 0x05, 0x06, 0x07, 0x66, 0x6A, 0xEA, 0xF6, 0xF7, 0xF8, 0xFA, 0xFB},
     {2, 1, 2, 1, 2, 1, 2, 1, 2, 3, 2, 3, 4},
     13, 0x5F, 0x6A, 0x00, 0x00, 0, 0x00, 0x00},
    /* General, main window U+0400 (Cyrillic), second window U+00C0: after a switch from the initial state. */
    {UINT64_C(0x180004000),
     {0x0, 0x9, 0xB, 0xD, 0xE, 0x20, 0x7F, 0x400, 0x480, 0xF00, 0x2000, 0x20D0, 0x20000, 0x110000},
     0x400, 0x80, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0,
     {0x01, 0x02, 0x04, 0x05, 0x06, 0x07, 0x66, 0x6A, 0xEA, 0xF5, 0xF6, 0xF7, 0xF9, 0xFA},
     {2, 1, 2, 1, 2, 1, 2, 1, 2, 3, 2, 3, 4},
     13, 0x5F, 0x6A, 0x00, 0x00, 0, 0x00, 0x00},
    /* General, main window U+0530 (Armenian), second window U+00C0: after a switch from the initial state. */
    {UINT64_C(0x180005300),
     {0x0, 0x9, 0xB, 0xD, 0xE, 0x20, 0x7F, 0x530, 0x590, 0xF00, 0x2000, 0x20D0, 0x20000, 0x110000},
     0x530, 0x60, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0,
     {0x01, 0x02, 0x04, 0x05, 0x06, 0x07, 0x66, 0x6B, 0xCB, 0xD5, 0xD6, 0xD7, 0xD9, 0xDA},
     {2, 1, 2, 1, 2, 1, 2, 1, 2, 3, 2, 3, 4},
     13, 0x5F, 0x6B, 0x00, 0x00, 0, 0x00, 0x00},
    /* General, main window U+0590 (Hebrew), second window U+00C0: after a switch from the initial state. */
    {UINT64_C(0x180005900),
     {0x0, 0x9, 0xB, 0xD, 0xE, 0x20, 0x7F, 0x590, 0x600, 0xF00, 0x2000, 0x20D0, 0x20000, 0x110000},
     0x590, 0x70, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0,
     {0x01, 0x02, 0x04, 0x05, 0x06, 0x07, 0x66, 0x6C, 0xDC, 0xE6, 0xE7, 0xE8, 0xEA, 0xEB},
     {2, 1, 2, 1, 2, 1, 2, 1, 2, 3, 2, 3, 4},
     13, 0x5F, 0x6C, 0x00, 0x00, 0, 0x00, 0x00},
    /* General, main window U+0600 (Arabic), second window U+00C0: after a switch from the initial state. */
    {UINT64_C(0x180006000),
     {0x0, 0x9, 0xB, 0xD, 0xE, 0x20, 0x7F, 0x600, 0x680, 0xF00, 0x2000, 0x20D0, 0x20000, 0x110000},
     0x600, 0x80, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0,
     {0x01, 0x02, 0x04, 0x05, 0x06, 0x07, 0x66, 0x6C, 0xEC, 0xF5, 0xF6, 0xF7, 0xF9, 0xFA},
     {2, 1, 2, 1, 2, 1, 2, 1, 2, 3, 2, 3, 4},
     13, 0x5F, 0x6C, 0x00, 0x00, 0, 0x00, 0x00},
    /* General, main window U+0680 (Arabic (Persian, Urdu)), second window U+00C0: after a switch from the initial state. */
    {UINT64_C(0x180006800),
     {0x0, 0x9, 0xB, 0xD, 0xE, 0x20, 0x7F, 0x680, 0x700, 0xF00, 0x2000, 0x20D0, 0x20000, 0x110000},
     0x680, 0x80, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0,
     {0x01, 0x02, 0x04, 0x05, 0x06, 0x07, 0x66, 0x6D, 0xED, 0xF6, 0xF7, 0xF8, 0xFA, 0xFB},
     {2, 1, 2, 1, 2, 1, 2, 1, 2, 3, 2, 3, 4},
     13, 0x5F, 0x6D, 0x00, 0x00, 0, 0x00, 0x00},
    /* General, main window U+0900 (Devanagari), second window U+00C0: after a switch from the initial state. */
    {UINT64_C(0x180009000),
     {0x0, 0x9, 0xB, 0xD, 0xE, 0x20, 0x7F, 0x900, 0x980, 0xF00, 0x2000, 0x20D0, 0x20000, 0x110000},
     0x900, 0x80, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0,
     {0x01, 0x02, 0x04, 0x05, 0x06, 0x07, 0x66, 0x6F, 0xEF, 0xF5, 0xF6, 0xF7, 0xF9, 0xFA},
     {2, 1, 2, 1, 2, 1, 2, 1, 2, 3, 2, 3, 4},
     13, 0x5F, 0x6F, 0x00, 0x00, 0, 0x00, 0x00},
    /* General, main window U+0980 (Bengali), second window U+00C0: after a switch from the initial state. */
    {UINT64_C(0x180009800),
     {0x0, 0x9, 0xB, 0xD, 0xE, 0x20, 0x7F, 0x980, 0xA00, 0xF00, 0x2000, 0x20D0, 0x20000, 0x110000},
     0x980, 0x80, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0,
     {0x01, 0x02, 0x04, 0x05, 0x06, 0x07, 0x66, 0x70, 0xF0, 0xF6, 0xF7, 0xF8, 0xFA, 0xFB},
     {2, 1, 2, 1, 2, 1, 2, 1, 2, 3, 2, 3, 4},
     13, 0x5F, 0x70, 0x00, 0x00, 0, 0x00, 0x00},
    /* General, main window U+0B80 (Tamil), second window U+00C0: after a switch from the initial state. */
    {UINT64_C(0x18000B800),
     {0x0, 0x9, 0xB, 0xD, 0xE, 0x20, 0x7F, 0xB80, 0xC00, 0xF00, 0x2000, 0x20D0, 0x20000, 0x110000},
     0xB80, 0x80, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0,
     {0x01, 0x02, 0x04, 0x05, 0x06, 0x07, 0x66, 0x72, 0xF2, 0xF6, 0xF7, 0xF8, 0xFA, 0xFB},
     {2, 1, 2, 1, 2, 1, 2, 1, 2, 3, 2, 3, 4},
     13, 0x5F, 0x72, 0x00, 0x00, 0, 0x00, 0x00},
    /* General, main window U+0E00 (Thai), second window U+00C0: after a switch from the initial state. */
    {UINT64_C(0x18000E000),
     {0x0, 0x9, 0xB, 0xD, 0xE, 0x20, 0x7F, 0xE00, 0xE80, 0xF78, 0x2000, 0x20D0, 0x20000, 0x110000},
     0xE00, 0x80, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0,
     {0x01, 0x02, 0x04, 0x05, 0x06, 0x07, 0x66, 0x74, 0xF4, 0xF5, 0xF6, 0xF7, 0xF9, 0xFA},
     {2, 1, 2, 1, 2, 1, 2, 1, 2, 3, 2, 3, 4},
     13, 0x5F, 0x74, 0x00, 0x00, 0, 0x00, 0x00},
    /* General, main window U+1080 (Georgian), second window U+00C0: after a switch from the initial state. */
    {UINT64_C(0x180010800),
     {0x0, 0x9, 0xB, 0xD, 0xE, 0x20, 0x7F, 0x1080, 0x1100, 0x11F8, 0x2000, 0x20D0, 0x20000, 0x110000},
     0x1080, 0x80, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0,
     {0x01, 0x02, 0x04, 0x05, 0x06, 0x07, 0x66, 0x77, 0xF7, 0xF8, 0xF9, 0xFA, 0xFC, 0xFD},
     {2, 1, 2, 1, 2, 1, 2, 1, 2, 3, 2, 3, 4},
     13, 0x5F, 0x77, 0x00, 0x00, 0, 0x00, 0x00},
    /* General, main window U+1200 (Ethiopic), second window U+00C0: after a switch from the initial state. */
    {UINT64_C(0x180012000),
     {0x0, 0x9, 0xB, 0xD, 0xE, 0x20, 0x7F, 0xF00, 0x1108, 0x1200, 0x1280, 0x1378, 0x2000, 0x20D0, 0x20000, 0x110000},
     0x1200, 0x80, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0,
     {0x01, 0x02, 0x04, 0x05, 0x06, 0x07, 0x66, 0x75, 0x76, 0x77, 0xF7, 0xF8, 0xF9, 0xFA, 0xFC, 0xFD},
     {2, 1, 2, 1, 2, 1, 2, 3, 2, 1, 2, 3, 2, 3, 4},
     15, 0x5F, 0x77, 0x00, 0x00, 0, 0x00, 0x00},
    /* General, main window U+1E80 (Latin Extended Additional (Vietnamese)), second window U+00C0: after a switch from the initial state. */
    {UINT64_C(0x18001E800),
     {0x0, 0x9, 0xB, 0xD, 0xE, 0x20, 0x7F, 0xF00, 0x1D88, 0x1E80, 0x1F00, 0x20D0, 0x20000, 0x110000},
     0x1E80, 0x80, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0,
     {0x01, 0x02, 0x04, 0x05, 0x06, 0x07, 0x66, 0x75, 0x76, 0x77, 0xF7, 0xF9, 0xFB, 0xFC},
     {2, 1, 2, 1, 2, 1, 2, 3, 2, 1, 2, 3, 4},
     13, 0x5F, 0x77, 0x00, 0x00, 0, 0x00, 0x00},
};

#define N_STATIC (sizeof STATIC_LAYOUTS / sizeof STATIC_LAYOUTS[0])
#define INITIAL_LAYOUT (STATIC_LAYOUTS[0])

static void lc_init(lcache *lc) {
    unsigned i;

    for (i = 0; i < NCACHE; i++) lc->entry[i].key = KEY_NONE;

    lc->next = 0;
}

/* The layout of state s, from the cache or built into it (the oldest entry is replaced). */
STENO_INLINE const layout *lc_get(lcache *lc, const st_t *s) {
    uint64_t key = key_of(s);
    unsigned i;
    layout *L;

    if (key == KEY_INITIAL) return &INITIAL_LAYOUT;

    for (i = 0; i < NCACHE; i++) {
        if (lc->entry[i].key == key) return &lc->entry[i];
    }

    /* Every constant layout has no second window or U+00C0..U+013F. */
    if ((key >> 25) == 0xC0u || (key >> 25) == 0x3FFFFFu) {
        for (i = 0; i < N_STATIC; i++) {
            if (STATIC_LAYOUTS[i].key == key) return &STATIC_LAYOUTS[i];
        }
    }

    if (lc->next >= NCACHE) lc->next = 0;

    L = &lc->entry[lc->next];
    lc->next = (lc->next + 1) % NCACHE;
    build_layout(L, s, key);
    return L;
}

/* ---------- One code point (8.2, 9.2) ---------- */

/* o / 248 for o below 65,536, without a division: o / 248 = (o / 8) / 31, and (x * 33,826) >> 20 = x / 31 for every x below 8,192 (the
   tests check every o). A range of size 2 has fewer than 254 * 248 = 62,992 code points, so its offsets are in bounds. */
STENO_INLINE uint32_t div248(uint32_t o) {
    return ((o >> 3) * 33826u) >> 20;
}

/* Writes the bytes of code point c into b and returns their number. */
static unsigned encode_cp(const layout *L, uint32_t c, uint8_t *b) {
    unsigned i = range_of(L, c);
    uint32_t o = c - L->lo[i], f = L->first[i], r;

    switch (L->size[i]) {
    case 1:
        b[0] = (uint8_t)(f + o);
        return 1;
    case 2:
        r = o / 248u;
        b[0] = (uint8_t)(f + r);
        b[1] = (uint8_t)(o - r * 248u + 8u);
        return 2;
    case 3:
        r = o % 61504u;
        b[0] = (uint8_t)(f + o / 61504u);
        b[1] = (uint8_t)(r / 248u + 8u);
        b[2] = (uint8_t)(r % 248u + 8u);
        return 3;
    default:
        /* A range of size 4 has one lead byte (fewer than 248^3 code points). */
        r = o % 61504u;
        b[0] = (uint8_t)f;
        b[1] = (uint8_t)(o / 61504u + 8u);
        b[2] = (uint8_t)(r / 248u + 8u);
        b[3] = (uint8_t)(r % 248u + 8u);
        return 4;
    }
}

/* Reads one code point at s (avail bytes, at least 1) in layout L, with the checks of 9.2 in their order. Returns the number of bytes
   (with *cp set), 0 if the input ends inside the code point after trail bytes that are all valid, or minus a status. */
static int decode_cp(const layout *L, const uint8_t *s, size_t avail, uint32_t *cp) {
    uint32_t b = s[0], o;
    unsigned i, k, j;

    if (b == 0) return -(int)STENO_ZERO_BYTE;
    if (b >= L->first[L->nr]) return -(int)STENO_UNUSED_LEAD;

    i = range_of_lead(L, b);
    k = L->size[i];
    o = b - L->first[i];

    for (j = 1; j < k; j++) {
        uint32_t u;

        if (j >= avail) return 0;

        u = s[j];
        if (u == 0) return -(int)STENO_ZERO_BYTE;
        if (u < 8) return -(int)STENO_INVALID_TRAIL;

        o = o * 248u + (u - 8u);
    }

    /* o is below 248^3, so the sum cannot wrap. */
    if (L->lo[i] + o >= L->lo[i + 1]) return -(int)STENO_PAST_RANGE;

    *cp = L->lo[i] + o;
    return (int)k;
}

/* ---------- UTF-8 input ---------- */

/* Reads one UTF-8 sequence at s (n bytes, at least 1). Returns its length with *cp set, 0 if the n bytes are a valid but unfinished
   start of a sequence, or -1 if the bytes are not valid UTF-8 (overlong forms, surrogates and values above U+10FFFF included). */
STENO_INLINE int utf8_read(const uint8_t *s, size_t n, uint32_t *cp) {
    uint32_t b0 = s[0], b1, b2, b3, lo = 0x80u, hi = 0xBFu;

    if (b0 < 0x80u) {
        *cp = b0;
        return 1;
    }

    if (b0 < 0xC2u) return -1;

    if (b0 < 0xE0u) {
        if (n < 2) return 0;
        b1 = s[1];
        if ((b1 & 0xC0u) != 0x80u) return -1;
        *cp = ((b0 & 0x1Fu) << 6) | (b1 & 0x3Fu);
        return 2;
    }

    if (b0 < 0xF0u) {
        if (b0 == 0xE0u) lo = 0xA0u;
        else if (b0 == 0xEDu) hi = 0x9Fu;

        if (n < 2) return 0;
        b1 = s[1];
        if (b1 < lo || b1 > hi) return -1;
        if (n < 3) return 0;
        b2 = s[2];
        if ((b2 & 0xC0u) != 0x80u) return -1;
        *cp = ((b0 & 0x0Fu) << 12) | ((b1 & 0x3Fu) << 6) | (b2 & 0x3Fu);
        return 3;
    }

    if (b0 < 0xF5u) {
        if (b0 == 0xF0u) lo = 0x90u;
        else if (b0 == 0xF4u) hi = 0x8Fu;

        if (n < 2) return 0;
        b1 = s[1];
        if (b1 < lo || b1 > hi) return -1;
        if (n < 3) return 0;
        b2 = s[2];
        if ((b2 & 0xC0u) != 0x80u) return -1;
        if (n < 4) return 0;
        b3 = s[3];
        if ((b3 & 0xC0u) != 0x80u) return -1;
        *cp = ((b0 & 0x07u) << 18) | ((b1 & 0x3Fu) << 12) | ((b2 & 0x3Fu) << 6) | (b3 & 0x3Fu);
        return 4;
    }

    return -1;
}

/* The length of the UTF-8 sequence that starts with the valid lead byte b0. */
static size_t utf8_seq_len(uint8_t b0) {
    return b0 < 0x80u ? 1 : b0 < 0xE0u ? 2 : b0 < 0xF0u ? 3 : 4;
}

/* ---------- Runs of 1-byte codes ---------- */

#define ONES UINT64_C(0x0101010101010101)
#define HIGHS UINT64_C(0x8080808080808080)

/* True if every byte of the 64-bit word x is in lo..hi (lo <= 128, hi <= 127). The two tests are the classic "has a byte less than"
   and "has a byte more than": exact as a whole, and the same on every byte order, since no carry crosses a byte that passes. */
STENO_INLINE int swar_all_in(uint64_t x, uint32_t lo, uint32_t hi) {
    uint64_t less = (x - ONES * lo) & ~x & HIGHS;
    uint64_t more = ((x + ONES * (127u - hi)) | x) & HIGHS;
    return (less | more) == 0;
}

/* Encoder, UTF-8 input: the length of the run of bytes 0x20..0x1F + span at the start of s (at most lim), written as b - 0x19 to d
   unless d is NULL. span is 0x5F (U+0020..U+007E) or 0x21 (U+0020..U+0040, CJK context with a kana slot). */
static size_t enc_ascii8(const uint8_t *s, size_t lim, uint32_t span, uint8_t *d) {
    size_t k = 0;

#if defined(STENO_SSE2)
    {
        const __m128i base = _mm_set1_epi8(0x20), top = _mm_set1_epi8((char)(span - 1u)), bias = _mm_set1_epi8(0x19);

        for (; k + 16 <= lim; k += 16) {
            __m128i v = _mm_loadu_si128((const __m128i *)(const void *)(s + k));
            __m128i t = _mm_sub_epi8(v, base);
            if (_mm_movemask_epi8(_mm_cmpeq_epi8(_mm_max_epu8(t, top), top)) != 0xFFFF) break;
            if (d != NULL) _mm_storeu_si128((__m128i *)(void *)(d + k), _mm_sub_epi8(v, bias));
        }
    }
#elif defined(STENO_NEON)
    {
        const uint8x16_t base = vdupq_n_u8(0x20), top = vdupq_n_u8((uint8_t)(span - 1u)), bias = vdupq_n_u8(0x19);

        for (; k + 16 <= lim; k += 16) {
            uint8x16_t v = vld1q_u8(s + k);
            if (vminvq_u8(vcleq_u8(vsubq_u8(v, base), top)) != 0xFF) break;
            if (d != NULL) vst1q_u8(d + k, vsubq_u8(v, bias));
        }
    }
#endif

    for (; k + 8 <= lim; k += 8) {
        uint64_t x;
        memcpy(&x, s + k, 8);
        if (!swar_all_in(x, 0x20u, 0x1Fu + span)) break;
        if (d != NULL) {
            x -= ONES * 0x19u;
            memcpy(d + k, &x, 8);
        }
    }

    for (; k < lim; k++) {
        uint32_t b = s[k];
        if (b - 0x20u >= span) break;
        if (d != NULL) d[k] = (uint8_t)(b - 0x19u);
    }

    return k;
}

/* Decoder, UTF-8 output: the length of the run of the bytes 07..06 + span (U+0020..U+0019 + span) at the start of s (at most lim),
   written as b + 0x19 to d unless d is NULL. */
static size_t dec_ascii8(const uint8_t *s, size_t lim, uint32_t span, uint8_t *d) {
    size_t k = 0;

#if defined(STENO_SSE2)
    {
        const __m128i base = _mm_set1_epi8(0x07), top = _mm_set1_epi8((char)(span - 1u)), bias = _mm_set1_epi8(0x19);

        for (; k + 16 <= lim; k += 16) {
            __m128i v = _mm_loadu_si128((const __m128i *)(const void *)(s + k));
            __m128i t = _mm_sub_epi8(v, base);
            if (_mm_movemask_epi8(_mm_cmpeq_epi8(_mm_max_epu8(t, top), top)) != 0xFFFF) break;
            if (d != NULL) _mm_storeu_si128((__m128i *)(void *)(d + k), _mm_add_epi8(v, bias));
        }
    }
#elif defined(STENO_NEON)
    {
        const uint8x16_t base = vdupq_n_u8(0x07), top = vdupq_n_u8((uint8_t)(span - 1u)), bias = vdupq_n_u8(0x19);

        for (; k + 16 <= lim; k += 16) {
            uint8x16_t v = vld1q_u8(s + k);
            if (vminvq_u8(vcleq_u8(vsubq_u8(v, base), top)) != 0xFF) break;
            if (d != NULL) vst1q_u8(d + k, vaddq_u8(v, bias));
        }
    }
#endif

    for (; k + 8 <= lim; k += 8) {
        uint64_t x;
        memcpy(&x, s + k, 8);
        if (!swar_all_in(x, 0x07u, 0x06u + span)) break;
        if (d != NULL) {
            x += ONES * 0x19u;
            memcpy(d + k, &x, 8);
        }
    }

    for (; k < lim; k++) {
        uint32_t b = s[k];
        if (b - 0x07u >= span) break;
        if (d != NULL) d[k] = (uint8_t)(b + 0x19u);
    }

    return k;
}

/* The parameters of the runs of 1-byte codes that the vector code of the UTF-16 paths takes: printable ASCII and one more run of code
   points below U+10000 (the main window, or the kana of the kana slot), with their lead bytes. */
typedef struct {
    uint32_t span;     /* the number of printable ASCII code points that take 1 byte, from U+0020 */
    uint32_t one_lo;   /* the first code point of the other run */
    uint32_t one_n;    /* its length, 0 if there is none or it is above U+FFFF */
    uint32_t one_first; /* the lead byte of one_lo */
} run1;

STENO_INLINE run1 run1_of(const layout *L) {
    run1 r;
    r.span = L->ascii_n;
    r.one_lo = L->one_lo;
    r.one_n = L->one_lo + L->one_n <= 0x10000u ? L->one_n : 0;
    r.one_first = L->one_first;
    return r;
}

/* Encoder, UTF-16 input: the length of the run of 1-byte code units (printable ASCII and the run of r) at the start of s (at most lim),
   written to d unless d is NULL. *ones is set to the number of units of the run of r, capped at 2. */
static size_t enc_run16(const uint16_t *s, size_t lim, run1 r, uint8_t *d, uint32_t *ones) {
    size_t k = 0;
    uint32_t seen = 0;

#if defined(STENO_SSE2)
    {
        const __m128i sbase = _mm_set1_epi16(0x20), stop = _mm_set1_epi16((short)(r.span - 1u));
        const __m128i obase = _mm_set1_epi16((short)(uint16_t)r.one_lo), otop = _mm_set1_epi16((short)(uint16_t)(r.one_n - 1u));
        const __m128i abias = _mm_set1_epi16(0x19), obias = _mm_set1_epi16((short)(uint16_t)(r.one_lo - r.one_first));
        const __m128i zero = _mm_setzero_si128();
        const int has_one = r.one_n > 0;

        for (; k + 16 <= lim; k += 16) {
            __m128i a = _mm_loadu_si128((const __m128i *)(const void *)(s + k));
            __m128i b = _mm_loadu_si128((const __m128i *)(const void *)(s + k + 8));
            /* A lane is in a run when its offset minus the run's last offset saturates to 0. */
            __m128i aa = _mm_cmpeq_epi16(_mm_subs_epu16(_mm_sub_epi16(a, sbase), stop), zero);
            __m128i ba = _mm_cmpeq_epi16(_mm_subs_epu16(_mm_sub_epi16(b, sbase), stop), zero);
            __m128i ao = zero, bo = zero;
            int om;

            if (has_one) {
                ao = _mm_cmpeq_epi16(_mm_subs_epu16(_mm_sub_epi16(a, obase), otop), zero);
                bo = _mm_cmpeq_epi16(_mm_subs_epu16(_mm_sub_epi16(b, obase), otop), zero);
            }

            if (_mm_movemask_epi8(_mm_packs_epi16(_mm_or_si128(aa, ao), _mm_or_si128(ba, bo))) != 0xFFFF) break;

            om = _mm_movemask_epi8(_mm_packs_epi16(ao, bo));
            if (om != 0) seen += (om & (om - 1)) != 0 ? 2u : 1u;

            if (d != NULL) {
                __m128i ra = _mm_or_si128(_mm_and_si128(aa, _mm_sub_epi16(a, abias)), _mm_andnot_si128(aa, _mm_sub_epi16(a, obias)));
                __m128i rb = _mm_or_si128(_mm_and_si128(ba, _mm_sub_epi16(b, abias)), _mm_andnot_si128(ba, _mm_sub_epi16(b, obias)));
                _mm_storeu_si128((__m128i *)(void *)(d + k), _mm_packus_epi16(ra, rb));
            }
        }
    }
#elif defined(STENO_NEON)
    {
        const uint16x8_t sbase = vdupq_n_u16(0x20), stop = vdupq_n_u16((uint16_t)(r.span - 1u));
        const uint16x8_t obase = vdupq_n_u16((uint16_t)r.one_lo), otop = vdupq_n_u16((uint16_t)(r.one_n - 1u));
        const uint16x8_t abias = vdupq_n_u16(0x19), obias = vdupq_n_u16((uint16_t)(r.one_lo - r.one_first));
        const uint16x8_t none = vdupq_n_u16(0);
        const int has_one = r.one_n > 0;

        for (; k + 16 <= lim; k += 16) {
            uint16x8_t a = vld1q_u16(s + k), b = vld1q_u16(s + k + 8);
            uint16x8_t aa = vcleq_u16(vsubq_u16(a, sbase), stop), ba = vcleq_u16(vsubq_u16(b, sbase), stop);
            uint16x8_t ao = none, bo = none;
            uint8x16_t om;

            if (has_one) {
                ao = vcleq_u16(vsubq_u16(a, obase), otop);
                bo = vcleq_u16(vsubq_u16(b, obase), otop);
            }

            if (vminvq_u16(vandq_u16(vorrq_u16(aa, ao), vorrq_u16(ba, bo))) != 0xFFFF) break;

            om = vcombine_u8(vmovn_u16(ao), vmovn_u16(bo));
            if (vmaxvq_u8(om) != 0) {
                /* Each lane of the run of r is 0xFF: the sum over the lanes, divided by 0xFF, is their number. */
                uint32_t n = (uint32_t)vaddlvq_u8(om) / 0xFFu;
                seen += n >= 2 ? 2u : 1u;
            }

            if (d != NULL) {
                uint16x8_t ra = vbslq_u16(aa, vsubq_u16(a, abias), vsubq_u16(a, obias));
                uint16x8_t rb = vbslq_u16(ba, vsubq_u16(b, abias), vsubq_u16(b, obias));
                vst1q_u8(d + k, vcombine_u8(vmovn_u16(ra), vmovn_u16(rb)));
            }
        }
    }
#endif

    for (; k < lim; k++) {
        uint32_t u = s[k];

        if (u - 0x20u < r.span) {
            if (d != NULL) d[k] = (uint8_t)(u - 0x19u);
        } else if (u - r.one_lo < r.one_n) {
            if (d != NULL) d[k] = (uint8_t)(u - r.one_lo + r.one_first);
            seen++;
        } else {
            break;
        }
    }

    *ones = seen > 2 ? 2 : seen;
    return k;
}

/* Decoder, UTF-16 output: the length of the run of 1-byte codes (printable ASCII and the run of r) at the start of s (at most lim),
   written to d as code units unless d is NULL. *ones is set to the number of codes of the run of r, capped at 2. */
static size_t dec_run16(const uint8_t *s, size_t lim, run1 r, uint16_t *d, uint32_t *ones) {
    size_t k = 0;
    uint32_t seen = 0;
    const uint32_t one_first = r.one_n > 0 ? r.one_first : 0, one_n = r.one_n;

#if defined(STENO_SSE2)
    {
        const __m128i abase = _mm_set1_epi8(0x07), atop = _mm_set1_epi8((char)(r.span - 1u));
        const __m128i obase = _mm_set1_epi8((char)(uint8_t)one_first), otop = _mm_set1_epi8((char)(uint8_t)(one_n - 1u));
        const __m128i abias = _mm_set1_epi16(0x19), obias = _mm_set1_epi16((short)(uint16_t)(r.one_lo - one_first));
        const __m128i zero = _mm_setzero_si128();
        const int has_one = one_n > 0;

        for (; k + 16 <= lim; k += 16) {
            __m128i v = _mm_loadu_si128((const __m128i *)(const void *)(s + k));
            __m128i ta = _mm_sub_epi8(v, abase);
            __m128i ia = _mm_cmpeq_epi8(_mm_max_epu8(ta, atop), atop);
            __m128i io = zero;
            int om;

            if (has_one) {
                __m128i to = _mm_sub_epi8(v, obase);
                io = _mm_cmpeq_epi8(_mm_max_epu8(to, otop), otop);
            }

            if (_mm_movemask_epi8(_mm_or_si128(ia, io)) != 0xFFFF) break;

            om = _mm_movemask_epi8(io);
            if (om != 0) seen += (om & (om - 1)) != 0 ? 2u : 1u;

            if (d != NULL) {
                __m128i lo = _mm_unpacklo_epi8(v, zero), hi = _mm_unpackhi_epi8(v, zero);
                __m128i mlo = _mm_unpacklo_epi8(ia, ia), mhi = _mm_unpackhi_epi8(ia, ia);
                lo = _mm_or_si128(_mm_and_si128(mlo, _mm_add_epi16(lo, abias)), _mm_andnot_si128(mlo, _mm_add_epi16(lo, obias)));
                hi = _mm_or_si128(_mm_and_si128(mhi, _mm_add_epi16(hi, abias)), _mm_andnot_si128(mhi, _mm_add_epi16(hi, obias)));
                _mm_storeu_si128((__m128i *)(void *)(d + k), lo);
                _mm_storeu_si128((__m128i *)(void *)(d + k + 8), hi);
            }
        }
    }
#elif defined(STENO_NEON)
    {
        const uint8x16_t abase = vdupq_n_u8(0x07), atop = vdupq_n_u8((uint8_t)(r.span - 1u));
        const uint8x16_t obase = vdupq_n_u8((uint8_t)one_first), otop = vdupq_n_u8((uint8_t)(one_n - 1u));
        const uint16x8_t abias = vdupq_n_u16(0x19), obias = vdupq_n_u16((uint16_t)(r.one_lo - one_first));
        const int has_one = one_n > 0;

        for (; k + 16 <= lim; k += 16) {
            uint8x16_t v = vld1q_u8(s + k);
            uint8x16_t ia = vcleq_u8(vsubq_u8(v, abase), atop);
            uint8x16_t io = vdupq_n_u8(0);

            if (has_one) io = vcleq_u8(vsubq_u8(v, obase), otop);
            if (vminvq_u8(vorrq_u8(ia, io)) != 0xFF) break;

            if (vmaxvq_u8(io) != 0) {
                uint32_t n = (uint32_t)vaddlvq_u8(io) / 0xFFu;
                seen += n >= 2 ? 2u : 1u;
            }

            if (d != NULL) {
                uint16x8_t lo = vmovl_u8(vget_low_u8(v)), hi = vmovl_u8(vget_high_u8(v));
                uint16x8_t mlo = vmovl_u8(vget_low_u8(ia)), mhi = vmovl_u8(vget_high_u8(ia));
                /* The masks widened to 16 bits are 0x00FF: make them 0xFFFF. */
                mlo = vorrq_u16(mlo, vshlq_n_u16(mlo, 8));
                mhi = vorrq_u16(mhi, vshlq_n_u16(mhi, 8));
                vst1q_u16(d + k, vbslq_u16(mlo, vaddq_u16(lo, abias), vaddq_u16(lo, obias)));
                vst1q_u16(d + k + 8, vbslq_u16(mhi, vaddq_u16(hi, abias), vaddq_u16(hi, obias)));
            }
        }
    }
#endif

    for (; k < lim; k++) {
        uint32_t b = s[k];

        if (b - 0x07u < r.span) {
            if (d != NULL) d[k] = (uint16_t)(b + 0x19u);
        } else if (b - one_first < one_n) {
            if (d != NULL) d[k] = (uint16_t)(b - one_first + r.one_lo);
            seen++;
        } else {
            break;
        }
    }

    *ones = seen > 2 ? 2 : seen;
    return k;
}

/* ---------- The encoder (8) ---------- */

enum { FORM_UTF8 = 0, FORM_UTF16 = 1, FORM_CP = 2 };

#define EMIT1(x)                                                                                                                        \
    do {                                                                                                                                \
        if (!count) {                                                                                                                   \
            if (pos >= cap) goto full;                                                                                                  \
            dst[pos] = (uint8_t)(x);                                                                                                    \
        }                                                                                                                               \
        pos++;                                                                                                                          \
    } while (0)

/* Encodes the code points of src (n elements of the given form) from state *stp, writing at dst + *io_pos (count: only counting), from
   element *io_i. With final 0, an unfinished UTF-8 sequence or a high surrogate code unit at the end is not encoded: *io_i stops before
   it. Returns the status; *io_i is then the position of the problem (or of the end), *io_pos the length written, *stp the state after
   the code points that were encoded. Inlined into each caller with constant form and count, so that each gets its own loop. */
STENO_INLINE steno_status enc_core(const int form, const int count, st_t *stp, lcache *lc, const void *srcv, size_t n, const int final,
                                   uint8_t *dst, size_t cap, size_t *io_pos, size_t *io_i) {
    /* The source as its form, converted only for that form: a pointer to bytes at an odd address is not converted to uint16_t * (C99
       6.3.2.3: a conversion to a type whose alignment the address lacks is undefined, even if the pointer is never used). */
    const uint8_t *s8 = form == FORM_UTF8 ? (const uint8_t *)srcv : NULL;
    const uint16_t *s16 = form == FORM_UTF16 ? (const uint16_t *)srcv : NULL;
    const uint32_t *s32 = form == FORM_CP ? (const uint32_t *)srcv : NULL;
    st_t st = *stp;
    const layout *L = lc_get(lc, &st);
    size_t i = *io_i, pos = *io_pos;
    size_t hi_end = st.last_high ? i : SIZE_MAX; /* the position after the last high surrogate code point that was encoded */
    uint32_t conf = st.conf, pending = st.pending;
    steno_status status = STENO_OK;

    while (i < n) {
        uint32_t c = form == FORM_UTF8 ? s8[i] : form == FORM_UTF16 ? s16[i] : s32[i];
        size_t adv = 1;

        /* A run of 1-byte codes. */
        if (c - 0x20u < L->ascii_n || (form == FORM_UTF16 && c - L->one_lo < L->one_n && L->one_lo < 0x10000u)) {
            size_t lim = n - i, r;
            uint32_t ones = 0;

            if (!count && cap - pos < lim) lim = cap - pos;
            if (lim == 0) goto full;

            if (form == FORM_UTF8) {
                r = enc_ascii8(s8 + i, lim, L->ascii_n, count ? NULL : dst + pos);
            } else if (form == FORM_UTF16) {
                r = enc_run16(s16 + i, lim, run1_of(L), count ? NULL : dst + pos, &ones);
            } else {
                for (r = 0; r < lim && s32[i + r] - 0x20u < L->ascii_n; r++) {
                    if (!count) dst[pos + r] = (uint8_t)(s32[i + r] - 0x19u);
                }
            }

            if (ones > 0) {
                conf = conf + ones > 2 ? 2 : conf + ones;
                pending = NONE;
            }

            i += r;
            pos += r;
            continue;
        }

        /* The rest of the code point. */
        if (form == FORM_UTF8) {
            if (c >= 0x80u) {
                int r = utf8_read(s8 + i, n - i, &c);

                if (r <= 0) {
                    if (r == 0 && !final) break;
                    status = STENO_INVALID_UTF8;
                    goto done;
                }

                adv = (size_t)r;
            }
        } else if (form == FORM_UTF16) {
            if (c - 0xD800u < 0x400u) {
                if (i + 1 < n) {
                    uint32_t c2 = s16[i + 1];

                    if (c2 - 0xDC00u < 0x400u) {
                        c = 0x10000u + ((c - 0xD800u) << 10) + (c2 - 0xDC00u);
                        adv = 2;
                    }
                } else if (!final) {
                    break;
                }
            }
        } else if (c > 0x10FFFFu) {
            status = STENO_INVALID_CODE_POINT;
            goto done;
        }

        /* Code points that never change the layout (14.2): only the confidence and the pending window. */
        if (c - L->one_lo < L->one_n) {
            EMIT1(L->one_first + (c - L->one_lo));
            conf = conf < 2 ? conf + 1 : 2;
            pending = NONE;
            i += adv;

            if (form == FORM_UTF8) {
                /* A run of the main window (or of the kana of the kana slot) in 2- and 3-byte UTF-8, with printable ASCII among it, in
                   a tight loop. A sequence leaves the loop unless it is valid UTF-8 and in the run: C0 and C1 give code points below
                   U+0080 and a 3-byte overlong form one below U+0800, neither of which is in the run, and no window has surrogates. */
                const uint32_t olo = L->one_lo, on = L->one_n, of = L->one_first, an = L->ascii_n;

                while (i < n) {
                    uint32_t b0 = s8[i], cp;
                    size_t k;

                    if (b0 - 0x20u < an) {
                        EMIT1(b0 - 0x19u);
                        i++;
                        if (i < n && s8[i] - 0x20u < an) break; /* a longer run of ASCII: the vector code takes it */
                        continue;
                    }

                    if ((b0 & 0xE0u) == 0xC0u) {
                        if (i + 1 >= n || (s8[i + 1] & 0xC0u) != 0x80u) break;
                        cp = ((b0 & 0x1Fu) << 6) | (s8[i + 1] & 0x3Fu);
                        k = 2;
                    } else if ((b0 & 0xF0u) == 0xE0u) {
                        if (i + 2 >= n || (s8[i + 1] & 0xC0u) != 0x80u || (s8[i + 2] & 0xC0u) != 0x80u) break;
                        cp = ((b0 & 0x0Fu) << 12) | ((s8[i + 1] & 0x3Fu) << 6) | (s8[i + 2] & 0x3Fu);
                        if (cp < 0x800u) break;
                        k = 3;
                    } else {
                        break;
                    }

                    if (cp - olo >= on) break;
                    EMIT1(of + (cp - olo));
                    conf = conf < 2 ? conf + 1 : 2;
                    i += k;
                }
            }

            continue;
        } else if (c - L->wide_lo < L->wide_n) {
            /* The ideographs (CJK context) or syllables (Hangul context), 2 bytes each, in a tight loop with single spaces and the
               other printable ASCII among them. Every code point of the range is at least U+4E00 and below U+D800, so a 3-byte UTF-8
               sequence that decodes into it is valid (no overlong form, no surrogate). */
            const uint32_t wlo = L->wide_lo, wn = L->wide_n, r0 = L->wide_r0, wf = L->wide_first, an = L->ascii_n;

            for (;;) {
                uint32_t o = c - r0, q = div248(o);

                if (!count) {
                    if (cap - pos < 2) goto full;
                    dst[pos] = (uint8_t)(wf + q);
                    dst[pos + 1] = (uint8_t)(o - q * 248u + 8u);
                }

                pos += 2;
                pending = NONE;
                i += adv;

                if (i >= n) break;
                c = form == FORM_UTF8 ? s8[i] : form == FORM_UTF16 ? s16[i] : s32[i];

                if (c - 0x20u < an) {
                    EMIT1(c - 0x19u);
                    i++;
                    if (i >= n) break;
                    c = form == FORM_UTF8 ? s8[i] : form == FORM_UTF16 ? s16[i] : s32[i];
                }

                if (form == FORM_UTF8) {
                    if ((c & 0xF0u) != 0xE0u || i + 2 >= n || (s8[i + 1] & 0xC0u) != 0x80u || (s8[i + 2] & 0xC0u) != 0x80u) break;
                    c = ((c & 0x0Fu) << 12) | ((s8[i + 1] & 0x3Fu) << 6) | (s8[i + 2] & 0x3Fu);
                    adv = 3;
                }

                if (c - wlo >= wn) break;
            }

            continue;
        } else if (c - L->punct_lo < L->punct_n) {
            EMIT1(L->punct_first + (c - L->punct_lo));
            pending = NONE;
        } else if (c - L->fw_lo < L->fw_n) {
            EMIT1(L->fw_first + (c - L->fw_lo));
        } else if (c == 0x09u || c == 0x0Au) {
            EMIT1(c - 7u);
        } else if (c == 0x0Du) {
            EMIT1(0x05u);
        } else {
            /* The general path: look up the range, then update the whole state. */
            uint8_t b[4];
            unsigned k;

            if (c - 0xDC00u < 0x400u && i == hi_end) {
                status = STENO_NOT_A_VALUE;
                goto done;
            }

            k = encode_cp(L, c, b);

            if (!count) {
                if (cap - pos < k) goto full;
                memcpy(dst + pos, b, k);
            }

            pos += k;
            if (c - 0xD800u < 0x400u) hi_end = i + adv;

            st.conf = (uint8_t)conf;
            st.pending = pending;
            update(&st, c, k);
            conf = st.conf;
            pending = st.pending;

            if (key_of(&st) != L->key) L = lc_get(lc, &st);
        }

        i += adv;
    }

    goto done;

full:
    status = STENO_DESTINATION_TOO_SMALL;

done:
    st.conf = (uint8_t)conf;
    st.pending = pending;
    st.last_high = (uint8_t)(hi_end == i);
    *stp = st;
    *io_i = i;
    *io_pos = pos;
    return status;
}

/* ---------- The decoder (9) ---------- */

/* Writes code point c (not a surrogate when the form is UTF-8) in the output form. */
#define PUT_CP(c)                                                                                                                       \
    do {                                                                                                                                \
        uint32_t c_ = (c);                                                                                                              \
        if (form == FORM_UTF8) {                                                                                                        \
            size_t need_ = c_ < 0x80u ? 1u : c_ < 0x800u ? 2u : c_ < 0x10000u ? 3u : 4u;                                               \
            if (!count) {                                                                                                               \
                if (cap - pos < need_) goto full;                                                                                       \
                if (need_ == 1) {                                                                                                       \
                    d8[pos] = (uint8_t)c_;                                                                                              \
                } else if (need_ == 2) {                                                                                                \
                    d8[pos] = (uint8_t)(0xC0u | (c_ >> 6));                                                                             \
                    d8[pos + 1] = (uint8_t)(0x80u | (c_ & 0x3Fu));                                                                      \
                } else if (need_ == 3) {                                                                                                \
                    d8[pos] = (uint8_t)(0xE0u | (c_ >> 12));                                                                            \
                    d8[pos + 1] = (uint8_t)(0x80u | ((c_ >> 6) & 0x3Fu));                                                               \
                    d8[pos + 2] = (uint8_t)(0x80u | (c_ & 0x3Fu));                                                                      \
                } else {                                                                                                                \
                    d8[pos] = (uint8_t)(0xF0u | (c_ >> 18));                                                                            \
                    d8[pos + 1] = (uint8_t)(0x80u | ((c_ >> 12) & 0x3Fu));                                                              \
                    d8[pos + 2] = (uint8_t)(0x80u | ((c_ >> 6) & 0x3Fu));                                                               \
                    d8[pos + 3] = (uint8_t)(0x80u | (c_ & 0x3Fu));                                                                      \
                }                                                                                                                       \
            }                                                                                                                           \
            pos += need_;                                                                                                               \
        } else if (form == FORM_UTF16) {                                                                                                \
            if (c_ < 0x10000u) {                                                                                                        \
                if (!count) {                                                                                                           \
                    if (pos >= cap) goto full;                                                                                          \
                    d16[pos] = (uint16_t)c_;                                                                                            \
                }                                                                                                                       \
                pos++;                                                                                                                  \
            } else {                                                                                                                    \
                if (!count) {                                                                                                           \
                    if (cap - pos < 2) goto full;                                                                                       \
                    d16[pos] = (uint16_t)(0xD800u + ((c_ - 0x10000u) >> 10));                                                          \
                    d16[pos + 1] = (uint16_t)(0xDC00u + ((c_ - 0x10000u) & 0x3FFu));                                                    \
                }                                                                                                                       \
                pos += 2;                                                                                                               \
            }                                                                                                                           \
        } else {                                                                                                                        \
            if (!count) {                                                                                                               \
                if (pos >= cap) goto full;                                                                                              \
                d32[pos] = c_;                                                                                                          \
            }                                                                                                                           \
            pos++;                                                                                                                      \
        }                                                                                                                               \
    } while (0)

/* Decodes the Steno bytes src[*io_i..n) from state *stp into the output form at *io_pos (count: only counting). With final 0, the start
   of a code point at the end whose trail bytes so far are valid is not decoded: *io_i stops before it. Returns the status; *io_i is then
   the position of the code point that broke a rule (or of the end), *io_pos the length written. */
STENO_INLINE steno_status dec_core(const int form, const int count, st_t *stp, lcache *lc, const uint8_t *s, size_t n, const int final,
                                   void *dstv, size_t cap, size_t *io_pos, size_t *io_i) {
    /* The destination as its form, converted only for that form (see enc_core). */
    uint8_t *d8 = form == FORM_UTF8 ? (uint8_t *)dstv : NULL;
    uint16_t *d16 = form == FORM_UTF16 ? (uint16_t *)dstv : NULL;
    uint32_t *d32 = form == FORM_CP ? (uint32_t *)dstv : NULL;
    st_t st = *stp;
    const layout *L = lc_get(lc, &st);
    size_t i = *io_i, pos = *io_pos;
    size_t hi_end = st.last_high ? i : SIZE_MAX; /* the position after the last high surrogate code point that was decoded */
    uint32_t conf = st.conf, pending = st.pending;
    steno_status status = STENO_OK;

    while (i < n) {
        uint32_t b = s[i], c;

        /* A run of 1-byte codes. */
        if (b - 0x07u < L->ascii_n || (form == FORM_UTF16 && b - L->one_first < L->one_n && L->one_lo < 0x10000u)) {
            size_t lim = n - i, r;
            uint32_t ones = 0;

            if (!count && cap - pos < lim) lim = cap - pos;
            if (lim == 0) goto full;

            if (form == FORM_UTF8) {
                r = dec_ascii8(s + i, lim, L->ascii_n, count ? NULL : d8 + pos);
            } else if (form == FORM_UTF16) {
                r = dec_run16(s + i, lim, run1_of(L), count ? NULL : d16 + pos, &ones);
            } else {
                for (r = 0; r < lim && s[i + r] - 0x07u < L->ascii_n; r++) {
                    if (!count) d32[pos + r] = (uint32_t)s[i + r] + 0x19u;
                }
            }

            if (ones > 0) {
                conf = conf + ones > 2 ? 2 : conf + ones;
                pending = NONE;
            }

            i += r;
            pos += r;
            continue;
        }

        /* Codes that never change the layout (14.2): only the confidence and the pending window. */
        if (b - L->one_first < L->one_n) {
            PUT_CP(L->one_lo + (b - L->one_first));
            conf = conf < 2 ? conf + 1 : 2;
            pending = NONE;
            i++;

            if (form == FORM_UTF8 && L->one_lo < 0x10000u) {
                /* A run of the main window (or of the kana) in 2- or 3-byte UTF-8 (a window lies wholly below or above U+0800), with
                   printable ASCII among it, in a tight loop. */
                const uint32_t of = L->one_first, on = L->one_n, delta = L->one_lo - L->one_first, an = L->ascii_n;
                const int two = L->one_lo < 0x800u;

                while (i < n) {
                    uint32_t x = s[i], cp;

                    if (x - 0x07u < an) {
                        if (!count) {
                            if (pos >= cap) goto full;
                            d8[pos] = (uint8_t)(x + 0x19u);
                        }

                        pos++;
                        i++;
                        if (i < n && s[i] - 0x07u < an) break; /* a longer run of ASCII: the vector code takes it */
                        continue;
                    }

                    if (x - of >= on) break;
                    cp = x + delta;

                    if (two) {
                        if (!count) {
                            if (cap - pos < 2) goto full;
                            d8[pos] = (uint8_t)(0xC0u | (cp >> 6));
                            d8[pos + 1] = (uint8_t)(0x80u | (cp & 0x3Fu));
                        }

                        pos += 2;
                    } else {
                        if (!count) {
                            if (cap - pos < 3) goto full;
                            d8[pos] = (uint8_t)(0xE0u | (cp >> 12));
                            d8[pos + 1] = (uint8_t)(0x80u | ((cp >> 6) & 0x3Fu));
                            d8[pos + 2] = (uint8_t)(0x80u | (cp & 0x3Fu));
                        }

                        pos += 3;
                    }

                    conf = conf < 2 ? conf + 1 : 2;
                    i++;
                }
            }

            continue;
        }

        if (b - L->wfull_first < L->wfull_n && i + 1 < n && s[i + 1] >= 8) {
            /* Ideographs or syllables of full lead bytes (all 248 code points of the lead byte are in the run, U+4E00..U+D7A3, so they
               take 3 UTF-8 bytes or 1 code unit), in a tight loop with single printable ASCII codes among them. c is the lead byte
               times 248 plus the trail byte plus a constant of the layout, in arithmetic modulo 2^32 (14.3). */
            const uint32_t ff = L->wfull_first, fn = L->wfull_n, base = L->wide_r0 - L->wide_first * 248u - 8u, an = L->ascii_n;

            for (;;) {
                c = base + b * 248u + s[i + 1];

                if (form == FORM_UTF8) {
                    if (!count) {
                        if (cap - pos < 3) goto full;
                        d8[pos] = (uint8_t)(0xE0u | (c >> 12));
                        d8[pos + 1] = (uint8_t)(0x80u | ((c >> 6) & 0x3Fu));
                        d8[pos + 2] = (uint8_t)(0x80u | (c & 0x3Fu));
                    }

                    pos += 3;
                } else {
                    PUT_CP(c);
                }

                pending = NONE;
                i += 2;
                if (i >= n) break;
                b = s[i];

                if (b - 0x07u < an) {
                    PUT_CP(b + 0x19u);
                    i++;
                    if (i >= n) break;
                    b = s[i];
                }

                if (b - ff >= fn || i + 1 >= n || s[i + 1] < 8) break;
            }

            continue;
        }

        if (b - L->punct_first < L->punct_n) {
            PUT_CP(L->punct_lo + (b - L->punct_first));
            pending = NONE;
            i++;
            continue;
        }

        if (b - L->fw_first < L->fw_n) {
            PUT_CP(L->fw_lo + (b - L->fw_first));
            i++;
            continue;
        }

        if (b == 0x02u || b == 0x03u) {
            PUT_CP(b + 7u);
            i++;
            continue;
        }

        if (b == 0x05u) {
            PUT_CP(0x0Du);
            i++;
            continue;
        }

        /* The general path. */
        {
            int r = decode_cp(L, s + i, n - i, &c);

            if (r <= 0) {
                if (r == 0 && !final) break;
                status = r == 0 ? STENO_TRUNCATED : (steno_status)(-r);
                goto done;
            }

            if (c - 0xDC00u < 0x400u && i == hi_end) {
                status = STENO_SURROGATE_PAIR;
                goto done;
            }

            if (form == FORM_UTF8 && c - 0xD800u < 0x800u) {
                status = STENO_SURROGATE;
                goto done;
            }

            PUT_CP(c);
            if (c - 0xD800u < 0x400u) hi_end = i + (size_t)r;

            st.conf = (uint8_t)conf;
            st.pending = pending;
            update(&st, c, (unsigned)r);
            conf = st.conf;
            pending = st.pending;

            if (key_of(&st) != L->key) L = lc_get(lc, &st);

            i += (size_t)r;
        }
    }

    goto done;

full:
    status = STENO_DESTINATION_TOO_SMALL;

done:
    st.conf = (uint8_t)conf;
    st.pending = pending;
    st.last_high = (uint8_t)(hi_end == i);
    *stp = st;
    *io_i = i;
    *io_pos = pos;
    return status;
}

/* ---------- Arguments ---------- */

struct steno_align_u16 {
    char c;
    uint16_t x;
};
struct steno_align_u32 {
    char c;
    uint32_t x;
};
struct steno_align_size {
    char c;
    size_t x;
};
struct steno_align_encoder {
    char c;
    steno_encoder x;
};
struct steno_align_decoder {
    char c;
    steno_decoder x;
};

#define ALIGN_U16 offsetof(struct steno_align_u16, x)
#define ALIGN_U32 offsetof(struct steno_align_u32, x)
#define ALIGN_SIZE offsetof(struct steno_align_size, x)
#define ALIGN_ENCODER offsetof(struct steno_align_encoder, x)
#define ALIGN_DECODER offsetof(struct steno_align_decoder, x)

/* True if (p, len elements of elem bytes, aligned to align) cannot be a buffer: NULL with a length, not aligned, larger than any object
   can be, or reaching past the end of the address space. */
STENO_INLINE int bad_buffer(const void *p, size_t len, size_t elem, size_t align) {
    uintptr_t a = (uintptr_t)p;

    if (len == 0) return 0;
    if (p == NULL || len > (size_t)PTRDIFF_MAX / elem || a % align != 0) return 1;
    return a > UINTPTR_MAX - len * elem;
}

/* True if the two buffers (lengths in bytes, already checked by bad_buffer) share a byte. */
STENO_INLINE int overlap(const void *a, size_t alen, const void *b, size_t blen) {
    uintptr_t a0 = (uintptr_t)a, b0 = (uintptr_t)b;
    return alen != 0 && blen != 0 && a0 < b0 + blen && b0 < a0 + alen;
}

STENO_INLINE int bad_out_len(const size_t *out_len) {
    return out_len == NULL || (uintptr_t)out_len % ALIGN_SIZE != 0;
}

/* True if the len elements of elem bytes at p share a byte with *out_len. The range saturates at the end of the address space, so this is
   safe to ask before the buffer's own checks; a buffer of length 0 shares no byte with anything. */
STENO_INLINE int out_len_in(const size_t *out_len, const void *p, size_t len, size_t elem) {
    /* elem is 1, 2 or 4: a length up to SIZE_MAX / 4 cannot overflow, and a longer one saturates (no division at run time). */
    uintptr_t o = (uintptr_t)out_len, a = (uintptr_t)p, bytes = len > SIZE_MAX / 4 ? SIZE_MAX : len * elem;
    uintptr_t end = a > UINTPTR_MAX - bytes ? UINTPTR_MAX : a + bytes;
    return len != 0 && o < end && a < o + sizeof *out_len;
}

/* True if out_len cannot take the result: NULL, not aligned, or inside the source or the destination (as in the Rust implementation).
   The call then fails with STENO_INVALID_ARGUMENT and writes nothing through out_len. */
STENO_INLINE int bad_out(const size_t *out_len, const void *src, size_t src_len, size_t src_elem, const void *dst, size_t dst_len,
                   size_t dst_elem) {
    return bad_out_len(out_len) || out_len_in(out_len, src, src_len, src_elem) || out_len_in(out_len, dst, dst_len, dst_elem);
}

/* The checks of a one-shot call: STENO_OK or STENO_INVALID_ARGUMENT. */
STENO_INLINE steno_status check_args(const void *src, size_t src_len, size_t src_elem, const void *dst, size_t dst_len, size_t dst_elem) {
    if (bad_buffer(src, src_len, src_elem, src_elem == 1 ? 1 : src_elem == 2 ? ALIGN_U16 : ALIGN_U32) ||
        bad_buffer(dst, dst_len, dst_elem, dst_elem == 1 ? 1 : dst_elem == 2 ? ALIGN_U16 : ALIGN_U32) ||
        overlap(src, src_len * src_elem, dst, dst_len * dst_elem)) {
        return STENO_INVALID_ARGUMENT;
    }

    return STENO_OK;
}

static size_t sat_mul(size_t n, size_t k) {
    return n > SIZE_MAX / k ? SIZE_MAX : n * k;
}

static size_t sat_add(size_t a, size_t b) {
    return a > SIZE_MAX - b ? SIZE_MAX : a + b;
}

/* ---------- One-shot functions ---------- */

/* Runs a whole encode (count: only counting) and sets *out_len as steno.h says. */
STENO_INLINE steno_status encode_all(const int form, const int count, const void *src, size_t n, uint8_t *dst, size_t cap,
                                     size_t *out_len) {
    st_t st = ST_INITIAL;
    lcache lc;
    size_t i = 0, pos = 0;
    steno_status r;

    lc_init(&lc);
    r = enc_core(form, count, &st, &lc, src, n, 1, dst, cap, &pos, &i);
    *out_len = r == STENO_OK ? pos : (r == STENO_INVALID_UTF8 || r == STENO_NOT_A_VALUE || r == STENO_INVALID_CODE_POINT) ? i : 0;
    return r;
}

/* Runs a whole decode (count: only counting) and sets *out_len as steno.h says. */
STENO_INLINE steno_status decode_all(const int form, const int count, const uint8_t *src, size_t n, void *dst, size_t cap,
                                     size_t *out_len) {
    st_t st = ST_INITIAL;
    lcache lc;
    size_t i = 0, pos = 0;
    steno_status r;

    lc_init(&lc);
    r = dec_core(form, count, &st, &lc, src, n, 1, dst, cap, &pos, &i);
    *out_len = r == STENO_OK ? pos : r == STENO_DESTINATION_TOO_SMALL ? 0 : i;
    return r;
}

/* The one-shot functions. A NULL dst has length 0 (check_args): the loops check the room before they form an address in dst, so
   they never touch it. Every public function refuses an out_len inside its source, destination or object (bad_out), and stores through
   out_len once, as its last action. */

steno_status steno_encode_utf8(const uint8_t *src, size_t src_len, uint8_t *dst, size_t dst_len, size_t *out_len) {
    size_t len = 0;
    steno_status r;

    if (bad_out(out_len, src, src_len, 1, dst, dst_len, 1)) return STENO_INVALID_ARGUMENT;
    if (check_args(src, src_len, 1, dst, dst_len, 1) != STENO_OK) r = STENO_INVALID_ARGUMENT;
    else r = encode_all(FORM_UTF8, 0, src, src_len, dst, dst_len, &len);
    *out_len = len; /* only now: out_len may point into src */
    return r;
}

steno_status steno_encode_utf16(const uint16_t *src, size_t src_len, uint8_t *dst, size_t dst_len, size_t *out_len) {
    size_t len = 0;
    steno_status r;

    if (bad_out(out_len, src, src_len, 2, dst, dst_len, 1)) return STENO_INVALID_ARGUMENT;
    if (check_args(src, src_len, 2, dst, dst_len, 1) != STENO_OK) r = STENO_INVALID_ARGUMENT;
    else r = encode_all(FORM_UTF16, 0, src, src_len, dst, dst_len, &len);
    *out_len = len; /* only now: out_len may point into src */
    return r;
}

steno_status steno_encode_code_points(const uint32_t *src, size_t src_len, uint8_t *dst, size_t dst_len, size_t *out_len) {
    size_t len = 0;
    steno_status r;

    if (bad_out(out_len, src, src_len, 4, dst, dst_len, 1)) return STENO_INVALID_ARGUMENT;
    if (check_args(src, src_len, 4, dst, dst_len, 1) != STENO_OK) r = STENO_INVALID_ARGUMENT;
    else r = encode_all(FORM_CP, 0, src, src_len, dst, dst_len, &len);
    *out_len = len; /* only now: out_len may point into src */
    return r;
}

steno_status steno_decode_utf8(const uint8_t *src, size_t src_len, uint8_t *dst, size_t dst_len, size_t *out_len) {
    size_t len = 0;
    steno_status r;

    if (bad_out(out_len, src, src_len, 1, dst, dst_len, 1)) return STENO_INVALID_ARGUMENT;
    if (check_args(src, src_len, 1, dst, dst_len, 1) != STENO_OK) r = STENO_INVALID_ARGUMENT;
    else r = decode_all(FORM_UTF8, 0, src, src_len, dst, dst_len, &len);
    *out_len = len; /* only now: out_len may point into src */
    return r;
}

steno_status steno_decode_utf16(const uint8_t *src, size_t src_len, uint16_t *dst, size_t dst_len, size_t *out_len) {
    size_t len = 0;
    steno_status r;

    if (bad_out(out_len, src, src_len, 1, dst, dst_len, 2)) return STENO_INVALID_ARGUMENT;
    if (check_args(src, src_len, 1, dst, dst_len, 2) != STENO_OK) r = STENO_INVALID_ARGUMENT;
    else r = decode_all(FORM_UTF16, 0, src, src_len, dst, dst_len, &len);
    *out_len = len; /* only now: out_len may point into src */
    return r;
}

steno_status steno_decode_code_points(const uint8_t *src, size_t src_len, uint32_t *dst, size_t dst_len, size_t *out_len) {
    size_t len = 0;
    steno_status r;

    if (bad_out(out_len, src, src_len, 1, dst, dst_len, 4)) return STENO_INVALID_ARGUMENT;
    if (check_args(src, src_len, 1, dst, dst_len, 4) != STENO_OK) r = STENO_INVALID_ARGUMENT;
    else r = decode_all(FORM_CP, 0, src, src_len, dst, dst_len, &len);
    *out_len = len; /* only now: out_len may point into src */
    return r;
}

/* The length functions: the same loops, counting. */

steno_status steno_encoded_len_utf8(const uint8_t *src, size_t src_len, size_t *out_len) {
    size_t len = 0;
    steno_status r;

    if (bad_out(out_len, src, src_len, 1, NULL, 0, 1)) return STENO_INVALID_ARGUMENT;
    if (bad_buffer(src, src_len, 1, 1)) r = STENO_INVALID_ARGUMENT;
    else r = encode_all(FORM_UTF8, 1, src, src_len, NULL, SIZE_MAX, &len);
    *out_len = len;
    return r;
}

steno_status steno_encoded_len_utf16(const uint16_t *src, size_t src_len, size_t *out_len) {
    size_t len = 0;
    steno_status r;

    if (bad_out(out_len, src, src_len, 2, NULL, 0, 1)) return STENO_INVALID_ARGUMENT;
    if (bad_buffer(src, src_len, 2, ALIGN_U16)) r = STENO_INVALID_ARGUMENT;
    else r = encode_all(FORM_UTF16, 1, src, src_len, NULL, SIZE_MAX, &len);
    *out_len = len;
    return r;
}

steno_status steno_encoded_len_code_points(const uint32_t *src, size_t src_len, size_t *out_len) {
    size_t len = 0;
    steno_status r;

    if (bad_out(out_len, src, src_len, 4, NULL, 0, 1)) return STENO_INVALID_ARGUMENT;
    if (bad_buffer(src, src_len, 4, ALIGN_U32)) r = STENO_INVALID_ARGUMENT;
    else r = encode_all(FORM_CP, 1, src, src_len, NULL, SIZE_MAX, &len);
    *out_len = len;
    return r;
}

steno_status steno_decoded_len_utf8(const uint8_t *src, size_t src_len, size_t *out_len) {
    size_t len = 0;
    steno_status r;

    if (bad_out(out_len, src, src_len, 1, NULL, 0, 1)) return STENO_INVALID_ARGUMENT;
    if (bad_buffer(src, src_len, 1, 1)) r = STENO_INVALID_ARGUMENT;
    else r = decode_all(FORM_UTF8, 1, src, src_len, NULL, SIZE_MAX, &len);
    *out_len = len;
    return r;
}

steno_status steno_decoded_len_utf16(const uint8_t *src, size_t src_len, size_t *out_len) {
    size_t len = 0;
    steno_status r;

    if (bad_out(out_len, src, src_len, 1, NULL, 0, 1)) return STENO_INVALID_ARGUMENT;
    if (bad_buffer(src, src_len, 1, 1)) r = STENO_INVALID_ARGUMENT;
    else r = decode_all(FORM_UTF16, 1, src, src_len, NULL, SIZE_MAX, &len);
    *out_len = len;
    return r;
}

steno_status steno_decoded_len_code_points(const uint8_t *src, size_t src_len, size_t *out_len) {
    size_t len = 0;
    steno_status r;

    if (bad_out(out_len, src, src_len, 1, NULL, 0, 1)) return STENO_INVALID_ARGUMENT;
    if (bad_buffer(src, src_len, 1, 1)) r = STENO_INVALID_ARGUMENT;
    else r = decode_all(FORM_CP, 1, src, src_len, NULL, SIZE_MAX, &len);
    *out_len = len;
    return r;
}

/* A code point takes at most 2 Steno bytes per UTF-8 byte: a C0 control other than tab, line feed and carriage return, or an ASCII letter
   in the CJK context with a kana slot, takes 2 bytes for 1; a code point of 2 or 3 UTF-8 bytes takes at most 3, one of 4 at most 4. */
size_t steno_max_encoded_len_utf8(size_t utf8_len) {
    return sat_mul(utf8_len, 2);
}

/* A code point below U+10000 (one code unit) takes at most 3 bytes; a supplementary one (two code units) at most 4. */
size_t steno_max_encoded_len_utf16(size_t utf16_len) {
    return sat_mul(utf16_len, 3);
}

size_t steno_max_encoded_len_code_points(size_t code_points) {
    return sat_mul(code_points, 4);
}

/* A code point takes at least 1 Steno byte, and at most 4 UTF-8 bytes or 2 code units. */
size_t steno_max_decoded_len_utf8(size_t steno_len) {
    return sat_mul(steno_len, 4);
}

size_t steno_max_decoded_len_utf16(size_t steno_len) {
    return sat_mul(steno_len, 2);
}

size_t steno_max_decoded_len_code_points(size_t steno_len) {
    return steno_len;
}

/* FORMAT.md 14.1: a value has only tab, line feed, carriage return and U+0020..U+007E if and only if every byte of its encoding is 02,
   03, 05 or 07..65, and bytes that are all such are valid. */
bool steno_is_ascii_text(const uint8_t *src, size_t src_len) {
    size_t k = 0;

    if (bad_buffer(src, src_len, 1, 1)) return false;

#if defined(STENO_SSE2)
    {
        const __m128i base = _mm_set1_epi8(0x07), top = _mm_set1_epi8(0x5E);
        const __m128i tab = _mm_set1_epi8(0x02), lf = _mm_set1_epi8(0x03), cr = _mm_set1_epi8(0x05);

        for (; k + 16 <= src_len; k += 16) {
            __m128i v = _mm_loadu_si128((const __m128i *)(const void *)(src + k));
            __m128i t = _mm_sub_epi8(v, base);
            __m128i ok = _mm_or_si128(_mm_cmpeq_epi8(_mm_max_epu8(t, top), top),
                                      _mm_or_si128(_mm_cmpeq_epi8(v, tab), _mm_or_si128(_mm_cmpeq_epi8(v, lf), _mm_cmpeq_epi8(v, cr))));
            if (_mm_movemask_epi8(ok) != 0xFFFF) return false;
        }
    }
#elif defined(STENO_NEON)
    {
        const uint8x16_t base = vdupq_n_u8(0x07), top = vdupq_n_u8(0x5E);
        const uint8x16_t tab = vdupq_n_u8(0x02), lf = vdupq_n_u8(0x03), cr = vdupq_n_u8(0x05);

        for (; k + 16 <= src_len; k += 16) {
            uint8x16_t v = vld1q_u8(src + k);
            uint8x16_t ok = vorrq_u8(vcleq_u8(vsubq_u8(v, base), top),
                                     vorrq_u8(vceqq_u8(v, tab), vorrq_u8(vceqq_u8(v, lf), vceqq_u8(v, cr))));
            if (vminvq_u8(ok) != 0xFF) return false;
        }
    }
#endif

    for (; k + 8 <= src_len; k += 8) {
        uint64_t x;
        memcpy(&x, src + k, 8);
        if (!swar_all_in(x, 0x07u, 0x65u)) break;
    }

    for (; k < src_len; k++) {
        uint32_t b = src[k];
        if (b - 0x07u > 0x5Eu && b != 0x02u && b != 0x03u && b != 0x05u) return false;
    }

    return true;
}

const char *steno_status_name(int status) {
    switch (status) {
    case STENO_OK: return "ok";
    case STENO_ZERO_BYTE: return "zero-byte";
    case STENO_UNUSED_LEAD: return "unused-lead";
    case STENO_TRUNCATED: return "truncated";
    case STENO_PAST_RANGE: return "past-range";
    case STENO_SURROGATE_PAIR: return "surrogate-pair";
    case STENO_DESTINATION_TOO_SMALL: return "destination-too-small";
    case STENO_INVALID_UTF8: return "invalid-utf8";
    case STENO_SURROGATE: return "surrogate";
    case STENO_INVALID_ARGUMENT: return "invalid-argument";
    case STENO_INVALID_TRAIL: return "invalid-trail";
    case STENO_NOT_A_VALUE: return "not-a-value";
    case STENO_INVALID_CODE_POINT: return "invalid-code-point";
    default: return "unknown";
    }
}

/* ---------- Streaming ---------- */

#define MAGIC_ENCODER 0x5354454Eu /* "STEN" */
#define MAGIC_DECODER 0x5354444Fu

enum { HELD_NONE = 0, HELD_UTF8 = 1, HELD_HIGH = 2 };

static void progress_init(progress *p) {
    memset(p, 0, sizeof *p);
    p->st = ST_INITIAL;
    p->held_kind = HELD_NONE;
}

void steno_encoder_init(steno_encoder *enc) {
    if (enc == NULL) return;
    enc->magic_ = MAGIC_ENCODER;
    progress_init(&enc->progress_);
    lc_init(&enc->cache_);
}

void steno_encoder_reset(steno_encoder *enc) {
    steno_encoder_init(enc);
}

void steno_decoder_init(steno_decoder *dec) {
    if (dec == NULL) return;
    dec->magic_ = MAGIC_DECODER;
    progress_init(&dec->progress_);
    lc_init(&dec->cache_);
}

void steno_decoder_reset(steno_decoder *dec) {
    steno_decoder_init(dec);
}

/* The encoder, if the pointer can be one: aligned, and initialized (or all zero, which is initialized here). */
static steno_encoder *encoder_of(steno_encoder *enc) {
    if (enc == NULL || (uintptr_t)enc % ALIGN_ENCODER != 0) return NULL;
    if (enc->magic_ == 0) steno_encoder_init(enc);
    return enc->magic_ == MAGIC_ENCODER ? enc : NULL;
}

static steno_decoder *decoder_of(steno_decoder *dec) {
    if (dec == NULL || (uintptr_t)dec % ALIGN_DECODER != 0) return NULL;
    if (dec->magic_ == 0) steno_decoder_init(dec);
    return dec->magic_ == MAGIC_DECODER ? dec : NULL;
}

/* Encodes one code point given as a uint32_t (a held high surrogate, or a pair joined across two calls). */
static steno_status enc_single(steno_encoder *e, uint32_t c, uint8_t *dst, size_t cap, size_t *pos) {
    size_t i = 0;
    return enc_core(FORM_CP, 0, &e->progress_.st, &e->cache_, &c, 1, 1, dst, cap, pos, &i);
}

/* The common part of the three feed functions of the encoder. It sets *len to what steno.h says out_len gets; the caller stores it. */
static steno_status enc_feed_body(steno_encoder *enc, const int form, const void *src, size_t n, size_t elem, uint8_t *dst, size_t cap,
                                  size_t *len) {
    steno_encoder *e;
    progress save;
    size_t pos = 0, i = 0, errpos = 0;
    steno_status r = STENO_OK;

    e = encoder_of(enc);

    if (e == NULL || check_args(src, n, elem, dst, cap, 1) != STENO_OK || overlap(dst, cap, e, sizeof *e) ||
        overlap(src, n * elem, e, sizeof *e)) {
        return STENO_INVALID_ARGUMENT;
    }

    if (n == 0) return STENO_OK;

    save = e->progress_;

    if (e->progress_.held_kind == HELD_UTF8) {
        progress *p = &e->progress_;
        uint8_t tmp[4];
        size_t k = p->held_n, take;
        uint32_t c;
        int seq;

        if (form != FORM_UTF8) {
            /* An unfinished UTF-8 sequence followed by other input. */
            r = STENO_INVALID_UTF8;
            errpos = p->fed - k;
            goto fail;
        }

        memcpy(tmp, p->held, k);
        take = utf8_seq_len(tmp[0]) - k;
        if (take > n) take = n;
        memcpy(tmp + k, src, take);
        seq = utf8_read(tmp, k + take, &c);

        if (seq < 0) {
            r = STENO_INVALID_UTF8;
            errpos = p->fed - k;
            goto fail;
        }

        if (seq == 0) {
            /* Still unfinished: the whole piece is held (take is n here). */
            memcpy(p->held + k, src, take);
            p->held_n = (uint8_t)(k + take);
            p->fed = sat_add(p->fed, n);
            return STENO_OK;
        }

        p->held_kind = HELD_NONE;
        p->held_n = 0;
        r = enc_single(e, c, dst, cap, &pos);
        if (r != STENO_OK) goto fail;
        i = take;
    } else if (e->progress_.held_kind == HELD_HIGH) {
        progress *p = &e->progress_;
        uint32_t high = p->held_unit, c2;

        p->held_kind = HELD_NONE;

        if (form == FORM_UTF16 && (c2 = ((const uint16_t *)src)[0]) - 0xDC00u < 0x400u) {
            r = enc_single(e, 0x10000u + ((high - 0xD800u) << 10) + (c2 - 0xDC00u), dst, cap, &pos);
            i = 1;
        } else {
            r = enc_single(e, high, dst, cap, &pos);
        }

        if (r != STENO_OK) goto fail;
    }

    r = enc_core(form, 0, &e->progress_.st, &e->cache_, src, n, form == FORM_CP, dst, cap, &pos, &i);

    if (r != STENO_OK) {
        errpos = sat_add(e->progress_.fed, i);
        goto fail;
    }

    if (i < n) {
        /* An unfinished UTF-8 sequence (1 to 3 bytes) or a high surrogate code unit at the end. */
        progress *p = &e->progress_;

        if (form == FORM_UTF8) {
            p->held_kind = HELD_UTF8;
            p->held_n = (uint8_t)(n - i);
            memcpy(p->held, (const uint8_t *)src + i, n - i);
        } else {
            p->held_kind = HELD_HIGH;
            p->held_unit = ((const uint16_t *)src)[i];
        }
    }

    e->progress_.fed = sat_add(e->progress_.fed, n);
    *len = pos;
    return STENO_OK;

fail:
    e->progress_ = save;
    *len = r == STENO_INVALID_UTF8 || r == STENO_NOT_A_VALUE || r == STENO_INVALID_CODE_POINT ? errpos : 0;
    return r;
}

/* Runs enc_feed_body and stores through out_len last (see the one-shot functions). */
static steno_status enc_feed(steno_encoder *enc, const int form, const void *src, size_t n, size_t elem, uint8_t *dst, size_t cap,
                             size_t *out_len) {
    size_t len = 0;
    steno_status r;

    if (bad_out(out_len, src, n, elem, dst, cap, 1) || out_len_in(out_len, enc, 1, sizeof *enc)) return STENO_INVALID_ARGUMENT;
    r = enc_feed_body(enc, form, src, n, elem, dst, cap, &len);
    *out_len = len;
    return r;
}

steno_status steno_encoder_feed_utf8(steno_encoder *enc, const uint8_t *src, size_t src_len, uint8_t *dst, size_t dst_len,
                                     size_t *out_len) {
    return enc_feed(enc, FORM_UTF8, src, src_len, 1, dst, dst_len, out_len);
}

steno_status steno_encoder_feed_utf16(steno_encoder *enc, const uint16_t *src, size_t src_len, uint8_t *dst, size_t dst_len,
                                      size_t *out_len) {
    return enc_feed(enc, FORM_UTF16, src, src_len, 2, dst, dst_len, out_len);
}

steno_status steno_encoder_feed_code_points(steno_encoder *enc, const uint32_t *src, size_t src_len, uint8_t *dst, size_t dst_len,
                                            size_t *out_len) {
    return enc_feed(enc, FORM_CP, src, src_len, 4, dst, dst_len, out_len);
}

/* The body of steno_encoder_finish: sets *len to what out_len gets. */
static steno_status enc_finish_body(steno_encoder *enc, uint8_t *dst, size_t dst_len, size_t *len) {
    steno_encoder *e = encoder_of(enc);
    progress save;
    size_t pos = 0;
    steno_status r;

    if (e == NULL || bad_buffer(dst, dst_len, 1, 1) || overlap(dst, dst_len, e, sizeof *e)) {
        return STENO_INVALID_ARGUMENT;
    }

    if (e->progress_.held_kind == HELD_UTF8) {
        *len = e->progress_.fed - e->progress_.held_n;
        return STENO_INVALID_UTF8;
    }

    if (e->progress_.held_kind != HELD_HIGH) return STENO_OK;

    save = e->progress_;
    e->progress_.held_kind = HELD_NONE;
    r = enc_single(e, e->progress_.held_unit, dst, dst_len, &pos);

    if (r != STENO_OK) {
        e->progress_ = save;
        return r;
    }

    *len = pos;
    return STENO_OK;
}

steno_status steno_encoder_finish(steno_encoder *enc, uint8_t *dst, size_t dst_len, size_t *out_len) {
    size_t len = 0;
    steno_status r;

    if (bad_out(out_len, NULL, 0, 1, dst, dst_len, 1) || out_len_in(out_len, enc, 1, sizeof *enc)) return STENO_INVALID_ARGUMENT;
    r = enc_finish_body(enc, dst, dst_len, &len);
    *out_len = len;
    return r;
}

/* The common part of the three feed functions of the decoder. It sets *len to what steno.h says out_len gets; the caller stores it. */
STENO_INLINE steno_status dec_feed_body(steno_decoder *dec, const int form, const uint8_t *src, size_t n, void *dst, size_t cap,
                                        size_t elem, size_t *len) {
    steno_decoder *d;
    progress save;
    size_t pos = 0, i = 0, errpos = 0;
    steno_status r;

    d = decoder_of(dec);

    if (d == NULL || check_args(src, n, 1, dst, cap, elem) != STENO_OK || overlap(dst, cap * elem, d, sizeof *d) ||
        overlap(src, n, d, sizeof *d)) {
        return STENO_INVALID_ARGUMENT;
    }

    if (n == 0) return STENO_OK;

    save = d->progress_;

    if (d->progress_.held_n > 0) {
        /* Finish the held code point with the first bytes of the piece, exactly as many as it lacks. Its lead byte was valid in the
           current state when it was held, and the state has not changed since. */
        progress *p = &d->progress_;
        const layout *L = lc_get(&d->cache_, &p->st);
        uint8_t tmp[4];
        size_t k = p->held_n, need = (size_t)L->size[range_of_lead(L, p->held[0])] - k, take = n < need ? n : need, ti = 0;

        memcpy(tmp, p->held, k);
        memcpy(tmp + k, src, take);
        r = dec_core(form, 0, &p->st, &d->cache_, tmp, k + take, 0, dst, cap, &pos, &ti);

        if (r != STENO_OK) {
            errpos = p->fed - k + ti;
            goto fail;
        }

        if (ti == 0) {
            /* Still unfinished, and the new trail bytes are valid: the whole piece is held (take is n here). */
            memcpy(p->held + k, src, take);
            p->held_n = (uint8_t)(k + take);
            p->fed = sat_add(p->fed, n);
            return STENO_OK;
        }

        p->held_n = 0;
        i = take;
    }

    r = dec_core(form, 0, &d->progress_.st, &d->cache_, src, n, 0, dst, cap, &pos, &i);

    if (r != STENO_OK) {
        errpos = sat_add(d->progress_.fed, i);
        goto fail;
    }

    if (i < n) {
        progress *p = &d->progress_;
        p->held_n = (uint8_t)(n - i);
        memcpy(p->held, src + i, n - i);
    }

    d->progress_.fed = sat_add(d->progress_.fed, n);
    *len = pos;
    return STENO_OK;

fail:
    d->progress_ = save;
    *len = r == STENO_DESTINATION_TOO_SMALL ? 0 : errpos;
    return r;
}

/* Runs dec_feed_body and stores through out_len last. */
STENO_INLINE steno_status dec_feed(steno_decoder *dec, const int form, const uint8_t *src, size_t n, void *dst, size_t cap,
                                   size_t elem, size_t *out_len) {
    size_t len = 0;
    steno_status r;

    if (bad_out(out_len, src, n, 1, dst, cap, elem) || out_len_in(out_len, dec, 1, sizeof *dec)) return STENO_INVALID_ARGUMENT;
    r = dec_feed_body(dec, form, src, n, dst, cap, elem, &len);
    *out_len = len;
    return r;
}

steno_status steno_decoder_feed_utf8(steno_decoder *dec, const uint8_t *src, size_t src_len, uint8_t *dst, size_t dst_len,
                                     size_t *out_len) {
    return dec_feed(dec, FORM_UTF8, src, src_len, dst, dst_len, 1, out_len);
}

steno_status steno_decoder_feed_utf16(steno_decoder *dec, const uint8_t *src, size_t src_len, uint16_t *dst, size_t dst_len,
                                      size_t *out_len) {
    return dec_feed(dec, FORM_UTF16, src, src_len, dst, dst_len, 2, out_len);
}

steno_status steno_decoder_feed_code_points(steno_decoder *dec, const uint8_t *src, size_t src_len, uint32_t *dst, size_t dst_len,
                                            size_t *out_len) {
    return dec_feed(dec, FORM_CP, src, src_len, dst, dst_len, 4, out_len);
}

steno_status steno_decoder_finish(const steno_decoder *dec, size_t *out_len) {
    size_t len = 0;
    steno_status r = STENO_OK;

    if (bad_out_len(out_len) || out_len_in(out_len, dec, 1, sizeof *dec)) return STENO_INVALID_ARGUMENT;

    if (dec == NULL || (uintptr_t)dec % ALIGN_DECODER != 0 || (dec->magic_ != 0 && dec->magic_ != MAGIC_DECODER)) {
        r = STENO_INVALID_ARGUMENT;
    } else if (dec->magic_ != 0 && dec->progress_.held_n > 0) {
        r = STENO_TRUNCATED;
        len = dec->progress_.fed - dec->progress_.held_n;
    }

    *out_len = len;
    return r;
}
