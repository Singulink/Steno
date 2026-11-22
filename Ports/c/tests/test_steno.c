/*
 * The tests of the C reference implementation of the Steno Encoding, through steno.h, against the plain reference codec ref.c and the
 * independent oracle oracle.c.
 *
 *   test_steno [vector file]                  every test (the vector file defaults to ../../TestVectors/steno-v1-vectors.txt)
 *   test_steno [vector file] --short4 F T     also every 4-byte string whose first byte is in F..T-1 (about 25 s per first byte)
 *
 * STENO_LARGE_VECTORS names the large vector file, which is then checked too. STENO_EXHAUSTIVE=1 adds every 3-byte string (20 s).
 */

#include "../steno.h"

#include <time.h>

#include "harness.h"
#include "oracle.h"
#include "ref.h"

/* ---------- Buffers with guard bytes on both sides ---------- */

#define GUARD 32
#define GUARD_BYTE 0xA5

/* A buffer of `bytes` bytes with GUARD bytes before and after it, aligned to 16 bytes. Free it with g_free. */
static void *g_alloc(size_t bytes) {
    uint8_t *m = (uint8_t *)xmalloc(bytes + 2 * GUARD);
    memset(m, GUARD_BYTE, bytes + 2 * GUARD);
    return m + GUARD;
}

/* True if the guard bytes around the buffer, and the bytes of the buffer from `used` on, are untouched. */
static int g_intact(const void *p, size_t used, size_t bytes) {
    const uint8_t *b = (const uint8_t *)p;
    size_t i;

    for (i = 0; i < GUARD; i++) {
        if (b[-1 - (long)i] != GUARD_BYTE || b[bytes + i] != GUARD_BYTE) return 0;
    }

    for (i = used; i < bytes; i++) {
        if (b[i] != GUARD_BYTE) return 0;
    }

    return 1;
}

static void g_free(void *p) {
    if (p != NULL) free((uint8_t *)p - GUARD);
}

/* ---------- Helpers ---------- */

static const size_t ELEM[3] = {1, 2, 4}; /* the element size of the forms: UTF-8, UTF-16, code points */

enum { F8 = 0, F16 = 1, F32 = 2 };

static steno_status decode_form(int form, const uint8_t *b, size_t n, void *dst, size_t cap, size_t *out) {
    if (form == F8) return steno_decode_utf8(b, n, (uint8_t *)dst, cap, out);
    if (form == F16) return steno_decode_utf16(b, n, (uint16_t *)dst, cap, out);
    return steno_decode_code_points(b, n, (uint32_t *)dst, cap, out);
}

static steno_status decoded_len_form(int form, const uint8_t *b, size_t n, size_t *out) {
    if (form == F8) return steno_decoded_len_utf8(b, n, out);
    if (form == F16) return steno_decoded_len_utf16(b, n, out);
    return steno_decoded_len_code_points(b, n, out);
}

static steno_status encode_form(int form, const void *src, size_t n, uint8_t *dst, size_t cap, size_t *out) {
    if (form == F8) return steno_encode_utf8((const uint8_t *)src, n, dst, cap, out);
    if (form == F16) return steno_encode_utf16((const uint16_t *)src, n, dst, cap, out);
    return steno_encode_code_points((const uint32_t *)src, n, dst, cap, out);
}

static steno_status encoded_len_form(int form, const void *src, size_t n, size_t *out) {
    if (form == F8) return steno_encoded_len_utf8((const uint8_t *)src, n, out);
    if (form == F16) return steno_encoded_len_utf16((const uint16_t *)src, n, out);
    return steno_encoded_len_code_points((const uint32_t *)src, n, out);
}

static size_t max_decoded(int form, size_t n) {
    if (form == F8) return steno_max_decoded_len_utf8(n);
    if (form == F16) return steno_max_decoded_len_utf16(n);
    return steno_max_decoded_len_code_points(n);
}

static size_t max_encoded(int form, size_t n) {
    if (form == F8) return steno_max_encoded_len_utf8(n);
    if (form == F16) return steno_max_encoded_len_utf16(n);
    return steno_max_encoded_len_code_points(n);
}

/* The text of code points in a form: returns the number of elements written to out (4 * n bytes). */
static size_t text_form(int form, const uint32_t *cps, size_t n, void *out) {
    if (form == F8) return to_utf8(cps, n, (uint8_t *)out);
    if (form == F16) return to_utf16(cps, n, (uint16_t *)out);
    memcpy(out, cps, n * 4);
    return n;
}

/* True if the progress of two encoders or decoders is the same (their caches may differ). */
static int same_progress(const steno_private_progress *a, const steno_private_progress *b) {
    return a->st.main == b->st.main && a->st.second == b->st.second && a->st.pending == b->st.pending && a->st.ctx == b->st.ctx &&
           a->st.slot == b->st.slot && a->st.conf == b->st.conf && a->st.last_high == b->st.last_high && a->fed == b->fed &&
           a->held_n == b->held_n && a->held_kind == b->held_kind && memcmp(a->held, b->held, a->held_n) == 0 &&
           (a->held_kind != 2 || a->held_unit == b->held_unit);
}

/* ---------- Streaming helpers ---------- */

/* Feeds `n` elements of one form to an encoder, sometimes first with a destination that is too small (which must change nothing). */
static steno_status enc_piece(steno_encoder *e, int form, const void *src, size_t n, uint8_t *dst, size_t cap, size_t *got, rng_t *r) {
    steno_status s;

    if (rng_below(r, 4) == 0) {
        steno_encoder before = *e;
        size_t small = rng_below(r, 4), g = 99;

        if (before.magic_ == 0) steno_encoder_init(&before); /* an object of zero bytes is initialized by the first call */

        if (small > cap) small = cap;
        if (form == F8) s = steno_encoder_feed_utf8(e, (const uint8_t *)src, n, dst, small, &g);
        else if (form == F16) s = steno_encoder_feed_utf16(e, (const uint16_t *)src, n, dst, small, &g);
        else s = steno_encoder_feed_code_points(e, (const uint32_t *)src, n, dst, small, &g);

        if (s == STENO_OK) {
            *got = g;
            return s;
        }

        CHECKF(g == 0 || s != STENO_DESTINATION_TOO_SMALL, "a failed feed wrote out_len %u", (unsigned)g);
        CHECKF(same_progress(&before.progress_, &e->progress_) && before.magic_ == e->magic_, "a failed feed changed the encoder");
    }

    if (form == F8) return steno_encoder_feed_utf8(e, (const uint8_t *)src, n, dst, cap, got);
    if (form == F16) return steno_encoder_feed_utf16(e, (const uint16_t *)src, n, dst, cap, got);
    return steno_encoder_feed_code_points(e, (const uint32_t *)src, n, dst, cap, got);
}

/* Encodes text of one form (n elements) in random pieces (max_piece 1: one element per call). Returns the status and sets *out_n (the
   length, or the position on an error). */
static steno_status stream_encode(int form, const void *src, size_t n, uint8_t *out, size_t cap, size_t *out_n, rng_t *r,
                                  size_t max_piece) {
    steno_encoder e;
    size_t i = 0, pos = 0, got;
    steno_status s;

    if (rng_below(r, 3) == 0) memset(&e, 0, sizeof e); /* an encoder of zero bytes is ready too */
    else steno_encoder_init(&e);

    while (i < n) {
        size_t piece = rng_below(r, (uint32_t)max_piece + 1);
        if (piece > n - i) piece = n - i;

        s = enc_piece(&e, form, (const uint8_t *)src + i * ELEM[form], piece, out + pos, cap - pos, &got, r);

        if (s != STENO_OK) {
            *out_n = got;
            return s;
        }

        pos += got;
        i += piece;
    }

    s = steno_encoder_finish(&e, out + pos, cap - pos, &got);
    *out_n = s == STENO_OK ? pos + got : got;
    return s;
}

/* Decodes Steno bytes in random pieces into a form. */
static steno_status stream_decode(int form, const uint8_t *b, size_t n, void *out, size_t cap, size_t *out_n, rng_t *r,
                                  size_t max_piece) {
    steno_decoder d;
    size_t i = 0, pos = 0, got;
    steno_status s;

    if (rng_below(r, 3) == 0) memset(&d, 0, sizeof d);
    else steno_decoder_init(&d);

    while (i < n) {
        size_t piece = rng_below(r, (uint32_t)max_piece + 1);
        void *dst = (uint8_t *)out + pos * ELEM[form];
        if (piece > n - i) piece = n - i;

        if (rng_below(r, 4) == 0) {
            steno_decoder before = d;
            size_t small = rng_below(r, 3), g = 99;

            if (before.magic_ == 0) steno_decoder_init(&before);

            if (small > cap - pos) small = cap - pos;
            if (form == F8) s = steno_decoder_feed_utf8(&d, b + i, piece, (uint8_t *)dst, small, &g);
            else if (form == F16) s = steno_decoder_feed_utf16(&d, b + i, piece, (uint16_t *)dst, small, &g);
            else s = steno_decoder_feed_code_points(&d, b + i, piece, (uint32_t *)dst, small, &g);

            if (s == STENO_OK) {
                pos += g;
                i += piece;
                continue;
            }

            if (s == STENO_DESTINATION_TOO_SMALL) CHECK(g == 0);
            CHECKF(same_progress(&before.progress_, &d.progress_), "a failed feed changed the decoder");
        }

        if (form == F8) s = steno_decoder_feed_utf8(&d, b + i, piece, (uint8_t *)dst, cap - pos, &got);
        else if (form == F16) s = steno_decoder_feed_utf16(&d, b + i, piece, (uint16_t *)dst, cap - pos, &got);
        else s = steno_decoder_feed_code_points(&d, b + i, piece, (uint32_t *)dst, cap - pos, &got);

        if (s != STENO_OK) {
            *out_n = got;
            return s;
        }

        pos += got;
        i += piece;
    }

    s = steno_decoder_finish(&d, &got);
    *out_n = s == STENO_OK ? pos : got;
    return s;
}

