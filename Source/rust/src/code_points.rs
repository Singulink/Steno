//! Encoding and decoding with code points as `u32`, which hold every value with full fidelity.
//!
//! A value is a sequence of code points U+0000..U+10FFFF, surrogate code points included, in which no high surrogate code point
//! (U+D800..U+DBFF) is directly followed by a low surrogate code point (U+DC00..U+DFFF) (FORMAT.md §3). The encoder rejects a sequence that
//! breaks this rule, and a number above 0x10FFFF.

use alloc::vec::Vec;

use crate::codec::{self, CodePointCounter, CodePointSource, Counter, DecodeState, DecodeStop, EncodeStop, SliceSink};
use crate::error::{DecodeError, DecodeToSliceError, EncodeError, EncodeToSliceError};
use crate::state::State;

/// Encodes code points as one value.
///
/// # Errors
///
/// An [`EncodeError`] if a number is above 0x10FFFF, or if a low surrogate code point directly follows a high surrogate code point.
///
/// # Examples
///
/// ```
/// use singulink_steno::{code_points, EncodeErrorKind};
///
/// assert_eq!(code_points::encode(&[0x48, 0x69, 0x20, 0x1F600]).unwrap(), [0x2F, 0x50, 0x07, 0xF9, 0xF4, 0x58]);
/// assert_eq!(code_points::encode(&[0xD83D, 0xDE00]).unwrap_err().kind(), EncodeErrorKind::NotAValue);
/// ```
pub fn encode(code_points: &[u32]) -> Result<Vec<u8>, EncodeError> {
    let mut out = Vec::with_capacity(max_encoded_len(code_points.len()));
    encode_to_vec(code_points, &mut out)?;
    Ok(out)
}

/// Encodes code points as one value and appends the bytes to `out`.
///
/// # Errors
///
/// As [`encode`]. On an error, `out` is left as it was.
pub fn encode_to_vec(code_points: &[u32], out: &mut Vec<u8>) -> Result<(), EncodeError> {
    out.reserve(max_encoded_len(code_points.len()));
    let len = out.len();
    let mut st = State::INITIAL;
    let mut src = CodePointSource { cps: code_points, i: 0, prev_high: false };

    match codec::encode(&mut st, &mut src, out) {
        Ok(()) => Ok(()),
        Err(EncodeStop::Full) => unreachable!("a Vec is never full"),
        Err(EncodeStop::Source(e)) => {
            out.truncate(len);
            Err(e)
        }
    }
}

/// Encodes code points as one value into `out`, and returns the number of bytes written.
///
/// A slice of [`max_encoded_len`]`(code_points.len())` bytes is always long enough. The contents of `out` after the returned length are
/// unspecified.
///
/// # Errors
///
/// As [`encode`], or [`EncodeToSliceError::BufferTooSmall`] if `out` is too short; the problem that comes first in the input is reported.
/// The bytes written to `out` are then unspecified.
pub fn encode_to_slice(code_points: &[u32], out: &mut [u8]) -> Result<usize, EncodeToSliceError> {
    let mut sink = SliceSink::new(out);
    let mut st = State::INITIAL;
    let mut src = CodePointSource { cps: code_points, i: 0, prev_high: false };

    match codec::encode(&mut st, &mut src, &mut sink) {
        Ok(()) => Ok(sink.pos),
        Err(EncodeStop::Full) => Err(EncodeToSliceError::BufferTooSmall),
        Err(EncodeStop::Source(e)) => Err(EncodeToSliceError::Encode(e)),
    }
}

/// Returns the number of bytes that [`encode`] gives for the code points.
///
/// # Errors
///
/// As [`encode`].
pub fn encoded_len(code_points: &[u32]) -> Result<usize, EncodeError> {
    let mut count = Counter(0);
    let mut st = State::INITIAL;
    let mut src = CodePointSource { cps: code_points, i: 0, prev_high: false };

    match codec::encode(&mut st, &mut src, &mut count) {
        Ok(()) => Ok(count.0),
        Err(EncodeStop::Full) => unreachable!("a counter is never full"),
        Err(EncodeStop::Source(e)) => Err(e),
    }
}

/// Returns the most bytes that `count` code points can take: `count × 4`.
pub const fn max_encoded_len(count: usize) -> usize {
    count.saturating_mul(4)
}

/// Decodes one value into code points. Every valid value can be decoded.
///
/// # Errors
///
/// A [`DecodeError`] if the bytes are not valid Steno bytes.
pub fn decode(bytes: &[u8]) -> Result<Vec<u32>, DecodeError> {
    let mut out = Vec::new();
    decode_to_vec(bytes, &mut out)?;
    Ok(out)
}

/// Decodes one value and appends the code points to `out`.
///
/// # Errors
///
/// As [`decode`]. On an error, `out` is left as it was.
pub fn decode_to_vec(bytes: &[u8], out: &mut Vec<u32>) -> Result<(), DecodeError> {
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

/// Decodes one value into `out` as code points, and returns the number of code points written.
///
/// A slice of [`max_decoded_len`]`(bytes.len())` code points is always long enough. The contents of `out` after the returned length are
/// unspecified.
///
/// # Errors
///
/// As [`decode`], or [`DecodeToSliceError::BufferTooSmall`] if `out` is too short. The code points written to `out` are then unspecified.
pub fn decode_to_slice(bytes: &[u8], out: &mut [u32]) -> Result<usize, DecodeToSliceError> {
    let mut sink = SliceSink::new(out);
    let mut ds = DecodeState::INITIAL;

    match codec::decode(&mut ds, bytes, 0, &mut sink, false) {
        Ok(_) => Ok(sink.pos),
        Err(DecodeStop::Full) => Err(DecodeToSliceError::BufferTooSmall),
        Err(DecodeStop::Error(e)) => Err(DecodeToSliceError::Decode(e)),
    }
}

/// Returns the number of code points of the value that `bytes` encode.
///
/// # Errors
///
/// As [`decode`].
pub fn decoded_len(bytes: &[u8]) -> Result<usize, DecodeError> {
    let mut count = CodePointCounter(0);
    let mut ds = DecodeState::INITIAL;
    codec::decode(&mut ds, bytes, 0, &mut count, false).map_err(crate::decode_error)?;
    Ok(count.0)
}

/// Returns the most code points that a value of `steno_len` bytes can have: `steno_len`, since a code point takes at least 1 byte.
pub const fn max_decoded_len(steno_len: usize) -> usize {
    steno_len
}
