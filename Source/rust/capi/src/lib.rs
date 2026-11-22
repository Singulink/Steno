//! A C interface to the Rust reference implementation of the Steno Encoding. The header is `include/steno.h`, which documents every
//! function.
//!
//! The unsafe code of this crate turns the pointers and lengths of the callers into slices. (The library crate `singulink-steno` has unsafe
//! code of its own only in its vector code, the module `src/simd`, and none without its `simd` feature.)
//! Each function checks what it can check (null pointers with a length that is not 0, alignment, overlap of the source and the destination,
//! lengths that no slice can have) and returns `STENO_INVALID_ARGUMENT` for them. What it cannot check (that a pointer points to as many
//! readable or writable elements as its length says) is the caller's duty, as the header states.

#![deny(unsafe_op_in_unsafe_fn)]
#![warn(missing_docs)]

use core::ffi::{c_char, c_int};
use core::mem::size_of;

use singulink_steno::{self as steno, DecodeError, DecodeReason, DecodeToSliceError, EncodeError, EncodeErrorKind, EncodeToSliceError};

/// The result of a function. The values are stable: they never change, and new values are only added at the end.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum StenoStatus {
    /// Success.
    Ok = 0,
    /// The input has a byte 00 (rule 1 of FORMAT.md §10.1, `zero-byte`).
    ZeroByte = 1,
    /// The input has a lead byte that the layout does not use (rule 2, `unused-lead`).
    UnusedLead = 2,
    /// The input ends in the middle of a code point (rule 3, `truncated`).
    Truncated = 3,
    /// The input has a code point past the end of its range (rule 4, `past-range`).
    PastRange = 4,
    /// The input has a low surrogate code point directly after a high surrogate code point (rule 5, `surrogate-pair`).
    SurrogatePair = 5,
    /// The destination is too short.
    DestinationTooSmall = 6,
    /// The UTF-8 input is not valid UTF-8.
    InvalidUtf8 = 7,
    /// The value has a surrogate code point, which has no UTF-8 form. Decode it to UTF-16 instead.
    Surrogate = 8,
    /// A pointer is null while its length is not 0, a pointer is not aligned, the source and the destination overlap, or a length is too
    /// large for any buffer.
    InvalidArgument = 9,
    /// The input has a byte `01`..`07` where a trail byte is expected (rule 6, `invalid-trail`).
    InvalidTrail = 10,
    /// The code points are not a value: a high surrogate code point directly followed by a low one (FORMAT.md §3).
    NotAValue = 11,
    /// A `uint32_t` of the code point input is above 0x10FFFF, so it is not a code point.
    InvalidCodePoint = 12,
}

impl From<DecodeReason> for StenoStatus {
    fn from(r: DecodeReason) -> Self {
        match r {
            DecodeReason::ZeroByte => StenoStatus::ZeroByte,
            DecodeReason::UnusedLead => StenoStatus::UnusedLead,
            DecodeReason::Truncated => StenoStatus::Truncated,
            DecodeReason::PastRange => StenoStatus::PastRange,
            DecodeReason::SurrogatePair => StenoStatus::SurrogatePair,
            DecodeReason::InvalidTrail => StenoStatus::InvalidTrail,
            DecodeReason::Surrogate => StenoStatus::Surrogate,
        }
    }
}

/// A slice of `len` elements at `ptr`, after the checks that can be made.
///
/// # Safety
///
/// If `len` is not 0 and `ptr` is not null, `ptr` must point to `len` initialized elements that nothing writes to while the slice lives.
unsafe fn in_slice<'a, T>(ptr: *const T, len: usize) -> Result<&'a [T], StenoStatus> {
    if len == 0 {
        return Ok(&[]);
    }

    if ptr.is_null() || !ptr.is_aligned() || len > isize::MAX as usize / size_of::<T>() {
        return Err(StenoStatus::InvalidArgument);
    }

    // SAFETY: `ptr` is not null and aligned, the size fits in `isize`, and the caller promises that `len` elements are readable and not
    // written to while the slice lives.
    Ok(unsafe { core::slice::from_raw_parts(ptr, len) })
}

