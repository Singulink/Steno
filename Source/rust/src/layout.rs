//! The layout of a state (FORMAT.md §6), with the tables that the encoder and the decoder use.

use crate::state::{window_end, CJK, GENERAL, HANGUL, HIRAGANA, KATAKANA, LETTERS};

/// A range of the layout (§6.4) with its first lead byte (§6.5).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct Range {
    pub lo: u32,
    pub hi: u32,
    pub size: u8,
    pub first: u8,
    pub leads: u8,
}

// A lead byte entry packs the first code point of the lead byte (bits 0..21), the last code point of its range (bits 21..42), the size
// (bits 42..45, 0 for an unused lead byte) and the "one-byte effect" flag (bit 45).
const HI_SHIFT: u32 = 21;
const SIZE_SHIFT: u32 = 42;
pub(crate) const LEAD_EFFECT: u64 = 1 << 45;
const CP_MASK: u64 = 0x1F_FFFF;

#[inline]
pub(crate) const fn lead_base(e: u64) -> u32 {
    (e & CP_MASK) as u32
}

#[inline]
pub(crate) const fn lead_hi(e: u64) -> u32 {
    ((e >> HI_SHIFT) & CP_MASK) as u32
}

#[inline]
pub(crate) const fn lead_size(e: u64) -> usize {
    ((e >> SIZE_SHIFT) & 7) as usize
}

/// The layout of a state, with lookup tables. It depends only on the key of the state.
#[derive(Debug)]
pub(crate) struct Layout {
    pub key: u64,
    pub ranges: Ranges,
    /// One entry per byte value, for the decoder. See the constants above.
    pub lead: [u64; 256],
    /// The last code point of the run from U+0020 that takes 1 byte (`c - 0x19`): U+007E, or U+0040 in the CJK context when the kana slot is
    /// hiragana or katakana.
    pub ascii_hi: u32,
    /// For each block of 256 code points below U+10000, the index of the range that holds its first code point.
    pub page: [u8; 256],
    /// A run of code points that take 1 byte and do not change the layout: the main window in the general context, the kana of the kana slot
    /// in the CJK context. Empty (`one_len` 0) otherwise. Code point `c` is `one_first + (c - one_lo)`.
    pub one_lo: u32,
    pub one_len: u32,
    pub one_first: u8,
    /// The ideographs U+4E00..U+9FFF in the CJK context, or the syllables U+AC00..U+D7A3 in the Hangul context: 2 bytes each, in one range
    /// that starts at `wide_r0` with first lead byte `wide_first` (§14.3). They change only the pending window. Empty in the general context.
    pub wide_lo: u32,
    pub wide_len: u32,
    pub wide_r0: u32,
    pub wide_first: u8,
    /// The number of lead bytes of that range (0 in the general context).
    pub wide_leads: u32,
    /// What the vector code needs.
    pub vec: crate::simd::Params,
}

/// The number of code points that one lead byte holds at each size: `248^(size - 1)` (§6.5).
const POW248: [u32; 4] = [1, 248, 248 * 248, 248 * 248 * 248];

