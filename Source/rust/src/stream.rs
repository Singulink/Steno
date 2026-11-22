//! The incremental encoder and decoder.

use alloc::string::String;
use alloc::vec::Vec;

use crate::codec::{self, CodePointSink, CodePointSource, DecodeState, EncodeStop, Source, StrSource, Utf16Source};
use crate::error::{DecodeError, DecodeReason, EncodeError, EncodeErrorKind};
use crate::state::State;

/// Encodes one value that is given in pieces.
///
/// Over any split of the value into pieces, the bytes that the encoder appends are the bytes that the one-shot functions give for the whole
/// value. That includes a split between the two code units of a surrogate pair: a high surrogate at the end of a piece of UTF-16 is held until
/// the next piece, which may start with its low surrogate. Pieces of a `str`, of UTF-16 and of code points can be mixed in one value; a held
/// high surrogate is then written as an unpaired surrogate before the next piece that is not UTF-16.
///
/// A high surrogate is joined with a low surrogate only when both are UTF-16 code units, also across pieces. Once a high surrogate has been
/// written as a code point of its own (given as a code point, or held and then written before a piece of another form), a low surrogate
/// that comes right after it, in any form, is rejected with [`EncodeErrorKind::NotAValue`](crate::EncodeErrorKind::NotAValue): the two are
/// not a value, so every mix of forms gives either the bytes of a value or an error.
///
/// [`finish`](Encoder::finish) ends the value and writes a held high surrogate. [`reset`](Encoder::reset) starts a new value and drops a held
/// high surrogate. There is no flush: the only data that the encoder holds is that one code unit.
///
/// The encoder is a small value with no heap memory of its own. It can be sent to another thread, but it is not meant to be shared.
///
/// # Examples
///
/// ```
/// use singulink_steno::Encoder;
///
/// let mut encoder = Encoder::new();
/// let mut bytes = Vec::new();
/// encoder.encode("Моск", &mut bytes);
/// encoder.encode("ва", &mut bytes);
/// encoder.finish(&mut bytes);
///
/// assert_eq!(bytes, singulink_steno::encode("Москва"));
/// ```
#[derive(Clone, Debug)]
pub struct Encoder {
    st: State,
    /// The last code point written was a high surrogate.
    prev_high: bool,
    /// A high surrogate at the end of the last UTF-16 piece.
    held: Option<u16>,
}

impl Default for Encoder {
    fn default() -> Self {
        Encoder::new()
    }
}

impl Encoder {
    /// Creates an encoder at the start of a value.
    pub const fn new() -> Self {
        Encoder { st: State::INITIAL, prev_high: false, held: None }
    }

    /// Encodes the next piece of the value, and appends the bytes to `out`.
    pub fn encode(&mut self, text: &str, out: &mut Vec<u8>) {
        if text.is_empty() {
            return;
        }

        self.write_held(out);
        self.run(&mut StrSource::new(text), out);
        self.prev_high = false;
    }

    /// Encodes the next piece of the value, given as UTF-16 code units, and appends the bytes to `out`.
    ///
    /// The code units are read as [`utf16::encode`](crate::utf16::encode) reads them, across the pieces: a high surrogate at the end of one
    /// piece and a low surrogate at the start of the next are one code point.
    ///
    /// # Errors
    ///
    /// An [`EncodeError`] with the kind [`NotAValue`](crate::EncodeErrorKind::NotAValue) at position 0 if the piece starts with a low surrogate
    /// and the last code point written is a high surrogate that was not a UTF-16 code unit of an earlier piece (see [`Encoder`]). The
    /// encoder and `out` are then left as they were.
    pub fn encode_utf16(&mut self, units: &[u16], out: &mut Vec<u8>) -> Result<(), EncodeError> {
        let mut units = units;

        if units.is_empty() {
            return Ok(());
        }

        // A high surrogate written as a code point of its own cannot be joined with this low surrogate any more.
        if self.held.is_none() && self.prev_high && is_low(units[0]) {
            return Err(EncodeError::new(EncodeErrorKind::NotAValue, 0));
        }

        if let Some(high) = self.held.take() {
            if is_low(units[0]) {
                self.run(&mut Utf16Source { units: &[high, units[0]], i: 0 }, out);
                self.prev_high = false;
                units = &units[1..];
            } else {
                self.run(&mut Utf16Source { units: &[high], i: 0 }, out);
                self.prev_high = true;
            }
        }

        if let Some((&last, rest)) = units.split_last() {
            if is_high(last) {
                self.held = Some(last);
                units = rest;
            }
        }

        if !units.is_empty() {
            self.run(&mut Utf16Source { units, i: 0 }, out);
            // The last unit is not a high surrogate: that one would have been held.
            self.prev_high = false;
        }

        Ok(())
    }