/// A mutable slice of `len` elements at `ptr`, after the checks that can be made.
///
/// # Safety
///
/// If `len` is not 0 and `ptr` is not null, `ptr` must point to `len` writable elements that nothing else reads or writes while the slice
/// lives.
unsafe fn out_slice<'a, T>(ptr: *mut T, len: usize) -> Result<&'a mut [T], StenoStatus> {
    if len == 0 {
        return Ok(&mut []);
    }

    if ptr.is_null() || !ptr.is_aligned() || len > isize::MAX as usize / size_of::<T>() {
        return Err(StenoStatus::InvalidArgument);
    }

    // SAFETY: `ptr` is not null and aligned, the size fits in `isize`, and the caller promises that `len` elements are writable and not
    // used elsewhere while the slice lives. Every bit pattern is a valid `u8`, `u16` and `u32`, the only element types used here.
    Ok(unsafe { core::slice::from_raw_parts_mut(ptr, len) })
}

/// Fails if the bytes of the two slices overlap. Slices that overlap cannot be a shared and a mutable slice at once.
fn check_no_overlap<A, B>(a: *const A, a_len: usize, b: *const B, b_len: usize) -> Result<(), StenoStatus> {
    let a0 = a as usize;
    let a1 = a0.saturating_add(a_len.saturating_mul(size_of::<A>()));
    let b0 = b as usize;
    let b1 = b0.saturating_add(b_len.saturating_mul(size_of::<B>()));

    if a_len != 0 && b_len != 0 && a0 < b1 && b0 < a1 {
        Err(StenoStatus::InvalidArgument)
    } else {
        Ok(())
    }
}

/// Writes `value` through `out`.
///
/// # Safety
///
/// `out` must be null or point to a writable `size_t`.
unsafe fn put(out: *mut usize, value: usize) -> Result<(), StenoStatus> {
    if out.is_null() || !out.is_aligned() {
        return Err(StenoStatus::InvalidArgument);
    }

    // SAFETY: `out` is not null and aligned, and the caller promises that it points to a writable `size_t`.
    unsafe { out.write(value) };
    Ok(())
}

/// Runs `f` and writes the length that it gives through `out_len`: the produced length on success, the position of the defect on a decode
/// error, the length of the valid UTF-8 prefix on invalid UTF-8, and 0 otherwise.
///
/// # Safety
///
/// `out_len` must be null or point to a writable `size_t`. `regions` are the source and the destination as start address and size in bytes.
unsafe fn finish(
    out_len: *mut usize,
    regions: [(usize, usize); 2],
    f: impl FnOnce() -> Result<usize, (StenoStatus, usize)>,
) -> StenoStatus {
    if out_len.is_null() || !out_len.is_aligned() {
        return StenoStatus::InvalidArgument;
    }

    // `out_len` must not point into the source or the destination: then nothing is written through it.
    let out = (out_len as usize, size_of::<usize>());

    if regions.iter().any(|&(start, bytes)| bytes != 0 && start < out.0.saturating_add(out.1) && out.0 < start.saturating_add(bytes)) {
        return StenoStatus::InvalidArgument;
    }

    let (status, len) = match f() {
        Ok(n) => (StenoStatus::Ok, n),
        Err(e) => e,
    };

    // SAFETY: the caller promises that `out_len` points to a writable `size_t`.
    match unsafe { put(out_len, len) } {
        Ok(()) => status,
        Err(e) => e,
    }
}

/// The memory of a slice given as a pointer and a length, as start address and size in bytes.
fn region<T>(ptr: *const T, len: usize) -> (usize, usize) {
    (ptr as usize, len.saturating_mul(size_of::<T>()))
}

fn arg(status: StenoStatus) -> (StenoStatus, usize) {
    (status, 0)
}

