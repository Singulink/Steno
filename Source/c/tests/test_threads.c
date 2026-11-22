/*
 * Concurrency: several threads encode and decode different texts at once, with one-shot calls and with their own streaming objects, and
 * must get the results of a single thread. Half of the texts hop among many windows and contexts, so that every call builds layouts.
 * The library has no global state that it writes, so this checks that claim; run it under ThreadSanitizer to check it harder (the CI
 * does). Uses Windows threads on Windows and POSIX threads elsewhere.
 *
 *   test_threads [THREADS [ROUNDS]]     (default 8 threads, 1,500 rounds each)
 */

#include "../steno.h"

#include "harness.h"

#if defined(_WIN32)
#include <windows.h>
#else
#include <pthread.h>
#endif

enum { MAX_THREADS = 64, TEXTS = 64, MAXLEN = 600 };

static uint32_t texts[TEXTS][MAXLEN];
static size_t lens[TEXTS];
static uint8_t expect[TEXTS][4 * MAXLEN];
static size_t expect_len[TEXTS];
static long errors[MAX_THREADS];
static int rounds = 1500;

/* Text that hops among windows, contexts and planes (from the adversarial review's ThreadSanitizer stress test). */
static size_t hop_text(rng_t *r, uint32_t *out) {
    size_t i;

    for (i = 0; i < MAXLEN; i++) {
        uint32_t x = (uint32_t)(rng_next(r) >> 20);

        switch (x % 6) {
        case 0: out[i] = 0x0400 + 0x80 * (x % 40) + (x >> 8) % 0x80; break;
        case 1: out[i] = 0xE000 + 0x80 * ((x >> 4) % 0x240) + (x >> 12) % 0x80; break;
        case 2: out[i] = 0x4E00 + (x >> 6) % 0x5200; break;
        case 3: out[i] = 0x20 + (x >> 3) % 0x5F; break;
        case 4: out[i] = 0xAC00 + (x >> 5) % 0x2BA4; break;
        default: out[i] = 0x40000 + (x >> 2) % 0xD0000; break;
        }
    }

    return MAXLEN;
}

static void work(int id) {
    rng_t r;
    int round;

    r.s = 1000 + (uint64_t)id;

    for (round = 0; round < rounds; round++) {
        int t = (int)rng_below(&r, TEXTS);
        uint8_t out[4 * MAXLEN];
        uint32_t back[MAXLEN];
        size_t got = 0, pos, i;

        if (steno_encode_code_points(texts[t], lens[t], out, sizeof out, &got) != STENO_OK || got != expect_len[t] ||
            memcmp(out, expect[t], got) != 0) {
            errors[id]++;
        }

        if (steno_decode_code_points(expect[t], expect_len[t], back, MAXLEN, &got) != STENO_OK || got != lens[t] ||
            memcmp(back, texts[t], got * 4) != 0) {
            errors[id]++;
        }

        if (round % 4 == 0) {
            /* Streamed, in pieces of 3 code points and of 5 bytes. */
            steno_encoder e;
            steno_decoder d;

            steno_encoder_init(&e);
            for (pos = 0, i = 0; i < lens[t]; i += 3) {
                size_t m = lens[t] - i < 3 ? lens[t] - i : 3;
                if (steno_encoder_feed_code_points(&e, texts[t] + i, m, out + pos, sizeof out - pos, &got) != STENO_OK) errors[id]++;
                pos += got;
            }

            if (pos != expect_len[t] || memcmp(out, expect[t], pos) != 0) errors[id]++;

            steno_decoder_init(&d);
            for (pos = 0, i = 0; i < expect_len[t]; i += 5) {
                size_t m = expect_len[t] - i < 5 ? expect_len[t] - i : 5;
                if (steno_decoder_feed_code_points(&d, expect[t] + i, m, back + pos, MAXLEN - pos, &got) != STENO_OK) errors[id]++;
                pos += got;
            }

            if (steno_decoder_finish(&d, &got) != STENO_OK || pos != lens[t] || memcmp(back, texts[t], 4 * pos) != 0) errors[id]++;
        }
    }
}

#if defined(_WIN32)
static DWORD WINAPI thread_main(LPVOID arg) {
    work((int)(intptr_t)arg);
    return 0;
}
#else
static void *thread_main(void *arg) {
    work((int)(intptr_t)arg);
    return NULL;
}
#endif

int main(int argc, char **argv) {
    rng_t r = {99};
    int t, threads = argc > 1 ? atoi(argv[1]) : 8;

    if (argc > 2) rounds = atoi(argv[2]);
    if (threads < 1 || threads > MAX_THREADS) threads = 8;

    for (t = 0; t < TEXTS; t++) {
        lens[t] = t % 2 == 0 ? random_text(&r, texts[t], MAXLEN, 1) : hop_text(&r, texts[t]);
        CHECK(steno_encode_code_points(texts[t], lens[t], expect[t], sizeof expect[t], &expect_len[t]) == STENO_OK);
    }

    {
#if defined(_WIN32)
        HANDLE h[MAX_THREADS];
        for (t = 0; t < threads; t++) h[t] = CreateThread(NULL, 0, thread_main, (LPVOID)(intptr_t)t, 0, NULL);
        WaitForMultipleObjects((DWORD)threads, h, TRUE, INFINITE);
        for (t = 0; t < threads; t++) CloseHandle(h[t]);
#else
        pthread_t th[MAX_THREADS];
        for (t = 0; t < threads; t++) CHECK(pthread_create(&th[t], NULL, thread_main, (void *)(intptr_t)t) == 0);
        for (t = 0; t < threads; t++) pthread_join(th[t], NULL);
#endif
    }

    for (t = 0; t < threads; t++) CHECKF(errors[t] == 0, "thread %d: %ld results differ", t, errors[t]);
    printf("%d threads x %d rounds\n", threads, rounds);
    return harness_report("threads");
}