impl Layout {
    /// Builds the layout of the state with the given key (see `State::key`).
    pub fn build(key: u64) -> Layout {
        let ctx = (key & 3) as u8;
        let slot = ((key >> 2) & 3) as u8;
        let m0 = ((key >> 4) & CP_MASK) as u32;
        let second = ((key >> 25) & 0x3F_FFFF) as u32;
        let ranges = build_ranges(ctx, slot, m0, if second == 0x3F_FFFF { None } else { Some(second) });

        let mut lead = [0u64; 256];

        for r in ranges.iter() {
            let per = POW248[usize::from(r.size) - 1];

            for j in 0..r.leads {
                let base = r.lo + u32::from(j) * per;
                let mut e = u64::from(base) | (u64::from(r.hi) << HI_SHIFT) | (u64::from(r.size) << SIZE_SHIFT);

                // A 1-byte code point other than ASCII raises the confidence (the main window, general context) or ends the pending window
                // (U+3000..U+300F and kana, CJK context). U+FF00..U+FF1F in the CJK context changes nothing (§7 step 4).
                if r.size == 1 && base >= 0x80 && !(ctx == CJK && (0xFF00..=0xFF1F).contains(&base)) {
                    e |= LEAD_EFFECT;
                }

                lead[usize::from(r.first + j)] = e;
            }
        }

        let mut layout = Layout {
            key,
            ranges,
            lead,
            ascii_hi: if ctx == CJK && slot != LETTERS { 0x40 } else { 0x7E },
            page: [0; 256],
            one_lo: 0,
            one_len: 0,
            one_first: 0,
            wide_lo: 0,
            wide_len: 0,
            wide_r0: 0,
            wide_first: 0,
            wide_leads: 0,
            vec: crate::simd::Params::default(),
        };

        let mut i = 0;

        for (p, entry) in layout.page.iter_mut().enumerate() {
            let c = (p as u32) << 8;

            while layout.ranges[i].hi < c {
                i += 1;
            }

            *entry = i as u8;
        }

        let one = match (ctx, slot) {
            (GENERAL, _) => Some((m0, window_end(m0))),
            (CJK, HIRAGANA) => Some((0x3041, 0x3096)),
            (CJK, KATAKANA) => Some((0x30A1, 0x30FC)),
            _ => None,
        };

        if let Some((lo, hi)) = one {
            let r = layout.range_of(lo);
            debug_assert!(r.size == 1 && r.hi >= hi);
            layout.one_lo = lo;
            layout.one_len = hi - lo + 1;
            layout.one_first = r.first + (lo - r.lo) as u8;
        }

        let wide = match ctx {
            CJK => Some((0x4E00, 0x9FFF)),
            HANGUL => Some((0xAC00, 0xD7A3)),
            _ => None,
        };

        if let Some((lo, hi)) = wide {
            let r = layout.range_of(lo);
            debug_assert!(r.size == 2 && r.hi >= hi);
            layout.wide_lo = lo;
            layout.wide_len = hi - lo + 1;
            layout.wide_r0 = r.lo;
            layout.wide_first = r.first;
            layout.wide_leads = u32::from(r.leads);
        }

        layout.vec = layout.vec_params(ctx);

        layout
    }

    fn vec_params(&self, ctx: u8) -> crate::simd::Params {
        let mut p = crate::simd::Params { ascii_count: (self.ascii_hi - 0x20 + 1) as u16, ..Default::default() };

        if self.one_len > 0 && self.one_lo + self.one_len <= 0x1_0000 {
            p.a_start = self.one_lo as u16;
            p.a_count = self.one_len as u16;
            p.a_first = self.one_first;
        }

        if ctx == CJK {
            let r = self.range_of(0x3000);
            debug_assert!(r.size == 1 && r.hi >= 0x300F);
            p.b_start = 0x3000;
            p.b_count = 16;
            p.b_first = r.first + (0x3000 - r.lo) as u8;

            if self.ascii_hi == 0x7E {
                let r = self.range_of(0xFF00);
                debug_assert!(r.size == 1 && r.hi >= 0xFF1F);
                p.c_start = 0xFF00;
                p.c_count = 0x20;
                p.c_first = r.first + (0xFF00 - r.lo) as u8;
            }
        }

        if self.wide_len > 0 {
            let r = self.range_of(self.wide_lo);
            let wide_hi = self.wide_lo + self.wide_len - 1;
            p.wide_start = self.wide_lo as u16;
            p.wide_count = self.wide_len as u16;
            p.wide_r0 = self.wide_r0 as u16;
            p.wide_lead = self.wide_first;
            // c = R0 + (lead - F) * 248 + trail - 8 = (lead * 256 + trail) - 8 * lead + (R0 - 248 * F - 8), in 16-bit arithmetic (§14.3).
            p.pair_delta = (self.wide_r0 as u16).wrapping_sub(u16::from(self.wide_first).wrapping_mul(248)).wrapping_sub(8);

            // The lead bytes all of whose 248 code points lie in the wide block; they are consecutive.
            for j in 0..r.leads {
                let base = r.lo + u32::from(j) * 248;

                if base >= self.wide_lo && base + 247 <= wide_hi {
                    if p.full_count == 0 {
                        p.full_lead = r.first + j;
                    }

                    p.full_count += 1;
                }
            }
        }

        p
    }