/* ---------- Checks of one value ---------- */

/* What decoding to UTF-8 gives for bytes that the reference decodes to (rs, cps, ref_n, ref_pos): the reference's result, or
   STENO_SURROGATE at the first surrogate code point before it. */
static void expected_utf8(const uint8_t *b, int rs, const uint32_t *cps, size_t ref_n, size_t ref_pos, int *status, size_t *pos) {
    uint32_t *prefix = NULL;
    const uint32_t *c = cps;
    size_t count = ref_n, j;

    if (rs != 0) {
        size_t pn, pp;
        prefix = (uint32_t *)xmalloc((ref_pos + 1) * 4);
        CHECK(ref_decode(b, ref_pos, prefix, &pn, &pp) == 0);
        c = prefix;
        count = pn;
    }

    *status = rs;
    *pos = rs != 0 ? ref_pos : 0;

    for (j = 0; j < count; j++) {
        if (is_surrogate(c[j])) {
            uint8_t *tmp = (uint8_t *)xmalloc(4 * (j + 1));
            size_t *starts = (size_t *)xmalloc(sizeof(size_t) * (j + 1)), tl, ti;
            CHECK(ref_encode(c, j + 1, tmp, &tl, &ti, starts) == 0);
            *status = STENO_SURROGATE;
            *pos = starts[j];
            free(tmp);
            free(starts);
            break;
        }
    }

    free(prefix);
}

/* Decodes bytes in every form, one-shot and streaming, and compares with the reference. Returns the reference's status. */
static int check_decode(const uint8_t *b, size_t n, rng_t *r, int streaming) {
    uint32_t *ref_out = (uint32_t *)xmalloc((n + 1) * 4);
    size_t ref_n = 0, ref_pos = 0;
    int rs = ref_decode(b, n, ref_out, &ref_n, &ref_pos), form;

    for (form = 0; form < 3; form++) {
        int es = rs;
        size_t ep = ref_pos, cap = max_decoded(form, n), got = 777, len = 777, exp_len = 0;
        void *dst = g_alloc(cap * ELEM[form] + 1);
        void *expect = xmalloc(4 * ref_n + 4);
        steno_status s;

        if (form == F8) expected_utf8(b, rs, ref_out, ref_n, ref_pos, &es, &ep);
        if (es == 0) exp_len = text_form(form, ref_out, ref_n, expect);

        s = decode_form(form, b, n, dst, cap, &got);
        CHECKF((int)s == es && got == (es == 0 ? exp_len : ep), "decode form %d: %s at %u, expected %s at %u", form,
               steno_status_name((int)s), (unsigned)got, steno_status_name(es), (unsigned)(es == 0 ? exp_len : ep));
        if (es == 0 && s == STENO_OK) CHECKF(memcmp(dst, expect, exp_len * ELEM[form]) == 0, "decode form %d: other text", form);
        CHECK(g_intact(dst, es == 0 ? exp_len * ELEM[form] : cap * ELEM[form], cap * ELEM[form] + 1));

        s = decoded_len_form(form, b, n, &len);
        CHECKF((int)s == es && len == (es == 0 ? exp_len : ep), "decoded length form %d", form);

        if (streaming) {
            void *sd = xmalloc(cap * ELEM[form] + 4);
            size_t sn = 0;
            s = stream_decode(form, b, n, sd, cap, &sn, r, rng_below(r, 2) == 0 ? 1 : 1 + rng_below(r, 40));
            CHECKF((int)s == es && sn == (es == 0 ? exp_len : ep), "streaming decode form %d: %s at %u, expected %s at %u", form,
                   steno_status_name((int)s), (unsigned)sn, steno_status_name(es), (unsigned)(es == 0 ? exp_len : ep));
            if (es == 0 && s == STENO_OK) CHECK(memcmp(sd, expect, exp_len * ELEM[form]) == 0);
            free(sd);
        }

        g_free(dst);
        free(expect);
    }

    if (rs == 0) {
        /* Accepted bytes are the encoding of their value (13.3). */
        uint8_t *again = (uint8_t *)xmalloc(4 * ref_n + 1);
        size_t an = 0;
        CHECK(steno_encode_code_points(ref_out, ref_n, again, 4 * ref_n + 1, &an) == STENO_OK && an == n && memcmp(again, b, n) == 0);
        free(again);
    }

    free(ref_out);
    return rs;
}

/* Encodes a value in every form, one-shot and streaming, and checks the bytes, the lengths, the bounds, the round trip and the
   properties of 13. expect may be NULL (then the reference gives it). */
