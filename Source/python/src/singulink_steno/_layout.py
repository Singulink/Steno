"""The state, the windows, the layouts and the state update of the Steno Encoding, version 1 (FORMAT.md sections 4 to 7).

This module is internal. It is shared by every backend that is written in Python.
"""

from __future__ import annotations

import codecs
import re
import threading
from bisect import bisect_right
from functools import lru_cache
from itertools import accumulate
from typing import Any, Dict, List, Optional, Tuple

GENERAL = 0
"""The general context."""

CJK = 1
"""The CJK context."""

HANGUL = 2
"""The Hangul context."""

LETTERS = 0
"""The kana slot "letters"."""

HIRAGANA = 1
"""The kana slot "hiragana"."""

KATAKANA = 2
"""The kana slot "katakana"."""

NONE = -1
"""No window (for the second window and the pending window)."""

MAX_CODE_POINT = 0x10FFFF

INITIAL_MAIN = 0x00C0
"""The first code point of the main window of the initial state."""


def window_start(c: int) -> int:
    """Gets the first code point of the window of a code point (FORMAT.md section 4), or -1 if it has none."""
    if c <= 0x00BF:
        return -1
    if c <= 0x013F:
        return 0x00C0
    if c <= 0x01BF:
        return 0x0140
    if c <= 0x023F:
        return 0x01C0
    if c <= 0x02BF:
        return 0x0240
    if c <= 0x02FF:
        return 0x02C0
    if c <= 0x036F:
        return 0x0300
    if c <= 0x037F:
        return -1
    if c <= 0x04FF:
        return c & ~0x7F
    if c <= 0x052F:
        return 0x0500
    if c <= 0x058F:
        return 0x0530
    if c <= 0x05FF:
        return 0x0590
    if c <= 0x1FFF:
        return c & ~0x7F
    if c <= 0x20FF:
        return -1
    if c <= 0x2FFF:
        return c & ~0x7F
    if c <= 0x30FF:
        return -1
    if c <= 0x33FF:
        return c & ~0x7F
    if c <= 0x9FFF:
        return -1
    if c <= 0xABFF:
        return c & ~0x7F
    if c <= 0xDFFF:
        return -1
    if c <= 0x1FFFF:
        return c & ~0x7F
    if c <= 0x3FFFF:
        return -1
    return c & ~0x7F


_ODD_WINDOW_ENDS = {0x02C0: 0x02FF, 0x0300: 0x036F, 0x0500: 0x052F, 0x0530: 0x058F, 0x0590: 0x05FF}


def window_end(start: int) -> int:
    """Gets the last code point of a window from its first code point."""
    return _ODD_WINDOW_ENDS.get(start, start + 0x7F)


def _all_windows() -> Tuple[int, ...]:
    w: List[int] = [0x00C0, 0x0140, 0x01C0, 0x0240, 0x02C0, 0x0300]
    w += range(0x0380, 0x0500, 0x80)
    w += [0x0500, 0x0530, 0x0590]
    w += range(0x0600, 0x2000, 0x80)
    w += range(0x2100, 0x3000, 0x80)
    w += range(0x3100, 0x3400, 0x80)
    w += range(0xA000, 0xAC00, 0x80)
    w += range(0xE000, 0x20000, 0x80)
    w += range(0x40000, 0x110000, 0x80)
    return tuple(w)


WINDOWS = _all_windows()
"""The first code point of every window, in code point order. There are 7,356 (FORMAT.md section 4)."""

WINDOW_INDEX: Dict[int, int] = {start: i for i, start in enumerate(WINDOWS)}
"""The index in WINDOWS of each window, by its first code point."""

assert len(WINDOWS) == 7356

# The sizes of a range: code points per lead byte for each size 1 to 4.
_PER_LEAD = (0, 1, 248, 61504, 15252992)


_BIG = 1 << 30