    /// Returns the range that holds `c` (a code point up to U+10FFFF).
    #[inline]
    pub fn range_of(&self, c: u32) -> Range {
        if c < 0x1_0000 {
            let mut i = usize::from(self.page[(c >> 8) as usize]);

            while self.ranges[i].hi < c {
                i += 1;
            }

            self.ranges[i]
        } else {
            let i = self.ranges.partition_point(|r| r.lo <= c);
            self.ranges[i - 1]
        }
    }

    /// Returns the bytes of `c` and their number (§8.2).
    #[inline]
    pub fn encode(&self, c: u32) -> ([u8; 4], usize) {
        let r = self.range_of(c);
        encode_offset(r.first, r.size, c - r.lo)
    }
}

/// A list of at most `N` elements on the stack.
#[derive(Clone, Copy, Debug)]
pub(crate) struct Fixed<T: Copy, const N: usize> {
    items: [T; N],
    len: usize,
}

impl<T: Copy, const N: usize> Fixed<T, N> {
    fn new(fill: T) -> Self {
        Fixed { items: [fill; N], len: 0 }
    }

    /// Adds an element. The callers never add more than `N` (see `build_ranges`); an element past `N` is dropped rather than panicking.
    #[inline]
    fn push(&mut self, x: T) {
        if let Some(slot) = self.items.get_mut(self.len) {
            *slot = x;
            self.len += 1;
        }
    }

    #[inline]
    fn last_mut(&mut self) -> Option<&mut T> {
        self.len.checked_sub(1).map(|i| &mut self.items[i])
    }
}

impl<T: Copy, const N: usize> core::ops::Deref for Fixed<T, N> {
    type Target = [T];

    #[inline]
    fn deref(&self) -> &[T] {
        &self.items[..self.len]
    }
}

impl<T: Copy, const N: usize> core::ops::DerefMut for Fixed<T, N> {
    #[inline]
    fn deref_mut(&mut self) -> &mut [T] {
        &mut self.items[..self.len]
    }
}

impl<T: Copy + PartialEq, const N: usize> PartialEq for Fixed<T, N> {
    fn eq(&self, other: &Self) -> bool {
        **self == **other
    }
}

/// The most ranges, pieces and cuts of a layout. A layout has at most 18 pieces (2 neighbours, 3 of piece 2, 2 of piece 3, pieces 4 to 7,
/// and at most 7 of piece 8), so at most 4 + 2 * 18 = 40 cuts, and fewer elementary intervals, parts and ranges than that.
const MAX: usize = 40;

/// The ranges of a layout, on the stack.
pub(crate) type Ranges = Fixed<Range, MAX>;

const NO_RANGE: Range = Range { lo: 0, hi: 0, size: 0, first: 0, leads: 0 };

