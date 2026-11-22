"""A plain reference codec of the Steno Encoding, version 1, written from FORMAT.md for the tests.

It builds the layout from the pieces again before every code point, so it is slow, but each step can be checked against the text of the
specification. It shares no code with the package. It works on lists of code points.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import List, Optional, Tuple

GENERAL, CJK, HANGUL = "general", "cjk", "hangul"
LETTERS, HIRAGANA, KATAKANA = "letters", "hiragana", "katakana"

Window = Tuple[int, int]


def _block(c: int) -> Window:
    s = c - c % 0x80
    return (s, s + 0x7F)


def window_of(c: int) -> Optional[Window]:
    rows = [
        (0x00BF, None),
        (0x013F, (0x00C0, 0x013F)),
        (0x01BF, (0x0140, 0x01BF)),
        (0x023F, (0x01C0, 0x023F)),
        (0x02BF, (0x0240, 0x02BF)),
        (0x02FF, (0x02C0, 0x02FF)),
        (0x036F, (0x0300, 0x036F)),
        (0x037F, None),
        (0x04FF, "block"),
        (0x052F, (0x0500, 0x052F)),
        (0x058F, (0x0530, 0x058F)),
        (0x05FF, (0x0590, 0x05FF)),
        (0x1FFF, "block"),
        (0x20FF, None),
        (0x2FFF, "block"),
        (0x30FF, None),
        (0x33FF, "block"),
        (0x9FFF, None),
        (0xABFF, "block"),
        (0xDFFF, None),
        (0x1FFFF, "block"),
        (0x3FFFF, None),
        (0x10FFFF, "block"),
    ]
    for last, w in rows:
        if c <= last:
            if w == "block":
                return _block(c)
            return w  # type: ignore[return-value]
    raise ValueError(c)


@dataclass
class State:
    context: str = GENERAL
    main: Window = (0xC0, 0x13F)
    second: Optional[Window] = None
    slot: str = LETTERS
    confidence: int = 0
    pending: Optional[Window] = None


@dataclass
class Range:
    lo: int
    hi: int
    size: int
    first_lead: int
    lead_count: int


def build_layout(s: State) -> List[Range]:
    segs: List[Tuple[int, int, int]] = [(0, 0x10FFFF, 0)]

    def piece(lo: int, hi: int, size: int) -> None:
        nonlocal segs
        if lo > hi:
            return
        nxt = []
        for g in segs:
            if g[1] < lo or g[0] > hi:
                nxt.append(g)
                continue
            if g[0] < lo:
                nxt.append((g[0], lo - 1, g[2]))
            if g[1] > hi:
                nxt.append((hi + 1, g[1], g[2]))
        nxt.append((lo, hi, size))
        nxt.sort()
        segs = nxt

    m0, m1 = s.main
    if s.context == GENERAL:
        piece(max(0x80, m0 - 248), m0 - 1, 2)
        if m1 < 0x10FFFF:
            piece(m1 + 1, min(0x10FFFF, m1 + 248), 2)
    piece(0x00, 0x1F, 2)
    piece(0x09, 0x0A, 1)
    piece(0x0D, 0x0D, 1)
    if s.context == CJK and s.slot != LETTERS:
        piece(0x20, 0x40, 1)
        piece(0x41, 0x7E, 2)
    else:
        piece(0x20, 0x7E, 1)
    piece(0x7F, 0xBF, 2)
    if s.context != CJK:
        piece(0x80, 0xEFF, 2)
    piece(0x2000, 0x20CF, 2)
    if s.second is not None:
        piece(s.second[0], s.second[1], 2)
    if s.context == GENERAL:
        piece(m0, m1, 1)
    elif s.context == HANGUL:
        piece(m0, m1, 2)
        piece(0x3000, 0x303F, 2)
        piece(0xAC00, 0xD7A3, 2)
        piece(0xFF00, 0xFFFF, 2)
    else:
        piece(m0, m1, 2)
        piece(0x3000, 0x300F, 1)
        piece(0x3010, 0x30FF, 2)
        if s.slot == HIRAGANA:
            piece(0x3041, 0x3096, 1)
        if s.slot == KATAKANA:
            piece(0x30A1, 0x30FC, 1)
        piece(0x4E00, 0x9FFF, 2)
        piece(0xFF00, 0xFFFF, 2)
        if s.slot == LETTERS:
            piece(0xFF00, 0xFF1F, 1)

    sized: List[Tuple[int, int, int]] = []
    i = 0
    while i < len(segs):
        if segs[i][2] != 0:
            sized.append(segs[i])
            i += 1
            continue
        lo, hi = segs[i][0], segs[i][1]
        i += 1
        while i < len(segs) and segs[i][2] == 0:
            hi = segs[i][1]
            i += 1
        start = lo
        for b in (0x10FC0, 0x20000):
            if start < b <= hi:
                n = b - start
                sized.append((start, b - 1, 2 if n <= 248 else 3 if n <= 61504 else 4))
                start = b
        n = hi - start + 1
        sized.append((start, hi, 2 if n <= 248 else 3 if n <= 61504 else 4))

    ranges: List[Range] = []
    lead = 1
    j = 0
    while j < len(sized):
        lo, hi, size = sized[j]
        j += 1
        while j < len(sized) and sized[j][2] == size and sized[j][0] == hi + 1:
            hi = sized[j][1]
            j += 1
        per = 248 ** (size - 1)
        leads = -(-(hi - lo + 1) // per)
        ranges.append(Range(lo, hi, size, lead, leads))
        lead += leads
    assert lead - 1 <= 254
    return ranges


def update(s: State, c: int, k: int) -> None:
    if c <= 0x7F:
        if s.context == CJK and (0x41 <= c <= 0x5A or 0x61 <= c <= 0x7A):
            s.slot = LETTERS
        return
    if 0xAC00 <= c <= 0xD7A3:
        s.pending = None
        s.context = HANGUL
        return
    if 0x3040 <= c <= 0x30FF or 0x3400 <= c <= 0x9FFF or 0x20000 <= c <= 0x3FFFF or (0x3000 <= c <= 0x303F and s.context != HANGUL):
        if s.context != CJK:
            s.context = CJK
            s.slot = LETTERS
        s.pending = None
        if 0x3041 <= c <= 0x3096:
            s.slot = HIRAGANA
        elif 0x30A1 <= c <= 0x30FA:
            s.slot = KATAKANA
        return
    if s.context != GENERAL and 0xFF00 <= c <= 0xFFFF:
        return
    w = window_of(c)
    if w is None:
        return
    if s.context == GENERAL and w == s.main:
        s.confidence = min(s.confidence + 1, 2)
        return
    if k > 2:
        s.second = w
    if s.context == GENERAL:
        if s.confidence > 0:
            s.confidence -= 1
            return
    elif s.pending != w:
        s.pending = w
        return
    if s.main != w:
        s.second = s.main
    elif s.second == w:
        s.second = None
    s.main = w
    s.context = GENERAL
    s.pending = None
    s.confidence = 0


class NotAValue(ValueError):
    pass


def encode(cps: List[int]) -> bytes:
    for i, c in enumerate(cps):
        if not 0 <= c <= 0x10FFFF:
            raise ValueError(c)
        if i > 0 and 0xD800 <= cps[i - 1] <= 0xDBFF and 0xDC00 <= c <= 0xDFFF:
            raise NotAValue("high surrogate followed by low surrogate")
    s = State()
    out = bytearray()
    for c in cps:
        layout = build_layout(s)
        r = next(x for x in layout if x.lo <= c <= x.hi)
        o = c - r.lo
        k = r.size
        q, rest = divmod(o, 248 ** (k - 1))
        out.append(r.first_lead + q)
        for e in range(k - 2, -1, -1):
            t, rest = divmod(rest, 248**e)
            out.append(t + 8)
        update(s, c, k)
    return bytes(out)


class RefError(ValueError):
    def __init__(self, reason: str, offset: int) -> None:
        super().__init__(f"{reason} at {offset}")
        self.reason = reason
        self.offset = offset


def decode(data: bytes) -> List[int]:
    s = State()
    out: List[int] = []
    pos = 0
    while pos < len(data):
        layout = build_layout(s)
        b = data[pos]
        if b == 0:
            raise RefError("zero-byte", pos)
        rg = next((r for r in layout if r.first_lead <= b < r.first_lead + r.lead_count), None)
        if rg is None:
            raise RefError("unused-lead", pos)
        start = pos
        pos += 1
        o = b - rg.first_lead
        for _ in range(rg.size - 1):
            if pos >= len(data):
                raise RefError("truncated", start)
            u = data[pos]
            if u == 0:
                raise RefError("zero-byte", start)
            if u < 8:
                raise RefError("invalid-trail", start)
            o = o * 248 + u - 8
            pos += 1
        c = rg.lo + o
        if c > rg.hi:
            raise RefError("past-range", start)
        if out and 0xD800 <= out[-1] <= 0xDBFF and 0xDC00 <= c <= 0xDFFF:
            raise RefError("surrogate-pair", start)
        out.append(c)
        update(s, c, pos - start)
    return out
