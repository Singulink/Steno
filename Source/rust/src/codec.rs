//! The encoding loop (FORMAT.md §8) and the decoding loop (§9), generic over the form of the text and over where the output goes.

use alloc::string::String;
use alloc::vec::Vec;
use core::convert::Infallible;

use crate::cache;
use crate::error::{DecodeError, DecodeReason, EncodeError, EncodeErrorKind};
use crate::layout::{self, lead_base, lead_hi, lead_size, Layout, LEAD_EFFECT};
use crate::simd::{self, Progress};
use crate::state::{State, CJK, LETTERS, NONE};

// ---------- Output of the encoder ----------

/// The output is full.
pub(crate) struct Full;

/// Where the encoder writes bytes.
pub(crate) trait ByteSink {
    fn push(&mut self, b: u8) -> Result<(), Full>;
    fn push_slice(&mut self, b: &[u8]) -> Result<(), Full>;

    /// Writes the code points U+0020..U+007E in `run` as their bytes, `c - 0x19`.
    fn ascii_run<T: Copy + Into<u32>>(&mut self, run: &[T]) -> Result<(), Full>;

    /// Lets `f` write up to `max` bytes (at least [`simd::MIN_ROOM`] if there is room) into the output, and keeps what it says it wrote.
    fn fill(&mut self, max: usize, f: impl FnOnce(&mut [u8]) -> Progress) -> Progress;

    /// Writes the first `k` (1 or 2) bytes of `b`.
    #[inline(always)]
    fn push_code2(&mut self, b: [u8; 2], k: usize) -> Result<(), Full> {
        if k == 1 {
            self.push(b[0])
        } else {
            self.push_slice(&b)
        }
    }

    /// Writes the first `k` bytes of `b`, with a copy of fixed length for each `k`.
    #[inline(always)]
    fn push_code(&mut self, b: [u8; 4], k: usize) -> Result<(), Full> {
        match k {
            1 => self.push(b[0]),
            2 => self.push_slice(&b[..2]),
            3 => self.push_slice(&b[..3]),
            _ => self.push_slice(&b),
        }
    }
}

/// Returns the length of the run at the start of `s` of values in `lo..=hi`. Checks 16 values at a time where it can, in a form that the
/// compiler can vectorize.
#[inline(always)]
pub(crate) fn run_len<T: Copy + Into<u32>>(s: &[T], lo: u32, hi: u32) -> usize {
    let span = hi - lo;

    // Most runs in text other than English are one space long: test the first value alone before the loops.
    match s.first() {
        Some(&x) if x.into().wrapping_sub(lo) <= span => {}
        _ => return 0,
    }

    let mut n = 0;

    while let Some(chunk) = s.get(n..n + 16) {
        if chunk.iter().fold(false, |bad, &x| bad | (x.into().wrapping_sub(lo) > span)) {
            break;
        }

        n += 16;
    }

    while let Some(&x) = s.get(n) {
        if x.into().wrapping_sub(lo) > span {
            break;
        }

        n += 1;
    }

    n
}

impl ByteSink for Vec<u8> {
    #[inline(always)]
    fn push(&mut self, b: u8) -> Result<(), Full> {
        Vec::push(self, b);
        Ok(())
    }

    #[inline(always)]
    fn push_slice(&mut self, b: &[u8]) -> Result<(), Full> {
        self.extend_from_slice(b);
        Ok(())
    }

    #[inline(always)]
    fn ascii_run<T: Copy + Into<u32>>(&mut self, run: &[T]) -> Result<(), Full> {
        self.extend(run.iter().map(|&c| (c.into() - 0x19) as u8));
        Ok(())
    }

    #[inline(always)]
    fn fill(&mut self, max: usize, f: impl FnOnce(&mut [u8]) -> Progress) -> Progress {
        let len = self.len();
        self.resize(len + max, 0);
        let p = f(&mut self[len..]);
        self.truncate(len + p.written);
        p
    }
}

/// A caller's slice and the number of elements written to it.
pub(crate) struct SliceSink<'a, T> {
    pub buf: &'a mut [T],
    pub pos: usize,
}

impl<'a, T: Copy> SliceSink<'a, T> {
    pub fn new(buf: &'a mut [T]) -> Self {
        SliceSink { buf, pos: 0 }
    }

    #[inline(always)]
    fn put(&mut self, v: T) -> Result<(), Full> {
        match self.buf.get_mut(self.pos) {
            Some(slot) => {
                *slot = v;
                self.pos += 1;
                Ok(())
            }
            None => Err(Full),
        }
    }

    #[inline(always)]
    fn put_slice(&mut self, v: &[T]) -> Result<(), Full> {
        match self.buf.get_mut(self.pos..self.pos + v.len()) {
            Some(slot) => {
                slot.copy_from_slice(v);
                self.pos += v.len();
                Ok(())
            }
            None => Err(Full),
        }
    }
}

impl ByteSink for SliceSink<'_, u8> {
    #[inline(always)]
    fn push(&mut self, b: u8) -> Result<(), Full> {
        self.put(b)
    }

    #[inline(always)]
    fn push_slice(&mut self, b: &[u8]) -> Result<(), Full> {
        self.put_slice(b)
    }

    #[inline(always)]
    fn ascii_run<T: Copy + Into<u32>>(&mut self, run: &[T]) -> Result<(), Full> {
        let dst = self.buf.get_mut(self.pos..self.pos + run.len()).ok_or(Full)?;

        for (d, &c) in dst.iter_mut().zip(run) {
            *d = (c.into() - 0x19) as u8;
        }

        self.pos += run.len();
        Ok(())
    }

    #[inline(always)]
    fn fill(&mut self, _max: usize, f: impl FnOnce(&mut [u8]) -> Progress) -> Progress {
        let p = f(self.buf.get_mut(self.pos..).unwrap_or_default());
        self.pos += p.written;
        p
    }
}

/// Counts the bytes instead of writing them.
pub(crate) struct Counter(pub usize);

impl ByteSink for Counter {
    #[inline(always)]
    fn push(&mut self, _: u8) -> Result<(), Full> {
        self.0 += 1;
        Ok(())
    }

    #[inline(always)]
    fn push_slice(&mut self, b: &[u8]) -> Result<(), Full> {
        self.0 += b.len();
        Ok(())
    }