    /// Encodes the next piece of the value, given as code points, and appends the bytes to `out`.
    ///
    /// # Errors
    ///
    /// An [`EncodeError`] if a number is above 0x10FFFF, or if a low surrogate code point directly follows a high surrogate code point,
    /// also across pieces. The encoder and `out` are then left as they were.
    pub fn encode_code_points(&mut self, code_points: &[u32], out: &mut Vec<u8>) -> Result<(), EncodeError> {
        if code_points.is_empty() {
            return Ok(());
        }

        let saved = self.clone();
        let len = out.len();
        self.write_held(out);
        let mut src = CodePointSource { cps: code_points, i: 0, prev_high: self.prev_high };
        let result = codec::encode(&mut self.st, &mut src, out);

        match result {
            Ok(()) => {
                self.prev_high = src.prev_high;
                Ok(())
            }
            Err(EncodeStop::Full) => unreachable!("a Vec is never full"),
            Err(EncodeStop::Source(e)) => {
                *self = saved;
                out.truncate(len);
                Err(e)
            }
        }
    }

    /// Ends the value: writes a held high surrogate as an unpaired surrogate.
    pub fn finish(mut self, out: &mut Vec<u8>) {
        self.write_held(out);
    }

    /// Starts a new value. A held high surrogate is dropped.
    pub fn reset(&mut self) {
        *self = Encoder::new();
    }

    fn write_held(&mut self, out: &mut Vec<u8>) {
        if let Some(high) = self.held.take() {
            self.run(&mut Utf16Source { units: &[high], i: 0 }, out);
            self.prev_high = true;
        }
    }

    fn run<S: Source<Error = core::convert::Infallible>>(&mut self, src: &mut S, out: &mut Vec<u8>) {
        out.reserve(4);

        match codec::encode(&mut self.st, src, out) {
            Ok(()) => {}
            Err(EncodeStop::Full) => unreachable!("a Vec is never full"),
            Err(EncodeStop::Source(e)) => match e {},
        }
    }
}

const fn is_high(u: u16) -> bool {
    u >= 0xD800 && u <= 0xDBFF
}

const fn is_low(u: u16) -> bool {
    u >= 0xDC00 && u <= 0xDFFF
}

/// Decodes one value that is given in pieces.
///
/// Over any split of the bytes into pieces, the decoder appends what the one-shot functions give for the whole value. The bytes of a code
/// point that a piece ends in the middle of are held until the next piece completes them.
///
/// [`finish`](Decoder::finish) says that the value ends there: it fails with [`DecodeReason::Truncated`] if bytes of an unfinished code point
/// are held. [`reset`](Decoder::reset) starts a new value. A call that fails leaves the decoder and the output as they were before the call.
///
/// The decoder is a small value with no heap memory of its own. It can be sent to another thread, but it is not meant to be shared.
///
/// # Examples
///
/// ```
/// use singulink_steno::Decoder;
///
/// let bytes = singulink_steno::encode("東京");
/// let (first, second) = bytes.split_at(2); // in the middle of the first character
///
/// let mut decoder = Decoder::new();
/// let mut text = String::new();
/// decoder.decode(first, &mut text)?;
/// decoder.decode(second, &mut text)?;
/// decoder.finish()?;
///
/// assert_eq!(text, "東京");
/// # Ok::<(), singulink_steno::DecodeError>(())
/// ```
#[derive(Clone, Debug)]
pub struct Decoder {
    ds: DecodeState,
    held: [u8; 3],
    held_len: u8,
    /// The number of bytes of the value before the held bytes.
    position: usize,
}

