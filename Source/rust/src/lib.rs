#![doc = include_str!("../README.md")]
#![no_std]
#![cfg_attr(not(feature = "simd"), forbid(unsafe_code))]
#![cfg_attr(feature = "simd", deny(unsafe_code))]
#![warn(missing_docs)]

extern crate alloc;
#[cfg(feature = "std")]
extern crate std;

mod cache;
mod codec;
mod error;
mod layout;
mod simd;
mod state;
mod stream;

pub mod code_points;
pub mod utf16;

use alloc::string::String;
use alloc::vec::Vec;

use crate::codec::{Counter, DecodeState, DecodeStop, EncodeStop, SliceSink, StrSource, Utf8Counter, Utf8SliceSink};
use crate::state::State;

pub use crate::error::{BufferTooSmall, DecodeError, DecodeReason, DecodeToSliceError, EncodeError, EncodeErrorKind, EncodeToSliceError};
pub use crate::stream::{Decoder, Encoder};

/// The name to store next to Steno bytes, so that a reader knows how to decode them: `x-steno`. The bytes themselves carry no header or
/// version marker.
pub const WEB_NAME: &str = "x-steno";

/// Encodes a string as one value.
///
/// # Examples
///
/// ```
/// let bytes = singulink_steno::encode("Hi!");
/// assert_eq!(bytes, [0x2F, 0x50, 0x08]);
/// ```
pub fn encode(text: &str) -> Vec<u8> {
    let mut out = Vec::with_capacity(max_encoded_len(text.len()));
    encode_to_vec(text, &mut out);
    out
}

/// Encodes a string as one value and appends the bytes to `out`.
pub fn encode_to_vec(text: &str, out: &mut Vec<u8>) {
    out.reserve(max_encoded_len(text.len()));
    let mut st = State::INITIAL;
    let result = codec::encode(&mut st, &mut StrSource::new(text), out);

    match result {
        Ok(()) => {}
        Err(EncodeStop::Full) => unreachable!("a Vec is never full"),
        Err(EncodeStop::Source(e)) => match e {},
    }
}

/// Encodes a string as one value into `out`, and returns the number of bytes written.
///
/// A slice of [`max_encoded_len`]`(text.len())` bytes is always long enough. The contents of `out` after the returned length are
/// unspecified.
///
/// # Errors
///
/// [`BufferTooSmall`] if `out` is too short. The bytes written to `out` are then unspecified.
pub fn encode_to_slice(text: &str, out: &mut [u8]) -> Result<usize, BufferTooSmall> {
    let mut sink = SliceSink::new(out);
    let mut st = State::INITIAL;
    let result = codec::encode(&mut st, &mut StrSource::new(text), &mut sink);

    match result {
        Ok(()) => Ok(sink.pos),
        Err(EncodeStop::Full) => Err(BufferTooSmall),
        Err(EncodeStop::Source(e)) => match e {},
    }
}

/// Returns the number of bytes that [`encode`] gives for a string.
pub fn encoded_len(text: &str) -> usize {
    let mut count = Counter(0);
    let mut st = State::INITIAL;
    let result = codec::encode(&mut st, &mut StrSource::new(text), &mut count);

    match result {
        Ok(()) => count.0,
        Err(EncodeStop::Full) => unreachable!("a counter is never full"),
        Err(EncodeStop::Source(e)) => match e {},
    }
}

/// Returns the most bytes that a string of `utf8_len` UTF-8 bytes can take: `utf8_len × 2`.
///
/// A character takes at most 2 Steno bytes per UTF-8 byte: an ASCII control character other than tab, line feed and carriage return
/// takes 2 bytes, a character of 2 or 3 UTF-8 bytes takes at most 3, and one of 4 UTF-8 bytes at most 4.
pub const fn max_encoded_len(utf8_len: usize) -> usize {
    utf8_len.saturating_mul(2)
}

/// Decodes one value into a new string.
///
/// # Errors
///
/// A [`DecodeError`] if the bytes are not valid Steno bytes, or if the value has a surrogate code point, which a `str` cannot hold
/// ([`DecodeReason::Surrogate`]). [`utf16::decode`] and [`code_points::decode`] decode every valid value.
///
/// # Examples
///
/// ```
/// assert_eq!(singulink_steno::decode(&[0x2F, 0x50, 0x08]).unwrap(), "Hi!");
/// ```
pub fn decode(bytes: &[u8]) -> Result<String, DecodeError> {
    let mut out = String::new();
    decode_to_string(bytes, &mut out)?;
    Ok(out)
}