    #[inline(always)]
    fn ascii_run<T: Copy + Into<u32>>(&mut self, run: &[T]) -> Result<(), Full> {
        self.0 += run.len();
        Ok(())
    }

    #[inline(always)]
    fn fill(&mut self, max: usize, f: impl FnOnce(&mut [u8]) -> Progress) -> Progress {
        let mut buf = [0u8; FILL_MAX];
        let p = f(&mut buf[..max.min(FILL_MAX)]);
        self.0 += p.written;
        p
    }
}

/// The most bytes or units that one call of a kernel writes into a growable output. Smaller calls waste less when a run is short; the
/// callers call again while the runs go on.
pub(crate) const FILL_MAX: usize = 512;

/// The bytes of a code point that takes 1 or 2 bytes and leaves the layout as it is (FORMAT.md §14.2, and the wide blocks of §14.3), with
/// whether it changes the confidence or the pending window: U+0020..U+007E (or U+0020..U+0040), tab, line feed, carriage return, range A
/// of the layout and its wide block. `None` for every other code point.
#[inline(always)]
fn fast_code(lay: &Layout, c: u32) -> Option<([u8; 2], usize, u32)> {
    if c.wrapping_sub(0x20) <= lay.ascii_hi - 0x20 {
        Some(([(c - 0x19) as u8, 0], 1, 0))
    } else if c.wrapping_sub(lay.one_lo) < lay.one_len {
        Some(([lay.one_first + (c - lay.one_lo) as u8, 0], 1, 1))
    } else if c.wrapping_sub(lay.wide_lo) < lay.wide_len {
        let o = c - lay.wide_r0;
        let q = o / 248;
        Some(([lay.wide_first + q as u8, (o - q * 248 + 8) as u8], 2, 1))
    } else if c.wrapping_sub(u32::from(lay.vec.b_start)) < u32::from(lay.vec.b_count) {
        // U+3000..U+300F in the CJK context: 1 byte, and they end the pending window.
        Some(([lay.vec.b_first + (c - u32::from(lay.vec.b_start)) as u8, 0], 1, 1))
    } else if c.wrapping_sub(u32::from(lay.vec.c_start)) < u32::from(lay.vec.c_count) {
        // U+FF00..U+FF1F in the CJK context with the kana slot on letters: 1 byte, and no change to the state.
        Some(([lay.vec.c_first + (c - u32::from(lay.vec.c_start)) as u8, 0], 1, 0))
    } else {
        match c {
            0x09 | 0x0A => Some(([(c - 7) as u8, 0], 1, 0)),
            0x0D => Some(([0x05, 0], 1, 0)),
            _ => None,
        }
    }
}

/// How many code points a fast run handles one at a time before it tries the vector code again.
const SCALAR_STRETCH: usize = 16;

// ---------- Input of the encoder ----------

/// The code points of the text to encode.
pub(crate) trait Source {
    type Error;

    /// Returns the next code point, or `None` at the end.
    fn next_cp(&mut self) -> Result<Option<u32>, Self::Error>;

    /// Reads the code points in U+0020..=`hi` that come next, and writes them as their bytes, `c - 0x19`. A source may read none.
    #[inline(always)]
    fn printable_run<K: ByteSink>(&mut self, _hi: u32, _out: &mut K) -> Result<(), Full> {
        Ok(())
    }

    /// True if `fast_run` reads every fast code point that comes next, so that the code point after a fast run is never one.
    const FAST_RUN: bool = false;

    /// Reads the code points that come next and that `fast_code` handles, and writes their bytes. Returns how many of them change the
    /// confidence or the pending window. A source may read none.
    #[inline(always)]
    fn fast_run<K: ByteSink>(&mut self, _lay: &Layout, _out: &mut K) -> Result<u32, Full> {
        Ok(0)
    }
}

/// The code points of a `str`.
pub(crate) struct StrSource<'a> {
    pub chars: core::str::Chars<'a>,
}

impl<'a> StrSource<'a> {
    pub fn new(text: &'a str) -> Self {
        StrSource { chars: text.chars() }
    }
}

impl Source for StrSource<'_> {
    type Error = Infallible;

    const FAST_RUN: bool = true;

    #[inline(always)]
    fn next_cp(&mut self) -> Result<Option<u32>, Infallible> {
        Ok(self.chars.next().map(u32::from))
    }

    #[inline(always)]
    fn printable_run<K: ByteSink>(&mut self, hi: u32, out: &mut K) -> Result<(), Full> {
        let rest = self.chars.as_str();
        let n = run_len(rest.as_bytes(), 0x20, hi);

        if n > 0 {
            out.ascii_run(&rest.as_bytes()[..n])?;
            // The run is ASCII, so `n` is on a char boundary.
            self.chars = rest.get(n..).unwrap_or_default().chars();
        }

        Ok(())
    }

    #[inline(always)]
    fn fast_run<K: ByteSink>(&mut self, lay: &Layout, out: &mut K) -> Result<u32, Full> {
        let mut effect = 0;

        // With the vector code: the UTF-8 converted to UTF-16 a block at a time on the stack, then encoded as UTF-16 is.
        if simd::active()
            && !lay.vec.wide()
            && self.chars.as_str().len() >= STR_BLOCKS_MIN
            && self.chars.clone().next().is_some_and(|ch| fast_code(lay, u32::from(ch)).is_some())
        {
            effect += self.fast_blocks(lay, out)?;
        }

        loop {
            let before = self.chars.clone();

            let Some(ch) = self.chars.next() else {
                return Ok(effect);
            };

            let c = u32::from(ch);

            if c.wrapping_sub(0x20) <= lay.ascii_hi - 0x20 {
                // A run of U+0020..U+007E, in a loop that the compiler vectorizes.
                let rest = before.as_str();
                let n = run_len(rest.as_bytes(), 0x20, lay.ascii_hi);
                out.ascii_run(&rest.as_bytes()[..n])?;
                // The run is ASCII, so `n` is on a char boundary.
                self.chars = rest.get(n..).unwrap_or_default().chars();
                continue;
            }

            let Some((bytes, k, e)) = fast_code(lay, c) else {
                self.chars = before;
                return Ok(effect);
            };

            out.push_code2(bytes, k)?;
            effect += e;
        }
    }
}

