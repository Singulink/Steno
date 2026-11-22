/*
 * An independent oracle of the Steno Encoding, version 1, for the tests: a line-by-line C port of the clean-room C# codec
 * Tests/Singulink.Steno.Tests/ReferenceSteno.cs, which was written from FORMAT.md alone by someone who never saw an implementation. The
 * adversarial reviewer of this library ported it. It shares no code with steno.c or with tests/ref.c (the author's own plain codec), and
 * it must stay independent of the library: never change it to agree with steno.c. A disagreement is a finding to investigate.
 */

#ifndef STENO_TEST_ORACLE_H
#define STENO_TEST_ORACLE_H

#include <stddef.h>
#include <stdint.h>

#define ORC_NONE (-1)

typedef struct {
    int ctx;  /* 0 general, 1 CJK, 2 Hangul */
    int slot; /* 0 letters, 1 hiragana, 2 katakana */
    int main_start, main_end;
    int second_start, second_end; /* ORC_NONE if none */
    int conf;
    int pending_start; /* ORC_NONE if none */
} orc_state;

typedef struct {
    int lo, hi, size, first, leads;
} orc_range;

void orc_initial(orc_state *s);
/* Returns 0 and the window, or -1 if none. */
int orc_window_of(int c, int *start, int *end);
/* Builds the layout into r (at least 64 entries); returns the count, or -1 if more than 254 lead bytes. */
int orc_layout(const orc_state *s, orc_range *r);
/* The same without the memo of orc_layout (which is not thread safe). */
int orc_layout_raw(const orc_state *s, orc_range *r);
void orc_update(orc_state *s, int c, int n);

/* 0 ok, 11 not a value (*err = index of low), 12 invalid code point (*err = index). out must hold 4 * n bytes. */
int orc_encode(const uint32_t *cps, size_t n, uint8_t *out, size_t *out_len, size_t *err);
/* The same, and starts[i] (if not NULL; n entries) is the byte offset where code point i starts. */
int orc_encode_starts(const uint32_t *cps, size_t n, uint8_t *out, size_t *out_len, size_t *err, size_t *starts);

/* 0 ok or a steno_status value (1,2,3,4,5,10) with *err = byte offset where the code point that broke a rule starts. out holds n. */
int orc_decode(const uint8_t *b, size_t n, uint32_t *out, size_t *out_n, size_t *err);

#endif