static void check_value(const uint32_t *cps, size_t n, const uint8_t *expect, size_t elen, rng_t *r, int full) {
    uint8_t *ref_bytes = (uint8_t *)xmalloc(4 * n + 1);
    size_t *starts = (size_t *)xmalloc(sizeof(size_t) * (n + 1)), ref_len = 0, ref_err = 0, i;
    void *text = xmalloc(4 * n + 4);
    int form, surr = has_surrogate(cps, n);

    CHECK(ref_encode(cps, n, ref_bytes, &ref_len, &ref_err, starts) == 0);

    if (expect != NULL) {
        CHECKF(ref_len == elen && memcmp(ref_bytes, expect, elen) == 0, "the reference codec disagrees with a vector");
    } else {
        expect = ref_bytes;
        elen = ref_len;
    }

    for (form = 0; form < 3; form++) {
        size_t tn, cap, got = 777, len = 777;
        uint8_t *dst;
        steno_status s;

        if (form == F8 && surr) continue;

        tn = text_form(form, cps, n, text);
        cap = max_encoded(form, tn);
        CHECKF(elen <= cap, "bound of form %d: %u bytes for %u elements", form, (unsigned)elen, (unsigned)tn);

        /* One-shot into a destination of the maximum length, and of the exact length. */
        dst = (uint8_t *)g_alloc(cap + 1);
        s = encode_form(form, text, tn, dst, cap, &got);
        CHECKF(s == STENO_OK && got == elen && memcmp(dst, expect, elen) == 0, "encode form %d: %s, %u bytes, expected %u", form,
               steno_status_name((int)s), (unsigned)got, (unsigned)elen);
        CHECK(g_intact(dst, elen, cap + 1));
        g_free(dst);

        dst = (uint8_t *)g_alloc(elen);
        s = encode_form(form, text, tn, elen == 0 ? NULL : dst, elen, &got);
        CHECK(s == STENO_OK && got == elen && memcmp(dst, expect, elen) == 0 && g_intact(dst, elen, elen));
        g_free(dst);

        CHECK(encoded_len_form(form, text, tn, &len) == STENO_OK && len == elen);

        /* Too small: fails cleanly, writes nothing past the destination. */
        if (elen > 0) {
            size_t small = full ? elen - 1 : rng_below(r, (uint32_t)elen);
            dst = (uint8_t *)g_alloc(small);
            got = 777;
            s = encode_form(form, text, tn, small == 0 ? NULL : dst, small, &got);
            CHECKF(s == STENO_DESTINATION_TOO_SMALL && got == 0, "encode form %d into %u of %u bytes: %s", form, (unsigned)small,
                   (unsigned)elen, steno_status_name((int)s));
            CHECK(g_intact(dst, small, small));
            g_free(dst);
        }

        /* Streaming, in random pieces and one element at a time. */
        {
            uint8_t *sd = (uint8_t *)xmalloc(cap + 8);
            size_t sn = 0;
            s = stream_encode(form, text, tn, sd, cap + 8, &sn, r, rng_below(r, 3) == 0 ? 1 : 1 + rng_below(r, 50));
            CHECKF(s == STENO_OK && sn == elen && memcmp(sd, expect, elen) == 0, "streaming encode form %d: %s", form,
                   steno_status_name((int)s));
            free(sd);
        }

        /* Decoding back into this form: exact, too small, and the length. */
        {
            size_t dcap = max_decoded(form, elen), dl = 777;
            void *dd = g_alloc(tn * ELEM[form]);

            CHECK(tn <= dcap);
            s = decode_form(form, expect, elen, tn == 0 ? NULL : dd, tn, &dl);
            CHECKF(s == STENO_OK && dl == tn && memcmp(dd, text, tn * ELEM[form]) == 0, "decode form %d: %s", form,
                   steno_status_name((int)s));
            CHECK(g_intact(dd, tn * ELEM[form], tn * ELEM[form]));
            g_free(dd);

            if (tn > 0) {
                size_t small = full ? tn - 1 : rng_below(r, (uint32_t)tn);
                dd = g_alloc(small * ELEM[form]);
                dl = 777;
                s = decode_form(form, expect, elen, small == 0 ? NULL : dd, small, &dl);
                CHECKF(s == STENO_DESTINATION_TOO_SMALL && dl == 0, "decode form %d into %u of %u: %s", form, (unsigned)small,
                       (unsigned)tn, steno_status_name((int)s));
                CHECK(g_intact(dd, small * ELEM[form], small * ELEM[form]));
                g_free(dd);
            }

            CHECK(decoded_len_form(form, expect, elen, &dl) == STENO_OK && dl == tn);
        }
    }

    /* A value with a surrogate code point has no UTF-8 form. */
    if (surr) {
        size_t got = 777, first = 0;
        uint8_t *dd = (uint8_t *)xmalloc(4 * elen + 1);

        while (!is_surrogate(cps[first])) first++;
        CHECK(steno_decode_utf8(expect, elen, dd, 4 * elen + 1, &got) == STENO_SURROGATE && got == starts[first]);
        CHECK(steno_decoded_len_utf8(expect, elen, &got) == STENO_SURROGATE && got == starts[first]);
        free(dd);
    }

    /* Streaming decode in every form, random pieces. */
    if (full || rng_below(r, 4) == 0) (void)check_decode(expect, elen, r, 1);

    /* Mixed forms in one encoder: pieces at code point boundaries, each in a form it can have. */
    {
        steno_encoder e;
        uint8_t *sd = (uint8_t *)xmalloc(4 * n + 8);
        size_t pos = 0, k = 0, got;
        steno_status s = STENO_OK;

        steno_encoder_init(&e);

        while (k < n && s == STENO_OK) {
            size_t piece = 1 + rng_below(r, 8), tn;
            int pf = (int)rng_below(r, 3);

            if (piece > n - k) piece = n - k;
            if (pf == F8 && has_surrogate(cps + k, piece)) pf = F16;
            tn = text_form(pf, cps + k, piece, text);
            s = enc_piece(&e, pf, text, tn, sd + pos, 4 * n + 8 - pos, &got, r);
            pos += got;
            k += piece;
        }

        if (s == STENO_OK) s = steno_encoder_finish(&e, sd + pos, 4 * n + 8 - pos, &got);
        CHECKF(s == STENO_OK && pos + got == elen && memcmp(sd, expect, elen) == 0, "mixed-form streaming: %s", steno_status_name((int)s));
        free(sd);
    }

    /* Properties: no byte 00; bytes 01..07 only as the first byte of a code point (13.4, 13.5); ASCII text (14.1). */
    {
        size_t j = 0;
        int ascii = 1;

        for (i = 0; i < elen; i++) {
            int start = j < n && starts[j] == i;
            if (start) j++;
            CHECKF(expect[i] != 0, "a byte 00 at %u", (unsigned)i);
            CHECKF(expect[i] >= 8 || start, "byte %02X inside a code point at %u", expect[i], (unsigned)i);
        }

        for (i = 0; i < n; i++) {
            if (!(cps[i] == 9 || cps[i] == 10 || cps[i] == 13 || (cps[i] >= 0x20 && cps[i] <= 0x7E))) ascii = 0;
        }

        CHECK(steno_is_ascii_text(expect, elen) == (ascii != 0));
    }

    free(ref_bytes);
    free(starts);
    free(text);
}

/* ---------- The vector files ---------- */

static int hexval(int ch) {
    if (ch >= '0' && ch <= '9') return ch - '0';
    if (ch >= 'A' && ch <= 'F') return ch - 'A' + 10;
    if (ch >= 'a' && ch <= 'f') return ch - 'a' + 10;
    return -1;
}

static size_t parse_bytes(const char *s, uint8_t *out) {
    size_t n = 0;

    if (strcmp(s, "-") == 0) return 0;

    while (s[0] && s[1]) {
        out[n++] = (uint8_t)(hexval(s[0]) * 16 + hexval(s[1]));
        s += 2;
    }

    return n;
}

static size_t parse_cps(char *s, uint32_t *out) {
    size_t n = 0;

    if (strcmp(s, "-") == 0) return 0;

    while (*s) {
        out[n++] = (uint32_t)strtoul(s, &s, 16);
        while (*s == ' ') s++;
    }

    return n;
}

static int reason_status(const char *s) {
    if (strcmp(s, "zero-byte") == 0) return STENO_ZERO_BYTE;
    if (strcmp(s, "unused-lead") == 0) return STENO_UNUSED_LEAD;
    if (strcmp(s, "truncated") == 0) return STENO_TRUNCATED;
    if (strcmp(s, "past-range") == 0) return STENO_PAST_RANGE;
    if (strcmp(s, "surrogate-pair") == 0) return STENO_SURROGATE_PAIR;
    if (strcmp(s, "invalid-trail") == 0) return STENO_INVALID_TRAIL;
    return -1;
}

/* Checks every vector of a file. With large set, the checks per vector are lighter (no reference codec, fewer streaming runs). */
static int test_vectors(const char *path, int large, rng_t *r) {
    FILE *f = fopen(path, "rb");
    static char line[1 << 20];
    uint8_t *bytes = (uint8_t *)xmalloc(1 << 19);
    uint32_t *cps = (uint32_t *)xmalloc((1 << 19) * 4);
    long valid = 0, invalid = 0, not_value = 0;
    long by_reason[16] = {0};

    if (f == NULL) {
        CHECKF(0, "cannot open %s", path);
        return 0;
    }

    while (fgets(line, sizeof line, f) != NULL) {
        char *fields[4] = {NULL, NULL, NULL, NULL}, *p = line;
        int nf = 0;
        size_t len = strlen(line);

        while (len > 0 && (line[len - 1] == '\n' || line[len - 1] == '\r')) line[--len] = 0;
        if (len == 0 || line[0] == '#') continue;

        while (nf < 4) {
            fields[nf++] = p;
            p = strchr(p, '\t');
            if (p == NULL) break;
            *p++ = 0;
        }

        if (strcmp(fields[0], "valid") == 0 && nf == 4) {
            size_t n = parse_cps(fields[2], cps), bn = parse_bytes(fields[3], bytes);

            if (large) {
                uint8_t *out = (uint8_t *)xmalloc(4 * n + 1);
                uint32_t *back = (uint32_t *)xmalloc(4 * bn + 4);
                uint16_t *u16 = (uint16_t *)xmalloc(4 * n + 2), *b16 = (uint16_t *)xmalloc(4 * bn + 2);
                size_t got = 0, un = to_utf16(cps, n, u16);

                CHECKF(steno_encode_code_points(cps, n, out, 4 * n + 1, &got) == STENO_OK && got == bn && memcmp(out, bytes, bn) == 0,
                       "vector %s", fields[1]);
                CHECKF(steno_encode_utf16(u16, un, out, 4 * n + 1, &got) == STENO_OK && got == bn && memcmp(out, bytes, bn) == 0,
                       "vector %s (UTF-16)", fields[1]);
                CHECKF(steno_decode_code_points(bytes, bn, back, bn + 1, &got) == STENO_OK && got == n && memcmp(back, cps, 4 * n) == 0,
                       "vector %s (decode)", fields[1]);
                CHECKF(steno_decode_utf16(bytes, bn, b16, 2 * bn + 1, &got) == STENO_OK && got == un && memcmp(b16, u16, 2 * un) == 0,
                       "vector %s (decode UTF-16)", fields[1]);

                if (!has_surrogate(cps, n)) {
                    uint8_t *u8 = (uint8_t *)xmalloc(4 * n + 1), *b8 = (uint8_t *)xmalloc(4 * bn + 1);
                    size_t n8 = to_utf8(cps, n, u8);
                    CHECKF(steno_encode_utf8(u8, n8, out, 4 * n + 1, &got) == STENO_OK && got == bn && memcmp(out, bytes, bn) == 0,
                           "vector %s (UTF-8)", fields[1]);
                    CHECKF(steno_decode_utf8(bytes, bn, b8, 4 * bn + 1, &got) == STENO_OK && got == n8 && memcmp(b8, u8, n8) == 0,
                           "vector %s (decode UTF-8)", fields[1]);
                    free(u8);
                    free(b8);
                }

                if (valid % 16 == 0) {
                    size_t sn;
                    CHECK(stream_encode(F16, u16, un, out, 4 * n + 1, &sn, r, 7) == STENO_OK && sn == bn && memcmp(out, bytes, bn) == 0);
                }

                free(out);
                free(back);
                free(u16);
                free(b16);
            } else {
                check_value(cps, n, bytes, bn, r, 1);
            }

            valid++;
        } else if (strcmp(fields[0], "invalid") == 0 && nf == 4) {
            size_t bn = parse_bytes(fields[2], bytes);
            int want = reason_status(fields[3]);

            if (large) {
                uint32_t *back = (uint32_t *)xmalloc(4 * bn + 4);
                size_t got = 0;
                steno_status s = steno_decode_code_points(bytes, bn, back, bn + 1, &got);
                CHECKF((int)s == want, "vector %s: %s, expected %s", fields[1], steno_status_name((int)s), fields[3]);
                s = steno_decode_utf16(bytes, bn, (uint16_t *)back, 2 * bn + 2, &got);
                CHECKF((int)s == want, "vector %s (UTF-16): %s", fields[1], steno_status_name((int)s));
                free(back);
            } else {
                int rs = check_decode(bytes, bn, r, 1);
                CHECKF(rs == want, "vector %s: the reference says %s, the file %s", fields[1], steno_status_name(rs), fields[3]);
            }

            if (want >= 0 && want < 16) by_reason[want]++;
            invalid++;
        } else if (strcmp(fields[0], "not-a-value") == 0 && nf >= 3) {
            size_t n = parse_cps(fields[2], cps), got = 777, idx = 0, j, rl, ri;
            uint8_t *out = (uint8_t *)xmalloc(4 * n + 1);
            steno_encoder e;

            for (j = 1; j < n; j++) {
                if (is_high(cps[j - 1]) && is_low(cps[j])) {
                    idx = j;
                    break;
                }
            }

            CHECKF(steno_encode_code_points(cps, n, out, 4 * n + 1, &got) == STENO_NOT_A_VALUE && got == idx, "vector %s", fields[1]);
            CHECK(steno_encoded_len_code_points(cps, n, &got) == STENO_NOT_A_VALUE && got == idx);
            if (!large) CHECK(ref_encode(cps, n, out, &rl, &ri, NULL) == STENO_NOT_A_VALUE && ri == idx);

            /* Streaming, one code point at a time: the low surrogate fails, at its position. */
            steno_encoder_init(&e);
            for (j = 0; j < n; j++) {
                steno_status s = steno_encoder_feed_code_points(&e, cps + j, 1, out, 4 * n + 1, &got);
                if (j < idx) CHECK(s == STENO_OK);
                if (j == idx) {
                    CHECK(s == STENO_NOT_A_VALUE && got == idx);
                    break;
                }
            }

            free(out);
            not_value++;
        } else {
            CHECKF(0, "a line of an unknown kind: %.60s", line);
        }
    }

    fclose(f);
    free(bytes);
    free(cps);
    printf("%s: %ld valid, %ld invalid (zero-byte %ld, unused-lead %ld, truncated %ld, past-range %ld, surrogate-pair %ld, "
           "invalid-trail %ld), %ld not-a-value: %ld vectors\n",
           path, valid, invalid, by_reason[1], by_reason[2], by_reason[3], by_reason[4], by_reason[5], by_reason[10], not_value,
           valid + invalid + not_value);

    if (!large) CHECK(valid == 6798 && invalid == 829 && not_value == 13);
    return (int)(valid + invalid + not_value);
}

