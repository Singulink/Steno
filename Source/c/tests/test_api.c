/*
 * The contract of the interface on abusive arguments, written by the adversarial reviewer of this library: NULL and misaligned
 * pointers, buffers that wrap the end of the address space, overlap with the object, an out_len inside the source or the object, held
 * input followed by another form, failed calls followed by retries, finish in every held state, and objects filled with zero bytes.
 * Run it under AddressSanitizer and UndefinedBehaviorSanitizer (make sanitize does).
 */

#include "../steno.h"

#include "harness.h"

/* out_len inside a buffer of the call (finding 1 of the review): it is refused with STENO_INVALID_ARGUMENT, and nothing is written
   through it, as in the Rust implementation. Inside the source, the destination (every form, at the first, middle and last byte), or a
   streaming object; next to them is fine. */
static void out_len_inside_buffers(void) {
    static const char text[] = "Hello, world! 1234567890 abcdefghij";
    union {
        size_t align[16];
        uint8_t b[128];
    } in, out;
    uint8_t expect[128];
    size_t n_expect = 0, k, ok_len = 0;
    steno_encoder e;
    steno_decoder d;

    CHECK(steno_encode_utf8((const uint8_t *)text, 35, expect, sizeof expect, &n_expect) == STENO_OK && n_expect == 35);

    for (k = 0; k < 35; k += sizeof(size_t)) { /* every aligned slot that overlaps the 35 bytes of the source */
        size_t *src_slot = (size_t *)(void *)(in.b + k), *dst_slot = (size_t *)(void *)(out.b + k), was;

        /* Inside the source: refused, the source and out_len unchanged. */
        memcpy(in.b, text, 35);
        was = *src_slot;
        CHECKF(steno_encode_utf8(in.b, 35, out.b + 64, 64, src_slot) == STENO_INVALID_ARGUMENT && *src_slot == was, "src + %u", (unsigned)k);
        CHECK(steno_encoded_len_utf8(in.b, 35, src_slot) == STENO_INVALID_ARGUMENT && *src_slot == was);
        CHECK(steno_encode_utf16((const uint16_t *)(const void *)in.b, 20, out.b + 64, 64, src_slot) == STENO_INVALID_ARGUMENT);
        CHECK(steno_encoded_len_code_points((const uint32_t *)(const void *)in.b, 10, src_slot) == STENO_INVALID_ARGUMENT);
        CHECK(memcmp(in.b, text, 35) == 0 || k + sizeof(size_t) > 35);

        memcpy(in.b, expect, n_expect);
        was = *src_slot;
        CHECK(steno_decode_utf8(in.b, n_expect, out.b + 64, 64, src_slot) == STENO_INVALID_ARGUMENT && *src_slot == was);
        CHECK(steno_decoded_len_utf16(in.b, n_expect, src_slot) == STENO_INVALID_ARGUMENT && *src_slot == was);

        /* Inside the destination: refused, out_len unchanged. */
        memset(out.b, 0x5A, sizeof out.b);
        was = *dst_slot;
        CHECK(steno_encode_utf8((const uint8_t *)text, 35, out.b, 64, dst_slot) == STENO_INVALID_ARGUMENT && *dst_slot == was);
        CHECK(steno_decode_utf16(expect, n_expect, (uint16_t *)(void *)out.b, 32, dst_slot) == STENO_INVALID_ARGUMENT);
        CHECK(steno_decode_code_points(expect, n_expect, (uint32_t *)(void *)out.b, 16, dst_slot) == STENO_INVALID_ARGUMENT);
        CHECK(*dst_slot == was);

        /* The same for the streaming functions. */
        steno_encoder_init(&e);
        memcpy(in.b, text, 35);
        CHECK(steno_encoder_feed_utf8(&e, in.b, 35, out.b + 64, 64, src_slot) == STENO_INVALID_ARGUMENT);
        CHECK(steno_encoder_feed_utf8(&e, (const uint8_t *)text, 35, out.b, 64, dst_slot) == STENO_INVALID_ARGUMENT);
        CHECK(steno_encoder_finish(&e, out.b, 64, dst_slot) == STENO_INVALID_ARGUMENT);
        CHECK(e.progress_.fed == 0);
        steno_decoder_init(&d);
        memcpy(in.b, expect, n_expect);
        CHECK(steno_decoder_feed_utf8(&d, in.b, n_expect, out.b + 64, 64, src_slot) == STENO_INVALID_ARGUMENT);
        CHECK(steno_decoder_feed_code_points(&d, expect, n_expect, (uint32_t *)(void *)out.b, 16, dst_slot) == STENO_INVALID_ARGUMENT);
        CHECK(d.progress_.fed == 0);
    }

    /* Right after the source and right before the destination: not inside, so it works. */
    memcpy(in.b, text, 32);
    CHECK(steno_encode_utf8(in.b, 32, out.b + 8, 64, (size_t *)(void *)(in.b + 32)) == STENO_OK);
    CHECK(*(size_t *)(void *)(in.b + 32) == 32 && memcmp(out.b + 8, expect, 32) == 0);
    CHECK(steno_encode_utf8(in.b, 32, out.b + 8, 64, &out.align[0]) == STENO_OK && out.align[0] == 32);
    CHECK(steno_encode_utf8(in.b, 0, out.b, 0, (size_t *)(void *)in.b) == STENO_OK && *(size_t *)(void *)in.b == 0);
    CHECK(steno_encode_utf8(NULL, 0, NULL, 0, &ok_len) == STENO_OK && ok_len == 0);

    /* Inside a streaming object: refused, nothing written. */
    steno_encoder_init(&e);
    {
        size_t *inside = &e.progress_.fed, was = *inside;
        CHECK(steno_encoder_feed_utf8(&e, (const uint8_t *)"abc", 3, out.b, 64, inside) == STENO_INVALID_ARGUMENT && *inside == was);
        CHECK(steno_encoder_finish(&e, out.b, 64, inside) == STENO_INVALID_ARGUMENT && *inside == was);
    }

    steno_decoder_init(&d);
    {
        size_t *inside = &d.progress_.fed, was = *inside;
        CHECK(steno_decoder_feed_utf8(&d, expect, 3, out.b, 64, inside) == STENO_INVALID_ARGUMENT && *inside == was);
        CHECK(steno_decoder_finish(&d, inside) == STENO_INVALID_ARGUMENT && *inside == was);
    }
}

