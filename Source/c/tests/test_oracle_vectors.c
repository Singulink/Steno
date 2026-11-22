/*
 * The vector file against the independent oracle (oracle.c), and the library against the oracle on every vector: every valid vector
 * encodes and decodes the same in the oracle and in every one-shot form of the library, and every invalid or not-a-value vector is
 * rejected by both with the same status and position. A check of the oracle itself as much as of the library.
 *
 *   test_oracle_vectors [vector file]     (default ../../TestVectors/steno-v1-vectors.txt; STENO_LARGE_VECTORS adds the large file)
 */

#include "../steno.h"

#include "harness.h"
#include "oracle.h"

static size_t parse_cps(char *f, uint32_t *out) {
    size_t n = 0;
    char *p = f;

    if (strcmp(f, "-") == 0) return 0;

    while (*p) {
        out[n++] = (uint32_t)strtoul(p, &p, 16);
        while (*p == ' ') p++;
    }

    return n;
}

static size_t parse_bytes(const char *f, uint8_t *out) {
    size_t n = 0, i;

    if (strcmp(f, "-") == 0) return 0;

    for (i = 0; f[i] && f[i + 1]; i += 2) {
        char h[3];
        h[0] = f[i];
        h[1] = f[i + 1];
        h[2] = 0;
        out[n++] = (uint8_t)strtoul(h, NULL, 16);
    }

    return n;
}

static int reason_of(const char *r) {
    if (strcmp(r, "zero-byte") == 0) return STENO_ZERO_BYTE;
    if (strcmp(r, "unused-lead") == 0) return STENO_UNUSED_LEAD;
    if (strcmp(r, "truncated") == 0) return STENO_TRUNCATED;
    if (strcmp(r, "past-range") == 0) return STENO_PAST_RANGE;
    if (strcmp(r, "surrogate-pair") == 0) return STENO_SURROGATE_PAIR;
    if (strcmp(r, "invalid-trail") == 0) return STENO_INVALID_TRAIL;
    return -1;
}

static void check_file(const char *path) {
    static char line[1 << 20];
    static uint32_t cps[1 << 17], got[1 << 17];
    static uint8_t bytes[1 << 19], enc[1 << 19];
    static uint16_t u16[1 << 18];
    unsigned long nvalid = 0, ninvalid = 0, nnav = 0;
    FILE *f = fopen(path, "rb");

    if (f == NULL) {
        CHECKF(0, "cannot open %s", path);
        return;
    }

    while (fgets(line, sizeof line, f) != NULL) {
        char *fields[5] = {NULL, NULL, NULL, NULL, NULL}, *p = line;
        int nf = 0;
        size_t len = strlen(line);

        while (len > 0 && (line[len - 1] == '\n' || line[len - 1] == '\r')) line[--len] = 0;
        if (len == 0 || line[0] == '#') continue;

        fields[nf++] = p;
        while ((p = strchr(p, '\t')) != NULL && nf < 5) {
            *p++ = 0;
            fields[nf++] = p;
        }

        if (strcmp(fields[0], "valid") == 0 && nf >= 4) {
            size_t n = parse_cps(fields[2], cps), nb = parse_bytes(fields[3], bytes), olen = 0, err = 0, gn = 0, nu;
            steno_status s;

            nvalid++;
            CHECKF(orc_encode(cps, n, enc, &olen, &err) == 0 && olen == nb && memcmp(enc, bytes, nb) == 0, "oracle encode %s", fields[1]);
            CHECKF(orc_decode(bytes, nb, got, &gn, &err) == 0 && gn == n && memcmp(got, cps, n * 4) == 0, "oracle decode %s", fields[1]);

            s = steno_encode_code_points(cps, n, enc, sizeof enc, &olen);
            CHECKF(s == STENO_OK && olen == nb && memcmp(enc, bytes, nb) == 0, "encode code points %s", fields[1]);
            nu = to_utf16(cps, n, u16);
            s = steno_encode_utf16(u16, nu, enc, sizeof enc, &olen);
            CHECKF(s == STENO_OK && olen == nb && memcmp(enc, bytes, nb) == 0, "encode UTF-16 %s", fields[1]);
            s = steno_decode_code_points(bytes, nb, got, sizeof got / 4, &gn);
            CHECKF(s == STENO_OK && gn == n && memcmp(got, cps, n * 4) == 0, "decode code points %s", fields[1]);
        } else if (strcmp(fields[0], "invalid") == 0 && nf >= 4) {
            size_t nb = parse_bytes(fields[2], bytes), gn = 0, err = 0, ln = 0;
            int want = reason_of(fields[3]), os = orc_decode(bytes, nb, got, &gn, &err);

            ninvalid++;
            CHECKF(os == want, "oracle on invalid %s: %d, the file says %d", fields[1], os, want);
            CHECKF((int)steno_decode_code_points(bytes, nb, got, sizeof got / 4, &ln) == want && ln == err, "decode invalid %s: at %u",
                   fields[1], (unsigned)ln);
            CHECKF((int)steno_decode_utf16(bytes, nb, u16, sizeof u16 / 2, &ln) == want && ln == err, "decode UTF-16 invalid %s", fields[1]);
            CHECKF((int)steno_decoded_len_code_points(bytes, nb, &ln) == want && ln == err, "decoded length invalid %s", fields[1]);
        } else if (strcmp(fields[0], "not-a-value") == 0 && nf >= 3) {
            size_t n = parse_cps(fields[2], cps), olen = 0, err = 0, ln = 0;

            nnav++;
            CHECKF(orc_encode(cps, n, enc, &olen, &err) == STENO_NOT_A_VALUE, "oracle on not-a-value %s", fields[1]);
            CHECKF(steno_encode_code_points(cps, n, enc, sizeof enc, &ln) == STENO_NOT_A_VALUE && ln == err, "not-a-value %s", fields[1]);
        } else {
            CHECKF(0, "a line of an unknown kind: %.60s", fields[0]);
        }
    }

    fclose(f);
    printf("%s: %lu valid, %lu invalid, %lu not-a-value, against the oracle\n", path, nvalid, ninvalid, nnav);
}

int main(int argc, char **argv) {
    const char *large = getenv("STENO_LARGE_VECTORS");

    check_file(argc > 1 ? argv[1] : "../../TestVectors/steno-v1-vectors.txt");
    if (large != NULL && large[0] != 0) check_file(large);
    return harness_report("oracle vectors");
}
