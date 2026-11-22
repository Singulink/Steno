//! The kernels, written once over the primitive operations of the module that includes this file (`x86.rs` or `neon.rs`).
//!
//! Each kernel reads and writes whole blocks that lie inside its input and output slices, stops at the first code it does not handle, and
//! returns how far it got. A block is always written whole, so the output past the progress may have been written over.

use super::*;
use crate::simd::tables::{lead_bytes, DECODE_WIDE, DECODE_WIDE_COUNT, ENCODE_WIDE, GATHER16, SPREAD4, UTF8_OUT};
use crate::simd::{Params, Progress, DIV31};

/// The first `N` elements of `s` from `at` as an array reference, if they are there.
#[inline(always)]
fn block<T, const N: usize>(s: &[T], at: usize) -> Option<&[T; N]> {
    s.get(at..at.checked_add(N)?)?.try_into().ok()
}

#[inline(always)]
fn block_mut<T, const N: usize>(s: &mut [T], at: usize) -> Option<&mut [T; N]> {
    s.get_mut(at..at.checked_add(N)?)?.try_into().ok()
}

/// The number of set bits from bit 0 before the first clear bit, at most `n`.
#[inline(always)]
fn prefix(bits: u32, n: u32) -> u32 {
    (!bits).trailing_zeros().min(n)
}

#[inline(always)]
fn low_bits(n: u32) -> u32 {
    if n >= 32 {
        u32::MAX
    } else {
        (1 << n) - 1
    }
}

/// The 16-bit-lane mask of `v` as eight bits.
#[cfg_attr(target_arch = "x86_64", target_feature(enable = "ssse3"))]
#[cfg_attr(target_arch = "aarch64", target_feature(enable = "neon"))]
#[inline]
fn mask16(v: V16) -> u32 {
    mask8(narrow_mask16(v, v)) & 0xFF
}

/// The classes of 1-byte codes and the deltas that map code points to their bytes (`c - delta`) and back (`b + delta`), as vectors of
/// 16-bit lanes for code points or for bytes.
struct Singles {
    ascii_start: V16,
    ascii_count: V16,
    a_start: V16,
    a_count: V16,
    b_start: V16,
    b_count: V16,
    c_start: V16,
    c_count: V16,
    control_start: V16,
    control_count: V16,
    cr: V16,
    ascii_delta: V16,
    a_delta: V16,
    b_delta: V16,
    c_delta: V16,
    control_delta: V16,
    cr_delta: V16,
}

/// The classes of each lane: (1-byte code, code that changes the confidence or the pending window, delta).
struct Classes {
    single: V16,
    effect: V16,
    delta: V16,
}

impl Singles {
    /// For code points (`bytes` false) or for the bytes of 1-byte codes widened to 16 bits (`bytes` true).
    #[cfg_attr(target_arch = "x86_64", target_feature(enable = "ssse3"))]
    #[cfg_attr(target_arch = "aarch64", target_feature(enable = "neon"))]
    #[inline]
    fn new(p: &Params, bytes: bool) -> Singles {
        let (ascii, a, b, control, cr) =
            if bytes { (0x07, u16::from(p.a_first), u16::from(p.b_first), 0x02, 0x05) } else { (0x20, p.a_start, p.b_start, 0x09, 0x0D) };
        let c = if bytes { u16::from(p.c_first) } else { p.c_start };

        Singles {
            ascii_start: splat16(ascii),
            ascii_count: splat16(p.ascii_count),
            a_start: splat16(a),
            a_count: splat16(p.a_count),
            b_start: splat16(b),
            b_count: splat16(p.b_count),
            c_start: splat16(c),
            c_count: splat16(p.c_count),
            control_start: splat16(control),
            control_count: splat16(2),
            cr: splat16(cr),
            ascii_delta: splat16(0x19),
            a_delta: splat16(p.a_start.wrapping_sub(u16::from(p.a_first))),
            b_delta: splat16(p.b_start.wrapping_sub(u16::from(p.b_first))),
            c_delta: splat16(p.c_start.wrapping_sub(u16::from(p.c_first))),
            control_delta: splat16(7),
            cr_delta: splat16(8),
        }
    }

