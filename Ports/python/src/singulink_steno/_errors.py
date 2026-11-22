"""The errors of the Python reference implementation of the Steno Encoding."""

from __future__ import annotations

import enum
from typing import Optional, Tuple, Union

ENCODING_NAME = "x-steno"
"""The name of the encoding: store it next to the data, and give it to the codec machinery."""


class StenoDecodeErrorReason(str, enum.Enum):
    """The rule of the specification (FORMAT.md section 10.1) that bytes broke when they were rejected.

    The members are strings, so ``reason == "zero-byte"`` also works.
    """

    ZERO_BYTE = "zero-byte"
    """A byte 00, as a lead byte or as a trail byte."""

    UNUSED_LEAD = "unused-lead"
    """A lead byte that the layout of the current state does not use."""

    TRUNCATED = "truncated"
    """The bytes end in the middle of the encoding of a code point."""

    PAST_RANGE = "past-range"
    """A code point beyond the last code point of its range (this includes every number above U+10FFFF)."""

    SURROGATE_PAIR = "surrogate-pair"
    """A low surrogate code point directly after a high surrogate code point."""

    INVALID_TRAIL = "invalid-trail"
    """A byte 01..07 where a trail byte is expected (trail bytes are 08..FF; a byte 00 there is ``ZERO_BYTE``)."""

    def __str__(self) -> str:
        return str(self.value)


_DESCRIPTIONS = {
    StenoDecodeErrorReason.ZERO_BYTE: "a byte 00",
    StenoDecodeErrorReason.UNUSED_LEAD: "a lead byte that the layout does not use",
    StenoDecodeErrorReason.TRUNCATED: "the bytes end in the middle of a code point",
    StenoDecodeErrorReason.PAST_RANGE: "a code point past the end of its range",
    StenoDecodeErrorReason.SURROGATE_PAIR: "a low surrogate directly after a high surrogate",
    StenoDecodeErrorReason.INVALID_TRAIL: "a byte 01..07 where a trail byte is expected",
}


class StenoDecodeError(UnicodeDecodeError):
    """The error that decoding raises for bytes that are not the Steno bytes of a value.

    It is a :class:`UnicodeDecodeError` (and so a :class:`ValueError`), so code that handles decoding errors of any codec handles it too.
    A program can test :attr:`reason`. The message is meant for people, and its wording is not part of the contract.

    Decoding gives no partial result: the state after invalid data is unknown, so nothing can replace the bad bytes and go on.

    Attributes:
        reason: The rule of the specification that the bytes broke.
        offset: Where the code point that broke the rule starts (its lead byte), counted from the first byte of the value. For an incremental
            decoder it counts over all calls since the value started (or since ``setstate``).
        object: The bytes that the call was decoding when it failed (for an incremental decoder, with the held bytes of an unfinished code
            point in front).
        start: Where the code point that broke the rule starts in :attr:`object`.
        end: The position in :attr:`object` after the last byte that was read of that code point.
    """

    reason: StenoDecodeErrorReason
    offset: int

    def __init__(
        self,
        reason: Union[StenoDecodeErrorReason, str],
        offset: int,
        data: bytes = b"",
        start: Optional[int] = None,
        end: Optional[int] = None,
    ) -> None:
        """Creates the error.

        Args:
            reason: The rule of the specification that the bytes broke.
            offset: Where the code point that broke the rule starts, counted from the first byte of the value.
            data: The bytes that were being decoded.
            start: Where the code point starts in ``data``. Defaults to ``offset``, limited to the length of ``data``.
            end: The position in ``data`` after the last byte that was read. Defaults to ``start + 1``, limited to the length of ``data``.
        """
        reason = StenoDecodeErrorReason(reason)
        data = bytes(data)
        if start is None:
            start = min(offset, len(data))
        if end is None:
            end = min(start + 1, len(data))
        super().__init__(ENCODING_NAME, data, start, end, reason)
        self.reason = reason
        self.offset = offset

    def __str__(self) -> str:
        return (
            f"'{ENCODING_NAME}' codec can't decode the code point that starts at byte {self.offset} of the value: "
            f"{_DESCRIPTIONS[self.reason]} ({self.reason.value})"
        )

    def __reduce__(self) -> Tuple[type, Tuple[StenoDecodeErrorReason, int, bytes, int, int]]:
        return (type(self), (self.reason, self.offset, self.object, self.start, self.end))


NOT_A_VALUE = (
    "a high surrogate directly followed by a low surrogate is not a value of the Steno Encoding (FORMAT.md section 3); "
    "join the pair into one code point first, for example with text.encode('utf-16', 'surrogatepass').decode('utf-16')"
)
"""The reason of the UnicodeEncodeError that encoding raises for a string that is not a value."""
