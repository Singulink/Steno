//! The error types.

use core::fmt;

/// Why decoding failed.
///
/// The first six reasons are the rules of FORMAT.md §10.1: the bytes are not valid Steno bytes. The decoder checks them in the order of
/// FORMAT.md §9.2, so the reason is the rule that the bytes break first. The last reason, [`Surrogate`](DecodeReason::Surrogate), is not a
/// defect of the bytes: it means that the value has a surrogate code point, which a Rust string cannot hold.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum DecodeReason {
    /// A byte `00`, as a lead byte or as a trail byte (rule 1, `zero-byte`).
    ZeroByte,
    /// A lead byte that the layout of the current state does not use (rule 2, `unused-lead`).
    UnusedLead,
    /// The bytes end in the middle of the encoding of a code point (rule 3, `truncated`).
    Truncated,
    /// A code point past the last code point of its range, which includes any value above U+10FFFF (rule 4, `past-range`).
    PastRange,
    /// A low surrogate code point directly after a high surrogate code point (rule 5, `surrogate-pair`).
    SurrogatePair,
    /// A byte `01`..`07` where a trail byte is expected: trail bytes are `08`..`FF` (rule 6, `invalid-trail`).
    InvalidTrail,
    /// The value has a surrogate code point (U+D800..U+DFFF), which has no UTF-8 form. Only the decoders that write a `str` or UTF-8 bytes
    /// report it. Decode to UTF-16 or to code points to read such a value.
    ///
    /// Like every reason, it is the first problem met, reading from the start: the bytes after the surrogate code point are not looked at,
    /// and may be invalid. So the same bytes can give `Surrogate` from a UTF-8 decoder and another reason from a UTF-16 decoder.
    Surrogate,
}

impl DecodeReason {
    /// Returns the name of the reason as the specification and its test vectors write it: `zero-byte`, `unused-lead`, `truncated`,
    /// `past-range`, `surrogate-pair` or `invalid-trail`, and `surrogate` for [`Surrogate`](DecodeReason::Surrogate).
    pub const fn as_str(self) -> &'static str {
        match self {
            DecodeReason::ZeroByte => "zero-byte",
            DecodeReason::UnusedLead => "unused-lead",
            DecodeReason::Truncated => "truncated",
            DecodeReason::PastRange => "past-range",
            DecodeReason::SurrogatePair => "surrogate-pair",
            DecodeReason::InvalidTrail => "invalid-trail",
            DecodeReason::Surrogate => "surrogate",
        }
    }

    /// Returns true for the six reasons of FORMAT.md §10.1, which mean that the bytes are not valid Steno bytes, and false for
    /// [`Surrogate`](DecodeReason::Surrogate).
    pub const fn is_invalid_data(self) -> bool {
        !matches!(self, DecodeReason::Surrogate)
    }
}

impl fmt::Display for DecodeReason {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(self.as_str())
    }
}

/// The error of a decoder: why it failed and where.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct DecodeError {
    reason: DecodeReason,
    position: usize,
}

impl DecodeError {
    pub(crate) const fn new(reason: DecodeReason, position: usize) -> Self {
        DecodeError { reason, position }
    }

    /// Returns why decoding failed.
    pub const fn reason(&self) -> DecodeReason {
        self.reason
    }

    /// Returns the offset of the first byte of the code point that broke the rule, from the start of the value. For a
    /// [`Decoder`](crate::Decoder), the offset counts all the bytes given to it since it was created or reset.
    pub const fn position(&self) -> usize {
        self.position
    }
}

impl fmt::Display for DecodeError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self.reason {
            DecodeReason::Surrogate => write!(f, "the value has a surrogate code point at byte {}, which has no UTF-8 form", self.position),
            reason => write!(f, "invalid Steno bytes ({reason}) in the code point at byte {}", self.position),
        }
    }
}

impl core::error::Error for DecodeError {}

/// Why encoding code points failed.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum EncodeErrorKind {
    /// A number above 0x10FFFF, which is not a code point.
    NotACodePoint,
    /// A low surrogate code point directly after a high surrogate code point: the sequence is not a value (FORMAT.md §3).
    NotAValue,
}

/// The error of the encoders that take code points. Encoding a `str` or UTF-16 never fails.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct EncodeError {
    kind: EncodeErrorKind,
    position: usize,
}

impl EncodeError {
    pub(crate) const fn new(kind: EncodeErrorKind, position: usize) -> Self {
        EncodeError { kind, position }
    }

    /// Returns why encoding failed.
    pub const fn kind(&self) -> EncodeErrorKind {
        self.kind
    }

    /// Returns the index of the code point that broke the rule, in the slice that was given to the call that failed.
    pub const fn position(&self) -> usize {
        self.position
    }
}

impl fmt::Display for EncodeError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self.kind {
            EncodeErrorKind::NotACodePoint => write!(f, "the number at index {} is not a code point", self.position),
            EncodeErrorKind::NotAValue => {
                write!(
                    f,
                    "the low surrogate code point at index {} follows a high surrogate code point, so the input is not a value",
                    self.position
                )
            }
        }
    }
}

impl core::error::Error for EncodeError {}

/// The error of the functions that write into a caller's slice when the slice is too short. The maximum-length functions give a length that
/// is always enough.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Default)]
pub struct BufferTooSmall;

impl fmt::Display for BufferTooSmall {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("the destination is too short")
    }
}

impl core::error::Error for BufferTooSmall {}

/// The error of [`code_points::encode_to_slice`](crate::code_points::encode_to_slice), which writes into a caller's slice.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum EncodeToSliceError {
    /// The code points are not a value, or a number is not a code point.
    Encode(EncodeError),
    /// The destination is too short. The problem that comes first in the input is reported: code points that are invalid after the point
    /// where the destination is full are not looked at.
    BufferTooSmall,
}

impl fmt::Display for EncodeToSliceError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            EncodeToSliceError::Encode(e) => e.fmt(f),
            EncodeToSliceError::BufferTooSmall => BufferTooSmall.fmt(f),
        }
    }
}

impl core::error::Error for EncodeToSliceError {
    fn source(&self) -> Option<&(dyn core::error::Error + 'static)> {
        match self {
            EncodeToSliceError::Encode(e) => Some(e),
            EncodeToSliceError::BufferTooSmall => None,
        }
    }
}

impl From<EncodeError> for EncodeToSliceError {
    fn from(e: EncodeError) -> Self {
        EncodeToSliceError::Encode(e)
    }
}

/// The error of the decoders that write into a caller's slice.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum DecodeToSliceError {
    /// The bytes are not valid Steno bytes, or the value cannot be written in the output form.
    Decode(DecodeError),
    /// The destination is too short. The problem that comes first in the input is reported: bytes that are invalid after the point where the
    /// destination is full are not looked at.
    BufferTooSmall,
}

impl fmt::Display for DecodeToSliceError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            DecodeToSliceError::Decode(e) => e.fmt(f),
            DecodeToSliceError::BufferTooSmall => BufferTooSmall.fmt(f),
        }
    }
}

impl core::error::Error for DecodeToSliceError {
    fn source(&self) -> Option<&(dyn core::error::Error + 'static)> {
        match self {
            DecodeToSliceError::Decode(e) => Some(e),
            DecodeToSliceError::BufferTooSmall => None,
        }
    }
}

impl From<DecodeError> for DecodeToSliceError {
    fn from(e: DecodeError) -> Self {
        DecodeToSliceError::Decode(e)
    }
}