impl Default for Decoder {
    fn default() -> Self {
        Decoder::new()
    }
}

impl Decoder {
    /// Creates a decoder at the start of a value.
    pub const fn new() -> Self {
        Decoder { ds: DecodeState::INITIAL, held: [0; 3], held_len: 0, position: 0 }
    }

    /// Decodes the next piece of the value, and appends the text to `out`.
    ///
    /// # Errors
    ///
    /// A [`DecodeError`] if the bytes are not valid Steno bytes, or if the value has a surrogate code point, which a `str` cannot hold
    /// ([`DecodeReason::Surrogate`]). Its position counts from the start of the value. The decoder and `out` are then left as they were.
    pub fn decode(&mut self, bytes: &[u8], out: &mut String) -> Result<(), DecodeError> {
        let len = out.len();
        let result = self.run(bytes, out);

        if result.is_err() {
            out.truncate(len);
        }

        result
    }

    /// Decodes the next piece of the value, and appends the text to `out` as UTF-16 code units.
    ///
    /// # Errors
    ///
    /// A [`DecodeError`] if the bytes are not valid Steno bytes. The decoder and `out` are then left as they were.
    pub fn decode_utf16(&mut self, bytes: &[u8], out: &mut Vec<u16>) -> Result<(), DecodeError> {
        let len = out.len();
        let result = self.run(bytes, out);

        if result.is_err() {
            out.truncate(len);
        }

        result
    }

    /// Decodes the next piece of the value, and appends its code points to `out`.
    ///
    /// # Errors
    ///
    /// A [`DecodeError`] if the bytes are not valid Steno bytes. The decoder and `out` are then left as they were.
    pub fn decode_code_points(&mut self, bytes: &[u8], out: &mut Vec<u32>) -> Result<(), DecodeError> {
        let len = out.len();
        let result = self.run(bytes, out);

        if result.is_err() {
            out.truncate(len);
        }

        result
    }

    /// Ends the value.
    ///
    /// # Errors
    ///
    /// A [`DecodeError`] with the reason [`DecodeReason::Truncated`] if the bytes ended in the middle of a code point.
    pub fn finish(self) -> Result<(), DecodeError> {
        if self.held_len > 0 {
            Err(DecodeError::new(DecodeReason::Truncated, self.position))
        } else {
            Ok(())
        }
    }

    /// Starts a new value. Held bytes are dropped.
    pub fn reset(&mut self) {
        *self = Decoder::new();
    }

    fn run<S: CodePointSink>(&mut self, bytes: &[u8], sink: &mut S) -> Result<(), DecodeError> {
        let saved = self.clone();
        let result = self.run_with(bytes, sink);

        if result.is_err() {
            *self = saved;
        }

        result
    }

    fn run_with<S: CodePointSink>(&mut self, bytes: &[u8], sink: &mut S) -> Result<(), DecodeError> {
        let mut bytes = bytes;
        let held = usize::from(self.held_len);

        if held > 0 {
            // Complete the held code point with the first bytes of this piece.
            let take = bytes.len().min(4 - held);
            let mut tmp = [0u8; 4];
            tmp[..held].copy_from_slice(&self.held[..held]);
            tmp[held..held + take].copy_from_slice(&bytes[..take]);
            let tmp = &tmp[..held + take];
            let used = codec::decode(&mut self.ds, tmp, self.position, sink, true).map_err(crate::decode_error)?;

            if used == 0 {
                // Still not complete: `tmp` is shorter than the code point, which has at most 4 bytes, so it fits in `held`.
                self.held[..tmp.len()].copy_from_slice(tmp);
                self.held_len = tmp.len() as u8;
                return Ok(());
            }

            // The held code point is longer than the held bytes, so `used` is more than `held`.
            self.position += used;
            self.held_len = 0;
            bytes = &bytes[used - held..];
        }

        let used = codec::decode(&mut self.ds, bytes, self.position, sink, true).map_err(crate::decode_error)?;
        self.position += used;
        let rest = &bytes[used..];
        self.held[..rest.len()].copy_from_slice(rest);
        self.held_len = rest.len() as u8;
        Ok(())
    }
}
