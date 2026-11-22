//! Byte shuffle tables of the wide blocks, built at compile time.

/// For encoding eight code units of a wide block: indexed by the mask of the units that take 1 byte (bit `j` for unit `j`). Unit `j` comes
/// as the 16-bit word `2j, 2j + 1` (lead byte, trail byte); the shuffle keeps byte `2j` of every unit and byte `2j + 1` of the units that
/// take 2 bytes, in order. Unused positions are 0x80, which the shuffles turn into 0.
pub(super) static ENCODE_WIDE: [[u8; 16]; 256] = encode_wide();

/// For decoding eight bytes of a wide block: indexed by the mask of the lead bytes of pairs among them (bit `p` for byte `p`, never two
/// adjacent bits). Code `k` becomes the 16-bit lane `k`: a single byte `p` as `(p, zero)`, a pair at `p` as `(p + 1, p)`, so that the lane is
/// `lead * 256 + trail`. A lead byte at position 7 takes byte 8 with it.
pub(super) static DECODE_WIDE: [[u8; 16]; 256] = decode_wide().0;

/// The number of codes that start in the eight bytes, for each mask of [`DECODE_WIDE`].
pub(super) static DECODE_WIDE_COUNT: [u8; 256] = decode_wide().1;

const fn encode_wide() -> [[u8; 16]; 256] {
    let mut table = [[0x80u8; 16]; 256];
    let mut mask = 0;

    while mask < 256 {
        let mut k = 0;
        let mut j = 0;

        while j < 8 {
            table[mask][k] = (2 * j) as u8;
            k += 1;

            if (mask >> j) & 1 == 0 {
                table[mask][k] = (2 * j + 1) as u8;
                k += 1;
            }

            j += 1;
        }

        mask += 1;
    }

    table
}

const fn decode_wide() -> ([[u8; 16]; 256], [u8; 256]) {
    let mut table = [[0x80u8; 16]; 256];
    let mut counts = [0u8; 256];
    let mut mask = 0;

    while mask < 256 {
        let mut k = 0;
        let mut p = 0;

        while p < 8 {
            if (mask >> p) & 1 == 1 {
                table[mask][2 * k] = (p + 1) as u8;
                table[mask][2 * k + 1] = p as u8;
                p += 2;
            } else {
                table[mask][2 * k] = p as u8;
                p += 1;
            }

            k += 1;
        }

        counts[mask] = k as u8;
        mask += 1;
    }

    (table, counts)
}

/// Marks the lead bytes of pairs in a run of bytes, from the mask of the bytes that can be lead bytes (`may_lead`, bit `i` for byte `i`).
///
/// A byte that cannot be a lead byte ends a pair or is a single, so the byte after it starts a code. In a run of bytes that can be lead
/// bytes, which starts at the start of a code, the first, third, fifth... bytes are lead bytes and the others are their trail bytes. The bit
/// before bit 0 counts as a byte that cannot be a lead byte. The result is exact for the bytes whose code starts within the mask.
#[inline(always)]
pub(super) const fn lead_bytes(may_lead: u32) -> u32 {
    let run_starts = may_lead & !(may_lead << 1);
    // Adding the starts at even positions carries through each run that starts at an even position and clears it, so these are the bits of
    // the runs that start at even positions. Their lead bytes are at even positions, those of the other runs at odd positions.
    let even_runs = may_lead & !may_lead.wrapping_add(run_starts & 0x5555_5555);
    (even_runs & 0x5555_5555) | (may_lead & !even_runs & 0xAAAA_AAAA)
}

/// For gathering 16-bit lanes: indexed by the mask of the lanes to keep (bit `j` for lane `j`), the byte shuffle that moves them to the
/// front, in order. Unused positions are 0x80.
pub(super) static GATHER16: [[u8; 16]; 256] = gather16();

const fn gather16() -> [[u8; 16]; 256] {
    let mut table = [[0x80u8; 16]; 256];
    let mut mask = 0;

    while mask < 256 {
        let mut k = 0;
        let mut j = 0;

        while j < 8 {
            if (mask >> j) & 1 == 1 {
                table[mask][2 * k] = (2 * j) as u8;
                table[mask][2 * k + 1] = (2 * j + 1) as u8;
                k += 1;
            }

            j += 1;
        }

        mask += 1;
    }

    table
}

/// For writing four code units as UTF-8: indexed by their lengths less one (2 bits each, unit `k` in bits `2k`), and by which four of the
/// eight code units in the registers they are (`0` for units 0..3, `1` for units 4..7). The registers hold the first bytes of the eight
/// units in bytes 0..7 and their second bytes in bytes 8..15 (`[0]`), and their third bytes in bytes 0..7 (`[1]`): the two shuffles, ORed,
/// give the UTF-8 bytes of the four units in order. Unused positions are 0x80.
pub(super) static UTF8_OUT: [[[[u8; 16]; 2]; 256]; 2] = utf8_out();

const fn utf8_out() -> [[[[u8; 16]; 2]; 256]; 2] {
    let mut table = [[[[0x80u8; 16]; 2]; 256]; 2];
    let mut half = 0;

    while half < 2 {
        let mut index = 0;

        while index < 256 {
            let mut at = 0;
            let mut k = 0;

            while k < 4 {
                let len = ((index >> (2 * k)) & 3) + 1;
                let unit = (4 * half + k) as u8;

                if len <= 3 {
                    table[half][index][0][at] = unit;
                    at += 1;

                    if len >= 2 {
                        table[half][index][0][at] = 8 + unit;
                        at += 1;
                    }

                    if len == 3 {
                        table[half][index][1][at] = unit;
                        at += 1;
                    }
                }

                k += 1;
            }

            index += 1;
        }

        half += 1;
    }

    table
}

/// Four bits spread to the even bits of a byte: bit `k` to bit `2k`.
pub(super) static SPREAD4: [u8; 16] = spread4();

const fn spread4() -> [u8; 16] {
    let mut table = [0u8; 16];
    let mut m = 0;

    while m < 16 {
        let mut k = 0;

        while k < 4 {
            if (m >> k) & 1 == 1 {
                table[m] |= 1 << (2 * k);
            }

            k += 1;
        }

        m += 1;
    }

    table
}