    /// The common case first: (ASCII, range A) masks and the delta for a block that has nothing else.
    #[cfg_attr(target_arch = "x86_64", target_feature(enable = "ssse3"))]
    #[cfg_attr(target_arch = "aarch64", target_feature(enable = "neon"))]
    #[inline]
    fn quick(&self, v: V16) -> (V16, V16, V16) {
        let ascii = lt16(sub16(v, self.ascii_start), self.ascii_count);
        let a = lt16(sub16(v, self.a_start), self.a_count);
        (or16(ascii, a), a, sel16(ascii, self.ascii_delta, self.a_delta))
    }

    #[cfg_attr(target_arch = "x86_64", target_feature(enable = "ssse3"))]
    #[cfg_attr(target_arch = "aarch64", target_feature(enable = "neon"))]
    #[inline]
    fn classify(&self, v: V16) -> Classes {
        let ascii = lt16(sub16(v, self.ascii_start), self.ascii_count);
        let a = lt16(sub16(v, self.a_start), self.a_count);
        let b = lt16(sub16(v, self.b_start), self.b_count);
        let c = lt16(sub16(v, self.c_start), self.c_count);
        let control = lt16(sub16(v, self.control_start), self.control_count);
        let cr = eq16(v, self.cr);
        let effect = or16(a, b);
        let single = or16(or16(or16(ascii, effect), c), or16(control, cr));
        let delta = sel16(
            ascii,
            self.ascii_delta,
            sel16(a, self.a_delta, sel16(b, self.b_delta, sel16(c, self.c_delta, sel16(cr, self.cr_delta, self.control_delta)))),
        );
        Classes { single, effect, delta }
    }
}

/// Encodes UTF-16 code units: runs of 1-byte codes, or the codes of a wide block.
#[cfg_attr(target_arch = "x86_64", target_feature(enable = "ssse3"))]
#[cfg_attr(target_arch = "aarch64", target_feature(enable = "neon"))]
pub(in crate::simd) fn encode_utf16(p: &Params, src: &[u16], dst: &mut [u8]) -> Progress {
    if p.wide() {
        return encode_wide(p, src, dst);
    }

    let k = Singles::new(p, false);
    let mut pr = Progress::NONE;
    let mut stopped = false;

    while let (Some(lo), Some(hi)) = (block::<u16, 8>(src, pr.read), block::<u16, 8>(src, pr.read + 8)) {
        let Some(out) = block_mut::<u8, 16>(dst, pr.written) else {
            break;
        };

        let (lo, hi) = (load_u16(lo), load_u16(hi));
        let ((ql, al, dl), (qh, ah, dh)) = (k.quick(lo), k.quick(hi));

        if mask8(narrow_mask16(ql, qh)) == 0xFFFF {
            store_u8(out, narrow16(sub16(lo, dl), sub16(hi, dh)));
            pr.read += 16;
            pr.written += 16;
            pr.effect += mask8(narrow_mask16(al, ah)).count_ones();
            continue;
        }

        let (cl, ch) = (k.classify(lo), k.classify(hi));
        let bytes = narrow16(sub16(lo, cl.delta), sub16(hi, ch.delta));
        let n = prefix(mask8(narrow_mask16(cl.single, ch.single)), 16);
        let effect = mask8(narrow_mask16(cl.effect, ch.effect)) & low_bits(n);

        store_u8(out, bytes);
        pr.read += n as usize;
        pr.written += n as usize;
        pr.effect += effect.count_ones();

        if n < 16 {
            stopped = true;
            break;
        }
    }

    if !stopped {
        if let (Some(rest), Some(out)) = (src.get(pr.read..), dst.get_mut(pr.written..)) {
            let t = encode_singles_tail(&k, rest, out);
            pr.read += t.read;
            pr.written += t.written;
            pr.effect += t.effect;
        }
    }

    pr
}

/// Four code units as the low eight bytes of a vector.
#[inline(always)]
fn pack4(a: &[u16; 4]) -> u64 {
    u64::from(a[0]) | (u64::from(a[1]) << 16) | (u64::from(a[2]) << 32) | (u64::from(a[3]) << 48)
}

#[inline(always)]
fn unpack4(x: u64) -> [u16; 4] {
    [x as u16, (x >> 16) as u16, (x >> 32) as u16, (x >> 48) as u16]
}