/* ---------- Random text against the reference, and the properties ---------- */

static void test_random(rng_t *r, int count, size_t max_len) {
    uint32_t *a = (uint32_t *)xmalloc(4 * (max_len + 1));
    int k;

    for (k = 0; k < count; k++) {
        size_t n = random_text(r, a, max_len, rng_below(r, 3) != 0);
        check_value(a, n, NULL, 0, r, k % 10 == 0);
    }

    free(a);
}

/* 13.1 and 13.2: byte order is code point order, and the encoding of a prefix is a prefix. */
static void test_order_and_prefixes(rng_t *r, int count) {
    uint32_t a[80], b[80];
    uint8_t ea[400], eb[400];
    int k;

    for (k = 0; k < count; k++) {
        size_t na = random_text(r, a, 40, 1), nb, la, lb, i;

        /* Often a variant of the first text: the same up to a point. */
        if (rng_below(r, 2) == 0) {
            nb = random_text(r, b, 40, 1);
        } else {
            size_t keep = na == 0 ? 0 : rng_below(r, (uint32_t)na + 1);
            memcpy(b, a, keep * 4);
            nb = keep + random_text(r, b + keep, 40 - keep > 0 ? 40 - keep : 1, 1);
            if (keep > 0 && nb > keep && is_high(b[keep - 1]) && is_low(b[keep])) b[keep] = 0x42;
        }

        CHECK(steno_encode_code_points(a, na, ea, sizeof ea, &la) == STENO_OK);
        CHECK(steno_encode_code_points(b, nb, eb, sizeof eb, &lb) == STENO_OK);
        CHECKF(cmp_bytes(ea, la, eb, lb) == cmp_cps(a, na, b, nb), "order of two values");

        for (i = 0; i <= na; i += 1 + rng_below(r, 5)) {
            uint8_t ep[400];
            size_t lp;
            CHECK(steno_encode_code_points(a, i, ep, sizeof ep, &lp) == STENO_OK);
            CHECKF(lp <= la && memcmp(ep, ea, lp) == 0, "the encoding of a prefix of %u code points", (unsigned)i);
        }
    }
}

/* ---------- Bad Steno bytes ---------- */

static void test_garbage(rng_t *r) {
    uint32_t a[16];
    uint8_t e[80], m[80];
    int k;
    unsigned pos, v;

    /* Every byte value at every position of short encodings, and every truncation. */
    for (k = 0; k < 120; k++) {
        size_t n = random_text(r, a, 6, 1), len;
        CHECK(steno_encode_code_points(a, n, e, sizeof e, &len) == STENO_OK);

        for (pos = 0; pos < len; pos++) {
            for (v = 0; v < 256; v++) {
                memcpy(m, e, len);
                m[pos] = (uint8_t)v;
                (void)check_decode(m, len, r, v % 61 == 0);
            }
        }

        for (pos = 0; pos < len; pos++) (void)check_decode(e, pos, r, 1);

        /* One more byte at the end, every value. */
        for (v = 0; v < 256; v++) {
            memcpy(m, e, len);
            m[len] = (uint8_t)v;
            (void)check_decode(m, len + 1, r, v % 37 == 0);
        }
    }

    /* Random mutations of longer encodings, and random bytes. */
    for (k = 0; k < 3000; k++) {
        uint32_t big[40];
        size_t n = random_text(r, big, 16, 1), len, j, flips;
        CHECK(steno_encode_code_points(big, n, m, sizeof m, &len) == STENO_OK);
        flips = 1 + rng_below(r, 3);

        for (j = 0; j < flips && len > 0; j++) {
            uint32_t how = rng_below(r, 4), at = rng_below(r, (uint32_t)len);
            if (how == 0) m[at] = (uint8_t)rng_below(r, 256);
            else if (how == 1) m[at] = (uint8_t)rng_below(r, 9);
            else if (how == 2) m[at] ^= (uint8_t)(1u << rng_below(r, 8));
            else len = at;
        }

        (void)check_decode(m, len, r, k % 7 == 0);
    }

    for (k = 0; k < 3000; k++) {
        size_t len = rng_below(r, 24), j;
        for (j = 0; j < len; j++) m[j] = (uint8_t)(rng_below(r, 3) == 0 ? rng_below(r, 256) : rng_between(r, 0xE0, 0xFF));
        (void)check_decode(m, len, r, k % 7 == 0);
    }
}

/* ---------- UTF-8 input ---------- */

static const struct {
    const char *bytes;
    size_t len;
} BAD_UTF8[] = {
    {"\x80", 1},         {"\xBF", 1},         {"\xC0\x80", 2},     {"\xC1\xBF", 2},     {"\xC2", 1},         {"\xC2\x41", 2},
    {"\xE0\x80\x80", 3}, {"\xE0\x9F\xBF", 3}, {"\xE0\xA0", 2},     {"\xED\xA0\x80", 3}, {"\xED\xBF\xBF", 3}, {"\xEF\xBF", 2},
    {"\xF0\x80\x80\x80", 4}, {"\xF0\x8F\xBF\xBF", 4}, {"\xF4\x90\x80\x80", 4}, {"\xF5\x80\x80\x80", 4}, {"\xFF", 1},
    {"\xFE", 1},         {"\xF0\x9F\x98", 3}, {"\xE2\x82\x41", 3}, {"\xF0\x9F\x41\x80", 4},
};

#define N_BAD_UTF8 (sizeof BAD_UTF8 / sizeof BAD_UTF8[0])

/* Invalid UTF-8 at every position of text around the block sizes of the fast paths (8 and 16 bytes), in every kind of run: one-shot,
   length, streaming. The status is STENO_INVALID_UTF8 and out_len the length of the valid prefix. */
