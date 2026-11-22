//! Vector code for runs of the codes that do not change the layout (FORMAT.md §14.2).
//!
//! The rest of the crate calls the safe functions of this module. Each returns how far it got ([`Progress`]); a function that cannot or need
//! not run returns [`Progress::NONE`], and the scalar code goes on from there. The scalar code is complete on its own: the vector code is an
//! optional fast path.
//!
//! - With the `simd` feature on x86_64, the kernels need SSSE3, which is detected at run time with `std` (and taken from the compile-time
//!   target features without it). On little-endian aarch64 they use NEON, which every aarch64 target has. The kernels are written once, in
//!   `kernels.rs`, over a few primitive operations that `x86.rs` and `neon.rs` define with the intrinsics of each architecture.
//! - The kernels and their tables assume little-endian lane order (byte 0 is the low byte of a 16-bit lane, and so on), so big-endian
//!   aarch64 (`aarch64_be`) takes the scalar code, which is endian-neutral.
//! - Without the feature, or on another architecture, every function returns [`Progress::NONE`].
//!
//! The kernels read only whole blocks that lie inside the input and write only whole blocks that lie inside the output; the scalar code does
//! the tails. A block is written whole even when only part of it is valid, so the output past the progress may have been written over.
//!
//! Unsafe code is confined to this module, in 11 blocks: the loads and stores of `x86.rs` and `neon.rs` (4 each), which take references to
//! arrays of exactly one vector; the 2 calls of the kernels in `dispatch!` (on x86-64 after SSSE3 was detected; on aarch64, where NEON is
//! a baseline feature); and `push_utf8`, which appends UTF-8 that `utf16_to_utf8` wrote to a `String` without checking it again.

#[cfg(all(feature = "simd", any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little"))))]
mod tables;

#[cfg(all(feature = "simd", target_arch = "x86_64"))]
#[allow(unsafe_code)]
mod x86;

#[cfg(all(feature = "simd", all(target_arch = "aarch64", target_endian = "little")))]
#[allow(unsafe_code)]
mod neon;

/// What the vector code needs to know of a layout. The ranges are given as a start and a count; a count of 0 is an empty range.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(crate) struct Params {
    /// The number of code points from U+0020 that take 1 byte, `c - 0x19`: 0x5F, or 0x21 in the CJK context when the kana slot is not
    /// letters.
    pub ascii_count: u16,
    /// Range A of 1-byte codes: the main window (general context) or the kana of the kana slot (CJK context), if it is below U+10000.
    pub a_start: u16,
    pub a_count: u16,
    pub a_first: u8,
    /// Range B of 1-byte codes: U+3000..U+300F in the CJK context.
    pub b_start: u16,
    pub b_count: u16,
    pub b_first: u8,
    /// Range C of 1-byte codes that change nothing in the state: U+FF00..U+FF1F in the CJK context when the kana slot is letters
    /// (FORMAT.md §7 step 4).
    pub c_start: u16,
    pub c_count: u16,
    pub c_first: u8,
    /// The wide block: the ideographs (CJK context) or the syllables (Hangul context), 2 bytes each, and the range that holds them, which
    /// starts at `wide_r0` with lead byte `wide_lead`.
    pub wide_start: u16,
    pub wide_count: u16,
    pub wide_r0: u16,
    pub wide_lead: u8,
    /// The lead bytes all of whose 248 code points lie in the wide block (for the decoder): from `full_lead`, `full_count` of them.
    pub full_lead: u8,
    pub full_count: u8,
    /// `c = (lead * 256 + trail) - 8 * lead + pair_delta` for a pair of the wide range (wrapping in 16 bits).
    pub pair_delta: u16,
}

impl Params {
    /// True if the layout has a wide block.
    #[inline(always)]
    pub fn wide(&self) -> bool {
        self.wide_count != 0
    }
}

/// How far a kernel got: `read` units of input, `written` units of output, and `effect` codes that change the confidence or the pending
/// window (all but ASCII and the controls).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(crate) struct Progress {
    pub read: usize,
    pub written: usize,
    pub effect: u32,
}

impl Progress {
    pub const NONE: Progress = Progress { read: 0, written: 0, effect: 0 };
}

/// `x / 31 == (x * DIV31) >> 20` for every `x` below 8,192, so `o / 248 == ((o >> 3) * DIV31) >> 20` for every 16-bit `o`.
#[cfg_attr(
    not(all(feature = "simd", any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little")))),
    allow(dead_code)
)]
pub(crate) const DIV31: u16 = 33826;

/// The fewest output units that a kernel needs room for to make progress.
pub(crate) const MIN_ROOM: usize = 16;

// ---------- Detection ----------

/// Whether the vector code runs in this process: 0 not known yet, 1 off, 2 on.
#[cfg(all(feature = "simd", feature = "std", any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little"))))]
static STATE: core::sync::atomic::AtomicU8 = core::sync::atomic::AtomicU8::new(0);