def _piece(segs: List[Tuple[int, int, int]], lo: int, hi: int, size: int) -> None:
    """Lays a piece over the segments: the code points lo..hi get the size."""
    i = bisect_right(segs, (lo, _BIG)) - 1
    j = bisect_right(segs, (hi, _BIG)) - 1
    new = [(lo, hi, size)]
    glo, _, gsize = segs[i]
    if glo < lo:
        new.insert(0, (glo, lo - 1, gsize))
    _, ghi, gsize = segs[j]
    if ghi > hi:
        new.append((hi + 1, ghi, gsize))
    segs[i : j + 1] = new


def _fill(segs: List[Tuple[int, int, int]], lo: int, hi: int) -> None:
    """Gives the code points lo..hi that no piece covers the size 2 (the neighbours, which every other piece overrides)."""
    i = bisect_right(segs, (lo, _BIG)) - 1
    while i < len(segs):
        glo, ghi, gsize = segs[i]
        if glo > hi:
            return
        if gsize != 0:
            i += 1
            continue
        a = glo if glo > lo else lo
        b = ghi if ghi < hi else hi
        new = [(a, b, 2)]
        if glo < a:
            new.insert(0, (glo, a - 1, 0))
        if ghi > b:
            new.append((b + 1, ghi, 0))
        segs[i : i + 1] = new
        i += len(new)


@lru_cache(maxsize=8)
def _base_segments(ctx: int, slot: int) -> Tuple[Tuple[int, int, int], ...]:
    """The segments after pieces 2 to 6 (FORMAT.md section 6.2), which depend only on the context and the kana slot."""
    segs: List[Tuple[int, int, int]] = [(0, MAX_CODE_POINT, 0)]
    # Piece 2.
    _piece(segs, 0x00, 0x1F, 2)
    _piece(segs, 0x09, 0x0A, 1)
    _piece(segs, 0x0D, 0x0D, 1)
    # Piece 3.
    if ctx == CJK and slot != LETTERS:
        _piece(segs, 0x20, 0x40, 1)
        _piece(segs, 0x41, 0x7E, 2)
    else:
        _piece(segs, 0x20, 0x7E, 1)
    # Pieces 4 to 6.
    _piece(segs, 0x7F, 0xBF, 2)
    if ctx != CJK:
        _piece(segs, 0x80, 0xEFF, 2)
    _piece(segs, 0x2000, 0x20CF, 2)
    return tuple(segs)


# The pieces of item 8 after the main window, in the wide contexts (FORMAT.md section 6.2).
_TOP_PIECES = {
    (HANGUL, LETTERS): [(0x3000, 0x303F, 2), (0xAC00, 0xD7A3, 2), (0xFF00, 0xFFFF, 2)],
    (CJK, LETTERS): [(0x3000, 0x300F, 1), (0x3010, 0x30FF, 2), (0x4E00, 0x9FFF, 2), (0xFF00, 0xFFFF, 2), (0xFF00, 0xFF1F, 1)],
    (CJK, HIRAGANA): [(0x3000, 0x300F, 1), (0x3010, 0x30FF, 2), (0x3041, 0x3096, 1), (0x4E00, 0x9FFF, 2), (0xFF00, 0xFFFF, 2)],
    (CJK, KATAKANA): [(0x3000, 0x300F, 1), (0x3010, 0x30FF, 2), (0x30A1, 0x30FC, 1), (0x4E00, 0x9FFF, 2), (0xFF00, 0xFFFF, 2)],
}


