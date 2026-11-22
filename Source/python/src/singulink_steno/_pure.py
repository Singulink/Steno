"""The pure Python backend of the Python reference implementation of the Steno Encoding.

It works everywhere CPython or PyPy runs, with no compiler. Runs of code points that take 1 byte are encoded and decoded by the C code of the
standard library (``re`` finds where a run ends, and ``codecs.charmap_encode`` and ``codecs.charmap_decode`` convert it); every other code
point takes one step of plain Python.
"""

from __future__ import annotations

import codecs
import re
from bisect import bisect_right
from typing import Any, List, Optional, Tuple

from ._errors import NOT_A_VALUE, StenoDecodeError, StenoDecodeErrorReason
from ._layout import CJK, GENERAL, NONE, State, pack_compact, pack_full, unpack_compact, unpack_full

NAME = "python"
"""The name of this backend."""

_charmap_encode = codecs.charmap_encode
_charmap_decode = codecs.charmap_decode

# A high surrogate directly followed by a low surrogate: not a value (FORMAT.md section 3).
_PAIR = re.compile(r"[\ud800-\udbff][\udc00-\udfff]")

# U+0009, U+000A, U+000D and U+0020..U+007E take one byte each in the initial layout, and do not change the state (FORMAT.md section 14.1).
# Every other byte maps to 00, which no such text gives.
_ASCII_TO_STENO = bytes(
    (c - 0x19 if 0x20 <= c <= 0x7E else {0x09: 0x02, 0x0A: 0x03, 0x0D: 0x05}.get(c, 0)) if c < 0x80 else 0 for c in range(256)
)
_STENO_TO_ASCII = bytes((b + 0x19 if 0x07 <= b <= 0x65 else {0x02: 0x09, 0x03: 0x0A, 0x05: 0x0D}.get(b, 0)) for b in range(256))
_ASCII_TEXT_BYTES = bytes([0x02, 0x03, 0x05, *range(0x07, 0x66)])


WIDE_RUN_MIN = 16
"""The shortest run of ideographs or Hangul syllables that the integer arithmetic below converts (measured: shorter runs are faster with a
plain loop)."""

_CJK_RUN: Any = re.compile("[\u4e00-\u9fff]*").match
_HANGUL_RUN: Any = re.compile("[\uac00-\ud7a3]*").match
_CJK_OUT = re.compile("[^\u4e00-\u9fff]").search
_HANGUL_OUT = re.compile("[^\uac00-\ud7a3]").search
_LANE = b"\x01\x00\x00\x00"

WIDE_DIV_MUL = 67651
"""ceil(2**24 / 248): (x * WIDE_DIV_MUL) >> 24 == x // 248 for 0 <= x < 65536."""


def _wide_encode(run: str, r0: int, f: int) -> bytearray:
    """Encodes a run of code points in U+4E00..U+9FFF or U+AC00..U+D7A3 that lie in one range of size 2 (first code point r0, first lead f).

    Each code point c takes the bytes f + (c - r0) // 248 and (c - r0) % 248 + 8. The code points are put in 32-bit lanes of one big integer,
    so that the C code of int does the arithmetic of all of them at once. The division by 248 is (x * WIDE_DIV_MUL) >> 24, which is exact
    for 0 <= x < 65536 (a test checks every x). Here x <= 0xD7A3, so x * WIDE_DIV_MUL stays below 2**32 and no lane carries into the next;
    after the shift, the low 8 bits of each lane are the quotient (at most 0xD7A3 // 248 = 222), and the mask drops the bits that the shift
    brought down from the next lane.
    """
    m = len(run)
    rep = int.from_bytes(_LANE * m, "little")
    x = int.from_bytes(run.encode("utf-32-le"), "little") - r0 * rep
    q = ((x * WIDE_DIV_MUL) >> 24) & (0xFF * rep)
    rem = x - 248 * q
    out = bytearray(2 * m)
    out[0::2] = (q + f * rep).to_bytes(4 * m, "little")[0::4]
    out[1::2] = (rem + 8 * rep).to_bytes(4 * m, "little")[0::4]
    return out