impl StrSource<'_> {
    /// The fast run of `fast_run` with the vector code: converts the UTF-8 to UTF-16 a block at a time, and encodes the code units as
    /// `Utf16Source` does, up to the first code unit that is not a fast code. Only for layouts without a wide block (`fast_run` checks it):
    /// there, every fast code takes 1 byte.
    #[inline(always)]
    fn fast_blocks<K: ByteSink>(&mut self, lay: &Layout, out: &mut K) -> Result<u32, Full> {
        debug_assert!(!lay.vec.wide());
        let text = self.chars.as_str();
        let b = text.as_bytes();
        let mut i = 0;
        let mut effect = 0;

        let result = 'blocks: loop {
            // A smaller block where the runs are short (Japanese changes the kana slot every few characters).
            let mut units = [0u16; 128];
            let units = if lay.vec.wide() { &mut units[..32] } else { &mut units[..] };
            let t = simd::utf8_to_utf16(b, i, units);

            if t.written == 0 {
                break Ok(());
            }

            let u = &units[..t.written];
            let p = out.fill((u.len() + simd::MIN_ROOM).min(FILL_MAX), |buf| simd::encode_utf16(&lay.vec, u, buf));
            let mut k = p.read;
            effect += p.effect;

            while let Some(&unit) = u.get(k) {
                let Some((bytes, n, e)) = fast_code(lay, u32::from(unit)) else {
                    break;
                };

                if let Err(full) = out.push_code2(bytes, n) {
                    i += utf8_len(&u[..k]);
                    break 'blocks Err(full);
                }

                effect += e;
                k += 1;
            }

            if k < u.len() {
                i += utf8_len(&u[..k]);
                break Ok(());
            }

            i += t.read;
        };

        // `i` is at the start of a code point.
        self.chars = text.get(i..).unwrap_or_default().chars();
        result.map(|()| effect)
    }
}

/// The fewest code units left for which the vector code of a wide block pays: measured on 16-character values, shorter runs of Chinese,
/// Japanese and Korean text are faster one code point at a time. (In a `str`, those are always one code point at a time: the conversion
/// to UTF-16 does not pay there.)
const WIDE_MIN: usize = 32;

/// The fewest UTF-8 bytes left in a `str` for which the conversion to UTF-16 in blocks pays, and twice the fewest Steno bytes left for which
/// decoding to UTF-8 through UTF-16 in blocks pays: measured on 16-character values, shorter runs are faster one code point at a time.
const STR_BLOCKS_MIN: usize = 64;

/// The number of UTF-8 bytes of code units below U+10000 that are not surrogates.
#[inline(always)]
fn utf8_len(units: &[u16]) -> usize {
    units.iter().map(|&u| 1 + usize::from(u >= 0x80) + usize::from(u >= 0x800)).sum()
}

/// The code points of UTF-16 code units, as FORMAT.md §3 reads them: a high surrogate followed by a low surrogate is one code point, and
/// every other code unit is the code point of the same value.
pub(crate) struct Utf16Source<'a> {
    pub units: &'a [u16],
    pub i: usize,
}

impl Source for Utf16Source<'_> {
    type Error = Infallible;

    const FAST_RUN: bool = true;

    #[inline(always)]
    fn next_cp(&mut self) -> Result<Option<u32>, Infallible> {
        let Some(&u) = self.units.get(self.i) else {
            return Ok(None);
        };

        self.i += 1;
        let c = u32::from(u);

        if (0xD800..=0xDBFF).contains(&c) {
            if let Some(&low) = self.units.get(self.i) {
                if (0xDC00..=0xDFFF).contains(&low) {
                    self.i += 1;
                    return Ok(Some(0x1_0000 + ((c - 0xD800) << 10) + (u32::from(low) - 0xDC00)));
                }
            }
        }

        Ok(Some(c))
    }

    #[inline(always)]
    fn printable_run<K: ByteSink>(&mut self, hi: u32, out: &mut K) -> Result<(), Full> {
        let rest = self.units.get(self.i..).unwrap_or_default();
        let n = run_len(rest, 0x20, hi);

        if n > 0 {
            out.ascii_run(&rest[..n])?;
            self.i += n;
        }

        Ok(())
    }

    #[inline(always)]
    fn fast_run<K: ByteSink>(&mut self, lay: &Layout, out: &mut K) -> Result<u32, Full> {
        let units = self.units;
        let mut i = self.i;
        let mut effect = 0;
        let per_unit = if lay.vec.wide() { 2 } else { 1 };

        let vector = simd::active();

        let result = 'run: loop {
            // Blocks, when the next code unit can start one (a value of 4 to 15 code units is one block).
            if vector
                && units.len() - i >= if lay.vec.wide() { WIDE_MIN } else { simd::MIN_TAIL }
                && fast_code(lay, u32::from(units[i])).is_some()
            {
                let rest = &units[i..];
                let p = out.fill((rest.len() * per_unit + simd::MIN_ROOM).min(FILL_MAX), |buf| simd::encode_utf16(&lay.vec, rest, buf));
                i += p.read;
                effect += p.effect;
            }

            // Then one at a time, up to the next code unit that is not a fast code.
            for _ in 0..SCALAR_STRETCH {
                let Some(&u) = units.get(i) else {
                    break 'run Ok(());
                };

                if u32::from(u).wrapping_sub(0x20) <= lay.ascii_hi - 0x20 {
                    // A run of U+0020..U+007E, in a loop that the compiler vectorizes.
                    let run = &units[i..];
                    let m = run_len(run, 0x20, lay.ascii_hi);

                    if let Err(full) = out.ascii_run(&run[..m]) {
                        break 'run Err(full);
                    }

                    i += m;
                    continue;
                }

                let Some((bytes, k, e)) = fast_code(lay, u32::from(u)) else {
                    break 'run Ok(());
                };

                if let Err(full) = out.push_code2(bytes, k) {
                    break 'run Err(full);
                }

                effect += e;
                i += 1;
            }
        };

        self.i = i;
        result.map(|()| effect)
    }
}

/// Code points, checked: each must be at most U+10FFFF, and a low surrogate must not follow a high surrogate.
pub(crate) struct CodePointSource<'a> {
    pub cps: &'a [u32],
    pub i: usize,
    /// The code point before was a high surrogate.
    pub prev_high: bool,
}