/// A cached branch: one relaxed load after the first call.
#[cfg(all(feature = "simd", feature = "std", any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little"))))]
#[inline(always)]
fn enabled() -> bool {
    match STATE.load(core::sync::atomic::Ordering::Relaxed) {
        2 => true,
        1 => false,
        _ => detect(),
    }
}

#[cfg(all(feature = "simd", target_arch = "x86_64", feature = "std"))]
#[cold]
fn detect() -> bool {
    let on = !forced_off() && std::is_x86_feature_detected!("ssse3");
    STATE.store(if on { 2 } else { 1 }, core::sync::atomic::Ordering::Relaxed);
    on
}

#[cfg(all(feature = "simd", target_arch = "x86_64", not(feature = "std")))]
#[inline(always)]
fn enabled() -> bool {
    cfg!(target_feature = "ssse3")
}

#[cfg(all(feature = "simd", all(target_arch = "aarch64", target_endian = "little"), feature = "std"))]
#[cold]
fn detect() -> bool {
    let on = !forced_off();
    STATE.store(if on { 2 } else { 1 }, core::sync::atomic::Ordering::Relaxed);
    on
}

#[cfg(all(feature = "simd", all(target_arch = "aarch64", target_endian = "little"), not(feature = "std")))]
#[inline(always)]
fn enabled() -> bool {
    true
}

/// The environment variable `STENO_SIMD=0` turns the vector code off for the whole process, so that the tests can run the scalar code
/// alone. It is read once.
#[cfg(all(feature = "simd", feature = "std", any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little"))))]
fn forced_off() -> bool {
    std::env::var_os("STENO_SIMD").is_some_and(|v| v == "0")
}

#[cfg(feature = "std")]
std::thread_local! {
    static THREAD_OFF: core::cell::Cell<bool> = const { core::cell::Cell::new(false) };
}

/// True once a thread has turned the vector code off: until then, the per-thread switch is not even looked at.
#[cfg(feature = "std")]
static ANY_THREAD_OFF: core::sync::atomic::AtomicBool = core::sync::atomic::AtomicBool::new(false);

/// Turns the vector code off (or on again) for the calling thread, for tests that compare it with the scalar code.
#[cfg(feature = "std")]
pub(crate) fn set_thread_enabled(on: bool) {
    if !on {
        ANY_THREAD_OFF.store(true, core::sync::atomic::Ordering::Relaxed);
    }

    THREAD_OFF.with(|off| off.set(!on));
}

/// True unless the vector code was turned off for the calling thread.
#[inline(always)]
#[allow(dead_code)]
fn thread_enabled() -> bool {
    #[cfg(feature = "std")]
    {
        !ANY_THREAD_OFF.load(core::sync::atomic::Ordering::Relaxed) || !THREAD_OFF.try_with(core::cell::Cell::get).unwrap_or(false)
    }

    #[cfg(not(feature = "std"))]
    {
        true
    }
}

/// True if the vector code runs for the calling thread: a cached branch, for the callers to skip work for the kernels when they do not run.
#[inline(always)]
pub(crate) fn active() -> bool {
    #[cfg(all(feature = "simd", any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little"))))]
    {
        enabled() && thread_enabled()
    }

    #[cfg(not(all(feature = "simd", any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little")))))]
    {
        false
    }
}

/// True if the vector code runs in this process.
#[allow(dead_code)]
pub(crate) fn is_active() -> bool {
    #[cfg(all(feature = "simd", any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little"))))]
    {
        enabled()
    }

    #[cfg(not(all(feature = "simd", any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little")))))]
    {
        false
    }
}

// ---------- Dispatch ----------

macro_rules! dispatch {
    ($name:ident ( $($arg:ident : $ty:ty),* ) -> $ret:ty, $none:expr) => {
        #[inline]
        #[allow(unused_variables)]
        #[cfg_attr(feature = "simd", allow(unsafe_code))]
        pub(super) fn $name($($arg: $ty),*) -> $ret {
            #[cfg(all(feature = "simd", target_arch = "x86_64"))]
            {
                if enabled() && thread_enabled() {
                    // SAFETY: `enabled` is true only if SSSE3 was detected (or is a compile-time target feature), which is the only target
                    // feature that the kernels enable.
                    return unsafe { x86::kernels::$name($($arg),*) };
                }
            }

            #[cfg(all(feature = "simd", all(target_arch = "aarch64", target_endian = "little")))]
            {
                if enabled() && thread_enabled() {
                    // SAFETY: NEON, the only target feature that the kernels enable, is a baseline feature of every aarch64 target.
                    return unsafe { neon::kernels::$name($($arg),*) };
                }
            }

            $none
        }
    };
}

mod raw {
    use super::*;

    dispatch!(encode_utf16(p: &Params, src: &[u16], dst: &mut [u8]) -> Progress, Progress::NONE);
    dispatch!(decode_utf16(p: &Params, src: &[u8], dst: &mut [u16]) -> Progress, Progress::NONE);
    dispatch!(ascii_text_prefix(src: &[u8]) -> usize, 0);
    dispatch!(utf8_to_utf16(src: &[u8], start: usize, end: usize, dst: &mut [u16]) -> Progress, Progress::NONE);
    dispatch!(utf16_to_utf8(src: &[u16], dst: &mut [u8]) -> Progress, Progress::NONE);
}