/// Encodes a run of 1-byte codes of 4 to 15 code units, the whole of `src` if it is one: two vectors of 8 (or 4) code units, which overlap
/// when there are fewer than 16 (or 8), read without going past `src`; the bytes are written the same way, inside `dst`.
#[cfg_attr(target_arch = "x86_64", target_feature(enable = "ssse3"))]
#[cfg_attr(target_arch = "aarch64", target_feature(enable = "neon"))]
fn encode_singles_tail(k: &Singles, src: &[u16], dst: &mut [u8]) -> Progress {
    let n = src.len();

    if !(4..16).contains(&n) || dst.len() < n {
        return Progress::NONE;
    }

    let half = if n >= 8 { 8 } else { 4 };
    let (va, vb) = if half == 8 {
        match (block::<u16, 8>(src, 0), block::<u16, 8>(src, n - 8)) {
            (Some(a), Some(b)) => (load_u16(a), load_u16(b)),
            _ => return Progress::NONE,
        }
    } else {
        match (block::<u16, 4>(src, 0), block::<u16, 4>(src, n - 4)) {
            (Some(a), Some(b)) => (as16(from_u64(pack4(a))), as16(from_u64(pack4(b)))),
            _ => return Progress::NONE,
        }
    };

    let (ca, cb) = (k.classify(va), k.classify(vb));
    let lanes = low_bits(half as u32);
    let shift = (n - half) as u32;
    let single = (mask16(ca.single) & lanes) | ((mask16(cb.single) & lanes) << shift);
    let m = prefix(single, n as u32);
    let effect = ((mask16(ca.effect) & lanes) | ((mask16(cb.effect) & lanes) << shift)) & low_bits(m);
    let ba = low_u64(narrow16(sub16(va, ca.delta), sub16(va, ca.delta))).to_le_bytes();
    let bb = low_u64(narrow16(sub16(vb, cb.delta), sub16(vb, cb.delta))).to_le_bytes();

    // The second write overlaps the first with the same bytes; past `m` the bytes are written over later.
    dst[..half].copy_from_slice(&ba[..half]);
    dst[n - half..n].copy_from_slice(&bb[..half]);
    Progress { read: m as usize, written: m as usize, effect: effect.count_ones() }
}

/// Encodes blocks of eight code units that are all 1-byte codes or code points of the wide block, as long as they come.
#[cfg_attr(target_arch = "x86_64", target_feature(enable = "ssse3"))]
#[cfg_attr(target_arch = "aarch64", target_feature(enable = "neon"))]
fn encode_wide(p: &Params, src: &[u16], dst: &mut [u8]) -> Progress {
    let k = Singles::new(p, false);
    let wide_start = splat16(p.wide_start);
    let wide_count = splat16(p.wide_count);
    let wide_r0 = splat16(p.wide_r0);
    let wide_lead = splat16(u16::from(p.wide_lead));
    let div31 = splat16(DIV31);
    let eight = splat16(8);
    let mut pr = Progress::NONE;

    while let Some(input) = block::<u16, 8>(src, pr.read) {
        let Some(out) = block_mut::<u8, 16>(dst, pr.written) else {
            break;
        };

        let v = load_u16(input);
        let c = k.classify(v);
        let wide = lt16(sub16(v, wide_start), wide_count);

        // The code units before the first one that is neither a 1-byte code nor in the wide block.
        let n = prefix(mask16(or16(c.single, wide)), 8);

        if n == 0 {
            break;
        }

        // offset / 248 is ((offset >> 3) * 33826) >> 20 for every 16-bit offset (see `simd::tests`): one multiply.
        let offset = sub16(v, wide_r0);
        let q = shr16::<4>(mulhi16(shr16::<3>(offset), div31));
        // trail = offset - 248 * q + 8 = offset - 256 * q + 8 * q + 8
        let trail = add16(add16(sub16(offset, shl16_8(q)), shl16::<3>(q)), eight);
        let pair = or16(add16(q, wide_lead), shl16_8(trail));
        let words = sel16(wide, pair, sub16(v, c.delta));

        let singles = mask16(c.single);
        let packed = shuffle8(as8(words), load_u8(&ENCODE_WIDE[singles as usize]));

        // The bytes of the first `n` code units: 1 each, and 1 more for each one of the wide block.
        let kept = low_bits(n);
        let count = n + (mask16(wide) & kept).count_ones();

        store_u8(out, packed);

        pr.read += n as usize;
        pr.written += count as usize;
        pr.effect += (mask16(or16(c.effect, wide)) & kept).count_ones();

        if n < 8 {
            break;
        }
    }

    pr
}