impl Source for CodePointSource<'_> {
    type Error = EncodeError;

    #[inline(always)]
    fn next_cp(&mut self) -> Result<Option<u32>, EncodeError> {
        let Some(&c) = self.cps.get(self.i) else {
            return Ok(None);
        };

        if c > 0x10_FFFF {
            return Err(EncodeError::new(EncodeErrorKind::NotACodePoint, self.i));
        }

        let low = (0xDC00..=0xDFFF).contains(&c);

        if low && self.prev_high {
            return Err(EncodeError::new(EncodeErrorKind::NotAValue, self.i));
        }

        self.prev_high = (0xD800..=0xDBFF).contains(&c);
        self.i += 1;
        Ok(Some(c))
    }
}

// ---------- The encoder ----------

/// Why the encoder stopped early.
pub(crate) enum EncodeStop<E> {
    Full,
    Source(E),
}

impl<E> From<Full> for EncodeStop<E> {
    #[inline(always)]
    fn from(_: Full) -> Self {
        EncodeStop::Full
    }
}

/// Encodes the code points of `src` from state `st`, and leaves `st` at the state after them.
pub(crate) fn encode<S: Source, K: ByteSink>(st: &mut State, src: &mut S, out: &mut K) -> Result<(), EncodeStop<S::Error>> {
    // U+0020..U+007E take 1 byte, `c - 0x19`, in every layout but the CJK layouts with the kana slot on hiragana or katakana, and they do
    // not change the state. Many values start with such a run, or are nothing else: write it without looking up a layout.
    if !(st.ctx == CJK && st.slot != LETTERS) {
        src.printable_run(0x7E, out)?;
    }

    let first = match src.next_cp() {
        Ok(Some(c)) => c,
        Ok(None) => return Ok(()),
        Err(e) => return Err(EncodeStop::Source(e)),
    };

    let mut key = st.key();
    let mut first = Some(first);

    // While the layouts are shared ones, no thread cache is needed.
    while let Some(lay) = cache::shared(key) {
        match encode_run(lay, st, src, out, first.take())? {
            Some(next) => key = next,
            None => return Ok(()),
        }
    }

    cache::with_cache(|cache| {
        let mut slot = None;
        // A layout that is not in the cache is first used for one code point from its ranges alone (`encode_one`), which is cheap; it is
        // built when the state is still the same after that code point. So text that reaches a new state at every code point never
        // builds a layout.
        let mut light = None;

        loop {
            let lay = match cache::shared(key) {
                Some(lay) => lay,
                None => {
                    let s = match cache.find(slot, key) {
                        Some(s) => s,
                        None if light == Some(key) => cache.slot(slot, key),
                        None => {
                            light = Some(key);

                            match encode_one(key, st, src, out, first.take())? {
                                Some(next) => {
                                    key = next;
                                    continue;
                                }
                                None => return Ok(()),
                            }
                        }
                    };

                    slot = Some(s);
                    cache.at(s)
                }
            };

            match encode_run(lay, st, src, out, first.take())? {
                Some(next) => key = next,
                None => return Ok(()),
            }
        }
    })
}

/// Encodes one code point with the layout of `key` built as ranges only, and returns the key of the state after it (`None` at the end of
/// the input).
#[inline(never)]
fn encode_one<S: Source, K: ByteSink>(
    key: u64,
    st: &mut State,
    src: &mut S,
    out: &mut K,
    first: Option<u32>,
) -> Result<Option<u64>, EncodeStop<S::Error>> {
    let c = match first {
        Some(c) => c,
        None => match src.next_cp() {
            Ok(Some(c)) => c,
            Ok(None) => return Ok(None),
            Err(e) => return Err(EncodeStop::Source(e)),
        },
    };

    let r = layout::ranges_of_key(key).find(c);
    let (bytes, k) = layout::encode_offset(r.first, r.size, c - r.lo);
    out.push_code(bytes, k)?;
    st.update(c, k);
    Ok(Some(st.key()))
}

/// Encodes until the layout changes (returns the key of the new layout) or the input ends (returns `None`).
#[inline(always)]
fn encode_run<S: Source, K: ByteSink>(
    mut lay: &Layout,
    st: &mut State,
    src: &mut S,
    out: &mut K,
    first: Option<u32>,
) -> Result<Option<u64>, EncodeStop<S::Error>> {
    // A code point that was read before the layout was looked up goes the general way.
    if let Some(c) = first {
        let (bytes, k) = lay.encode(c);
        out.push_code(bytes, k)?;
        st.update(c, k);
        let next = st.key();

        if next != lay.key {
            match cache::shared(next) {
                Some(shared) => lay = shared,
                None => return Ok(Some(next)),
            }
        }
    }

    // The confidence and the pending window are the only fields that the fast paths change (FORMAT.md §14.2). Each fast path sets both:
    // the one that the context does not use does not matter (§5).
    let mut conf = st.conf;
    let mut pending = st.pending;

    let result = loop {
        // The code points that leave the layout as it is, in a run.
        match src.fast_run(lay, out) {
            Ok(0) => {}
            Ok(e) => {
                conf = (u32::from(conf) + e).min(2) as u8;
                pending = NONE;
            }
            Err(e) => break Err(e.into()),
        }

        let c = match src.next_cp() {
            Ok(Some(c)) => c,
            Ok(None) => break Ok(None),
            Err(e) => break Err(EncodeStop::Source(e)),
        };

        // A source without a fast run gives the fast code points here. (After a fast run, `c` is not one.)
        if !S::FAST_RUN {
            if let Some((bytes, k, e)) = fast_code(lay, c) {
                if let Err(e) = out.push_code2(bytes, k) {
                    break Err(e.into());
                }

                if e > 0 {
                    conf = if conf < 2 { conf + 1 } else { 2 };
                    pending = NONE;
                }

                continue;
            }
        }

        // Everything else: look up the range, then update the whole state.
        let (bytes, k) = lay.encode(c);

        if let Err(e) = out.push_code(bytes, k) {
            break Err(e.into());
        }

        st.conf = conf;
        st.pending = pending;

        if st.update_moves(c, k) {
            let next = st.key();

            if next != lay.key {
                // A shared layout is taken here, without leaving the loop; any other is looked up by the caller.
                match cache::shared(next) {
                    Some(shared) => lay = shared,
                    None => return Ok(Some(next)),
                }
            }
        }

        conf = st.conf;
        pending = st.pending;
    };

    st.conf = conf;
    st.pending = pending;
    result
}