int main(void) {
    uint8_t out[256], out2[256];
    uint32_t cps[64];
    size_t ol, ol2;
    steno_encoder *e = (steno_encoder *)malloc(sizeof *e), *e2 = (steno_encoder *)malloc(sizeof *e2);
    steno_decoder *d = (steno_decoder *)malloc(sizeof *d);

    /* Buffers that would wrap the end of the address space are rejected before any access. */
    {
        const uint8_t *top = (const uint8_t *)(uintptr_t)(UINTPTR_MAX - 3);
        CHECK(steno_encode_utf8(top, 8, out, sizeof out, &ol) == STENO_INVALID_ARGUMENT && ol == 0);
        CHECK(steno_decoded_len_utf8(top, 8, &ol) == STENO_INVALID_ARGUMENT);
        CHECK(steno_encode_utf8((const uint8_t *)"ab", 2, (uint8_t *)(uintptr_t)(UINTPTR_MAX - 3), 8, &ol) == STENO_INVALID_ARGUMENT);
        CHECK(!steno_is_ascii_text(top, 8));
    }

    /* Misaligned code point buffers, huge lengths. */
    {
        uint32_t buf[8] = {0x41, 0x42, 0, 0, 0, 0, 0, 0};
        CHECK(steno_encode_code_points((const uint32_t *)(const void *)((const uint8_t *)buf + 2), 1, out, sizeof out, &ol) == STENO_INVALID_ARGUMENT);
        CHECK(steno_decode_code_points((const uint8_t *)"\x2F", 1, (uint32_t *)(void *)((uint8_t *)buf + 1), 4, &ol) == STENO_INVALID_ARGUMENT);
        CHECK(steno_encode_code_points(buf, SIZE_MAX / 4 + 1, out, sizeof out, &ol) == STENO_INVALID_ARGUMENT);
        CHECK(steno_decode_code_points((const uint8_t *)"\x2F", 1, buf, SIZE_MAX / 4 + 1, &ol) == STENO_INVALID_ARGUMENT);
        CHECK(steno_encoded_len_code_points(NULL, 1, &ol) == STENO_INVALID_ARGUMENT);
        CHECK(steno_encoded_len_code_points(NULL, 0, &ol) == STENO_OK && ol == 0);
        /* Source and destination share bytes. */
        CHECK(steno_encode_code_points(buf, 2, (uint8_t *)buf + 4, 8, &ol) == STENO_INVALID_ARGUMENT);
        CHECK(steno_decode_code_points((const uint8_t *)buf, 5, buf + 1, 2, &ol) == STENO_INVALID_ARGUMENT);
        CHECK(steno_decode_code_points((const uint8_t *)buf, 4, buf + 1, 2, &ol) != STENO_INVALID_ARGUMENT); /* adjacent: no overlap */
    }

    /* out_len misaligned: rejected, nothing written. */
    {
        uint8_t raw[24];
        memset(raw, 0xEE, sizeof raw);
        CHECK(steno_encode_code_points(NULL, 0, NULL, 0, (size_t *)(void *)(raw + 1)) == STENO_INVALID_ARGUMENT);
        CHECK(steno_decoder_finish(NULL, (size_t *)(void *)(raw + 1)) == STENO_INVALID_ARGUMENT);
        {
            size_t i;
            int same = 1;
            for (i = 0; i < sizeof raw; i++) same &= raw[i] == 0xEE;
            CHECK(same);
        }
    }

    /* Streaming: the object itself as a buffer, a misaligned object, a decoder given to an encoder function. */
    steno_encoder_init(e);
    CHECK(steno_encoder_feed_utf8(e, (const uint8_t *)"abc", 3, (uint8_t *)e + 64, 16, &ol) == STENO_INVALID_ARGUMENT);
    CHECK(steno_encoder_feed_utf8(e, (const uint8_t *)e + 64, 3, out, sizeof out, &ol) == STENO_INVALID_ARGUMENT);
    CHECK(steno_encoder_finish(e, (uint8_t *)e, 3, &ol) == STENO_INVALID_ARGUMENT);
    CHECK(steno_encoder_feed_utf8((steno_encoder *)(void *)((uint8_t *)e2 + 1), (const uint8_t *)"a", 1, out, 8, &ol) == STENO_INVALID_ARGUMENT);
    steno_decoder_init(d);
    CHECK(steno_encoder_feed_utf8((steno_encoder *)(void *)d, (const uint8_t *)"a", 1, out, 8, &ol) == STENO_INVALID_ARGUMENT);
    CHECK(steno_decoder_feed_utf8(d, (const uint8_t *)"\x2F", 1, (uint8_t *)d + 8, 8, &ol) == STENO_INVALID_ARGUMENT);
    CHECK(steno_decoder_feed_code_points(d, (const uint8_t *)"\x2F", 1, (uint32_t *)(void *)((uint8_t *)d + 16), 2, &ol) == STENO_INVALID_ARGUMENT);

    /* A held UTF-8 sequence followed by UTF-16 or code points: invalid UTF-8 at the held sequence; nothing changes; UTF-8 completes it. */
    steno_encoder_init(e);
    CHECK(steno_encoder_feed_utf8(e, (const uint8_t *)"ab\xE6\x9D", 4, out, sizeof out, &ol) == STENO_OK && ol == 2);
    {
        const uint16_t u[1] = {0x41};
        CHECK(steno_encoder_feed_utf16(e, u, 1, out + 2, sizeof out - 2, &ol2) == STENO_INVALID_UTF8 && ol2 == 2);
        cps[0] = 0x41;
        CHECK(steno_encoder_feed_code_points(e, cps, 1, out + 2, sizeof out - 2, &ol2) == STENO_INVALID_UTF8 && ol2 == 2);
        CHECK(steno_encoder_finish(e, out + 2, 3, &ol2) == STENO_INVALID_UTF8 && ol2 == 2);
        /* An empty piece of another form is not an error, even while a UTF-8 sequence is held. */
        CHECK(steno_encoder_feed_utf16(e, NULL, 0, NULL, 0, &ol2) == STENO_OK && ol2 == 0);
    }
    CHECK(steno_encoder_feed_utf8(e, (const uint8_t *)"\xB1", 1, out + 2, sizeof out - 2, &ol2) == STENO_OK);
    CHECK(steno_encoder_finish(e, out + 2 + ol2, 3, &ol) == STENO_OK && ol == 0);
    CHECK(steno_encode_utf8((const uint8_t *)"ab\xE6\x9D\xB1", 5, out2, sizeof out2, &ol) == STENO_OK && ol == 2 + ol2 &&
          memcmp(out, out2, ol) == 0);

    /* A held high surrogate: finish with no room fails and keeps it; with room it writes it; a later low code unit is then not a value. */
    steno_encoder_init(e);
    {
        const uint16_t u[2] = {0x41, 0xD83D}, lo[1] = {0xDE00};
        CHECK(steno_encoder_feed_utf16(e, u, 2, out, sizeof out, &ol) == STENO_OK && ol == 1);
        CHECK(steno_encoder_finish(e, NULL, 0, &ol2) == STENO_DESTINATION_TOO_SMALL && ol2 == 0);
        CHECK(steno_encoder_finish(e, out + 1, 2, &ol2) == STENO_DESTINATION_TOO_SMALL && ol2 == 0);
        memcpy(e2, e, sizeof *e); /* a copy keeps the progress */
        CHECK(steno_encoder_feed_utf16(e2, lo, 1, out2, sizeof out2, &ol2) == STENO_OK);
        CHECK(steno_encoder_finish(e, out + 1, 3, &ol2) == STENO_OK && ol2 == 3);
        CHECK(steno_encoder_feed_utf16(e, lo, 1, out + 4, sizeof out - 4, &ol2) == STENO_NOT_A_VALUE && ol2 == 2);
        cps[0] = 0xDE00;
        CHECK(steno_encoder_feed_code_points(e, cps, 1, out + 4, sizeof out - 4, &ol2) == STENO_NOT_A_VALUE && ol2 == 2);
        cps[0] = 0x110000;
        CHECK(steno_encoder_feed_code_points(e, cps, 1, out + 4, sizeof out - 4, &ol2) == STENO_INVALID_CODE_POINT && ol2 == 2);
    }

    /* A held high code unit followed by a low code point (other form): not a value at the position of the low one. */
    steno_encoder_init(e);
    {
        const uint16_t u[1] = {0xD800};
        CHECK(steno_encoder_feed_utf16(e, u, 1, out, sizeof out, &ol) == STENO_OK && ol == 0);
        cps[0] = 0xDC00;
        CHECK(steno_encoder_feed_code_points(e, cps, 1, out, sizeof out, &ol) == STENO_NOT_A_VALUE && ol == 1);
        /* Nothing changed: the high unit is still held, and a low unit joins it. */
        {
            const uint16_t lo[1] = {0xDC00};
            const uint32_t sup[1] = {0x10000};
            CHECK(steno_encoder_feed_utf16(e, lo, 1, out, sizeof out, &ol) == STENO_OK);
            CHECK(steno_encode_code_points(sup, 1, out2, sizeof out2, &ol2) == STENO_OK && ol == ol2 && memcmp(out, out2, ol) == 0);
        }
    }

    /* Zero-filled objects work, also in finish. */
    memset(e, 0, sizeof *e);
    CHECK(steno_encoder_finish(e, NULL, 0, &ol) == STENO_OK && ol == 0);
    memset(d, 0, sizeof *d);
    CHECK(steno_decoder_finish(d, &ol) == STENO_OK && ol == 0);
    CHECK(steno_decoder_feed_utf8(d, (const uint8_t *)"\xF6\x0B", 2, out, sizeof out, &ol) == STENO_OK && ol == 0);
    CHECK(steno_decoder_finish(d, &ol) == STENO_TRUNCATED && ol == 0);
    CHECK(steno_decoder_feed_utf8(d, (const uint8_t *)"\x50", 1, out, 2, &ol) == STENO_DESTINATION_TOO_SMALL && ol == 0);
    CHECK(steno_decoder_feed_utf8(d, (const uint8_t *)"\x50", 1, out, 3, &ol) == STENO_OK && ol == 3);
    CHECK(steno_decoder_finish(d, &ol) == STENO_OK && ol == 0);

    /* A decoder error inside a held code point: the position is where the code point starts, in the value. */
    steno_decoder_init(d);
    CHECK(steno_decoder_feed_code_points(d, (const uint8_t *)"\x2F\x50\xF6", 3, cps, 64, &ol) == STENO_OK && ol == 2);
    CHECK(steno_decoder_feed_code_points(d, (const uint8_t *)"\x0B", 1, cps, 64, &ol) == STENO_OK && ol == 0);
    CHECK(steno_decoder_feed_code_points(d, (const uint8_t *)"\x03", 1, cps, 64, &ol) == STENO_INVALID_TRAIL && ol == 2);
    CHECK(steno_decoder_feed_code_points(d, (const uint8_t *)"\x00", 1, cps, 64, &ol) == STENO_ZERO_BYTE && ol == 2);
    CHECK(steno_decoder_finish(d, &ol) == STENO_TRUNCATED && ol == 2);

    /* Status names. */
    CHECK(strcmp(steno_status_name(-2147483647 - 1), "unknown") == 0 && strcmp(steno_status_name(13), "unknown") == 0);
    CHECK(steno_max_encoded_len_code_points(SIZE_MAX / 4 + 1) == SIZE_MAX && steno_max_decoded_len_code_points(SIZE_MAX) == SIZE_MAX);

    out_len_inside_buffers();

    free(e);
    free(e2);
    free(d);
    return harness_report("interface");
}