def _wide_decode(seg: bytes, k: int) -> str:
    """Decodes pairs of a lead byte b and a trail byte u of one range of size 2 to the code points 248 * b + u + k, in 32-bit lanes.

    Every lane stays in 0..2**32, so no lane borrows from or carries into the next. The caller checks that the code points are in its block.
    """
    m = len(seg) // 2
    lanes = bytearray(4 * m)
    lanes[0::4] = seg[0::2]
    lead = int.from_bytes(lanes, "little")
    lanes[0::4] = seg[1::2]
    trail = int.from_bytes(lanes, "little")
    rep = int.from_bytes(_LANE * m, "little")
    return (248 * lead + trail + k * rep).to_bytes(4 * m, "little").decode("utf-32-le", "surrogatepass")


def is_ascii_text(data: bytes) -> bool:
    """Returns True if the Steno bytes are those of a value with only tab, line feed, carriage return and U+0020..U+007E."""
    return not data.translate(None, _ASCII_TEXT_BYTES)


def _not_a_value(text: str, start: int, end: int) -> UnicodeEncodeError:
    return UnicodeEncodeError("x-steno", text, start, end, NOT_A_VALUE)


def _encode_run_effects(st: State, b: bytes) -> None:
    """Updates the state for a run of code points that took 1 byte each, from their bytes (FORMAT.md section 14.2)."""
    layout = st.layout
    if layout.ctx == GENERAL:
        if st.conf < 2:
            n = len(b) - len(b.translate(None, layout.main_bytes))
            if n:
                st.conf = 2 if st.conf + n >= 2 else 1
    elif st.pending != NONE and layout.cjk1_bytes and len(b.translate(None, layout.cjk1_bytes)) != len(b):
        st.pending = NONE


def _encode(st: State, text: str, out: bytearray) -> None:
    """Encodes a string that holds no high surrogate directly followed by a low one, from the state, and updates the state."""
    pos = 0
    n = len(text)
    layout = st.layout
    while True:
        if not layout.ready:
            # A layout gets its tables for runs when it is used a second time (see Layout).
            layout.uses += 1
            if layout.uses >= 2:
                layout.build_tables()
        end = layout.enc_run(text, pos).end() if layout.ready else pos
        if end > pos:
            b = _charmap_encode(text[pos:end], "strict", layout.enc_map)[0]
            out += b
            if layout.ctx == GENERAL:
                if st.conf < 2:
                    _encode_run_effects(st, b)
            elif st.pending != NONE:
                _encode_run_effects(st, b)
            pos = end
        if pos >= n:
            return

        c = ord(text[pos])
        pos += 1

        # The ideographs in the CJK context and the Hangul syllables in the Hangul context: 2 bytes, and the pending window becomes none.
        if layout.wide_lo <= c <= layout.wide_hi:
            st.pending = NONE
            # Several in a row stay in the same layout.
            end = (_CJK_RUN if layout.ctx == CJK else _HANGUL_RUN)(text, pos).end()
            r0 = layout.wide_r0
            f = layout.wide_first
            if end - pos + 1 >= WIDE_RUN_MIN:
                out += _wide_encode(text[pos - 1 : end], r0, f)
            else:
                q, t = divmod(c - r0, 248)
                out.append(f + q)
                out.append(t + 8)
                for ch in text[pos:end]:
                    q, t = divmod(ord(ch) - r0, 248)
                    out.append(f + q)
                    out.append(t + 8)
            pos = end
            continue

        r = bisect_right(layout.lo, c) - 1
        o = c - layout.lo[r]
        k = layout.size[r]
        f = layout.first[r]
        if k == 2:
            q, t = divmod(o, 248)
            out.append(f + q)
            out.append(t + 8)
        elif k == 3:
            q, rest = divmod(o, 61504)
            t1, t2 = divmod(rest, 248)
            out.append(f + q)
            out.append(t1 + 8)
            out.append(t2 + 8)
        elif k == 4:
            q, rest = divmod(o, 15252992)
            t1, rest = divmod(rest, 61504)
            t2, t3 = divmod(rest, 248)
            out.append(f + q)
            out.append(t1 + 8)
            out.append(t2 + 8)
            out.append(t3 + 8)
        else:
            out.append(f + o)
        st.update(c, k)
        layout = st.layout