// ---------- Output of the decoder ----------

/// Why a decoder output cannot take a code point.
pub(crate) enum SinkError {
    Full,
    /// A surrogate code point for an output that has no form for it.
    Surrogate,
}

impl From<Full> for SinkError {
    #[inline(always)]
    fn from(_: Full) -> Self {
        SinkError::Full
    }
}

/// Where the decoder writes code points.
pub(crate) trait CodePointSink {
    /// Writes a code point below U+0080.
    fn ascii(&mut self, c: u8) -> Result<(), SinkError>;
    /// Writes any code point.
    fn code_point(&mut self, c: u32) -> Result<(), SinkError>;

    /// Writes the code points of a run of Steno bytes `07`..`65`, each `b + 0x19`.
    fn ascii_run(&mut self, run: &[u8]) -> Result<(), SinkError>;

    /// True if the output is UTF-16, which the vector code writes directly.
    const UTF16: bool = false;

    /// Lets `f` write up to `max` UTF-16 code units (at least [`simd::MIN_ROOM`] if there is room), all below U+10000 and none a surrogate,
    /// and writes them to the output.
    #[inline(always)]
    fn fill_units(&mut self, max: usize, f: impl FnOnce(&mut [u16]) -> Progress) -> Result<Progress, SinkError> {
        let mut buf = [0u16; FILL_MAX];
        let p = f(&mut buf[..max.min(FILL_MAX)]);

        for &u in buf.get(..p.written).unwrap_or_default() {
            if u < 0x80 {
                self.ascii(u as u8)?;
            } else {
                self.code_point(u32::from(u))?;
            }
        }

        Ok(p)
    }
}

impl CodePointSink for String {
    #[inline(always)]
    fn ascii(&mut self, c: u8) -> Result<(), SinkError> {
        self.push(char::from(c));
        Ok(())
    }

    #[inline(always)]
    fn code_point(&mut self, c: u32) -> Result<(), SinkError> {
        match char::from_u32(c) {
            Some(ch) => {
                self.push(ch);
                Ok(())
            }
            None => Err(SinkError::Surrogate),
        }
    }

    #[inline(always)]
    fn ascii_run(&mut self, run: &[u8]) -> Result<(), SinkError> {
        let mut buf = [0u8; 64];

        for chunk in run.chunks(64) {
            let buf = &mut buf[..chunk.len()];

            for (d, &b) in buf.iter_mut().zip(chunk) {
                *d = b + 0x19;
            }

            // The bytes are U+0020..U+007E, so they are always valid UTF-8.
            self.push_str(core::str::from_utf8(buf).unwrap_or_default());
        }

        Ok(())
    }

    #[cfg(feature = "simd")]
    #[inline(always)]
    fn fill_units(&mut self, max: usize, f: impl FnOnce(&mut [u16]) -> Progress) -> Result<Progress, SinkError> {
        let mut units = [0u16; UNITS_MAX];
        let p = f(&mut units[..max.min(UNITS_MAX)]);
        let units = units.get(..p.written).unwrap_or_default();
        let mut utf8 = [0u8; UNITS_MAX * 3 + 32];
        let e = simd::utf16_to_utf8(units, &mut utf8);
        simd::push_utf8(self, utf8.get(..e.written).unwrap_or_default());

        for &u in units.get(e.read..).unwrap_or_default() {
            self.code_point(u32::from(u))?;
        }

        Ok(p)
    }
}

/// Writes UTF-8 into a caller's slice.
pub(crate) struct Utf8SliceSink<'a>(pub SliceSink<'a, u8>);

impl CodePointSink for Utf8SliceSink<'_> {
    #[inline(always)]
    fn ascii(&mut self, c: u8) -> Result<(), SinkError> {
        Ok(self.0.put(c)?)
    }

    #[inline(always)]
    fn code_point(&mut self, c: u32) -> Result<(), SinkError> {
        if c < 0x80 {
            Ok(self.0.put(c as u8)?)
        } else if c < 0x800 {
            Ok(self.0.put_slice(&[0xC0 | (c >> 6) as u8, 0x80 | (c & 0x3F) as u8])?)
        } else if c < 0x1_0000 {
            if (0xD800..=0xDFFF).contains(&c) {
                return Err(SinkError::Surrogate);
            }

            Ok(self.0.put_slice(&[0xE0 | (c >> 12) as u8, 0x80 | ((c >> 6) & 0x3F) as u8, 0x80 | (c & 0x3F) as u8])?)
        } else {
            Ok(self.0.put_slice(&[
                0xF0 | (c >> 18) as u8,
                0x80 | ((c >> 12) & 0x3F) as u8,
                0x80 | ((c >> 6) & 0x3F) as u8,
                0x80 | (c & 0x3F) as u8,
            ])?)
        }
    }

    #[inline(always)]
    fn ascii_run(&mut self, run: &[u8]) -> Result<(), SinkError> {
        let s = &mut self.0;
        let dst = s.buf.get_mut(s.pos..s.pos + run.len()).ok_or(SinkError::Full)?;

        for (d, &b) in dst.iter_mut().zip(run) {
            *d = b + 0x19;
        }

        s.pos += run.len();
        Ok(())
    }

    #[inline(always)]
    fn fill_units(&mut self, max: usize, f: impl FnOnce(&mut [u16]) -> Progress) -> Result<Progress, SinkError> {
        let mut units = [0u16; UNITS_MAX];
        let p = f(&mut units[..max.min(UNITS_MAX)]);
        let units = units.get(..p.written).unwrap_or_default();
        let e = simd::utf16_to_utf8(units, self.0.buf.get_mut(self.0.pos..).unwrap_or_default());
        self.0.pos += e.written;

        for &u in units.get(e.read..).unwrap_or_default() {
            self.code_point(u32::from(u))?;
        }

        Ok(p)
    }
}

/// The most code units that a UTF-8 output takes from the vector code at a time.
const UNITS_MAX: usize = 128;

/// Counts UTF-8 bytes.
pub(crate) struct Utf8Counter(pub usize);

impl CodePointSink for Utf8Counter {
    #[inline(always)]
    fn ascii(&mut self, _: u8) -> Result<(), SinkError> {
        self.0 += 1;
        Ok(())
    }