/// Decodes bytes into UTF-16 code units: runs of 1-byte codes, or the codes of a wide block.
#[cfg_attr(target_arch = "x86_64", target_feature(enable = "ssse3"))]
#[cfg_attr(target_arch = "aarch64", target_feature(enable = "neon"))]
pub(in crate::simd) fn decode_utf16(p: &Params, src: &[u8], dst: &mut [u16]) -> Progress {
    if p.wide() {
        return decode_wide(p, src, dst);
    }

    let k = Singles::new(p, true);
    let mut pr = Progress::NONE;
    let mut stopped = false;

    while let Some(input) = block::<u8, 16>(src, pr.read) {
        let Some(out) = block_mut::<u16, 16>(dst, pr.written) else {
            break;
        };

        let v = load_u8(input);
        let (lo, hi) = (widen_lo(v), widen_hi(v));
        let ((ql, al, dl), (qh, ah, dh)) = (k.quick(lo), k.quick(hi));

        if mask8(narrow_mask16(ql, qh)) == 0xFFFF {
            let (olo, ohi) = out.split_at_mut(8);

            if let (Ok(olo), Ok(ohi)) = (<&mut [u16; 8]>::try_from(olo), <&mut [u16; 8]>::try_from(ohi)) {
                store_u16(olo, add16(lo, dl));
                store_u16(ohi, add16(hi, dh));
            }

            pr.read += 16;
            pr.written += 16;
            pr.effect += mask8(narrow_mask16(al, ah)).count_ones();
            continue;
        }

        let (cl, ch) = (k.classify(lo), k.classify(hi));
        let n = prefix(mask8(narrow_mask16(cl.single, ch.single)), 16);
        let effect = mask8(narrow_mask16(cl.effect, ch.effect)) & low_bits(n);
        let (ulo, uhi) = (add16(lo, cl.delta), add16(hi, ch.delta));

        let (olo, ohi) = out.split_at_mut(8);

        if let (Ok(olo), Ok(ohi)) = (<&mut [u16; 8]>::try_from(olo), <&mut [u16; 8]>::try_from(ohi)) {
            store_u16(olo, ulo);
            store_u16(ohi, uhi);
        }

        pr.read += n as usize;
        pr.written += n as usize;
        pr.effect += effect.count_ones();

        if n < 16 {
            stopped = true;
            break;
        }
    }

    if !stopped {
        if let (Some(rest), Some(out)) = (src.get(pr.read..), dst.get_mut(pr.written..)) {
            let t = decode_singles_tail(&k, rest, out);
            pr.read += t.read;
            pr.written += t.written;
            pr.effect += t.effect;
        }
    }

    pr
}

/// Decodes a run of 1-byte codes of 4 to 15 bytes, the whole of `src` if it is one, as `encode_singles_tail` encodes one.
#[cfg_attr(target_arch = "x86_64", target_feature(enable = "ssse3"))]
#[cfg_attr(target_arch = "aarch64", target_feature(enable = "neon"))]
fn decode_singles_tail(k: &Singles, src: &[u8], dst: &mut [u16]) -> Progress {
    let n = src.len();

    if !(4..16).contains(&n) || dst.len() < n {
        return Progress::NONE;
    }

    let half = if n >= 8 { 8 } else { 4 };
    let word = |at: usize| -> Option<u64> {
        if half == 8 {
            Some(u64::from_le_bytes(*block::<u8, 8>(src, at)?))
        } else {
            Some(u64::from(u32::from_le_bytes(*block::<u8, 4>(src, at)?)))
        }
    };

    let (Some(a), Some(b)) = (word(0), word(n - half)) else {
        return Progress::NONE;
    };

    let (va, vb) = (widen_lo(from_u64(a)), widen_lo(from_u64(b)));
    let (ca, cb) = (k.classify(va), k.classify(vb));
    let lanes = low_bits(half as u32);
    let shift = (n - half) as u32;
    let single = (mask16(ca.single) & lanes) | ((mask16(cb.single) & lanes) << shift);
    let m = prefix(single, n as u32);
    let effect = ((mask16(ca.effect) & lanes) | ((mask16(cb.effect) & lanes) << shift)) & low_bits(m);
    let (ua, ub) = (add16(va, ca.delta), add16(vb, cb.delta));

    if half == 8 {
        if let Some(out) = block_mut::<u16, 8>(dst, 0) {
            store_u16(out, ua);
        }

        if let Some(out) = block_mut::<u16, 8>(dst, n - 8) {
            store_u16(out, ub);
        }
    } else {
        dst[..4].copy_from_slice(&unpack4(low_u64(as8(ua))));
        dst[n - 4..n].copy_from_slice(&unpack4(low_u64(as8(ub))));
    }

    Progress { read: m as usize, written: m as usize, effect: effect.count_ones() }
}