def _build_ranges(ctx: int, slot: int, main: int, second: int) -> Tuple[List[int], List[int], List[int]]:
    """Builds the ranges of a layout (steps 1 to 3 of FORMAT.md section 6.1): their first and last code points and their sizes, in order."""
    # Segments that cover U+0000..U+10FFFF: (first, last, size), where size 0 means that no piece covers them. Pieces 2 to 6 depend only on
    # the context and the kana slot, so they come laid from a table.
    segs = list(_base_segments(ctx, slot))
    m0 = main
    m1 = window_end(main)

    # Piece 1: the neighbours of the main window. They come first, so they show only where pieces 2 to 6 leave gaps.
    if ctx == GENERAL:
        _fill(segs, max(0x80, m0 - 248), m0 - 1)
        if m1 < MAX_CODE_POINT:
            _fill(segs, m1 + 1, min(MAX_CODE_POINT, m1 + 248))

    # Piece 7.
    if second >= 0:
        _piece(segs, second, window_end(second), 2)

    # Piece 8.
    if ctx == GENERAL:
        _piece(segs, m0, m1, 1)
    else:
        _piece(segs, m0, m1, 2)
        for lo, hi, size in _TOP_PIECES[(ctx, slot)]:
            _piece(segs, lo, hi, size)

    # Section 6.3: size the parts of each stretch. Section 6.4: join runs of the same size into ranges.
    los: List[int] = []
    his: List[int] = []
    sizes: List[int] = []
    last = 0
    stretch_lo = -1
    stretch_hi = -1
    for lo, hi, k in segs:
        if k == 0:
            if stretch_lo < 0:
                stretch_lo = lo
            stretch_hi = hi
            continue
        if stretch_lo >= 0:
            for a, b, pk in _stretch_parts(stretch_lo, stretch_hi):
                if pk == last:
                    his[-1] = b
                else:
                    los.append(a)
                    his.append(b)
                    sizes.append(pk)
                    last = pk
            stretch_lo = -1
        if k == last:
            his[-1] = hi
        else:
            los.append(lo)
            his.append(hi)
            sizes.append(k)
            last = k
    if stretch_lo >= 0:
        for a, b, pk in _stretch_parts(stretch_lo, stretch_hi):
            if pk == last:
                his[-1] = b
            else:
                los.append(a)
                his.append(b)
                sizes.append(pk)
                last = pk
    return los, his, sizes


def _stretch_parts(lo: int, hi: int) -> List[Tuple[int, int, int]]:
    """Splits a stretch into parts at U+10FC0 and U+20000 and gives each its size (FORMAT.md section 6.3)."""
    parts = []
    for bound in (0x10FC0, 0x20000):
        if lo < bound <= hi:
            n = bound - lo
            parts.append((lo, bound - 1, 2 if n <= 248 else 3 if n <= 61504 else 4))
            lo = bound
    n = hi - lo + 1
    parts.append((lo, hi, 2 if n <= 248 else 3 if n <= 61504 else 4))
    return parts


def _class_escape(c: int) -> str:
    return "\\U%08x" % c


def _bytes_class(runs: List[Tuple[int, int]]) -> bytes:
    """Writes runs of byte values (first, last) as the inside of a character class."""
    return b"".join(b"\\x%02x-\\x%02x" % run for run in runs)


_NO_CHAR = chr(0xFFFE)
"""What the tables of the codecs module put for a byte that has no code point."""


@lru_cache(maxsize=2048)
def _compile(pattern: Any) -> Any:
    """Compiles a pattern. Many layouts share their patterns, and compiling takes longer than the rest of building a layout."""
    return re.compile(pattern).match


_NO_PAIRS = _compile(b"")