    #[inline(always)]
    fn code_point(&mut self, c: u32) -> Result<(), SinkError> {
        self.0 += char::from_u32(c).ok_or(SinkError::Surrogate)?.len_utf8();
        Ok(())
    }

    #[inline(always)]
    fn fill_units(&mut self, max: usize, f: impl FnOnce(&mut [u16]) -> Progress) -> Result<Progress, SinkError> {
        // The vector code writes no surrogates.
        let mut units = [0u16; UNITS_MAX];
        let p = f(&mut units[..max.min(UNITS_MAX)]);
        self.0 += utf8_len(units.get(..p.written).unwrap_or_default());
        Ok(p)
    }

    #[inline(always)]
    fn ascii_run(&mut self, run: &[u8]) -> Result<(), SinkError> {
        self.0 += run.len();
        Ok(())
    }
}

#[inline(always)]
fn utf16_units(c: u32) -> ([u16; 2], usize) {
    if c < 0x1_0000 {
        ([c as u16, 0], 1)
    } else {
        let v = c - 0x1_0000;
        ([0xD800 + (v >> 10) as u16, 0xDC00 + (v & 0x3FF) as u16], 2)
    }
}

impl CodePointSink for Vec<u16> {
    #[inline(always)]
    fn ascii(&mut self, c: u8) -> Result<(), SinkError> {
        self.push(u16::from(c));
        Ok(())
    }

    #[inline(always)]
    fn code_point(&mut self, c: u32) -> Result<(), SinkError> {
        let (u, n) = utf16_units(c);

        if n == 1 {
            self.push(u[0]);
        } else {
            self.extend_from_slice(&u);
        }

        Ok(())
    }

    #[inline(always)]
    fn ascii_run(&mut self, run: &[u8]) -> Result<(), SinkError> {
        self.extend(run.iter().map(|&b| u16::from(b + 0x19)));
        Ok(())
    }

    const UTF16: bool = true;

    #[inline(always)]
    fn fill_units(&mut self, max: usize, f: impl FnOnce(&mut [u16]) -> Progress) -> Result<Progress, SinkError> {
        let len = self.len();
        self.resize(len + max, 0);
        let p = f(&mut self[len..]);
        self.truncate(len + p.written);
        Ok(p)
    }
}

impl CodePointSink for SliceSink<'_, u16> {
    #[inline(always)]
    fn ascii(&mut self, c: u8) -> Result<(), SinkError> {
        Ok(self.put(u16::from(c))?)
    }

    #[inline(always)]
    fn code_point(&mut self, c: u32) -> Result<(), SinkError> {
        let (u, n) = utf16_units(c);

        if n == 1 {
            Ok(self.put(u[0])?)
        } else {
            Ok(self.put_slice(&u)?)
        }
    }

    #[inline(always)]
    fn ascii_run(&mut self, run: &[u8]) -> Result<(), SinkError> {
        let dst = self.buf.get_mut(self.pos..self.pos + run.len()).ok_or(SinkError::Full)?;

        for (d, &b) in dst.iter_mut().zip(run) {
            *d = u16::from(b + 0x19);
        }

        self.pos += run.len();
        Ok(())
    }

    const UTF16: bool = true;

    #[inline(always)]
    fn fill_units(&mut self, _max: usize, f: impl FnOnce(&mut [u16]) -> Progress) -> Result<Progress, SinkError> {
        let p = f(self.buf.get_mut(self.pos..).unwrap_or_default());
        self.pos += p.written;
        Ok(p)
    }
}

/// Counts UTF-16 code units.
pub(crate) struct Utf16Counter(pub usize);

impl CodePointSink for Utf16Counter {
    #[inline(always)]
    fn ascii(&mut self, _: u8) -> Result<(), SinkError> {
        self.0 += 1;
        Ok(())
    }

    #[inline(always)]
    fn code_point(&mut self, c: u32) -> Result<(), SinkError> {
        self.0 += if c < 0x1_0000 { 1 } else { 2 };
        Ok(())
    }

    #[inline(always)]
    fn ascii_run(&mut self, run: &[u8]) -> Result<(), SinkError> {
        self.0 += run.len();
        Ok(())
    }
}

impl CodePointSink for Vec<u32> {
    #[inline(always)]
    fn ascii(&mut self, c: u8) -> Result<(), SinkError> {
        self.push(u32::from(c));
        Ok(())
    }

    #[inline(always)]
    fn code_point(&mut self, c: u32) -> Result<(), SinkError> {
        self.push(c);
        Ok(())
    }

    #[inline(always)]
    fn ascii_run(&mut self, run: &[u8]) -> Result<(), SinkError> {
        self.extend(run.iter().map(|&b| u32::from(b + 0x19)));
        Ok(())
    }
}

impl CodePointSink for SliceSink<'_, u32> {
    #[inline(always)]
    fn ascii(&mut self, c: u8) -> Result<(), SinkError> {
        Ok(self.put(u32::from(c))?)
    }

    #[inline(always)]
    fn code_point(&mut self, c: u32) -> Result<(), SinkError> {
        Ok(self.put(c)?)
    }

    #[inline(always)]
    fn ascii_run(&mut self, run: &[u8]) -> Result<(), SinkError> {
        let dst = self.buf.get_mut(self.pos..self.pos + run.len()).ok_or(SinkError::Full)?;

        for (d, &b) in dst.iter_mut().zip(run) {
            *d = u32::from(b + 0x19);
        }

        self.pos += run.len();
        Ok(())
    }
}

/// Counts code points.
pub(crate) struct CodePointCounter(pub usize);

impl CodePointSink for CodePointCounter {
    #[inline(always)]
    fn ascii(&mut self, _: u8) -> Result<(), SinkError> {
        self.0 += 1;
        Ok(())
    }

    #[inline(always)]
    fn code_point(&mut self, _: u32) -> Result<(), SinkError> {
        self.0 += 1;
        Ok(())
    }

    #[inline(always)]
    fn ascii_run(&mut self, run: &[u8]) -> Result<(), SinkError> {
        self.0 += run.len();
        Ok(())
    }
}

// ---------- The decoder ----------

/// The state of a decoder between code points.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct DecodeState {
    pub st: State,
    /// The last code point was a high surrogate (for rule 5).
    pub prev_high: bool,
}