fn encode_error(e: EncodeError) -> (StenoStatus, usize) {
    match e.kind() {
        EncodeErrorKind::NotAValue => (StenoStatus::NotAValue, e.position()),
        EncodeErrorKind::NotACodePoint => (StenoStatus::InvalidCodePoint, e.position()),
    }
}

fn decode_error(e: DecodeError) -> (StenoStatus, usize) {
    (e.reason().into(), e.position())
}

fn decode_to_slice_error(e: DecodeToSliceError) -> (StenoStatus, usize) {
    match e {
        DecodeToSliceError::Decode(e) => decode_error(e),
        DecodeToSliceError::BufferTooSmall => (StenoStatus::DestinationTooSmall, 0),
    }
}

/// See `steno.h`.
///
/// # Safety
///
/// See `steno.h`.
#[no_mangle]
pub unsafe extern "C" fn steno_encode_utf8(
    src: *const u8,
    src_len: usize,
    dst: *mut u8,
    dst_len: usize,
    out_len: *mut usize,
) -> StenoStatus {
    // SAFETY: the caller keeps the promises of `steno.h`, which are those of `in_slice`, `out_slice` and `finish`.
    unsafe {
        finish(out_len, [region(src, src_len), region(dst, dst_len)], || {
            check_no_overlap(src, src_len, dst, dst_len).map_err(arg)?;
            let src = in_slice(src, src_len).map_err(arg)?;
            let dst = out_slice(dst, dst_len).map_err(arg)?;
            // The first problem met, reading from the start, is reported (see `steno.h`): so the valid UTF-8 before a bad sequence is
            // encoded first, and a destination that is full before the bad sequence is `STENO_DESTINATION_TOO_SMALL`.
            let (text, bad) = match core::str::from_utf8(src) {
                Ok(text) => (text, None),
                Err(e) => (core::str::from_utf8(&src[..e.valid_up_to()]).unwrap_or_default(), Some(e.valid_up_to())),
            };

            let n = steno::encode_to_slice(text, dst).map_err(|_| (StenoStatus::DestinationTooSmall, 0))?;

            match bad {
                None => Ok(n),
                Some(at) => Err((StenoStatus::InvalidUtf8, at)),
            }
        })
    }
}

/// See `steno.h`.
///
/// # Safety
///
/// See `steno.h`.
#[no_mangle]
pub unsafe extern "C" fn steno_encode_utf16(
    src: *const u16,
    src_len: usize,
    dst: *mut u8,
    dst_len: usize,
    out_len: *mut usize,
) -> StenoStatus {
    // SAFETY: as in `steno_encode_utf8`.
    unsafe {
        finish(out_len, [region(src, src_len), region(dst, dst_len)], || {
            check_no_overlap(src, src_len, dst, dst_len).map_err(arg)?;
            let src = in_slice(src, src_len).map_err(arg)?;
            let dst = out_slice(dst, dst_len).map_err(arg)?;
            steno::utf16::encode_to_slice(src, dst).map_err(|_| (StenoStatus::DestinationTooSmall, 0))
        })
    }
}

/// See `steno.h`.
///
/// # Safety
///
/// See `steno.h`.
#[no_mangle]
pub unsafe extern "C" fn steno_decode_utf8(
    src: *const u8,
    src_len: usize,
    dst: *mut u8,
    dst_len: usize,
    out_len: *mut usize,
) -> StenoStatus {
    // SAFETY: as in `steno_encode_utf8`.
    unsafe {
        finish(out_len, [region(src, src_len), region(dst, dst_len)], || {
            check_no_overlap(src, src_len, dst, dst_len).map_err(arg)?;
            let src = in_slice(src, src_len).map_err(arg)?;
            let dst = out_slice(dst, dst_len).map_err(arg)?;
            steno::decode_to_slice(src, dst).map_err(decode_to_slice_error)
        })
    }
}

