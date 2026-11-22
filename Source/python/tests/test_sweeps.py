"""Deterministic sweeps against the second oracle: wide runs of every length in every kind of wide layout, every window as
the main and the second window, the windows that hold U+FFFE/U+FFFF, and the edges of the format at the start of long values."""

from __future__ import annotations

import random

import pytest

import singulink_steno as steno

from . import oracle as O
from .generators import WINDOWS, s

# About a minute: run with `pytest -m slow` (CI does).
pytestmark = pytest.mark.slow

# Prefixes that reach different wide layouts: the range of the ideographs or syllables starts before U+4E00/U+AC00 when a neighbour of
# the same size joins it (second window U+A000.., main window U+AB80..), kana slots, second windows next to the blocks.
WIDE_PREFIXES = [
    [0x4E00],
    [0x3042],
    [0x30A2],
    [0x3042, 0x41],
    [0xA000, 0xA001, 0x4E00],  # main A000 (next to 9FFF), then CJK
    [0xA000, 0xA001, 0xC0, 0xC1, 0x4E00],  # second A000
    [0xAB80, 0xAB81, 0xAC00],  # main AB80 next to AC00, then Hangul
    [0xAB80, 0xAB81, 0xC0, 0xC1, 0xAC00],  # second AB80
    [0xE000, 0xE001, 0xAC00],
    [0xAC00],
    [0xAC00, 0x3B1],  # Hangul with an odd pending window
    [0x4E00, 0x3B1, 0x3B2],
    [0x1F600, 0x1F601, 0x4E00],
    [0x10FF80, 0x10FFFF, 0xAC00],
]


@pytest.mark.parametrize("prefix", WIDE_PREFIXES, ids=lambda p: "-".join("%X" % c for c in p))
def test_wide_runs_of_every_length(prefix: list) -> None:  # type: ignore[type-arg]
    rng = random.Random(sum(prefix))
    for lo, hi in ((0x4E00, 0x9FFF), (0xAC00, 0xD7A3)):
        for n in [*range(41), 63, 64, 65, 255, 1000, 5000]:
            for variant in range(3):
                if variant == 0:
                    run = [rng.randint(lo, hi) for _ in range(n)]
                elif variant == 1:
                    run = [lo + (i % 3) for i in range(n)] + [hi] * min(n, 3)
                else:
                    run = [hi - (i % 250) for i in range(n)]
                for suffix in ([], [0x61], [0x3B1], [hi + 1 if hi + 1 < 0xD800 else 0xD7FF], [lo - 1], [0xFFFE], [0xD800]):
                    cps = prefix + run + suffix
                    want = O.encode(cps)
                    assert steno.encode(s(cps)) == want, (prefix, n, variant, suffix)
                    assert steno.decode(want) == s(cps), (prefix, n, variant, suffix)


@pytest.mark.parametrize("prefix", WIDE_PREFIXES, ids=lambda p: "-".join("%X" % c for c in p))
def test_wide_runs_with_a_bad_pair_inside(prefix: list) -> None:  # type: ignore[type-arg]
    rng = random.Random(len(prefix))
    for lo, hi in ((0x4E00, 0x9FFF), (0xAC00, 0xD7A3)):
        for n in (1, 2, 15, 16, 17, 31, 32, 33, 100):
            cps = prefix + [rng.randint(lo, hi) for _ in range(n)]
            data = O.encode(cps)
            base = len(O.encode(prefix))
            for j in range(base, len(data)):
                for v in (0, 1, 7, 8, 0xFE, 0xFF):
                    bad = data[:j] + bytes([v]) + data[j + 1 :]
                    try:
                        want = ("ok", s(O.decode(bad)))
                    except O.OErr as e:
                        want = (e.reason, e.offset)  # type: ignore[assignment]
                    try:
                        got = ("ok", steno.decode(bad))
                    except steno.StenoDecodeError as e:
                        got = (e.reason.value, e.offset)  # type: ignore[assignment]
                    assert got == want, (prefix, n, j, v)


def test_every_window_as_main_and_second() -> None:
    rng = random.Random(3)
    for w in WINDOWS:
        lo, hi = O.win(w)  # type: ignore[misc]
        other = rng.choice(WINDOWS)
        olo, ohi = O.win(other)  # type: ignore[misc]
        body = [lo, hi, rng.randint(lo, hi), 0x20, rng.randint(lo, hi), hi, lo]
        for cps in (
            [lo, lo + 1, *body],  # w main
            [lo, lo + 1, olo, olo + 1, *body, ohi],  # w second, then main again
            [olo, olo + 1, lo, olo, *body],
            [0x4E00, lo, lo, *body, 0x4E01, lo],  # leaving CJK into w
            [0xAC00, lo, 0x61, lo, *body, 0xAC01],
        ):
            want = O.encode(cps)
            assert steno.encode(s(cps)) == want, ("%X" % w, cps)
            assert steno.decode(want) == s(cps), ("%X" % w, cps)


def test_windows_with_fffe_and_ffff() -> None:
    for w in (0xFF80, 0xFF00, 0xFE80, 0x1FF80, 0x4FF80, 0x10FF80, 0xFFF80, 0x10F80):
        lo, hi = O.win(w)  # type: ignore[misc]
        all_cps = list(range(lo, hi + 1))
        for start in ([lo, lo + 1], [lo, lo + 1, 0xC0], [0x4E00], [0x3042], [0xAC00], [0x41, 0xFFFE], [0xFFFE, 0xFFFF, 0xFFFE]):
            for cps in (start + all_cps, start + all_cps[::-1], start + [0xFFFE] * 300 + [0xFFFF] * 3, start + [0xFFFF, 0xFFFE] * 50):
                want = O.encode(cps)
                assert steno.encode(s(cps)) == want, ("%X" % w, start)
                assert steno.decode(want) == s(cps), ("%X" % w, start)
                dec = steno.Decoder()
                assert "".join(dec.decode(want[i : i + 1]) for i in range(len(want))) + dec.decode(b"", True) == s(cps)


@pytest.mark.parametrize("first", [0xFEFF, 0xFFFE, 0xFFFF, 0x0EFF, 0x0F00, 0x10FBF, 0x10FC0, 0x1FFFF, 0x20000, 0x10FFFF, 0xD800, 0xDC00])
def test_edges_at_the_start_of_long_values(first: int) -> None:
    rng = random.Random(first)
    pools = [(0x20, 0x7E), (0x400, 0x47F), (0x4E00, 0x9FFF), (0xAC00, 0xD7A3), (0x3041, 0x30FC), (0x1F600, 0x1F64F), (0xFF00, 0xFFFF)]
    for lo, hi in pools:
        cps = [first] + [rng.randint(lo, hi) for _ in range(60000)]
        for i in range(1, len(cps)):
            if 0xD800 <= cps[i - 1] <= 0xDBFF and 0xDC00 <= cps[i] <= 0xDFFF:
                cps[i] = 0x61
        want = O.encode(cps)
        assert steno.encode(s(cps)) == want
        assert steno.decode(want) == s(cps)
