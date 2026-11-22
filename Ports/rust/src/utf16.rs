//! Encoding and decoding with UTF-16 code units, which hold every value with full fidelity.
//!
//! The encoder reads the code units as FORMAT.md §3 says: a high surrogate followed by a low surrogate is one code point, and every other
//! code unit, an unpaired surrogate included, is the code point of the same value. So every `&[u16]` can be encoded, and decoding gives the
//! same code units back.

use alloc::vec::Vec;

use crate::codec::{self, Counter, DecodeState, DecodeStop, EncodeStop, SliceSink, Utf16Counter, Utf16Source};
use crate::error::{BufferTooSmall, DecodeError, DecodeToSliceError};
use crate::state::State;

/// Encodes UTF-16 code units as one value. Unpaired surrogates are encoded as code points of their own.
///
/// # Examples
///
/// ```
/// use singulink_steno::utf16;
///
/// let units = [0x0061, 0xD800, 0x0062, 0xDC00]; // "a", an unpaired high surrogate, "b", an unpaired low surrogate
/// let bytes = utf16::encode(&units);
/// assert_eq!(bytes, [0x48, 0xF8, 0xC5, 0x20, 0x49, 0xF8, 0xC9, 0x40]);
/// assert_eq!(utf16::decode(&bytes).unwrap(), units);
/// ```
pub fn encode(units: &[u16]) -> Vec<u8> {
    let mut out = Vec::with_capacity(max_encoded_len(units.len()));
    encode_to_vec(units, &mut out);
    out
}

/// Encodes UTF-16 code units as one value and appends the bytes to `out`.
pub fn encode_to_vec(units: &[u16], out: &mut Vec<u8>) {
    out.reserve(max_encoded_len(units.len()));
    let mut st = State::INITIAL;

    match codec::encode(&mut st, &mut Utf16Source { units, i: 0 }, out) {
        Ok(()) => {}
        Err(EncodeStop::Full) => unreachable!("a Vec is never full"),
        Err(EncodeStop::Source(e)) => match e {},
    }
}

/// Encodes UTF-16 code units as one value into `out`, and returns the number of bytes written.
///
/// A slice of [`max_encoded_len`]`(units.len())` bytes is always long enough. The contents of `out` after the returned length are
/// unspecified.
///
/// # Errors
///
/// [`BufferTooSmall`] if `out` is too short. The bytes written to `out` are then unspecified.
pub fn encode_to_slice(units: &[u16], out: &mut [u8]) -> Result<usize, BufferTooSmall> {
    let mut sink = SliceSink::new(out);
    let mut st = State::INITIAL;

    match codec::encode(&mut st, &mut Utf16Source { units, i: 0 }, &mut sink) {
        Ok(()) => Ok(sink.pos),
        Err(EncodeStop::Full) => Err(BufferTooSmall),
        Err(EncodeStop::Source(e)) => match e {},
    }
}

/// Returns the number of bytes that [`encode`] gives for the code units.
pub fn encoded_len(units: &[u16]) -> usize {
    let mut count = Counter(0);
    let mut st = State::INITIAL;

    match codec::encode(&mut st, &mut Utf16Source { units, i: 0 }, &mut count) {
        Ok(()) => count.0,
        Err(EncodeStop::Full) => unreachable!("a counter is never full"),
        Err(EncodeStop::Source(e)) => match e {},
    }
}

/// Returns the most bytes that `units` UTF-16 code units can take: `units × 3`.
///
/// A code point below U+10000 (one code unit) takes at most 3 bytes, and a supplementary code point (two code units) at most 4.
pub const fn max_encoded_len(units: usize) -> usize {
    units.saturating_mul(3)
}

/// Decodes one value into UTF-16 code units. Every valid value can be decoded: a surrogate code point becomes an unpaired surrogate code unit.
///
/// # Errors
///
/// A [`DecodeError`] if the bytes are not valid Steno bytes.
pub fn decode(bytes: &[u8]) -> Result<Vec<u16>, DecodeError> {
    let mut out = Vec::new();
    decode_to_vec(bytes, &mut out)?;
    Ok(out)
}

/// Decodes one value and appends the code units to `out`.
///
/// # Errors
///
/// As [`decode`]. On an error, `out` is left as it was.
pub fn decode_to_vec(bytes: &[u8], out: &mut Vec<u16>) -> Result<(), DecodeError> {
    out.reserve(bytes.len());
    let len = out.len();
    let mut ds = DecodeState::INITIAL;

    match codec::decode(&mut ds, bytes, 0, out, false) {
        Ok(_) => Ok(()),
        Err(stop) => {
            out.truncate(len);
            Err(crate::decode_error(stop))
        }
    }
}

/// Decodes one value into `out` as UTF-16, and returns the number of code units written.
///
/// A slice of [`max_decoded_len`]`(bytes.len())` code units is always long enough. The contents of `out` after the returned length are
/// unspecified.
///
/// # Errors
///
/// As [`decode`], or [`DecodeToSliceError::BufferTooSmall`] if `out` is too short. The code units written to `out` are then unspecified.
pub fn decode_to_slice(bytes: &[u8], out: &mut [u16]) -> Result<usize, DecodeToSliceError> {
    let mut sink = SliceSink::new(out);
    let mut ds = DecodeState::INITIAL;

    match codec::decode(&mut ds, bytes, 0, &mut sink, false) {
        Ok(_) => Ok(sink.pos),
        Err(DecodeStop::Full) => Err(DecodeToSliceError::BufferTooSmall),
        Err(DecodeStop::Error(e)) => Err(DecodeToSliceError::Decode(e)),
    }
}

/// Returns the number of UTF-16 code units of the value that `bytes` encode.
///
/// # Errors
///
/// As [`decode`].
pub fn decoded_len(bytes: &[u8]) -> Result<usize, DecodeError> {
    let mut count = Utf16Counter(0);
    let mut ds = DecodeState::INITIAL;
    codec::decode(&mut ds, bytes, 0, &mut count, false).map_err(crate::decode_error)?;
    Ok(count.0)
}

/// Returns the most UTF-16 code units that a value of `steno_len` bytes can take: `steno_len × 2`, saturating at `usize::MAX`.
///
/// A code point takes at least 1 byte and at most 2 code units.
pub const fn max_decoded_len(steno_len: usize) -> usize {
    steno_len.saturating_mul(2)
}