/// See `steno.h`.
///
/// # Safety
///
/// See `steno.h`.
#[no_mangle]
pub unsafe extern "C" fn steno_decode_utf16(
    src: *const u8,
    src_len: usize,
    dst: *mut u16,
    dst_len: usize,
    out_len: *mut usize,
) -> StenoStatus {
    // SAFETY: as in `steno_encode_utf8`.
    unsafe {
        finish(out_len, [region(src, src_len), region(dst, dst_len)], || {
            check_no_overlap(src, src_len, dst, dst_len).map_err(arg)?;
            let src = in_slice(src, src_len).map_err(arg)?;
            let dst = out_slice(dst, dst_len).map_err(arg)?;
            steno::utf16::decode_to_slice(src, dst).map_err(decode_to_slice_error)
        })
    }
}

/// See `steno.h`.
///
/// # Safety
///
/// See `steno.h`.
#[no_mangle]
pub unsafe extern "C" fn steno_encoded_len_utf8(src: *const u8, src_len: usize, out_len: *mut usize) -> StenoStatus {
    // SAFETY: as in `steno_encode_utf8`.
    unsafe {
        finish(out_len, [region(src, src_len), (0, 0)], || {
            let src = in_slice(src, src_len).map_err(arg)?;
            let text = core::str::from_utf8(src).map_err(|e| (StenoStatus::InvalidUtf8, e.valid_up_to()))?;
            Ok(steno::encoded_len(text))
        })
    }
}

/// See `steno.h`.
///
/// # Safety
///
/// See `steno.h`.
#[no_mangle]
pub unsafe extern "C" fn steno_encoded_len_utf16(src: *const u16, src_len: usize, out_len: *mut usize) -> StenoStatus {
    // SAFETY: as in `steno_encode_utf8`.
    unsafe { finish(out_len, [region(src, src_len), (0, 0)], || Ok(steno::utf16::encoded_len(in_slice(src, src_len).map_err(arg)?))) }
}

/// See `steno.h`.
///
/// # Safety
///
/// See `steno.h`.
#[no_mangle]
pub unsafe extern "C" fn steno_decoded_len_utf8(src: *const u8, src_len: usize, out_len: *mut usize) -> StenoStatus {
    // SAFETY: as in `steno_encode_utf8`.
    unsafe {
        finish(out_len, [region(src, src_len), (0, 0)], || steno::decoded_len(in_slice(src, src_len).map_err(arg)?).map_err(decode_error))
    }
}

/// See `steno.h`.
///
/// # Safety
///
/// See `steno.h`.
#[no_mangle]
pub unsafe extern "C" fn steno_decoded_len_utf16(src: *const u8, src_len: usize, out_len: *mut usize) -> StenoStatus {
    // SAFETY: as in `steno_encode_utf8`.
    unsafe {
        finish(out_len, [region(src, src_len), (0, 0)], || {
            steno::utf16::decoded_len(in_slice(src, src_len).map_err(arg)?).map_err(decode_error)
        })
    }
}

/// See `steno.h`.
///
/// # Safety
///
/// See `steno.h`.
#[no_mangle]
pub unsafe extern "C" fn steno_encode_code_points(
    src: *const u32,
    src_len: usize,
    dst: *mut u8,
    dst_len: usize,
    out_len: *mut usize,
) -> StenoStatus {
    // SAFETY: as in `steno_encode_utf8`.
    unsafe {
        finish(out_len, [region(src, src_len), region(dst, dst_len)], || {
            check_no_overlap(src, src_len, dst, dst_len).map_err(arg)?;
            let src = in_slice(src, src_len).map_err(arg)?;
            let dst = out_slice(dst, dst_len).map_err(arg)?;
            steno::code_points::encode_to_slice(src, dst).map_err(|e| match e {
                EncodeToSliceError::Encode(e) => encode_error(e),
                EncodeToSliceError::BufferTooSmall => (StenoStatus::DestinationTooSmall, 0),
            })
        })
    }
}