impl DecodeState {
    pub const INITIAL: DecodeState = DecodeState { st: State::INITIAL, prev_high: false };
}

/// Why the decoder stopped early.
pub(crate) enum DecodeStop {
    Error(DecodeError),
    Full,
}

/// How a run of the decoder ended.
enum RunEnd {
    /// All bytes were read.
    Done,
    /// The layout changed after the code point that ends at the offset.
    Changed(usize, u64),
    /// The bytes end in the middle of the code point that starts at the offset (and the bytes present have no other defect).
    Partial(usize),
}

/// Decodes `bytes` from state `ds` into `sink`. `base` is the offset of `bytes[0]` in the value, for error positions.
///
/// Returns the number of bytes read: all of them, or, when `partial` is true and the bytes end in the middle of a code point, the offset of
/// that code point. When `partial` is false, that is the error `Truncated`.
pub(crate) fn decode<S: CodePointSink>(
    ds: &mut DecodeState,
    bytes: &[u8],
    base: usize,
    sink: &mut S,
    partial: bool,
) -> Result<usize, DecodeStop> {
    let mut i = 0;

    // As in `encode`: a run of U+0020..U+007E at the start needs no layout.
    if !(ds.st.ctx == CJK && ds.st.slot != LETTERS) {
        i = run_len(bytes, 0x07, 0x65);

        if i > 0 {
            sink.ascii_run(&bytes[..i]).map_err(|e| sink_stop(e, base))?;
            ds.prev_high = false;
        }
    }

    if i == bytes.len() {
        return Ok(i);
    }

    let mut key = ds.st.key();
    let end = |run: RunEnd, i: &mut usize, key: &mut u64| -> Result<Option<usize>, DecodeStop> {
        match run {
            RunEnd::Done => Ok(Some(bytes.len())),
            RunEnd::Changed(at, next) => {
                *i = at;
                *key = next;
                Ok(None)
            }
            RunEnd::Partial(at) if partial => Ok(Some(at)),
            RunEnd::Partial(at) => Err(DecodeStop::Error(DecodeError::new(DecodeReason::Truncated, base + at))),
        }
    };

    // While the layouts are shared ones, no thread cache is needed.
    while let Some(lay) = cache::shared(key) {
        if let Some(done) = end(decode_run(lay, ds, bytes, i, base, sink)?, &mut i, &mut key)? {
            return Ok(done);
        }
    }

    cache::with_cache(|cache| {
        let mut slot = None;
        // As in `encode`: a layout that is not in the cache is first used for one code point from its ranges alone.
        let mut light = None;

        loop {
            let run = match cache::shared(key) {
                Some(lay) => decode_run(lay, ds, bytes, i, base, sink)?,
                None => match cache.find(slot, key) {
                    Some(s) => {
                        slot = Some(s);
                        decode_run(cache.at(s), ds, bytes, i, base, sink)?
                    }
                    None if light == Some(key) => {
                        let s = cache.slot(slot, key);
                        slot = Some(s);
                        decode_run(cache.at(s), ds, bytes, i, base, sink)?
                    }
                    None => {
                        light = Some(key);
                        decode_one(key, ds, bytes, i, base, sink)?
                    }
                },
            };

            if let Some(done) = end(run, &mut i, &mut key)? {
                return Ok(done);
            }
        }
    })
}

/// Decodes one code point at `i` with the layout of `key` built as ranges only, in the order of checks of `decode_run`.
#[inline(never)]
fn decode_one<S: CodePointSink>(
    key: u64,
    ds: &mut DecodeState,
    bytes: &[u8],
    i: usize,
    base: usize,
    sink: &mut S,
) -> Result<RunEnd, DecodeStop> {
    let fail = |reason: DecodeReason| Err(DecodeStop::Error(DecodeError::new(reason, base + i)));

    let Some(&b) = bytes.get(i) else {
        return Ok(RunEnd::Done);
    };

    if b == 0 {
        return fail(DecodeReason::ZeroByte);
    }

    let Some(r) = layout::ranges_of_key(key).find_lead(b) else {
        return fail(DecodeReason::UnusedLead);
    };

    let k = usize::from(r.size);
    let trail = bytes.get(i + 1..i + k);
    let present = trail.unwrap_or(&bytes[i + 1..]);

    if let Some(&t) = present.iter().find(|&&t| t < 8) {
        return fail(if t == 0 { DecodeReason::ZeroByte } else { DecodeReason::InvalidTrail });
    }

    let Some(trail) = trail else {
        return Ok(RunEnd::Partial(i));
    };

    let mut o = u32::from(b - r.first);

    for &t in trail {
        o = o * 248 + u32::from(t - 8);
    }

    let c = r.lo + o;

    if c > r.hi {
        return fail(DecodeReason::PastRange);
    }

    if ds.prev_high && (0xDC00..=0xDFFF).contains(&c) {
        return fail(DecodeReason::SurrogatePair);
    }

    let written = if c < 0x80 { sink.ascii(c as u8) } else { sink.code_point(c) };
    written.map_err(|err| sink_stop(err, base + i))?;
    ds.prev_high = (0xD800..=0xDBFF).contains(&c);
    ds.st.update(c, k);
    Ok(RunEnd::Changed(i + k, ds.st.key()))
}

