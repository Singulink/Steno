/*
 * A deterministic sweep of the fast paths, written by the adversarial reviewer of this library. For each kind of run (printable ASCII,
 * the main window in 2- and 3-byte UTF-8, kana of the kana slot, ideographs, Hangul syllables, CJK punctuation, fullwidth forms), each
 * offset of the run after a context-setting prefix, each run length around the 8- and 16-element blocks, and each kind of breaker after
 * the run (another window, a control, a surrogate, an ASCII letter, an ideograph, a supplementary code point, a value above 0x10FFFF),
 * the text goes through every check of fuzz_differential.c (all forms, all destination sizes, streaming, decoding of mutations) against
 * the independent oracle.
 *
 *   test_sweep            a reduced sweep (offsets and lengths around the block sizes), for make test
 *   test_sweep full [P]   the whole sweep (945,152 texts; P: one prefix only, 0..7, to run the prefixes in parallel)
 */

#include "fuzz_differential.c"

int main(int argc, char **argv) {
    static const uint32_t prefixes[][3] = {
        {0x0430, 0x0431, 0},   /* Cyrillic main window (2-byte UTF-8) */
        {0x0E01, 0x0E02, 0},   /* Thai (3-byte UTF-8) */
        {0x3042, 0, 0},        /* CJK, hiragana slot */
        {0x30A2, 0, 0},        /* CJK, katakana slot */
        {0x4E00, 0, 0},        /* CJK, letters slot */
        {0xAC00, 0, 0},        /* Hangul */
        {0x10400, 0x10401, 0}, /* a supplementary main window */
        {0, 0, 0},             /* the initial state */
    };
    static const uint32_t breakers[] = {0x1200, 0x0001, 0x0009, 0xD800, 0xDC00, 0x0041, 0x4E01,
                                        0x1F600, 0x110000, 0x00E9, 0x3001, 0xFF21, 0x0435};
    static const size_t full_offs[] = {0, 1, 2, 3, 5, 7, 8, 9, 15, 16, 17, 24, 31, 32, 33, 40};
    static const size_t quick_offs[] = {0, 1, 7, 8, 9, 15, 16, 17};
    static const size_t quick_lens[] = {0, 1, 2, 7, 8, 9, 15, 16, 17, 23, 24, 31, 32, 33, 47, 48, 49, 63, 64, 65, 70};
    const int full = argc > 1 && strcmp(argv[1], "full") == 0;
    const size_t *offs = full ? full_offs : quick_offs;
    const size_t noffs = full ? sizeof full_offs / sizeof full_offs[0] : sizeof quick_offs / sizeof quick_offs[0];
    const size_t nlens = full ? 71 : sizeof quick_lens / sizeof quick_lens[0];
    long only = full && argc > 2 ? atol(argv[2]) : -1;
    uint32_t text[256];
    unsigned long count = 0;
    size_t p, kind, oi, li, br;
    uint8_t seed[8] = {0};
    src_t s;

    g_enc_a = (steno_encoder *)xmalloc(sizeof *g_enc_a);
    g_enc_b = (steno_encoder *)xmalloc(sizeof *g_enc_b);
    g_dec_a = (steno_decoder *)xmalloc(sizeof *g_dec_a);
    g_dec_b = (steno_decoder *)xmalloc(sizeof *g_dec_b);

    for (p = 0; p < sizeof prefixes / sizeof prefixes[0]; p++) {
        if (only >= 0 && (size_t)only != p) continue;

        for (kind = 0; kind < 8; kind++) {
            for (oi = 0; oi < noffs; oi++) {
                for (li = 0; li < nlens; li++) {
                    size_t off = offs[oi], len = full ? li : quick_lens[li];

                    for (br = 0; br < sizeof breakers / sizeof breakers[0]; br++) {
                        size_t n = 0, k;

                        for (k = 0; k < 3 && prefixes[p][k] != 0; k++) text[n++] = prefixes[p][k];

                        /* Letters, or punctuation that keeps the kana slot. */
                        for (k = 0; k < off; k++) text[n++] = 0x61 + (uint32_t)(k % 26) - (kind == 2 || kind == 3 ? 0x40 : 0);

                        for (k = 0; k < len; k++) {
                            uint32_t c;

                            switch (kind) {
                            case 0: c = 0x20 + (uint32_t)((k * 7) % 0x5F); break;
                            case 1: c = (k % 5 == 4) ? 0x20 : 0x0430 + (uint32_t)(k % 32); break;
                            case 2: c = (k % 4 == 3) ? 0x20 : 0x3041 + (uint32_t)(k % 0x56); break;
                            case 3: c = 0x4E00 + (uint32_t)(k * 97 % 0x5200); break;
                            case 4: c = (k % 6 == 5) ? 0x20 : 0xAC00 + (uint32_t)(k * 31 % 0x2BA4); break;
                            case 5: c = 0x3000 + (uint32_t)(k % 0x10); break;
                            case 6: c = 0xFF00 + (uint32_t)(k % 0x20); break;
                            default: c = 0x0E01 + (uint32_t)(k % 0x50); break;
                            }

                            text[n++] = c;
                        }

                        text[n++] = breakers[br];
                        for (k = 0; k < 20; k++) text[n++] = 0x0430 + (uint32_t)k; /* and a run after it */

                        seed[0] = (uint8_t)count;
                        seed[1] = (uint8_t)(count >> 8);
                        s.d = seed;
                        s.n = 0;
                        s.i = 0;
                        s.x = UINT64_C(0x9E3779B97F4A7C15) ^ count;
                        g_exact_alloc = (int)(count & 1);
                        check_text(&s, text, n);
                        count++;
                    }
                }
            }
        }
    }

    printf("sweep%s: %lu texts, no failure\n", full ? " (full)" : "", count);
    return 0;
}
