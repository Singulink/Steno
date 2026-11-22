"""A second independent plain codec of the Steno Encoding, version 1, written from FORMAT.md only by an independent reviewer.

It shares no code with the package or with tests/reference.py. Layouts are memoised by (context, slot, main, second), nothing else.
Code points are lists of ints. Errors are (reason, offset of the lead byte).
"""

from __future__ import annotations

from typing import Dict, List, Optional, Tuple

MAXC = 0x10FFFF
_ROWS = [
    (0x00BF, None),
    (0x013F, 0xC0),
    (0x01BF, 0x140),
    (0x023F, 0x1C0),
    (0x02BF, 0x240),
    (0x02FF, 0x2C0),
    (0x036F, 0x300),
    (0x037F, None),
    (0x04FF, "b"),
    (0x052F, 0x500),
    (0x058F, 0x530),
    (0x05FF, 0x590),
    (0x1FFF, "b"),
    (0x20FF, None),
    (0x2FFF, "b"),
    (0x30FF, None),
    (0x33FF, "b"),
    (0x9FFF, None),
    (0xABFF, "b"),
    (0xDFFF, None),
    (0x1FFFF, "b"),
    (0x3FFFF, None),
    (0x10FFFF, "b"),
]
_ENDS = {0xC0: 0x13F, 0x140: 0x1BF, 0x1C0: 0x23F, 0x240: 0x2BF, 0x2C0: 0x2FF, 0x300: 0x36F, 0x500: 0x52F, 0x530: 0x58F, 0x590: 0x5FF}


def win(c: int) -> Optional[Tuple[int, int]]:
    for last, w in _ROWS:
        if c <= last:
            if w is None:
                return None
            if w == "b":
                s = c - c % 0x80
                return (s, s + 0x7F)
            return (w, _ENDS[w])  # type: ignore[index]
    raise ValueError(c)


_LCACHE: Dict[tuple, list] = {}


