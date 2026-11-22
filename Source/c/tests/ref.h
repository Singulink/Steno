/*
 * A plain reference codec of the Steno Encoding, version 1, for the tests. It is written from FORMAT.md, separately from steno.c and
 * with another structure: it lays the pieces of 6.2 one over the other on a list of segments before every code point, and finds ranges
 * and lead bytes by a linear search. It is slow and simple, so that each step can be checked against the text of the specification.
 */

#ifndef STENO_TEST_REF_H
#define STENO_TEST_REF_H

#include <stddef.h>
#include <stdint.h>

/* A range of a layout. */
typedef struct {
    uint32_t lo, hi;
    unsigned size, first, leads;
} ref_range;

/* The state of FORMAT.md 5. Windows are given by their first code point, or REF_NONE. */
#define REF_NONE 0xFFFFFFFFu

typedef struct {
    int ctx;  /* 0 general, 1 CJK, 2 Hangul */
    int slot; /* 0 letters, 1 hiragana, 2 katakana */
    uint32_t main, second, pending;
    int conf;
} ref_state;

void ref_initial(ref_state *s);
uint32_t ref_window(uint32_t c);
uint32_t ref_window_end(uint32_t w);

/* Builds the layout of a state into r (at least 64 entries) and returns the number of ranges. */
size_t ref_layout(const ref_state *s, ref_range *r);

/* The state update of 7.1 after c, which took k bytes. */
void ref_update(ref_state *s, uint32_t c, unsigned k);

/* Encodes code points. Returns 0, or 11 (not a value; *err_index is the index of the low surrogate) or 12 (a value above 0x10FFFF).
   out must hold 4 * n bytes. starts (may be NULL) receives the byte offset where each code point starts. */
int ref_encode(const uint32_t *cps, size_t n, uint8_t *out, size_t *out_len, size_t *err_index, size_t *starts);

/* Decodes Steno bytes. Returns 0, or the status value of steno.h of the rule met first (1 zero-byte, 2 unused-lead, 3 truncated,
   4 past-range, 5 surrogate-pair, 10 invalid-trail), with *err_pos the offset where the code point that broke it starts. out must
   hold n code points. */
int ref_decode(const uint8_t *bytes, size_t n, uint32_t *out, size_t *out_n, size_t *err_pos);

#endif