def encode(text: str) -> bytes:
    """Encodes one whole value."""
    if text.isascii():
        b = text.encode("ascii").translate(_ASCII_TO_STENO)
        if b.find(0) < 0:
            return b
    m = _PAIR.search(text)
    if m is not None:
        raise _not_a_value(text, m.start(), m.start() + 2)
    out = bytearray()
    _encode(State(), text, out)
    return bytes(out)


class EncoderCore:
    """The state of an incremental encoder: the state of the value, and whether the last code point was a high surrogate."""

    __slots__ = ("last_high", "st")

    def __init__(self) -> None:
        self.st = State()
        self.last_high = False

    def reset(self) -> None:
        """Starts a new value."""
        self.st.reset()
        self.last_high = False

    def encode(self, text: str) -> bytes:
        """Encodes the next piece of the value. On an error, nothing changes."""
        if not text:
            return b""
        if self.last_high and "\udc00" <= text[0] <= "\udfff":
            raise _not_a_value(text, 0, 1)
        m = _PAIR.search(text)
        if m is not None:
            raise _not_a_value(text, m.start(), m.start() + 2)
        out = bytearray()
        _encode(self.st, text, out)
        self.last_high = "\ud800" <= text[-1] <= "\udbff"
        return bytes(out)

    def pack(self) -> int:
        """Packs the state into a number; the initial state is 0."""
        return pack_full(self.st) * 2 + self.last_high

    def unpack(self, n: int) -> None:
        """Sets the state from a number of pack(). Raises ValueError if the number is not one."""
        rest, high = divmod(n, 2)
        unpack_full(self.st, rest)
        self.last_high = bool(high)


def _decode_error(reason: StenoDecodeErrorReason, buf: bytes, start: int, end: int, base: int) -> StenoDecodeError:
    return StenoDecodeError(reason, base + start, buf, start, min(max(end, start + 1), len(buf)))


def _decode(st: State, buf: bytes, final: bool, last_high: bool, base: int, parts: List[str]) -> Tuple[int, bool]:
    """Decodes bytes from the state, appends the text to parts and updates the state.

    Returns:
        The position after the last whole code point (the rest is an unfinished code point, only if final is False), and whether the last
        code point was a high surrogate.

    Raises:
        StenoDecodeError: The bytes are not the Steno bytes of a value. The state is then undefined.
    """
    pos = 0
    n = len(buf)
    layout = st.layout
    while pos < n:
        if not layout.ready:
            layout.uses += 1
            if layout.uses >= 2:
                layout.build_tables()
        end = layout.dec_run(buf, pos).end() if layout.ready else pos
        if end > pos:
            seg = buf[pos:end]
            parts.append(_charmap_decode(seg, "strict", layout.dec_table)[0])
            if layout.ctx == GENERAL:
                if st.conf < 2:
                    _encode_run_effects(st, seg)
            elif st.pending != NONE:
                _encode_run_effects(st, seg)
            last_high = False
            pos = end
            if pos >= n:
                break

        b = buf[pos]
        lead_range = layout.lead_range
        r = lead_range[b] if lead_range is not None else layout.range_of_lead(b)
        if r < 0:
            raise _decode_error(StenoDecodeErrorReason.ZERO_BYTE if b == 0 else StenoDecodeErrorReason.UNUSED_LEAD, buf, pos, pos + 1, base)
        # The ideographs in the CJK context and the Hangul syllables in the Hangul context: a run of them only sets the pending window to
        # none. A code point of the same range outside the block, and any error, take the general path below.
        if r == layout.wide_range:
            end = layout.wide_run(buf, pos).end()
            if end > pos:
                f = layout.wide_first
                r0 = layout.wide_r0
                if end - pos >= 2 * WIDE_RUN_MIN:
                    s = _wide_decode(buf[pos:end], r0 - 248 * f - 8)
                else:
                    s = "".join([chr(r0 + (buf[i] - f) * 248 + buf[i + 1] - 8) for i in range(pos, end, 2)])
                m = (_CJK_OUT if layout.ctx == CJK else _HANGUL_OUT)(s)
                if m is not None:
                    s = s[: m.start()]
                if s:
                    parts.append(s)
                    st.pending = NONE
                    last_high = False
                    pos += 2 * len(s)
                    continue

        k = layout.size[r]
        f = layout.first[r]
        if k == 1:
            o = b - f
        elif k == 2 and pos + 1 < n and buf[pos + 1] >= 8:
            # The common case, unrolled: a whole 2-byte code point with a valid trail byte.
            o = (b - f) * 248 + buf[pos + 1] - 8
        else:
            # Each trail byte is checked as it is read (FORMAT.md section 9.2): the end of the bytes before it, then 00, then 01..07.
            last = pos + k if pos + k <= n else n
            o = b - f
            for i in range(pos + 1, last):
                u = buf[i]
                if u < 8:
                    raise _decode_error(
                        StenoDecodeErrorReason.ZERO_BYTE if u == 0 else StenoDecodeErrorReason.INVALID_TRAIL, buf, pos, i + 1, base
                    )
                o = o * 248 + u - 8
            if last < pos + k:
                if final:
                    raise _decode_error(StenoDecodeErrorReason.TRUNCATED, buf, pos, n, base)
                break
        c = layout.lo[r] + o
        if c > layout.hi[r]:
            raise _decode_error(StenoDecodeErrorReason.PAST_RANGE, buf, pos, pos + k, base)
        if last_high and 0xDC00 <= c <= 0xDFFF:
            raise _decode_error(StenoDecodeErrorReason.SURROGATE_PAIR, buf, pos, pos + k, base)
        parts.append(chr(c))
        last_high = 0xD800 <= c <= 0xDBFF
        pos += k
        st.update(c, k)
        layout = st.layout
    return pos, last_high