def layout(ctx: str, slot: str, main: Tuple[int, int], second: Optional[Tuple[int, int]]) -> List[Tuple[int, int, int, int, int]]:
    """Returns ranges (lo, hi, size, firstLead, leadCount)."""
    key = (ctx, slot if ctx == "cjk" else "letters", main, second)
    got = _LCACHE.get(key)
    if got is not None:
        return got
    # Breakpoint overlay: a dict of boundaries is overkill; use an interval list rebuilt per piece.
    iv: List[List[int]] = [[0, MAXC, 0]]

    def put(lo: int, hi: int, s: int) -> None:
        nonlocal iv
        if lo > hi:
            return
        new: List[List[int]] = []
        for a, b, z in iv:
            if b < lo or a > hi:
                new.append([a, b, z])
            else:
                if a < lo:
                    new.append([a, lo - 1, z])
                if b > hi:
                    new.append([hi + 1, b, z])
        new.append([lo, hi, s])
        new.sort()
        iv = new

    m0, m1 = main
    if ctx == "general":
        put(max(0x80, m0 - 248), m0 - 1, 2)
        if m1 < MAXC:
            put(m1 + 1, min(MAXC, m1 + 248), 2)
    put(0, 0x1F, 2)
    put(9, 10, 1)
    put(13, 13, 1)
    if ctx == "cjk" and slot != "letters":
        put(0x20, 0x40, 1)
        put(0x41, 0x7E, 2)
    else:
        put(0x20, 0x7E, 1)
    put(0x7F, 0xBF, 2)
    if ctx != "cjk":
        put(0x80, 0xEFF, 2)
    put(0x2000, 0x20CF, 2)
    if second is not None:
        put(second[0], second[1], 2)
    if ctx == "general":
        put(m0, m1, 1)
    elif ctx == "hangul":
        put(m0, m1, 2)
        put(0x3000, 0x303F, 2)
        put(0xAC00, 0xD7A3, 2)
        put(0xFF00, 0xFFFF, 2)
    else:
        put(m0, m1, 2)
        put(0x3000, 0x300F, 1)
        put(0x3010, 0x30FF, 2)
        if slot == "hiragana":
            put(0x3041, 0x3096, 1)
        if slot == "katakana":
            put(0x30A1, 0x30FC, 1)
        put(0x4E00, 0x9FFF, 2)
        put(0xFF00, 0xFFFF, 2)
        if slot == "letters":
            put(0xFF00, 0xFF1F, 1)
    # stretches: merge adjacent uncovered
    pieces: List[List[int]] = []
    for a, b, z in iv:
        if z == 0 and pieces and pieces[-1][2] == 0 and pieces[-1][1] == a - 1:
            pieces[-1][1] = b
        else:
            pieces.append([a, b, z])
    sized: List[List[int]] = []
    for a, b, z in pieces:
        if z:
            sized.append([a, b, z])
            continue
        parts = []
        cur = a
        for cut in (0x10FC0, 0x20000):
            if cur < cut <= b:
                parts.append((cur, cut - 1))
                cur = cut
        parts.append((cur, b))
        for x, y in parts:
            n = y - x + 1
            sized.append([x, y, 2 if n <= 248 else (3 if n <= 61504 else 4)])
    ranges: List[List[int]] = []
    for a, b, z in sized:
        if ranges and ranges[-1][2] == z and ranges[-1][1] == a - 1:
            ranges[-1][1] = b
        else:
            ranges.append([a, b, z])
    out = []
    lead = 1
    for a, b, z in ranges:
        per = 248 ** (z - 1)
        n = -(-(b - a + 1) // per)
        out.append((a, b, z, lead, n))
        lead += n
    assert lead - 1 <= 254
    _LCACHE[key] = out
    return out


class St:
    __slots__ = ("conf", "ctx", "main", "pending", "second", "slot")

    def __init__(self) -> None:
        self.ctx = "general"
        self.main = (0xC0, 0x13F)
        self.second: Optional[Tuple[int, int]] = None
        self.slot = "letters"
        self.conf = 0
        self.pending: Optional[Tuple[int, int]] = None

    def lay(self):
        return layout(self.ctx, self.slot, self.main, self.second)

    def upd(self, c: int, k: int) -> None:
        if c <= 0x7F:
            if self.ctx == "cjk" and (0x41 <= c <= 0x5A or 0x61 <= c <= 0x7A):
                self.slot = "letters"
            return
        if 0xAC00 <= c <= 0xD7A3:
            self.pending = None
            self.ctx = "hangul"
            return
        if 0x3040 <= c <= 0x30FF or 0x3400 <= c <= 0x9FFF or 0x20000 <= c <= 0x3FFFF or (self.ctx != "hangul" and 0x3000 <= c <= 0x303F):
            if self.ctx != "cjk":
                self.ctx = "cjk"
                self.slot = "letters"
            self.pending = None
            if 0x3041 <= c <= 0x3096:
                self.slot = "hiragana"
            elif 0x30A1 <= c <= 0x30FA:
                self.slot = "katakana"
            return
        if self.ctx != "general" and 0xFF00 <= c <= 0xFFFF:
            return
        w = win(c)
        if w is None:
            return
        if self.ctx == "general" and w == self.main:
            self.conf = min(self.conf + 1, 2)
            return
        if k >= 3:
            self.second = w
        if self.ctx == "general":
            if self.conf > 0:
                self.conf -= 1
                return
        else:
            if self.pending != w:
                self.pending = w
                return
        if self.main != w:
            self.second = self.main
        elif self.second == w:
            self.second = None
        self.main = w
        self.ctx = "general"
        self.pending = None
        self.conf = 0


def is_value(cps: List[int]) -> bool:
    return not any(0xD800 <= a <= 0xDBFF and 0xDC00 <= b <= 0xDFFF for a, b in zip(cps, cps[1:]))


def encode(cps: List[int]) -> bytes:
    if not is_value(cps):
        raise ValueError("not a value")
    st = St()
    out = bytearray()
    for c in cps:
        for lo, hi, k, f, _n in st.lay():  # noqa: B007 (k and f are used after the loop)
            if lo <= c <= hi:
                break
        o = c - lo
        per = 248 ** (k - 1)
        q, r = divmod(o, per)
        out.append(f + q)
        for e in range(k - 2, -1, -1):
            t, r = divmod(r, 248**e)
            out.append(t + 8)
        st.upd(c, k)
    return bytes(out)


class OErr(Exception):
    def __init__(self, reason: str, offset: int) -> None:
        super().__init__(reason, offset)
        self.reason = reason
        self.offset = offset


def decode(data: bytes) -> List[int]:
    st = St()
    out: List[int] = []
    i = 0
    while i < len(data):
        b = data[i]
        if b == 0:
            raise OErr("zero-byte", i)
        rg = None
        for r in st.lay():
            if r[3] <= b < r[3] + r[4]:
                rg = r
                break
        if rg is None:
            raise OErr("unused-lead", i)
        lo, hi, k, f, _n = rg
        start = i
        o = b - f
        i += 1
        for _ in range(k - 1):
            if i >= len(data):
                raise OErr("truncated", start)
            u = data[i]
            if u == 0:
                raise OErr("zero-byte", start)
            if u < 8:
                raise OErr("invalid-trail", start)
            o = o * 248 + u - 8
            i += 1
        c = lo + o
        if c > hi:
            raise OErr("past-range", start)
        if out and 0xD800 <= out[-1] <= 0xDBFF and 0xDC00 <= c <= 0xDFFF:
            raise OErr("surrogate-pair", start)
        out.append(c)
        st.upd(c, k)
    return out