/// Builds the ranges of a layout from its pieces (§6.2 to §6.5), without allocating: cheap enough to build for one code point.
pub(crate) fn build_ranges(ctx: u8, slot: u8, m0: u32, second: Option<u32>) -> Ranges {
    let m1 = window_end(m0);
    let mut pieces: Fixed<(u32, u32, u8), MAX> = Fixed::new((0, 0, 0));

    // 1. The neighbours, in the general context.
    if ctx == GENERAL {
        pieces.push((m0.saturating_sub(248).max(0x80), m0 - 1, 2));

        if m1 < 0x10_FFFF {
            pieces.push((m1 + 1, (m1 + 248).min(0x10_FFFF), 2));
        }
    }

    // 2.
    pieces.push((0x00, 0x1F, 2));
    pieces.push((0x09, 0x0A, 1));
    pieces.push((0x0D, 0x0D, 1));

    // 3.
    if ctx == CJK && slot != LETTERS {
        pieces.push((0x20, 0x40, 1));
        pieces.push((0x41, 0x7E, 2));
    } else {
        pieces.push((0x20, 0x7E, 1));
    }

    // 4.
    pieces.push((0x7F, 0xBF, 2));

    // 5.
    if ctx != CJK {
        pieces.push((0x80, 0x0EFF, 2));
    }

    // 6.
    pieces.push((0x2000, 0x20CF, 2));

    // 7.
    if let Some(s0) = second {
        pieces.push((s0, window_end(s0), 2));
    }

    // 8.
    match ctx {
        GENERAL => pieces.push((m0, m1, 1)),
        HANGUL => {
            pieces.push((m0, m1, 2));
            pieces.push((0x3000, 0x303F, 2));
            pieces.push((0xAC00, 0xD7A3, 2));
            pieces.push((0xFF00, 0xFFFF, 2));
        }
        _ => {
            pieces.push((m0, m1, 2));
            pieces.push((0x3000, 0x300F, 1));
            pieces.push((0x3010, 0x30FF, 2));

            if slot == HIRAGANA {
                pieces.push((0x3041, 0x3096, 1));
            } else if slot == KATAKANA {
                pieces.push((0x30A1, 0x30FC, 1));
            }

            pieces.push((0x4E00, 0x9FFF, 2));
            pieces.push((0xFF00, 0xFFFF, 2));

            if slot == LETTERS {
                pieces.push((0xFF00, 0xFF1F, 1));
            }
        }
    }

    // Cut the code points into elementary intervals at every piece boundary and at the bounds of the three runs of §6.3. Each interval is
    // either covered wholly by a piece (the last one wins) or by none.
    let mut cuts: Fixed<u32, { 2 * MAX }> = Fixed::new(0);

    for c in [0, 0x1_0FC0, 0x2_0000, 0x11_0000] {
        cuts.push(c);
    }

    for &(lo, hi, _) in pieces.iter() {
        cuts.push(lo);
        cuts.push(hi + 1);
    }

    cuts.sort_unstable();

    // The intervals between distinct cuts, sized by the last piece that covers them (0: none), joined into parts: an uncovered interval
    // joins an uncovered part before it in the same run of §6.3.
    let run = |c: u32| u8::from(c >= 0x1_0FC0) + u8::from(c >= 0x2_0000);
    let mut parts: Fixed<(u32, u32, u8), { 2 * MAX }> = Fixed::new((0, 0, 0));
    let mut prev = 0;

    for &cut in cuts.iter().skip(1) {
        if cut == prev {
            continue;
        }

        let (lo, hi) = (prev, cut - 1);
        prev = cut;
        let size = pieces.iter().rev().find(|p| p.0 <= lo && hi <= p.1).map_or(0, |p| p.2);

        if size == 0 {
            if let Some(last) = parts.last_mut() {
                if last.2 == 0 && last.1 + 1 == lo && run(last.0) == run(lo) {
                    last.1 = hi;
                    continue;
                }
            }
        }

        parts.push((lo, hi, size));
    }

    // Size the parts of the stretches, join runs of the same size into ranges, then assign the lead bytes.
    let mut ranges = Ranges::new(NO_RANGE);
    let mut lead: u32 = 1;

    for &(lo, hi, size) in parts.iter() {
        let size = if size != 0 {
            size
        } else {
            match hi - lo + 1 {
                0..=248 => 2,
                249..=61504 => 3,
                _ => 4,
            }
        };

        match ranges.last_mut() {
            Some(last) if last.size == size => last.hi = hi,
            _ => ranges.push(Range { lo, hi, size, first: 0, leads: 0 }),
        }
    }

    for r in ranges.iter_mut() {
        let n = r.hi - r.lo + 1;
        let per = POW248[usize::from(r.size) - 1];
        let leads = n.div_ceil(per);
        // §13.6: no layout needs more than 254 lead bytes, so `lead + leads - 1` is at most 0xFE.
        debug_assert!(lead + leads - 1 <= 0xFE, "a layout needs more than 254 lead bytes");
        r.first = lead as u8;
        r.leads = leads as u8;
        lead += leads;
    }

    ranges
}

impl Ranges {
    /// The range that holds `c` (a code point up to U+10FFFF), by a scan.
    #[inline]
    pub fn find(&self, c: u32) -> Range {
        self.iter().copied().find(|r| c <= r.hi).unwrap_or(NO_RANGE)
    }

    /// The range that has the lead byte `b`, if one has.
    #[inline]
    pub fn find_lead(&self, b: u8) -> Option<Range> {
        self.iter().copied().find(|r| b >= r.first && u32::from(b) < u32::from(r.first) + u32::from(r.leads))
    }
}

/// Builds the ranges of the layout with the given key (see `State::key`), without its tables: for a state that may be used for one code
/// point only.
#[inline]
pub(crate) fn ranges_of_key(key: u64) -> Ranges {
    let second = ((key >> 25) & 0x3F_FFFF) as u32;
    build_ranges(
        (key & 3) as u8,
        ((key >> 2) & 3) as u8,
        ((key >> 4) & CP_MASK) as u32,
        if second == 0x3F_FFFF { None } else { Some(second) },
    )
}