def decode(data: bytes) -> str:
    """Decodes one whole value."""
    if not data.translate(None, _ASCII_TEXT_BYTES):
        return data.translate(_STENO_TO_ASCII).decode("ascii")
    parts: List[str] = []
    _decode(State(), data, True, False, 0, parts)
    return "".join(parts)


class DecoderCore:
    """The state of an incremental decoder.

    It holds the state of the value, the bytes of an unfinished code point (``partial``), whether the last code point was a high surrogate,
    and how many bytes of the value came before ``partial`` (for the offsets of errors).
    """

    __slots__ = ("base", "last_high", "partial", "st")

    def __init__(self) -> None:
        self.st = State()
        self.partial = b""
        self.last_high = False
        self.base = 0

    def reset(self) -> None:
        """Starts a new value."""
        self.st.reset()
        self.partial = b""
        self.last_high = False
        self.base = 0

    def decode(self, data: bytes, final: bool) -> str:
        """Decodes the next piece of the value. On an error, nothing changes."""
        buf = self.partial + data if self.partial else data
        if not buf:
            return ""
        st = State()
        st.copy_from(self.st)
        st.recent = self.st.recent
        parts: List[str] = []
        pos, last_high = _decode(st, buf, final, self.last_high, self.base, parts)
        self.st = st
        self.last_high = last_high
        self.partial = buf[pos:]
        self.base += pos
        return "".join(parts)

    def drop_partial(self) -> None:
        """Forgets the bytes of an unfinished code point; the caller passes them in again in front of the next bytes."""
        self.partial = b""

    def compact_state(self) -> Optional[int]:
        """Returns the compact number of the state (below 2**30), or None if it has none."""
        return pack_compact(self.st, self.last_high)

    def full_state(self) -> int:
        """Returns a number that holds the whole state but ``partial`` and the offset."""
        return pack_full(self.st) * 2 + self.last_high

    def set_compact_state(self, n: int) -> None:
        """Sets the state from a compact number, with no held bytes. Raises ValueError if the number is not one; nothing changes then."""
        st = State()
        last_high = unpack_compact(st, n)
        self.st = st
        self.partial = b""
        self.last_high = last_high
        self.base = 0

    def set_full_state(self, n: int, partial: bytes = b"", base: int = 0) -> None:
        """Sets the state from a number of full_state(). Raises ValueError if the number is not one; nothing changes then."""
        if n < 0:
            raise ValueError("not a Steno state number")
        rest, high = divmod(n, 2)
        st = State()
        unpack_full(st, rest)
        self.st = st
        self.partial = bytes(partial)
        self.last_high = bool(high)
        self.base = base