// ---------- Entry points ----------
//
// The kernels of 1-byte codes do their own tails, with two overlapping partial vectors: a run of 4 to 15 code units, and so a short value,
// takes one step. The wide kernels take whole blocks only (8 code units, or 32 bytes to look at): measured on 16-character values, a tail
// through a block on the stack was slower than the scalar code.

/// The fewest code units or bytes of a tail that are worth a block.
pub(crate) const MIN_TAIL: usize = 4;

/// Encodes UTF-16 code units: runs of 1-byte codes, or the codes of a wide block. See [`Progress`].
#[inline]
pub(crate) fn encode_utf16(p: &Params, src: &[u16], dst: &mut [u8]) -> Progress {
    if p.wide() && src.len() < 8 {
        return Progress::NONE;
    }

    raw::encode_utf16(p, src, dst)
}

/// Decodes bytes into UTF-16 code units: runs of 1-byte codes, or the codes of a wide block. See [`Progress`].
#[inline]
pub(crate) fn decode_utf16(p: &Params, src: &[u8], dst: &mut [u16]) -> Progress {
    if p.wide() && src.len() < 40 {
        return Progress::NONE;
    }

    raw::decode_utf16(p, src, dst)
}

/// The length of the prefix of `src` whose bytes are all `02`, `03`, `05` or `07`..`65`, or a shorter prefix (the caller checks the rest).
#[inline]
pub(crate) fn ascii_text_prefix(src: &[u8]) -> usize {
    let n = raw::ascii_text_prefix(src);
    let rest = src.get(n..).unwrap_or_default();

    if rest.len() < MIN_TAIL || rest.len() >= 16 {
        return n;
    }

    // Padding `07` is valid, so the result is capped at the tail.
    let mut buf = [0x07u8; 16];
    buf[..rest.len()].copy_from_slice(rest);
    n + raw::ascii_text_prefix(&buf).min(rest.len())
}

/// Converts UTF-8 to UTF-16 code units below U+10000, from byte `start` of `src`, which must be valid UTF-8 (a `str`) with `start` at the
/// start of a sequence; stops before a sequence of 4 bytes, and when `dst` is nearly full. Returns the bytes read and the code units written.
#[inline]
pub(crate) fn utf8_to_utf16(src: &[u8], start: usize, dst: &mut [u16]) -> Progress {
    if src.len() >= 16 {
        return raw::utf8_to_utf16(src, start, src.len(), dst);
    }

    // A value of fewer than 16 bytes: in a block on the stack, of which only the bytes of the value are read.
    let rest = src.get(start..).unwrap_or_default();

    if rest.len() < MIN_TAIL {
        return Progress::NONE;
    }

    let mut buf = [0u8; 16];
    buf[..rest.len()].copy_from_slice(rest);
    raw::utf8_to_utf16(&buf, 0, rest.len(), dst)
}

/// Converts UTF-16 code units to UTF-8, as long as no surrogate comes and `dst` has room. Returns the code units read and the bytes written.
#[inline]
pub(crate) fn utf16_to_utf8(src: &[u16], dst: &mut [u8]) -> Progress {
    let mut pr = raw::utf16_to_utf8(src, dst);
    let rest = src.get(pr.read..).unwrap_or_default();

    if rest.is_empty() || rest.len() >= 8 {
        return pr;
    }

    // The tail, padded with U+0000, which takes 1 byte each and is taken off again.
    let mut buf = [0u16; 8];
    buf[..rest.len()].copy_from_slice(rest);
    let mut out = [0u8; 32];
    let t = raw::utf16_to_utf8(&buf, &mut out);

    if t.read == 8 {
        let written = t.written - (8 - rest.len());

        if let Some(d) = dst.get_mut(pr.written..pr.written + written) {
            d.copy_from_slice(&out[..written]);
            pr.read += rest.len();
            pr.written += written;
        }
    }

    pr
}

/// Appends UTF-8 that `utf16_to_utf8` wrote to a `String`.
///
/// `utf16_to_utf8` writes each code unit below U+10000 that is not a surrogate as its UTF-8 sequence, chosen by comparing the unit with
/// 0x80 and 0x800, and it stops at a surrogate, so its output is always valid UTF-8; debug builds check it again.
#[cfg(feature = "simd")]
#[allow(unsafe_code)]
pub(crate) fn push_utf8(out: &mut alloc::string::String, utf8: &[u8]) {
    debug_assert!(core::str::from_utf8(utf8).is_ok());

    // SAFETY: `utf8` is valid UTF-8 (see above), so the string stays valid UTF-8.
    unsafe { out.as_mut_vec() }.extend_from_slice(utf8);
}

#[cfg(test)]
mod tests;