/// The classes of 1-byte codes, on the bytes.
struct SingleBytes {
    ascii_first: V8,
    ascii_count: V8,
    a_first: V8,
    a_count: V8,
    b_first: V8,
    b_count: V8,
    c_first: V8,
    c_count: V8,
    control_first: V8,
    two: V8,
    cr: V8,
}

impl SingleBytes {
    #[cfg_attr(target_arch = "x86_64", target_feature(enable = "ssse3"))]
    #[cfg_attr(target_arch = "aarch64", target_feature(enable = "neon"))]
    #[inline]
    fn new(p: &Params) -> SingleBytes {
        SingleBytes {
            ascii_first: splat8(0x07),
            ascii_count: splat8(p.ascii_count as u8),
            a_first: splat8(p.a_first),
            a_count: splat8(p.a_count.min(255) as u8),
            b_first: splat8(p.b_first),
            b_count: splat8(p.b_count.min(255) as u8),
            c_first: splat8(p.c_first),
            c_count: splat8(p.c_count.min(255) as u8),
            control_first: splat8(0x02),
            two: splat8(2),
            cr: splat8(0x05),
        }
    }

    /// The mask of the bytes that are 1-byte codes.
    #[cfg_attr(target_arch = "x86_64", target_feature(enable = "ssse3"))]
    #[cfg_attr(target_arch = "aarch64", target_feature(enable = "neon"))]
    #[inline]
    fn singles(&self, v: V8) -> V8 {
        or8(
            or8(lt8(sub8(v, self.ascii_first), self.ascii_count), lt8(sub8(v, self.a_first), self.a_count)),
            or8(
                or8(lt8(sub8(v, self.b_first), self.b_count), lt8(sub8(v, self.c_first), self.c_count)),
                or8(lt8(sub8(v, self.control_first), self.two), eq8(v, self.cr)),
            ),
        )
    }
}

/// Decodes 1-byte codes and pairs under the full lead bytes of the wide block (whose 248 code points all lie in the block), as long as they
/// come. See `tables::lead_bytes` for how the lead bytes are told from the trail bytes.
#[cfg_attr(target_arch = "x86_64", target_feature(enable = "ssse3"))]
#[cfg_attr(target_arch = "aarch64", target_feature(enable = "neon"))]
fn decode_wide(p: &Params, src: &[u8], dst: &mut [u16]) -> Progress {
    let bytes = SingleBytes::new(p);
    let full_lead = splat8(p.full_lead);
    let full_count = splat8(p.full_count);
    let eight8 = splat8(8);

    let k = Singles::new(p, true);
    let pair_delta = splat16(p.pair_delta);
    let zero16 = splat16(0);
    let mut pr = Progress::NONE;

    // Looks at 32 bytes at a time; a group of eight that starts at byte 23 reads up to byte 39.
    'windows: while let Some(window) = block::<u8, 40>(src, pr.read) {
        let (Some(first), Some(second)) = (block::<u8, 16>(window, 0), block::<u8, 16>(window, 16)) else {
            break;
        };

        let (first, second) = (load_u8(first), load_u8(second));
        let may_lead = mask8(lt8(sub8(first, full_lead), full_count)) | (mask8(lt8(sub8(second, full_lead), full_count)) << 16);
        let singles = mask8(bytes.singles(first)) | (mask8(bytes.singles(second)) << 16);
        // Bytes below 08 (00 included), which are never trail bytes.
        let lows = mask8(lt8(first, eight8)) | (mask8(lt8(second, eight8)) << 16);

        let leads = lead_bytes(may_lead);
        let trails = leads << 1;

        // Stop at a code that is neither a 1-byte code nor a pair under a full lead byte, and at a lead byte whose trail byte is below 08. Byte 31
        // is left out, since a lead byte there takes byte 32 with it.
        let stops = (!(may_lead | singles) & !trails) | ((lows & trails) >> 1);
        let limit = stops.trailing_zeros().min(31) as usize;
        let mut pos = 0;

        while pos + 8 <= limit {
            let Some(out) = block_mut::<u16, 8>(dst, pr.written) else {
                pr.read += pos;
                break 'windows;
            };

            let Some(data) = block::<u8, 16>(window, pos) else {
                break;
            };

            let group = ((leads >> pos) & 0xFF) as usize;
            let units = as16(shuffle8(load_u8(data), load_u8(&DECODE_WIDE[group])));
            let upper = shr16_8(units);
            let is_single = eq16(upper, zero16);
            let c = k.classify(units);
            // A pair is lead * 256 + trail, and its code point that minus 8 times the lead, plus a constant of the range (§14.3).
            let chars = sel16(is_single, add16(units, c.delta), add16(sub16(units, shl16::<3>(upper)), pair_delta));
            store_u16(out, chars);

            let count = u32::from(DECODE_WIDE_COUNT[group]);
            let single_bits = mask16(is_single);
            let effect = ((!single_bits & 0xFF) | (mask16(c.effect) & single_bits)) & low_bits(count);

            pr.written += count as usize;
            pr.effect += effect.count_ones();
            pos += 8 + (group >> 7);
        }

        pr.read += pos;

        if pos == 0 || limit < 31 {
            break;
        }
    }

    pr
}