#[inline(always)]
fn decode_run<S: CodePointSink>(
    mut lay: &Layout,
    ds: &mut DecodeState,
    bytes: &[u8],
    start: usize,
    base: usize,
    sink: &mut S,
) -> Result<RunEnd, DecodeStop> {
    let mut conf = ds.st.conf;
    let mut pending = ds.st.pending;
    let mut prev_high = ds.prev_high;
    let mut i = start;

    let fail = |reason: DecodeReason, at: usize| DecodeStop::Error(DecodeError::new(reason, base + at));
    let vector = simd::active();
    // After the vector code stops, the scalar code goes on for a stretch before the vector code is tried again.
    let mut next_vector = start;

    let result = loop {
        let Some(&b) = bytes.get(i) else {
            break Ok(RunEnd::Done);
        };

        // A long run of 1-byte codes, or of the codes of a wide block, in blocks: when the output is UTF-16, or when the first code is not
        // ASCII (the scalar code below writes ASCII runs to UTF-8 at least as fast).
        let ascii = u32::from(b).wrapping_sub(0x07) <= lay.ascii_hi - 0x20;

        if vector
            && i >= next_vector
            && bytes.len() - i >= if S::UTF16 { simd::MIN_TAIL } else { STR_BLOCKS_MIN / 2 }
            && (if ascii {
                S::UTF16
            } else {
                u32::from(b.wrapping_sub(lay.one_first)) < lay.one_len || b.wrapping_sub(lay.vec.full_lead) < lay.vec.full_count
            })
        {
            let rest = &bytes[i..];

            match sink.fill_units((rest.len() + simd::MIN_ROOM).min(FILL_MAX), |buf| simd::decode_utf16(&lay.vec, rest, buf)) {
                Ok(p) if p.read > 0 => {
                    if p.read < rest.len() {
                        next_vector = i + p.read + SCALAR_STRETCH;
                    }

                    if p.effect > 0 {
                        conf = (u32::from(conf) + p.effect).min(2) as u8;
                        pending = NONE;
                    }

                    prev_high = false;
                    i += p.read;
                    continue;
                }
                Ok(_) => next_vector = i + SCALAR_STRETCH,
                Err(err) => break Err(sink_stop(err, base + i)),
            }
        }

        if ascii {
            // A run of U+0020..U+007E (or U+0020..U+0040): 1 byte each, `b + 0x19`, with no change to the state.
            let run = &bytes[i..];
            let n = run_len(run, 0x07, lay.ascii_hi - 0x19);

            if let Err(err) = sink.ascii_run(&run[..n]) {
                break Err(sink_stop(err, base + i));
            }

            prev_high = false;
            i += n;
            continue;
        }

        if u32::from(b.wrapping_sub(lay.one_first)) < lay.one_len {
            // A run of the main window (general context) or of the kana of the kana slot (CJK context): 1 byte each, never a surrogate. They
            // raise the confidence or end the pending window (see `encode_run`).
            let mut n: u32 = 0;

            while let Some(&b) = bytes.get(i) {
                let o = u32::from(b.wrapping_sub(lay.one_first));

                if o >= lay.one_len {
                    break;
                }

                if let Err(err) = sink.code_point(lay.one_lo + o) {
                    ds.st.conf = conf;
                    ds.st.pending = pending;
                    ds.prev_high = prev_high;
                    return Err(sink_stop(err, base + i));
                }

                n += 1;
                i += 1;
            }

            conf = (u32::from(conf) + n).min(2) as u8;
            pending = NONE;
            prev_high = false;
            continue;
        }

        let lead = u32::from(b.wrapping_sub(lay.wide_first));

        if lead < lay.wide_leads {
            // Probably an ideograph in the CJK context or a syllable in the Hangul context, which change only the pending window.
            if let Some(&t) = bytes.get(i + 1) {
                let c = lay.wide_r0 + lead * 248 + u32::from(t.wrapping_sub(8));

                if t >= 8 && c.wrapping_sub(lay.wide_lo) < lay.wide_len {
                    if let Err(err) = sink.code_point(c) {
                        break Err(sink_stop(err, base + i));
                    }

                    pending = NONE;
                    prev_high = false;
                    i += 2;
                    continue;
                }
            }
        }

        let e = lay.lead[usize::from(b)];
        let k = lead_size(e);

        if k == 1 {
            // A code point of 1 byte: it never changes the layout (FORMAT.md §14.2), and it is never a surrogate.
            let c = lead_base(e);

            let written = if c < 0x80 { sink.ascii(c as u8) } else { sink.code_point(c) };

            if let Err(err) = written {
                break Err(sink_stop(err, base + i));
            }

            if e & LEAD_EFFECT != 0 {
                conf = if conf < 2 { conf + 1 } else { 2 };
                pending = NONE;
            }

            prev_high = false;
            i += 1;
            continue;
        }

        if k == 0 {
            break Err(fail(if b == 0 { DecodeReason::ZeroByte } else { DecodeReason::UnusedLead }, i));
        }

        // Read the trail bytes. Each is checked as it is read (00, then 01..07), before the end of the input is (FORMAT.md §9.2 step 3):
        // so the first bad trail byte decides the reason, also when the input ends after it.
        let trail = bytes.get(i + 1..i + k);
        let present = trail.unwrap_or(&bytes[i + 1..]);

        if let Some(&t) = present.iter().find(|&&t| t < 8) {
            break Err(fail(if t == 0 { DecodeReason::ZeroByte } else { DecodeReason::InvalidTrail }, i));
        }

        let Some(trail) = trail else {
            break Ok(RunEnd::Partial(i));
        };

        let mut o: u32 = 0;

        for &t in trail {
            o = o * 248 + u32::from(t - 8);
        }

        let c = lead_base(e) + o;

        if c > lead_hi(e) {
            break Err(fail(DecodeReason::PastRange, i));
        }

        if prev_high && (0xDC00..=0xDFFF).contains(&c) {
            break Err(fail(DecodeReason::SurrogatePair, i));
        }

        if let Err(err) = sink.code_point(c) {
            break Err(sink_stop(err, base + i));
        }

        prev_high = (0xD800..=0xDBFF).contains(&c);
        i += k;

        if c.wrapping_sub(lay.wide_lo) < lay.wide_len {
            // An ideograph in the CJK context or a syllable in the Hangul context: only the pending window changes.
            pending = NONE;
            continue;
        }

        ds.st.conf = conf;
        ds.st.pending = pending;
        if ds.st.update_moves(c, k) {
            let next = ds.st.key();

            if next != lay.key {
                // A shared layout is taken here, without leaving the loop; any other is looked up by the caller.
                match cache::shared(next) {
                    Some(shared) => lay = shared,
                    None => {
                        ds.prev_high = prev_high;
                        return Ok(RunEnd::Changed(i, next));
                    }
                }
            }
        }

        conf = ds.st.conf;
        pending = ds.st.pending;
    };

    ds.st.conf = conf;
    ds.st.pending = pending;
    ds.prev_high = prev_high;
    result
}

#[inline(always)]
fn sink_stop(err: SinkError, at: usize) -> DecodeStop {
    match err {
        SinkError::Full => DecodeStop::Full,
        SinkError::Surrogate => DecodeStop::Error(DecodeError::new(DecodeReason::Surrogate, at)),
    }
}