static void test_invalid_utf8(rng_t *r) {
    static const uint32_t fill[4] = {0x61, 0x0436, 0x4E2D, 0x1F600};
    uint8_t text[200], out[600];
    uint32_t cps[80];
    unsigned fk, len, at, b;

    for (fk = 0; fk < 4; fk++) {
        for (len = 0; len <= 40; len++) {
            for (at = 0; at <= len; at++) {
                for (b = 0; b < N_BAD_UTF8; b++) {
                    size_t pre, k, n, got = 777, cut;
                    unsigned i;
                    steno_status s;

                    /* at code points of the fill (ASCII for the first and every 3rd), then the bad bytes, then more text. */
                    for (i = 0; i < at; i++) cps[i] = fk == 0 || i % 3 == 0 ? 0x61 + (i % 26) : fill[fk];
                    pre = to_utf8(cps, at, text);
                    memcpy(text + pre, BAD_UTF8[b].bytes, BAD_UTF8[b].len);
                    k = pre + BAD_UTF8[b].len;
                    for (i = 0; i < len - at; i++) cps[i] = 0x41 + (i % 20);
                    n = k + to_utf8(cps, len - at, text + k);

                    /* A sequence that is only unfinished is valid as far as it goes when it is at the very end. */
                    cut = n;
                    s = steno_encode_utf8(text, cut, out, sizeof out, &got);
                    CHECKF(s == STENO_INVALID_UTF8 && got == pre, "bad UTF-8 %u at %u of %u (fill %u): %s at %u", b, at, len, fk,
                           steno_status_name((int)s), (unsigned)got);
                    CHECK(steno_encoded_len_utf8(text, cut, &got) == STENO_INVALID_UTF8 && got == pre);

                    if ((at + b) % 3 == 0) {
                        size_t sn = 777;
                        s = stream_encode(F8, text, cut, out, sizeof out, &sn, r, 1 + rng_below(r, 9));
                        CHECKF(s == STENO_INVALID_UTF8 && sn == pre, "streaming: bad UTF-8 %u at %u of %u: %s at %u", b, at, len,
                               steno_status_name((int)s), (unsigned)sn);
                    }
                }
            }
        }
    }

    /* Every byte value after a valid prefix of each length. */
    for (len = 0; len < 40; len++) {
        for (b = 0x80; b < 0x100; b++) {
            size_t got;
            unsigned i;
            steno_status s;
            for (i = 0; i < len; i++) text[i] = (uint8_t)(0x30 + i % 40);
            text[len] = (uint8_t)b;
            text[len + 1] = 0x41;
            s = steno_encode_utf8(text, len + 2, out, sizeof out, &got);
            CHECK(s == STENO_INVALID_UTF8 && got == len);
        }
    }
}

/* ---------- State changes around the block sizes ---------- */

/* Runs of 1-byte codes of every length up to 40 at several alignments, broken by a code point that changes the state (or takes the
   general path), in every form; against the reference. */
static void test_block_boundaries(rng_t *r) {
    static const uint32_t runs[][2] = {{0x20, 0x7E}, {0x0430, 0x044F}, {0x3041, 0x3096}, {0x30A1, 0x30FA}, {0x1F600, 0x1F64F},
                                       {0xAC00, 0xD7A3}, {0x4E00, 0x9FFF}, {0x3000, 0x300F}, {0xFF01, 0xFF1F}};
    static const uint32_t breakers[] = {0x0A, 0x09, 0x0D, 0x00, 0x1F, 0x7F, 0x41, 0x61, 0xE9, 0x0416, 0x03B1, 0x2014, 0x3042, 0x30AB,
                                        0x4E2D, 0xD55C, 0x1F600, 0xD800, 0xDC00, 0xFF21, 0x10FFFF, 0x3001, 0xFF0C, 0x0E01};
    uint32_t cps[200];
    unsigned ri, len, at, bi, pre;

    for (ri = 0; ri < sizeof runs / sizeof runs[0]; ri++) {
        for (pre = 0; pre < 4; pre++) {
            for (len = 0; len <= 40; len++) {
                for (at = 0; at <= len; at += (len > 20 && at > 3 && at + 3 < len) ? 2 : 1) {
                    for (bi = 0; bi < sizeof breakers / sizeof breakers[0]; bi++) {
                        size_t n = 0;
                        unsigned i;

                        /* A prefix that sets up the state of the run (a CJK or Hangul context, a main window), and a few ASCII. */
                        if (pre == 1) cps[n++] = 0x4E2D;
                        if (pre == 2) cps[n++] = runs[ri][0];
                        if (pre == 3) {
                            cps[n++] = 0x30AB;
                            cps[n++] = 0x20;
                        }

                        for (i = 0; i < len; i++) {
                            cps[n++] = i == at ? breakers[bi] : rng_between(r, runs[ri][0], runs[ri][1]);
                            if (n >= 2 && is_high(cps[n - 2]) && is_low(cps[n - 1])) cps[n - 1] = 0x20;
                        }

                        if (at == len) cps[n++] = breakers[bi];
                        check_value(cps, n, NULL, 0, r, 0);
                    }
                }
            }
        }
    }

    /* Long runs, up to a few hundred, of each fast path, then a break. */
    for (ri = 0; ri < sizeof runs / sizeof runs[0]; ri++) {
        for (len = 100; len <= 330; len += 23) {
            size_t n = 0;
            unsigned i;
            for (i = 0; i < len && n < 190; i++) cps[n++] = rng_between(r, runs[ri][0], runs[ri][1]);
            cps[n++] = breakers[rng_below(r, sizeof breakers / sizeof breakers[0])];
            if (is_high(cps[n - 2]) && is_low(cps[n - 1])) cps[n - 1] = 0x20;
            check_value(cps, n, NULL, 0, r, 0);
        }
    }
}

/* ---------- Bounds ---------- */

static void test_bounds(void) {
    enum { N = 300 };
    static uint32_t cps[N + 2];
    static uint8_t text[4 * N + 8], out[4 * N + 8];
    static uint16_t u16[2 * N + 4];
    size_t got, n, i;

    CHECK(steno_max_encoded_len_utf8(10) == 20 && steno_max_encoded_len_utf16(10) == 30 && steno_max_encoded_len_code_points(10) == 40);
    CHECK(steno_max_decoded_len_utf8(10) == 40 && steno_max_decoded_len_utf16(10) == 20 && steno_max_decoded_len_code_points(10) == 10);
    CHECK(steno_max_encoded_len_utf8(SIZE_MAX / 2 + 1) == SIZE_MAX && steno_max_encoded_len_utf16(SIZE_MAX / 3 + 1) == SIZE_MAX);
    CHECK(steno_max_encoded_len_utf8(SIZE_MAX / 2) == SIZE_MAX - 1 && steno_max_decoded_len_utf8(SIZE_MAX) == SIZE_MAX);
    CHECK(steno_max_decoded_len_utf16(SIZE_MAX) == SIZE_MAX && steno_max_encoded_len_code_points(SIZE_MAX / 4 + 1) == SIZE_MAX);
    CHECK(steno_max_decoded_len_code_points(SIZE_MAX) == SIZE_MAX && steno_max_encoded_len_utf8(0) == 0);

    /* UTF-8: C0 controls take 2 bytes for 1 byte; so do U+0041..U+007E in the CJK context with a kana slot (after one hiragana), of
       which the ones that are not letters keep the kana slot. */
    for (i = 0; i < N; i++) text[i] = (uint8_t)(i % 9 == 0 ? 0x1F : i % 9);
    CHECK(steno_encode_utf8(text, N, out, steno_max_encoded_len_utf8(N), &got) == STENO_OK && got == 2 * N);
    cps[0] = 0x3042;
    for (i = 1; i < N; i++) cps[i] = (uint32_t)"[\\]^_`{|}~"[i % 10];
    n = to_utf8(cps, N, text);
    CHECK(steno_encode_utf8(text, n, out, steno_max_encoded_len_utf8(n), &got) == STENO_OK && got == 3 + 2 * (N - 1));
    CHECK(got <= steno_max_encoded_len_utf8(n));

    /* UTF-16: code points of 1 unit that take 3 bytes (CJK extension A in the CJK context, U+0F00.. far from the main window). */
    for (i = 0; i < N; i++) cps[i] = 0x3400 + (uint32_t)i * 7;
    n = to_utf16(cps, N, u16);
    CHECK(steno_encode_utf16(u16, n, out, steno_max_encoded_len_utf16(n), &got) == STENO_OK && got == 3 * N);

    /* Code points: U+20000.. take 4 bytes each. */
    for (i = 0; i < N; i++) cps[i] = 0x20000 + (uint32_t)i * 977;
    CHECK(steno_encode_code_points(cps, N, out, steno_max_encoded_len_code_points(N), &got) == STENO_OK && got == 4 * N);

    /* Decoding: a supplementary main window gives 4 UTF-8 bytes or 2 code units for each 1-byte code. */
    for (i = 0; i < N; i++) cps[i] = 0x1F600 + (uint32_t)(i % 64);
    CHECK(steno_encode_code_points(cps, N, out, sizeof out, &got) == STENO_OK);
    n = got;
    CHECK(steno_decoded_len_utf8(out, n, &got) == STENO_OK && got == 4 * N && got <= steno_max_decoded_len_utf8(n));
    CHECK(4 * N - got + 0 == 0 && n == N + 2);
    CHECK(steno_decoded_len_utf16(out, n, &got) == STENO_OK && got == 2 * N && got <= steno_max_decoded_len_utf16(n));
    CHECK(steno_decode_utf8(out, n, text, steno_max_decoded_len_utf8(n), &got) == STENO_OK);
    CHECK(steno_decode_utf16(out, n, u16, steno_max_decoded_len_utf16(n), &got) == STENO_OK);
}