/// The length of the prefix of `src`, in whole blocks of 16 and the valid start of the block after them, whose bytes are all `02`, `03`,
/// `05` or `07`..`65`.
#[cfg_attr(target_arch = "x86_64", target_feature(enable = "ssse3"))]
#[cfg_attr(target_arch = "aarch64", target_feature(enable = "neon"))]
pub(in crate::simd) fn ascii_text_prefix(src: &[u8]) -> usize {
    let seven = splat8(0x07);
    let count = splat8(0x5F);
    let two = splat8(0x02);
    let five = splat8(0x05);
    let mut i = 0;

    while let Some(input) = block::<u8, 16>(src, i) {
        let v = load_u8(input);
        let ok = or8(or8(lt8(sub8(v, seven), count), lt8(sub8(v, two), two)), eq8(v, five));
        let n = prefix(mask8(ok), 16) as usize;
        i += n;

        if n < 16 {
            break;
        }
    }

    i
}

/// Converts UTF-8 to UTF-16 code units, from byte `start` up to byte `end` of `src`, 16 bytes at a time, as long as the sequences have 1 to
/// 3 bytes (code points below U+10000); stops before a sequence of 4 bytes. The bytes must be valid UTF-8 (they come from a `str`) and
/// `start` must be at the start of a sequence. A last block shorter than 16 bytes is read as the 16 bytes that end at `end`, with the bytes
/// before `start` left out, so nothing outside `src` is read. Returns the bytes read from `start` and the code units written.
#[cfg_attr(target_arch = "x86_64", target_feature(enable = "ssse3"))]
#[cfg_attr(target_arch = "aarch64", target_feature(enable = "neon"))]
pub(in crate::simd) fn utf8_to_utf16(src: &[u8], start: usize, end: usize, dst: &mut [u16]) -> Progress {
    let (x80, xc0, xe0, xf0, xf8) = (splat8(0x80), splat8(0xC0), splat8(0xE0), splat8(0xF0), splat8(0xF8));
    let (w80, wc0, we0, wf0, w1f, w3f, w0f) =
        (splat16(0x80), splat16(0xC0), splat16(0xE0), splat16(0xF0), splat16(0x1F), splat16(0x3F), splat16(0x0F));
    let mut i = start;
    let mut w = 0;

    while i < end && end <= src.len() && src.len() >= 16 {
        if dst.len() < w + 16 {
            break;
        }

        // The block: the 16 bytes from `i`; or, near the end, the last 16 bytes before `end` (or the first 16 of `src`, if `end` is less
        // than 16), with the bytes before `i` and from `end` on left out.
        let (at, skip) = if end - i >= 16 {
            (i, 0)
        } else if end >= 16 {
            (end - 16, 16 - (end - i))
        } else {
            (0, i)
        };
        let Some(input) = block::<u8, 16>(src, at) else {
            break;
        };

        let v = load_u8(input);
        let (a, b) = (prev1(v), prev2(v));
        let ascii = lt8(v, x80);
        let cont = eq8(and8(v, xc0), x80);
        let end2 = and8(cont, eq8(and8(a, xe0), xc0));
        let end3 = and8(and8(cont, eq8(and8(a, xc0), x80)), eq8(and8(b, xf0), xe0));
        let done = !low_bits(skip as u32);
        let four = mask8(eq8(and8(v, xf8), xf0)) & done;
        let limit = (if four == 0 { 16 } else { four.trailing_zeros() }).min((end - at) as u32);
        let ends = mask8(or8(ascii, or8(end2, end3))) & done & low_bits(limit);

        if ends == 0 {
            break;
        }

        // The block ends after its last complete sequence.
        let last = 32 - ends.leading_zeros();

        for h in 0..2 {
            let (w0, w1, w2) = if h == 0 { (widen_lo(v), widen_lo(a), widen_lo(b)) } else { (widen_hi(v), widen_hi(a), widen_hi(b)) };
            let c2 = or16(shl16::<6>(and16(w1, w1f)), and16(w0, w3f));
            let c3 = or16(or16(shl16::<12>(and16(w2, w0f)), shl16::<6>(and16(w1, w3f))), and16(w0, w3f));
            let is3 = eq16(and16(w2, wf0), we0);
            let is2 = eq16(and16(w1, we0), wc0);
            let units = sel16(lt16(w0, w80), w0, sel16(is3, c3, sel16(is2, c2, w0)));

            let keep = (ends >> (8 * h)) & 0xFF;
            let Some(out) = block_mut::<u16, 8>(dst, w) else {
                break;
            };

            store_u16(out, as16(shuffle8(as8(units), load_u8(&GATHER16[keep as usize]))));
            w += keep.count_ones() as usize;
        }

        i = at + last as usize;

        if four != 0 || at + 16 >= end {
            break;
        }
    }

    Progress { read: i - start, written: w, effect: 0 }
}