/// Returns the bytes of the code point at offset `o` of a range of size `size` with first lead byte `first`, and their number (§8.2).
#[inline]
pub(crate) fn encode_offset(first: u8, size: u8, o: u32) -> ([u8; 4], usize) {
    match size {
        1 => ([first + o as u8, 0, 0, 0], 1),
        2 => ([first + (o / 248) as u8, (o % 248 + 8) as u8, 0, 0], 2),
        3 => {
            let rest = o % 61504;
            ([first + (o / 61504) as u8, (rest / 248 + 8) as u8, (rest % 248 + 8) as u8, 0], 3)
        }
        _ => {
            // The size 4 range has one lead byte, so o / 248^3 is 0.
            let rest = o % 61504;
            ([first, (o / 61504 + 8) as u8, (rest / 248 + 8) as u8, (rest % 248 + 8) as u8], 4)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::state::{window_of, NONE};
    use alloc::vec;
    use alloc::vec::Vec;

    fn table(ranges: &[Range]) -> Vec<(u32, u32, u8, u8, u8)> {
        ranges.iter().map(|r| (r.lo, r.hi, r.size, r.first, r.first + r.leads - 1)).collect()
    }

    fn all_windows() -> Vec<u32> {
        let mut out = Vec::new();
        let mut c = 0;

        while c <= 0x10_FFFF {
            let w = window_of(c);

            if w == NONE {
                c += 1;
            } else {
                out.push(w);
                c = window_end(w) + 1;
            }
        }

        out
    }

    /// FORMAT.md §13.6, fact 2, on a sample: with a second window, a layout needs at most 254 lead bytes, and the layout that the cache
    /// builds from the key of a state (which leaves out a second window that changes nothing) is the layout of the state.
    #[test]
    fn layouts_with_a_second_window() {
        use crate::state::State;

        let windows = all_windows();
        let mut seed: u64 = 7;
        let mut next = |n: usize| {
            seed = seed.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
            (seed >> 33) as usize % n
        };
        let combos = [(GENERAL, LETTERS), (HANGUL, LETTERS), (CJK, LETTERS), (CJK, HIRAGANA), (CJK, KATAKANA)];

        // The fullest case of §13.6, and random ones, with the main window and the second window often close together.
        let mut cases = vec![(GENERAL, LETTERS, 0x1000, 0x1200), (GENERAL, LETTERS, 0x11200, 0x10E80)];

        for _ in 0..20_000 {
            let (ctx, slot) = combos[next(combos.len())];
            let i = next(windows.len());
            let j = if next(2) == 0 { (i + windows.len() - 3 + next(7)) % windows.len() } else { next(windows.len()) };

            if ctx != GENERAL || i != j {
                cases.push((ctx, slot, windows[i], windows[j]));
            }
        }

        let mut most = 0;

        for (ctx, slot, main, second) in cases {
            let ranges = build_ranges(ctx, slot, main, Some(second));
            let last = ranges[ranges.len() - 1];
            most = most.max(u32::from(last.first + last.leads - 1));

            let st = State { ctx, slot, conf: 0, main, second, pending: NONE };
            assert_eq!(Layout::build(st.key()).ranges, ranges, "{ctx} {slot} {main:X} {second:X}");
        }

        assert_eq!(most, 254);
    }

    #[test]
    fn initial_layout_matches_section_6_6() {
        let r = build_ranges(GENERAL, LETTERS, 0xC0, None);
        assert_eq!(
            table(&r),
            vec![
                (0x0000, 0x0008, 2, 0x01, 0x01),
                (0x0009, 0x000A, 1, 0x02, 0x03),
                (0x000B, 0x000C, 2, 0x04, 0x04),
                (0x000D, 0x000D, 1, 0x05, 0x05),
                (0x000E, 0x001F, 2, 0x06, 0x06),
                (0x0020, 0x007E, 1, 0x07, 0x65),
                (0x007F, 0x00BF, 2, 0x66, 0x66),
                (0x00C0, 0x013F, 1, 0x67, 0xE6),
                (0x0140, 0x0EFF, 2, 0xE7, 0xF5),
                (0x0F00, 0x1FFF, 3, 0xF6, 0xF6),
                (0x2000, 0x20CF, 2, 0xF7, 0xF7),
                (0x20D0, 0x1FFFF, 3, 0xF8, 0xF9),
                (0x20000, 0x10FFFF, 4, 0xFA, 0xFA),
            ]
        );
    }

    #[test]
    fn layouts_of_section_11_1() {
        let r = build_ranges(GENERAL, LETTERS, 0x400, Some(0x1200));
        assert_eq!(r.len(), 15);
        assert_eq!(table(&r)[6], (0x007F, 0x03FF, 2, 0x66, 0x69));
        assert_eq!(table(&r)[13], (0x20D0, 0x1FFFF, 3, 0xF9, 0xFA));
        assert_eq!(table(&r)[14], (0x20000, 0x10FFFF, 4, 0xFB, 0xFB));

        let r = build_ranges(CJK, HIRAGANA, 0xC0, None);
        assert_eq!(r.len(), 20);
        assert_eq!(table(&r)[6], (0x0041, 0x013F, 2, 0x28, 0x29));
        assert_eq!(table(&r)[15], (0x4E00, 0x9FFF, 2, 0x96, 0xEA));
        assert_eq!(table(&r)[18], (0x10000, 0x1FFFF, 3, 0xEE, 0xEF));
        assert_eq!(table(&r)[19], (0x20000, 0x10FFFF, 4, 0xF0, 0xF0));

        let r = build_ranges(HANGUL, LETTERS, 0xC0, None);
        assert_eq!(r.len(), 17);
        assert_eq!(table(&r)[12], (0xAC00, 0xD7A3, 2, 0x7A, 0xA7));
        assert_eq!(table(&r)[16], (0x20000, 0x10FFFF, 4, 0xAD, 0xAD));

        let r = build_ranges(GENERAL, LETTERS, 0x1000, Some(0x1200));
        assert_eq!(
            table(&r)[6..],
            [
                (0x007F, 0x0FFF, 2, 0x66, 0x76),
                (0x1000, 0x107F, 1, 0x77, 0xF6),
                (0x1080, 0x127F, 2, 0xF7, 0xF9),
                (0x1280, 0x1FFF, 3, 0xFA, 0xFA),
                (0x2000, 0x20CF, 2, 0xFB, 0xFB),
                (0x20D0, 0x1FFFF, 3, 0xFC, 0xFD),
                (0x20000, 0x10FFFF, 4, 0xFE, 0xFE),
            ]
        );
    }

    /// FORMAT.md §13.6, fact 1: without a second window, every layout needs at most 252 lead bytes. The assert in `build_ranges` checks 254;
    /// this checks the tighter bound and that every layout covers U+0000..U+10FFFF without gaps.
    #[test]
    fn every_layout_without_a_second_window_fits() {
        let combos = [(GENERAL, LETTERS), (HANGUL, LETTERS), (CJK, LETTERS), (CJK, HIRAGANA), (CJK, KATAKANA)];
        let mut most = 0;
        let mut w = 0xC0;

        while w != NONE {
            for &(ctx, slot) in &combos {
                let r = build_ranges(ctx, slot, w, None);
                assert_eq!(r[0].lo, 0);
                assert_eq!(r[r.len() - 1].hi, 0x10_FFFF);

                for p in r.windows(2) {
                    assert_eq!(p[0].hi + 1, p[1].lo);
                    assert_ne!(p[0].size, p[1].size);
                    assert_eq!(p[0].first + p[0].leads, p[1].first);
                }

                let last = r[r.len() - 1];
                most = most.max(u32::from(last.first + last.leads - 1));
            }

            // The next window.
            let mut c = window_end(w) + 1;
            w = NONE;

            while c <= 0x10_FFFF {
                let n = window_of(c);

                if n != NONE {
                    w = n;
                    break;
                }

                c += 1;
            }
        }

        assert_eq!(most, 252);
    }
}

#[cfg(test)]
mod size_tests {
    use super::*;
    use crate::state::State;

    /// The README states the memory of a layout: keep it true.
    #[test]
    fn a_layout_takes_under_3_kb() {
        let layout = Layout::build(State::INITIAL.key());
        let total = core::mem::size_of_val(&layout);
        assert!(total < 3000, "{total} bytes");
    }
}