/// See `steno.h`.
///
/// # Safety
///
/// See `steno.h`.
#[no_mangle]
pub unsafe extern "C" fn steno_decode_code_points(
    src: *const u8,
    src_len: usize,
    dst: *mut u32,
    dst_len: usize,
    out_len: *mut usize,
) -> StenoStatus {
    // SAFETY: as in `steno_encode_utf8`.
    unsafe {
        finish(out_len, [region(src, src_len), region(dst, dst_len)], || {
            check_no_overlap(src, src_len, dst, dst_len).map_err(arg)?;
            let src = in_slice(src, src_len).map_err(arg)?;
            let dst = out_slice(dst, dst_len).map_err(arg)?;
            steno::code_points::decode_to_slice(src, dst).map_err(decode_to_slice_error)
        })
    }
}

/// See `steno.h`.
///
/// # Safety
///
/// See `steno.h`.
#[no_mangle]
pub unsafe extern "C" fn steno_encoded_len_code_points(src: *const u32, src_len: usize, out_len: *mut usize) -> StenoStatus {
    // SAFETY: as in `steno_encode_utf8`.
    unsafe {
        finish(out_len, [region(src, src_len), (0, 0)], || {
            steno::code_points::encoded_len(in_slice(src, src_len).map_err(arg)?).map_err(encode_error)
        })
    }
}

/// See `steno.h`.
///
/// # Safety
///
/// See `steno.h`.
#[no_mangle]
pub unsafe extern "C" fn steno_decoded_len_code_points(src: *const u8, src_len: usize, out_len: *mut usize) -> StenoStatus {
    // SAFETY: as in `steno_encode_utf8`.
    unsafe {
        finish(out_len, [region(src, src_len), (0, 0)], || {
            steno::code_points::decoded_len(in_slice(src, src_len).map_err(arg)?).map_err(decode_error)
        })
    }
}

/// See `steno.h`.
#[no_mangle]
pub extern "C" fn steno_max_encoded_len_code_points(code_points: usize) -> usize {
    steno::code_points::max_encoded_len(code_points)
}

/// See `steno.h`.
#[no_mangle]
pub extern "C" fn steno_max_decoded_len_code_points(steno_len: usize) -> usize {
    steno::code_points::max_decoded_len(steno_len)
}

/// See `steno.h`.
#[no_mangle]
pub extern "C" fn steno_max_encoded_len_utf8(utf8_len: usize) -> usize {
    steno::max_encoded_len(utf8_len)
}

/// See `steno.h`.
#[no_mangle]
pub extern "C" fn steno_max_encoded_len_utf16(utf16_len: usize) -> usize {
    steno::utf16::max_encoded_len(utf16_len)
}

/// See `steno.h`.
#[no_mangle]
pub extern "C" fn steno_max_decoded_len_utf8(steno_len: usize) -> usize {
    steno::max_decoded_len(steno_len)
}

/// See `steno.h`.
#[no_mangle]
pub extern "C" fn steno_max_decoded_len_utf16(steno_len: usize) -> usize {
    steno::utf16::max_decoded_len(steno_len)
}

/// See `steno.h`.
///
/// # Safety
///
/// See `steno.h`.
#[no_mangle]
pub unsafe extern "C" fn steno_is_ascii_text(src: *const u8, src_len: usize) -> bool {
    // SAFETY: the caller keeps the promise of `steno.h`, which is that of `in_slice`.
    match unsafe { in_slice(src, src_len) } {
        Ok(src) => steno::is_ascii_text(src),
        Err(_) => false,
    }
}

/// See `steno.h`.
#[no_mangle]
pub extern "C" fn steno_status_name(status: c_int) -> *const c_char {
    let name = match status {
        0 => c"ok",
        1 => c"zero-byte",
        2 => c"unused-lead",
        3 => c"truncated",
        4 => c"past-range",
        5 => c"surrogate-pair",
        6 => c"destination-too-small",
        7 => c"invalid-utf8",
        8 => c"surrogate",
        9 => c"invalid-argument",
        10 => c"invalid-trail",
        11 => c"not-a-value",
        12 => c"invalid-code-point",
        _ => c"unknown",
    };

    name.as_ptr()
}