/* ---------- Streaming rules ---------- */

static void test_streaming_rules(void) {
    steno_encoder e;
    steno_decoder d;
    uint8_t out[64];
    uint16_t u[64];
    uint32_t c[64];
    size_t got;

    /* A surrogate pair split across two UTF-16 calls is one code point. */
    {
        static const uint16_t a[] = {0x48, 0xD83D}, b[] = {0xDE00, 0x21};
        static const uint16_t whole[] = {0x48, 0xD83D, 0xDE00, 0x21};
        uint8_t one[16];
        size_t on, p = 0;

        CHECK(steno_encode_utf16(whole, 4, one, sizeof one, &on) == STENO_OK);
        steno_encoder_init(&e);
        CHECK(steno_encoder_feed_utf16(&e, a, 2, out, sizeof out, &got) == STENO_OK && got == 1);
        p += got;
        CHECK(steno_encoder_feed_utf16(&e, b, 2, out + p, sizeof out - p, &got) == STENO_OK);
        p += got;
        CHECK(steno_encoder_finish(&e, out + p, sizeof out - p, &got) == STENO_OK && got == 0);
        CHECK(p == on && memcmp(out, one, on) == 0);
    }

    /* A held high surrogate code unit, then a low surrogate as a code point: not a value. Then as UTF-8: the high stands alone. */
    {
        static const uint16_t hi[] = {0xD83D};
        static const uint32_t lo[] = {0xDE00};
        steno_encoder before;

        steno_encoder_init(&e);
        CHECK(steno_encoder_feed_utf16(&e, hi, 1, out, sizeof out, &got) == STENO_OK && got == 0);
        before = e;
        CHECK(steno_encoder_feed_code_points(&e, lo, 1, out, sizeof out, &got) == STENO_NOT_A_VALUE && got == 1);
        CHECK(same_progress(&before.progress_, &e.progress_));
        CHECK(steno_encoder_feed_utf8(&e, (const uint8_t *)"a", 1, out, sizeof out, &got) == STENO_OK && got == 4);
        CHECK(out[0] == 0xF8 && out[3] == 0x48);
    }

    /* A high surrogate code point, then a low surrogate code unit: not a value. */
    {
        static const uint32_t hi[] = {0xD800};
        static const uint16_t lo[] = {0xDC00, 0x41};

        steno_encoder_init(&e);
        CHECK(steno_encoder_feed_code_points(&e, hi, 1, out, sizeof out, &got) == STENO_OK && got == 3);
        CHECK(steno_encoder_feed_utf16(&e, lo, 2, out, sizeof out, &got) == STENO_NOT_A_VALUE && got == 1);
        CHECK(steno_encoder_feed_utf16(&e, lo + 1, 1, out, sizeof out, &got) == STENO_OK && got == 1);
    }

    /* Finish writes a held high surrogate and does not start a new value; a held UTF-8 start fails finish. */
    {
        static const uint16_t hi[] = {0xD800}, lo[] = {0xDC00};

        steno_encoder_init(&e);
        CHECK(steno_encoder_feed_utf16(&e, hi, 1, out, sizeof out, &got) == STENO_OK && got == 0);
        CHECK(steno_encoder_finish(&e, out, 2, &got) == STENO_DESTINATION_TOO_SMALL && got == 0);
        CHECK(steno_encoder_finish(&e, out, 3, &got) == STENO_OK && got == 3 && out[0] == 0xF8 && out[1] == 0xC5 && out[2] == 0x20);
        CHECK(steno_encoder_finish(&e, out, 3, &got) == STENO_OK && got == 0);
        /* The value goes on: a low surrogate code unit now follows a high surrogate code point. */
        CHECK(steno_encoder_feed_utf16(&e, lo, 1, out, sizeof out, &got) == STENO_NOT_A_VALUE && got == 1);

        steno_encoder_init(&e);
        CHECK(steno_encoder_feed_utf8(&e, (const uint8_t *)"ab\xE4\xB8", 4, out, sizeof out, &got) == STENO_OK && got == 2);
        CHECK(steno_encoder_finish(&e, out, sizeof out, &got) == STENO_INVALID_UTF8 && got == 2);
        CHECK(steno_encoder_feed_utf16(&e, lo, 1, out, sizeof out, &got) == STENO_INVALID_UTF8 && got == 2);
        CHECK(steno_encoder_feed_utf8(&e, (const uint8_t *)"\xAD", 1, out, sizeof out, &got) == STENO_OK && got == 3);
        CHECK(steno_encoder_finish(&e, out, sizeof out, &got) == STENO_OK && got == 0);
        steno_encoder_reset(&e);
        CHECK(steno_encoder_feed_utf8(&e, (const uint8_t *)"\xAD", 1, out, sizeof out, &got) == STENO_INVALID_UTF8 && got == 0);
    }

    /* The decoder: finish reports an unfinished code point and changes nothing; reset starts a new value. */
    {
        static const uint8_t b[] = {0xF8, 0x50, 0xE9, 0x7C, 0xB4}; /* U+6771 U+4EAC */
        steno_decoder_init(&d);
        CHECK(steno_decoder_feed_code_points(&d, b, 2, c, 64, &got) == STENO_OK && got == 0);
        CHECK(steno_decoder_finish(&d, &got) == STENO_TRUNCATED && got == 0);
        CHECK(steno_decoder_feed_code_points(&d, b + 2, 2, c, 64, &got) == STENO_OK && got == 1 && c[0] == 0x6771);
        CHECK(steno_decoder_finish(&d, &got) == STENO_TRUNCATED && got == 3);
        CHECK(steno_decoder_feed_code_points(&d, b + 4, 1, c, 64, &got) == STENO_OK && got == 1 && c[0] == 0x4EAC);
        CHECK(steno_decoder_finish(&d, &got) == STENO_OK && got == 0);
        steno_decoder_reset(&d);
        CHECK(steno_decoder_feed_utf16(&d, b + 3, 2, u, 64, &got) == STENO_OK && got == 2 && u[0] == 0xD5 && u[1] == 0x10D);
    }

    /* The decoder: an error in a trail byte that arrives in a later call is reported at the start of its code point. */
    {
        static const uint8_t b[] = {0x48, 0xE9, 0x05};
        steno_decoder_init(&d);
        CHECK(steno_decoder_feed_utf8(&d, b, 2, out, sizeof out, &got) == STENO_OK && got == 1);
        CHECK(steno_decoder_feed_utf8(&d, b + 2, 1, out, sizeof out, &got) == STENO_INVALID_TRAIL && got == 1);
        CHECK(steno_decoder_feed_utf8(&d, (const uint8_t *)"\x08", 1, out, sizeof out, &got) == STENO_OK && got == 2);
    }

    /* Uninitialized objects are refused, and objects of zero bytes work. */
    {
        steno_encoder z;
        memset(&z, 0x5A, sizeof z);
        CHECK(steno_encoder_feed_utf8(&z, (const uint8_t *)"a", 1, out, sizeof out, &got) == STENO_INVALID_ARGUMENT && got == 0);
        CHECK(steno_encoder_finish(&z, out, sizeof out, &got) == STENO_INVALID_ARGUMENT);
        memset(&d, 0x5A, sizeof d);
        CHECK(steno_decoder_feed_utf8(&d, (const uint8_t *)"\x48", 1, out, sizeof out, &got) == STENO_INVALID_ARGUMENT);
        CHECK(steno_decoder_finish(&d, &got) == STENO_INVALID_ARGUMENT);
        memset(&z, 0, sizeof z);
        CHECK(steno_encoder_feed_utf8(&z, (const uint8_t *)"Hi", 2, out, sizeof out, &got) == STENO_OK && got == 2 && out[0] == 0x2F);
        memset(&d, 0, sizeof d);
        CHECK(steno_decoder_finish(&d, &got) == STENO_OK);
        CHECK(steno_decoder_feed_utf8(&d, out, 2, (uint8_t *)c, 8, &got) == STENO_OK && got == 2 && memcmp(c, "Hi", 2) == 0);
        steno_encoder_init(NULL);
        steno_decoder_init(NULL);
    }
}