/// Converts UTF-16 code units to UTF-8, eight at a time, as long as no surrogate comes. Returns the code units read and the bytes written.
#[cfg_attr(target_arch = "x86_64", target_feature(enable = "ssse3"))]
#[cfg_attr(target_arch = "aarch64", target_feature(enable = "neon"))]
pub(in crate::simd) fn utf16_to_utf8(src: &[u16], dst: &mut [u8]) -> Progress {
    let (w7f, w7ff, w800, wd800) = (splat16(0x7F), splat16(0x7FF), splat16(0x800), splat16(0xD800));
    let (w80, w3f, wc0, we0) = (splat16(0x80), splat16(0x3F), splat16(0xC0), splat16(0xE0));
    let mut pr = Progress::NONE;

    while let Some(input) = block::<u16, 8>(src, pr.read) {
        if dst.len() < pr.written + 32 {
            break;
        }

        let u = load_u16(input);

        if mask16(lt16(sub16(u, wd800), w800)) != 0 {
            break;
        }

        let len2 = lt16(w7f, u);
        let len3 = lt16(w7ff, u);
        let (m2, m3) = (mask16(len2) & 0xFF, mask16(len3) & 0xFF);

        let b0 = sel16(len3, or16(we0, shr16::<12>(u)), sel16(len2, or16(wc0, shr16::<6>(u)), u));
        let b1 = or16(w80, sel16(len3, and16(shr16::<6>(u), w3f), and16(u, w3f)));
        let b2 = or16(w80, and16(u, w3f));
        let (r1, r2) = (narrow16(b0, b1), narrow16(b2, b2));

        for half in 0..2 {
            let index = usize::from(SPREAD4[((m2 >> (4 * half)) & 0xF) as usize] + SPREAD4[((m3 >> (4 * half)) & 0xF) as usize]);
            let control = &UTF8_OUT[half][index];
            let bytes = or8(shuffle8(r1, load_u8(&control[0])), shuffle8(r2, load_u8(&control[1])));
            let len = 4 + ((m2 >> (4 * half)) & 0xF).count_ones() + ((m3 >> (4 * half)) & 0xF).count_ones();

            if let Some(out) = block_mut::<u8, 16>(dst, pr.written) {
                store_u8(out, bytes);
            }

            pr.written += len as usize;
        }

        pr.read += 8;
    }

    pr
}