class Layout:
    """The ranges of one layout (FORMAT.md section 6), with the tables that the codec looks things up in.

    Building a layout makes only what one code point needs: the ranges and their lead bytes. The tables for runs of 1-byte code points (the
    charmap tables of the codecs module and the regular expressions that find a run) are built by build_tables() when the layout is used a
    second time, so text that hops between windows, with a new layout at almost every code point, does not pay for them.

    A layout holds no reference to another layout, so the cache can drop any of them.
    """

    __slots__ = (
        "cjk1_bytes",
        "ctx",
        "dec_run",
        "dec_table",
        "enc_map",
        "enc_run",
        "first",
        "hi",
        "lead_count",
        "lead_range",
        "leads",
        "lo",
        "main",
        "main_bytes",
        "ready",
        "second",
        "size",
        "slot",
        "uses",
        "wide_first",
        "wide_hi",
        "wide_lo",
        "wide_r0",
        "wide_range",
        "wide_run",
    )

    def __init__(self, ctx: int, slot: int, main: int, second: int) -> None:
        self.ctx = ctx
        self.slot = slot
        self.main = main
        self.second = second
        lo, hi, size = _build_ranges(ctx, slot, main, second)
        per = _PER_LEAD
        counts = [(b - a + per[k]) // per[k] for a, b, k in zip(lo, hi, size)]
        first = list(accumulate(counts, initial=1))
        lead = first.pop()
        if lead - 1 > 0xFE:
            raise AssertionError("A Steno layout needs more than 254 lead bytes. This is a defect of the implementation.")
        self.lo = lo
        self.hi = hi
        self.size = size
        self.first = first
        self.leads = counts
        self.lead_count = lead - 1
        self.ready = False
        self.uses = 0
        self.lead_range: Any = None
        self.enc_run: Any = None
        self.dec_run: Any = None
        self.enc_map: Any = None
        self.dec_table: Any = None
        self.main_bytes = b""
        self.cjk1_bytes = b""
        self.wide_run: Any = _NO_PAIRS

        # In the wide contexts, the block of the ideographs (CJK) or of the Hangul syllables (Hangul) lies in one range of size 2 (FORMAT.md
        # section 14.3); a code point of it only sets the pending window to none.
        if ctx == GENERAL:
            self.wide_lo, self.wide_hi, self.wide_r0, self.wide_first, self.wide_range = 1, 0, 0, 0, -1
        else:
            self.wide_lo, self.wide_hi = (0x4E00, 0x9FFF) if ctx == CJK else (0xAC00, 0xD7A3)
            r = self.find(self.wide_lo)
            if size[r] != 2 or hi[r] < self.wide_hi:
                raise AssertionError("The wide block is not in one range of size 2. This is a defect of the implementation.")
            self.wide_r0 = lo[r]
            self.wide_first = first[r]
            self.wide_range = r

    def build_tables(self) -> None:
        """Builds the tables for runs of 1-byte code points and the table of lead bytes. Safe to call from several threads at once."""
        if self.ready:
            return
        ctx = self.ctx
        lead_range = [-1] * 256
        dec = [_NO_CHAR] * 256
        one_byte: List[Tuple[int, int]] = []  # runs of code points that take 1 byte
        one_leads: List[Tuple[int, int]] = []  # their bytes
        main_bytes: List[Tuple[int, int]] = []
        cjk1_bytes: List[Tuple[int, int]] = []
        for r, (a, b, k, lead, n) in enumerate(zip(self.lo, self.hi, self.size, self.first, self.leads)):
            lead_range[lead : lead + n] = [r] * n
            if k == 1:
                # U+FFFE marks a byte without a code point in the tables of the codecs module, so it cannot be in them. It takes the
                # slower path of the other code points.
                runs = [(x, y) for x, y in ((a, 0xFFFD), (0xFFFF, b)) if x <= y] if a <= 0xFFFE <= b else [(a, b)]
                one_byte += runs
                for x, y in runs:
                    byte_run = (lead + x - a, lead + y - a)
                    one_leads.append(byte_run)
                    if ctx == GENERAL and x >= 0x80:
                        main_bytes.append(byte_run)
                    if ctx == CJK and x >= 0x3000 and y <= 0x30FF:
                        cjk1_bytes.append(byte_run)
                dec[lead : lead + n] = map(chr, range(a, b + 1))

        # The code points that take 1 byte: a run of them is encoded and decoded by the C code of the codecs module.
        dec_table = "".join(dec)
        self.dec_table = dec_table
        # charmap_build makes a fast table only if byte 00 maps to U+0000. No run passes U+0000 to it, so the entry is never used.
        self.enc_map = codecs.charmap_build("\x00" + dec_table[1:])
        self.dec_run = _compile(b"[" + _bytes_class(one_leads) + b"]*")
        # In the general context, the bytes of the main window, which raise the confidence. In the CJK context, the 1-byte code points in
        # U+3000..U+30FF, which set the pending window to none.
        self.main_bytes = b"".join(bytes(range(x, y + 1)) for x, y in main_bytes)
        self.cjk1_bytes = b"".join(bytes(range(x, y + 1)) for x, y in cjk1_bytes)
        if self.wide_range >= 0:
            r = self.wide_range
            # A run of pairs of a lead byte of that range and a trail byte (08..FF).
            lead_run = (self.first[r], self.first[r] + self.leads[r] - 1)
            self.wide_run = _compile(b"(?:[" + _bytes_class([lead_run]) + b"][\\x08-\\xff])*")
        self.lead_range = lead_range
        self.enc_run = _compile("[" + "".join(_class_escape(a) + "-" + _class_escape(b) for a, b in one_byte) + "]*")
        self.ready = True

    def range_of_lead(self, b: int) -> int:
        """Finds the index of the range that a lead byte belongs to, or -1 if the byte is not a lead byte of the layout (also 00)."""
        lead_range = self.lead_range
        if lead_range is not None:
            return lead_range[b]  # type: ignore[no-any-return]
        r = bisect_right(self.first, b) - 1
        if r < 0 or b >= self.first[r] + self.leads[r]:
            return -1
        return r

    def find(self, c: int) -> int:
        """Finds the index of the range that holds a code point."""
        return bisect_right(self.lo, c) - 1


LAYOUT_CACHE_LIMIT = 1024
"""The most layouts that the cache keeps."""

RECENT_LAYOUTS = 4
"""The most layouts that one state keeps for itself."""

_LayoutKey = Tuple[int, int, int, int]
_cache: Dict[_LayoutKey, Layout] = {}
_cache_lock = threading.Lock()

INITIAL_LAYOUT = Layout(GENERAL, LETTERS, INITIAL_MAIN, NONE)
INITIAL_LAYOUT.build_tables()
"""The layout of the initial state. It is kept outside the cache, so it is never dropped."""


def get_layout(ctx: int, slot: int, main: int, second: int) -> Layout:
    """Gets the layout of a state from the cache, or builds it and adds it.

    Reading the cache takes no lock. Adding to it takes a lock, and when the cache is full the oldest layout is dropped. A state that still
    uses a dropped layout keeps it alive by itself; layouts refer to no other layouts.
    """
    if ctx != CJK:
        slot = LETTERS
    key = (ctx, slot, main, second)
    layout = _cache.get(key)
    if layout is not None:
        return layout
    if key == (GENERAL, LETTERS, INITIAL_MAIN, NONE):
        return INITIAL_LAYOUT
    layout = Layout(ctx, slot, main, second)
    with _cache_lock:
        existing = _cache.get(key)
        if existing is not None:
            return existing
        while len(_cache) >= LAYOUT_CACHE_LIMIT:
            del _cache[next(iter(_cache))]
        _cache[key] = layout
    return layout


def layout_cache_size() -> int:
    """Gets the number of layouts in the cache. For tests."""
    return len(_cache)


def clear_layout_cache() -> None:
    """Empties the layout cache. For tests."""
    with _cache_lock:
        _cache.clear()


class State:
    """The state of FORMAT.md section 5, with its layout."""

    __slots__ = ("conf", "ctx", "layout", "main", "pending", "recent", "second", "slot")

    def __init__(self) -> None:
        self.ctx = GENERAL
        self.slot = LETTERS
        self.main = INITIAL_MAIN
        self.second = NONE
        self.conf = 0
        self.pending = NONE
        self.layout = INITIAL_LAYOUT
        # The last few layouts of this state, so that text that goes back and forth between a few states does not depend on the shared
        # cache, which text that hops between many windows can fill.
        self.recent: Dict[_LayoutKey, Layout] = {}

    def reset(self) -> None:
        """Sets the state back to the initial state."""
        self.ctx = GENERAL
        self.slot = LETTERS
        self.main = INITIAL_MAIN
        self.second = NONE
        self.conf = 0
        self.pending = NONE
        self.layout = INITIAL_LAYOUT

    def copy_from(self, other: State) -> None:
        """Copies the fields of another state into this one."""
        self.ctx = other.ctx
        self.slot = other.slot
        self.main = other.main
        self.second = other.second
        self.conf = other.conf
        self.pending = other.pending
        self.layout = other.layout

    def set_fields(self, ctx: int, slot: int, main: int, second: int, conf: int, pending: int) -> None:
        """Sets all fields and the layout that belongs to them."""
        self.ctx = ctx
        self.slot = slot if ctx == CJK else LETTERS
        self.main = main
        self.second = second
        self.conf = conf if ctx == GENERAL else 0
        self.pending = pending if ctx != GENERAL else NONE
        self.layout = get_layout(ctx, self.slot, main, second)

    def update(self, c: int, k: int) -> None:
        """Updates the state after a code point (FORMAT.md section 7.1), and the layout if the update changed it.

        Args:
            c: The code point.
            k: The number of bytes that the code point took in the layout before the update.
        """
        # Step 1.
        if c <= 0x7F:
            if self.ctx == CJK and self.slot != LETTERS and (0x41 <= c <= 0x5A or 0x61 <= c <= 0x7A):
                self.slot = LETTERS
                self._relayout()
            return

        # Below U+3000 no code point is a Hangul syllable or a CJK code point, so steps 2 to 4 do nothing there.
        if c >= 0x3000:
            # Step 2.
            if 0xAC00 <= c <= 0xD7A3:
                self.pending = NONE
                if self.ctx != HANGUL:
                    self.ctx = HANGUL
                    self._relayout()
                return

            # Step 3.
            if 0x3040 <= c <= 0x30FF or 0x3400 <= c <= 0x9FFF or 0x20000 <= c <= 0x3FFFF or (0x3000 <= c <= 0x303F and self.ctx != HANGUL):
                changed = False
                if self.ctx != CJK:
                    self.ctx = CJK
                    self.slot = LETTERS
                    changed = True
                self.pending = NONE
                if 0x3041 <= c <= 0x3096:
                    if self.slot != HIRAGANA:
                        self.slot = HIRAGANA
                        changed = True
                elif 0x30A1 <= c <= 0x30FA and self.slot != KATAKANA:
                    self.slot = KATAKANA
                    changed = True
                if changed:
                    self._relayout()
                return

            # Step 4.
            if self.ctx != GENERAL and 0xFF00 <= c <= 0xFFFF:
                return

        # Step 5.
        w = window_start(c)
        if w < 0:
            return

        # Step 6.
        if self.ctx == GENERAL and w == self.main:
            if self.conf < 2:
                self.conf += 1
            return

        # Step 7.
        changed = False
        if k >= 3 and self.second != w:
            self.second = w
            changed = True

        # Step 8.
        if self.ctx == GENERAL:
            if self.conf > 0:
                self.conf -= 1
                if changed:
                    self._relayout()
                return
        elif self.pending != w:
            self.pending = w
            if changed:
                self._relayout()
            return

        # Step 9.
        if self.main != w:
            self.second = self.main
        elif self.second == w:
            self.second = NONE
        self.main = w
        self.ctx = GENERAL
        self.pending = NONE
        self.conf = 0
        self._relayout()

    def _relayout(self) -> None:
        ctx = self.ctx
        key = (ctx, self.slot if ctx == CJK else LETTERS, self.main, self.second)
        recent = self.recent
        layout = recent.get(key)
        if layout is None:
            layout = get_layout(*key)
            if len(recent) >= RECENT_LAYOUTS:
                del recent[next(iter(recent))]
            recent[key] = layout
        self.layout = layout


# --- Packing a state into an integer, for getstate() and setstate() of the codec machinery. ---

_NW = len(WINDOWS)  # main window: an index 0.._NW-1
_NS = _NW + 1  # second window or pending window: 0 for none, else the index + 1

COMPACT_AUX = 18
COMPACT_LIMIT = COMPACT_AUX * _NS * _NW
"""The number of compact state numbers: 974,125,656, below 2**30, so that one fits the state field of a text file's tell() cookie."""

STATE_NUMBER_LIMIT = 1 << 30
"""Every state number that a decoder gives out is below this."""

_FULL_AUX = 3 + 4 * _NS


def _window_code(w: int) -> int:
    return 0 if w < 0 else WINDOW_INDEX[w] + 1


def _window_from_code(code: int) -> int:
    return NONE if code == 0 else WINDOWS[code - 1]


def pack_compact(st: State, last_high: bool) -> Optional[int]:
    """Packs a state, and whether the last code point was a high surrogate, into a number below COMPACT_LIMIT, or returns None.

    The compact number holds every state of the general context (with either value of last_high), and every state of a wide context whose
    pending window is none, the main window or the second window and whose last code point was not a high surrogate. The initial state is
    0.
    """
    ctx = st.ctx
    if ctx == GENERAL:
        aux = st.conf + (15 if last_high else 0)
    else:
        if last_high:
            return None
        p = st.pending
        if p == NONE:
            sel = 0
        elif p == st.main:
            sel = 1
        elif p == st.second:
            sel = 2
        else:
            return None
        aux = 3 + st.slot * 3 + sel if ctx == CJK else 12 + sel
    return (aux * _NS + _window_code(st.second)) * _NW + WINDOW_INDEX[st.main]


def unpack_compact(st: State, n: int) -> bool:
    """Sets a state from a number of pack_compact and returns last_high. Raises ValueError if the number is not one; st is then unchanged."""
    if not 0 <= n < COMPACT_LIMIT:
        raise ValueError("not a compact Steno state number")
    rest, main_index = divmod(n, _NW)
    aux, second_code = divmod(rest, _NS)
    main = WINDOWS[main_index]
    second = _window_from_code(second_code)
    if second == main:
        raise ValueError("not a compact Steno state number")
    if aux < 3 or aux >= 15:
        st.set_fields(GENERAL, LETTERS, main, second, aux % 15, NONE)
        return aux >= 15
    if aux < 12:
        ctx = CJK
        slot, sel = divmod(aux - 3, 3)
    else:
        ctx = HANGUL
        slot, sel = LETTERS, aux - 12
    if sel == 2 and second == NONE:
        raise ValueError("not a compact Steno state number")
    pending = (NONE, main, second)[sel]
    st.set_fields(ctx, slot, main, second, 0, pending)
    return False


def pack_full(st: State) -> int:
    """Packs any state into a number. The initial state is 0."""
    ctx = st.ctx
    if ctx == GENERAL:
        aux = st.conf
    elif ctx == CJK:
        aux = 3 + st.slot * _NS + _window_code(st.pending)
    else:
        aux = 3 + 3 * _NS + _window_code(st.pending)
    return (aux * _NS + _window_code(st.second)) * _NW + WINDOW_INDEX[st.main]


def unpack_full(st: State, n: int) -> None:
    """Sets a state from a number of pack_full. Raises ValueError if the number is not one."""
    if not 0 <= n < _FULL_AUX * _NS * _NW:
        raise ValueError("not a Steno state number")
    rest, main_index = divmod(n, _NW)
    aux, second_code = divmod(rest, _NS)
    main = WINDOWS[main_index]
    second = _window_from_code(second_code)
    if second == main:
        raise ValueError("not a Steno state number")
    if aux < 3:
        st.set_fields(GENERAL, LETTERS, main, second, aux, NONE)
    elif aux < 3 + 3 * _NS:
        slot, code = divmod(aux - 3, _NS)
        st.set_fields(CJK, slot, main, second, 0, _window_from_code(code))
    else:
        st.set_fields(HANGUL, LETTERS, main, second, 0, _window_from_code(aux - 3 - 3 * _NS))