/* ---------- Abuse of the C interface ---------- */

static void test_abuse(void) {
    uint8_t buf[64], src[16] = {0x48, 0x49};
    uint16_t u[16] = {0x48};
    uint32_t c[16] = {0x48};
    size_t got;
    steno_encoder e;
    steno_decoder d;
    union {
        uint32_t align;
        uint8_t b[64];
    } raw;

    /* NULL with a length of 0 is an empty buffer; with a length, an error. */
    CHECK(steno_encode_utf8(NULL, 0, NULL, 0, &got) == STENO_OK && got == 0);
    CHECK(steno_decode_utf16(NULL, 0, NULL, 0, &got) == STENO_OK && got == 0);
    CHECK(steno_encode_code_points(NULL, 0, buf, sizeof buf, &got) == STENO_OK && got == 0);
    CHECK(steno_encode_utf8(src, 2, NULL, 0, &got) == STENO_DESTINATION_TOO_SMALL && got == 0);
    CHECK(steno_decode_code_points(src, 2, NULL, 0, &got) == STENO_DESTINATION_TOO_SMALL);
    CHECK(steno_encode_utf8(NULL, 1, buf, sizeof buf, &got) == STENO_INVALID_ARGUMENT && got == 0);
    CHECK(steno_encode_utf8(src, 1, NULL, 1, &got) == STENO_INVALID_ARGUMENT);
    CHECK(steno_decode_utf8(NULL, 5, buf, sizeof buf, &got) == STENO_INVALID_ARGUMENT);
    CHECK(steno_encoded_len_utf16(NULL, 1, &got) == STENO_INVALID_ARGUMENT);
    CHECK(steno_decoded_len_code_points(NULL, 0, &got) == STENO_OK && got == 0);
    CHECK(steno_encode_utf8(src, 1, buf, sizeof buf, NULL) == STENO_INVALID_ARGUMENT);
    CHECK(steno_encoded_len_utf8(src, 1, NULL) == STENO_INVALID_ARGUMENT);
    CHECK(steno_is_ascii_text(NULL, 0) && !steno_is_ascii_text(NULL, 1));

    /* Lengths that no buffer can have. */
    CHECK(steno_encode_utf8(src, SIZE_MAX, buf, sizeof buf, &got) == STENO_INVALID_ARGUMENT && got == 0);
    CHECK(steno_encode_utf8(src, 2, buf, SIZE_MAX, &got) == STENO_INVALID_ARGUMENT);
    CHECK(steno_encode_utf16(u, SIZE_MAX / 2, buf, sizeof buf, &got) == STENO_INVALID_ARGUMENT);
    CHECK(steno_encode_code_points(c, SIZE_MAX / 4 + 1, buf, sizeof buf, &got) == STENO_INVALID_ARGUMENT);
    CHECK(steno_decode_utf16(src, 2, u, SIZE_MAX / 2 + 1, &got) == STENO_INVALID_ARGUMENT);
    CHECK(steno_decoded_len_utf8(src, SIZE_MAX, &got) == STENO_INVALID_ARGUMENT);
    CHECK(steno_encoded_len_code_points(c, SIZE_MAX - 3, &got) == STENO_INVALID_ARGUMENT);
    CHECK(!steno_is_ascii_text(src, SIZE_MAX));

    /* Misaligned pointers to code units, code points and out_len. */
    CHECK(steno_encode_utf16((const uint16_t *)(const void *)(raw.b + 1), 2, buf, sizeof buf, &got) == STENO_INVALID_ARGUMENT);
    CHECK(steno_decode_utf16(src, 2, (uint16_t *)(void *)(raw.b + 1), 4, &got) == STENO_INVALID_ARGUMENT);
    CHECK(steno_encode_code_points((const uint32_t *)(const void *)(raw.b + 2), 2, buf, sizeof buf, &got) == STENO_INVALID_ARGUMENT);
    CHECK(steno_decode_code_points(src, 2, (uint32_t *)(void *)(raw.b + 2), 4, &got) == STENO_INVALID_ARGUMENT);
    CHECK(steno_encoded_len_utf16((const uint16_t *)(const void *)(raw.b + 1), 1, &got) == STENO_INVALID_ARGUMENT);
    if (sizeof(size_t) > 1) CHECK(steno_encode_utf8(src, 1, buf, sizeof buf, (size_t *)(void *)(raw.b + 1)) == STENO_INVALID_ARGUMENT);

    /* Overlapping source and destination, also partly, also in streaming calls and with the object itself. */
    memcpy(buf, "\x2F\x50\x08", 3);
    CHECK(steno_decode_utf8(buf, 3, buf, 3, &got) == STENO_INVALID_ARGUMENT);
    CHECK(steno_decode_utf8(buf, 3, buf + 2, 8, &got) == STENO_INVALID_ARGUMENT);
    CHECK(steno_decode_utf8(buf + 2, 3, buf, 3, &got) == STENO_INVALID_ARGUMENT);
    CHECK(steno_decode_utf8(buf, 3, buf + 3, 8, &got) == STENO_OK && got == 3 && memcmp(buf + 3, "Hi!", 3) == 0);
    CHECK(steno_encode_utf8(buf + 3, 3, buf + 1, 3, &got) == STENO_INVALID_ARGUMENT);
    CHECK(steno_decode_utf16(buf, 3, (uint16_t *)(void *)buf, 4, &got) == STENO_INVALID_ARGUMENT || ((uintptr_t)buf % 2) != 0);
    steno_encoder_init(&e);
    CHECK(steno_encoder_feed_utf8(&e, buf, 3, buf + 1, 8, &got) == STENO_INVALID_ARGUMENT);
    CHECK(steno_encoder_feed_utf8(&e, buf, 3, (uint8_t *)&e, 8, &got) == STENO_INVALID_ARGUMENT);
    CHECK(steno_encoder_feed_utf8(&e, (const uint8_t *)&e + 8, 3, buf, 8, &got) == STENO_INVALID_ARGUMENT);
    CHECK(steno_encoder_finish(&e, (uint8_t *)&e + 4, 3, &got) == STENO_INVALID_ARGUMENT);
    steno_decoder_init(&d);
    CHECK(steno_decoder_feed_code_points(&d, buf, 3, (uint32_t *)(void *)&d, 3, &got) == STENO_INVALID_ARGUMENT);
    CHECK(steno_decoder_feed_utf8(NULL, buf, 3, buf + 8, 8, &got) == STENO_INVALID_ARGUMENT);
    CHECK(steno_encoder_feed_utf8(NULL, buf, 3, buf + 8, 8, &got) == STENO_INVALID_ARGUMENT);
    CHECK(steno_decoder_finish(NULL, &got) == STENO_INVALID_ARGUMENT);
    CHECK(steno_decoder_feed_utf8(&d, buf, 3, buf + 8, 8, NULL) == STENO_INVALID_ARGUMENT);
    CHECK(steno_encoder_feed_utf8(&e, NULL, 0, NULL, 0, &got) == STENO_OK && got == 0);
    CHECK(steno_decoder_feed_utf16(&d, NULL, 0, NULL, 0, &got) == STENO_OK && got == 0);

    /* Code points that are not code points, and the status names. */
    c[0] = 0x41;
    c[1] = 0x110000;
    CHECK(steno_encode_code_points(c, 2, buf, sizeof buf, &got) == STENO_INVALID_CODE_POINT && got == 1);
    c[1] = 0xFFFFFFFFu;
    CHECK(steno_encoded_len_code_points(c, 2, &got) == STENO_INVALID_CODE_POINT && got == 1);
    CHECK(strcmp(steno_status_name(STENO_NOT_A_VALUE), "not-a-value") == 0);
    CHECK(strcmp(steno_status_name(STENO_INVALID_CODE_POINT), "invalid-code-point") == 0);
    CHECK(strcmp(steno_status_name(-1), "unknown") == 0 && strcmp(steno_status_name(13), "unknown") == 0);
    CHECK(strcmp(STENO_VERSION_STRING, "0.1.0") == 0);

    /* The first problem met wins: a destination that is too short before bad UTF-8. */
    CHECK(steno_encode_utf8((const uint8_t *)"abc\xFF", 4, buf, 2, &got) == STENO_DESTINATION_TOO_SMALL && got == 0);
    CHECK(steno_encode_utf8((const uint8_t *)"abc\xFF", 4, buf, 3, &got) == STENO_INVALID_UTF8 && got == 3);
}

/* ---------- Long values (from the adversarial review) ---------- */

/* Values of n code points (megabytes), starting with U+FEFF or U+FFFE, in long runs of one script separated by changes of every kind,
   in every form, one-shot and streamed in pieces of 65,537 code units and of 1 to 9 bytes, against the independent oracle. */