/// Decodes one value and appends the text to `out`.
///
/// # Errors
///
/// As [`decode`]. On an error, `out` is left as it was.
pub fn decode_to_string(bytes: &[u8], out: &mut String) -> Result<(), DecodeError> {
    // Most values take at least as many UTF-8 bytes as Steno bytes; the String grows if they take more.
    out.reserve(bytes.len());
    let len = out.len();
    let mut ds = DecodeState::INITIAL;

    match codec::decode(&mut ds, bytes, 0, out, false) {
        Ok(_) => Ok(()),
        Err(stop) => {
            out.truncate(len);
            Err(decode_error(stop))
        }
    }
}

/// Decodes one value into `out` as UTF-8, and returns the number of bytes written.
///
/// A slice of [`max_decoded_len`]`(bytes.len())` bytes is always long enough. The contents of `out` after the returned length are
/// unspecified.
///
/// # Errors
///
/// As [`decode`], or [`DecodeToSliceError::BufferTooSmall`] if `out` is too short. The bytes written to `out` are then unspecified.
pub fn decode_to_slice(bytes: &[u8], out: &mut [u8]) -> Result<usize, DecodeToSliceError> {
    let mut sink = Utf8SliceSink(SliceSink::new(out));
    let mut ds = DecodeState::INITIAL;

    match codec::decode(&mut ds, bytes, 0, &mut sink, false) {
        Ok(_) => Ok(sink.0.pos),
        Err(DecodeStop::Full) => Err(DecodeToSliceError::BufferTooSmall),
        Err(DecodeStop::Error(e)) => Err(DecodeToSliceError::Decode(e)),
    }
}

/// Returns the number of UTF-8 bytes of the value that `bytes` encode.
///
/// # Errors
///
/// As [`decode`].
pub fn decoded_len(bytes: &[u8]) -> Result<usize, DecodeError> {
    let mut count = Utf8Counter(0);
    let mut ds = DecodeState::INITIAL;
    codec::decode(&mut ds, bytes, 0, &mut count, false).map_err(decode_error)?;
    Ok(count.0)
}

/// Returns the most UTF-8 bytes that a value of `steno_len` bytes can take: `steno_len × 4`, saturating at `usize::MAX`.
///
/// A code point takes at least 1 Steno byte and at most 4 UTF-8 bytes. The bound is nearly reached by a value whose main window holds
/// supplementary code points, which take 1 byte each.
pub const fn max_decoded_len(steno_len: usize) -> usize {
    steno_len.saturating_mul(4)
}

/// Returns true if the value that `bytes` encode has only ASCII text in it: tab, line feed, carriage return and U+0020..U+007E. The empty
/// value counts. Bytes that are not valid Steno bytes give false.
///
/// The answer comes from the bytes alone, without decoding (FORMAT.md §14.1): such a value is encoded in the initial layout throughout, so
/// its bytes are exactly `02`, `03`, `05` and `07`..`65`, and bytes that are all of these values are valid. When the result is true, the
/// UTF-8 form has as many bytes as the Steno form. The bytes are not the ASCII bytes: U+0020..U+007E are the code point minus 0x19.
///
/// # Examples
///
/// ```
/// assert!(singulink_steno::is_ascii_text(&singulink_steno::encode("Hello, world.\r\n")));
/// assert!(!singulink_steno::is_ascii_text(&singulink_steno::encode("Grüße")));
/// ```
pub fn is_ascii_text(bytes: &[u8]) -> bool {
    let n = simd::ascii_text_prefix(bytes);
    bytes[n..].iter().all(|&b| matches!(b, 0x02 | 0x03 | 0x05 | 0x07..=0x65))
}

/// For the tests of this crate: turns the vector code off (`false`) or on again for the calling thread, and returns whether vector code
/// runs in this process at all (the `simd` feature, a supported CPU, and no `STENO_SIMD=0`). Not part of the API.
#[doc(hidden)]
#[cfg(feature = "std")]
pub fn __set_simd_for_thread(on: bool) -> bool {
    simd::set_thread_enabled(on);
    simd::is_active()
}

pub(crate) fn decode_error(stop: DecodeStop) -> DecodeError {
    match stop {
        DecodeStop::Error(e) => e,
        DecodeStop::Full => unreachable!("a growable output is never full"),
    }
}