static void test_large(size_t n) {
    uint32_t *cps = (uint32_t *)xmalloc(4 * n), *back = (uint32_t *)xmalloc(4 * n);
    uint16_t *u = (uint16_t *)xmalloc(4 * n), *u2 = (uint16_t *)xmalloc(4 * n);
    uint8_t *ref = (uint8_t *)xmalloc(4 * n), *out = (uint8_t *)xmalloc(4 * n + 16);
    rng_t r = {777};
    size_t i, len = 0, ol = 0, nu, pos, a, err;
    int pass;
    steno_encoder e;
    steno_decoder d;

    for (pass = 0; pass < 2; pass++) {
        i = 0;
        cps[i++] = pass == 0 ? 0xFEFF : 0xFFFE;

        while (i < n) {
            uint32_t k = rng_below(&r, 8), w = 0x80 * rng_below(&r, 0x20000 / 0x80), m = 1 + rng_below(&r, k < 2 ? 5000 : 200), j;

            for (j = 0; j < m && i < n; j++) {
                uint32_t c;

                switch (k) {
                case 0: c = 0x20 + rng_below(&r, 0x5F); break;
                case 1: c = rng_below(&r, 5) ? 0x430 + rng_below(&r, 32) : 0x20; break;
                case 2: c = 0x4E00 + rng_below(&r, 0x5200); break;
                case 3: c = 0xAC00 + rng_below(&r, 0x2BA4); break;
                case 4: c = rng_below(&r, 2) ? 0x3041 + rng_below(&r, 0x56) : 0x30A1 + rng_below(&r, 0x5A); break;
                case 5: c = w + rng_below(&r, 0x80); break;
                case 6: c = 0x1F600 + rng_below(&r, 0x50); break;
                default: c = rng_below(&r, 0x110000); break;
                }

                if (i > 0 && is_high(cps[i - 1]) && is_low(c)) c = 0x41;
                cps[i++] = c;
            }
        }

        CHECK(orc_encode(cps, n, ref, &len, &err) == 0);
        CHECK(steno_encode_code_points(cps, n, out, 4 * n + 16, &ol) == STENO_OK && ol == len && memcmp(out, ref, len) == 0);
        CHECK(steno_encoded_len_code_points(cps, n, &ol) == STENO_OK && ol == len);
        nu = to_utf16(cps, n, u);
        CHECK(steno_encode_utf16(u, nu, out, 4 * n + 16, &ol) == STENO_OK && ol == len && memcmp(out, ref, len) == 0);
        CHECK(steno_decode_code_points(ref, len, back, n, &ol) == STENO_OK && ol == n && memcmp(back, cps, 4 * n) == 0);
        CHECK(steno_decode_utf16(ref, len, u2, 2 * n, &ol) == STENO_OK && ol == nu && memcmp(u2, u, 2 * nu) == 0);
        CHECK(steno_decoded_len_utf16(ref, len, &ol) == STENO_OK && ol == nu);

        steno_encoder_init(&e);
        for (pos = 0, a = 0; a < nu; a += 65537) {
            size_t m = nu - a < 65537 ? nu - a : 65537;
            CHECK(steno_encoder_feed_utf16(&e, u + a, m, out + pos, 4 * n + 16 - pos, &ol) == STENO_OK);
            pos += ol;
        }

        CHECK(steno_encoder_finish(&e, out + pos, 3, &ol) == STENO_OK);
        pos += ol;
        CHECK(pos == len && memcmp(out, ref, len) == 0);

        steno_decoder_init(&d);
        for (pos = 0, a = 0; a < len;) {
            size_t m = 1 + rng_below(&r, 9);
            if (m > len - a) m = len - a;
            CHECK(steno_decoder_feed_code_points(&d, ref + a, m, back + pos, n - pos, &ol) == STENO_OK);
            pos += ol;
            a += m;
        }

        CHECK(steno_decoder_finish(&d, &ol) == STENO_OK && pos == n && memcmp(back, cps, 4 * n) == 0);
    }

    printf("long values: 2 x %u code points\n", (unsigned)n);
    free(cps);
    free(back);
    free(u);
    free(u2);
    free(ref);
    free(out);
}

/* ---------- Every short byte string (from the adversarial review) ---------- */

/* One byte string, decoded by the library in every form and by the independent oracle: the same status, position and text; accepted
   strings re-encode to themselves; is_ascii_text agrees. */
static void short_string(const uint8_t *b, size_t n) {
    uint32_t oc[8], lc[8];
    uint16_t u[16];
    uint8_t u8[32], re[32];
    size_t on = 0, oerr = 0, ln = 0, x = 0, i;
    int os = orc_decode(b, n, oc, &on, &oerr), sur = 0, ascii = os == 0;
    steno_status s = steno_decode_code_points(b, n, lc, 8, &ln), s8;

    if ((int)s != os || (os == 0 ? (ln != on || memcmp(lc, oc, 4 * on) != 0) : ln != oerr)) {
        CHECKF(0, "%u bytes %02X %02X %02X %02X: the library %d at %u, the oracle %d at %u", (unsigned)n, b[0], n > 1 ? b[1] : 0,
               n > 2 ? b[2] : 0, n > 3 ? b[3] : 0, (int)s, (unsigned)ln, os, (unsigned)(os == 0 ? on : oerr));
        return;
    }

    if (steno_decode_utf16(b, n, u, 16, &x) != s || (s != STENO_OK && x != ln)) CHECKF(0, "short string: UTF-16");
    if (steno_decoded_len_code_points(b, n, &x) != s || x != ln) CHECKF(0, "short string: decoded length");

    for (i = 0; i < on; i++) {
        sur |= is_surrogate(oc[i]);
        ascii &= oc[i] == 9 || oc[i] == 10 || oc[i] == 13 || (oc[i] >= 0x20 && oc[i] <= 0x7E);
    }

    if (os == 0 && (steno_encode_code_points(oc, on, re, 32, &x) != STENO_OK || x != n || memcmp(re, b, n) != 0)) {
        CHECKF(0, "short string: re-encoding");
    }

    s8 = steno_decode_utf8(b, n, u8, 32, &x);
    if (!(s8 == s || (s8 == STENO_SURROGATE && (sur || os != 0)))) CHECKF(0, "short string: UTF-8");
    if (steno_is_ascii_text(b, n) != (ascii != 0)) CHECKF(0, "short string: is_ascii_text");
}

/* Every byte string of 1 and 2 bytes; of 3 bytes too with three set (16.8 million, about 20 seconds); and of 4 bytes whose first byte
   is in from..to-1 (about 25 seconds per first byte). */
static void test_short_strings(int three, unsigned from, unsigned to) {
    uint8_t s[4];
    unsigned a, b, c, d;
    long before = harness_failures;

    for (a = 0; a < 256; a++) {
        s[0] = (uint8_t)a;
        short_string(s, 1);

        for (b = 0; b < 256; b++) {
            s[1] = (uint8_t)b;
            short_string(s, 2);

            for (c = 0; three && c < 256; c++) {
                s[2] = (uint8_t)c;
                short_string(s, 3);
            }
        }
    }

    for (a = from; a < to && a < 256; a++) {
        s[0] = (uint8_t)a;

        for (b = 0; b < 256; b++) {
            s[1] = (uint8_t)b;

            for (c = 0; c < 256; c++) {
                s[2] = (uint8_t)c;

                for (d = 0; d < 256; d++) {
                    s[3] = (uint8_t)d;
                    short_string(s, 4);
                }
            }
        }
    }

    printf("every byte string of 1%s bytes%s: %s\n", three ? " to 3" : " and 2", from < to ? " and the selected ones of 4" : "",
           harness_failures == before ? "ok" : "FAILED");
}

int main(int argc, char **argv) {
    rng_t r = {20251121};
    const char *path = "../../TestVectors/steno-v1-vectors.txt";
    const char *large = getenv("STENO_LARGE_VECTORS");
    clock_t t0 = clock();

    const char *exhaustive = getenv("STENO_EXHAUSTIVE");
    unsigned from = 0, to = 0;
    int k;

    for (k = 1; k < argc; k++) {
        if (strcmp(argv[k], "--short4") == 0 && k + 2 < argc) {
            from = (unsigned)strtoul(argv[k + 1], NULL, 0);
            to = (unsigned)strtoul(argv[k + 2], NULL, 0);
            k += 2;
        } else {
            path = argv[k];
        }
    }

    test_abuse();
    test_streaming_rules();
    test_bounds();
    test_vectors(path, 0, &r);
    test_random(&r, 6000, 60);
    test_random(&r, 60, 3000);
    test_order_and_prefixes(&r, 3000);
    test_block_boundaries(&r);
    test_invalid_utf8(&r);
    test_garbage(&r);
    test_large(1000000);
    test_short_strings(exhaustive != NULL && strcmp(exhaustive, "1") == 0, from, to);

    if (large != NULL && large[0] != 0) {
        test_vectors(large, 1, &r);
    } else {
        printf("STENO_LARGE_VECTORS is not set: the large vector file is skipped\n");
    }

    printf("time: %.1f s\n", (double)(clock() - t0) / CLOCKS_PER_SEC);
    return harness_report("steno");
}
