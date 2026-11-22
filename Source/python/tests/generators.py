"""Adversarial text generators for the fuzz tests, built on the second oracle's own window table."""

from __future__ import annotations

import random
from typing import List

from . import oracle as O

# Every window start, from the oracle's own table (not the package's).
WINDOWS: List[int] = sorted({O.win(c)[0] for c in range(0xC0, 0x110000, 16) if O.win(c) is not None})  # type: ignore[index]

EDGES = [
    0x0,
    0x8,
    0x9,
    0xA,
    0xB,
    0xD,
    0x1F,
    0x20,
    0x40,
    0x41,
    0x5A,
    0x61,
    0x7A,
    0x7E,
    0x7F,
    0x80,
    0xBF,
    0xC0,
    0x13F,
    0x140,
    0x2FF,
    0x300,
    0x36F,
    0x370,
    0x37F,
    0x380,
    0x4FF,
    0x500,
    0x52F,
    0x530,
    0x5FF,
    0x600,
    0xEFF,
    0xF00,
    0x1FFF,
    0x2000,
    0x20CF,
    0x20D0,
    0x20FF,
    0x2100,
    0x2FFF,
    0x3000,
    0x300F,
    0x3010,
    0x303F,
    0x3040,
    0x3041,
    0x3096,
    0x3097,
    0x30A0,
    0x30A1,
    0x30FA,
    0x30FB,
    0x30FC,
    0x30FD,
    0x30FF,
    0x3100,
    0x33FF,
    0x3400,
    0x4DFF,
    0x4E00,
    0x9FFF,
    0xA000,
    0xABFF,
    0xAC00,
    0xD7A3,
    0xD7A4,
    0xD7FF,
    0xD800,
    0xDBFF,
    0xDC00,
    0xDFFF,
    0xE000,
    0xFEFF,
    0xFF00,
    0xFF1F,
    0xFF20,
    0xFF7F,
    0xFF80,
    0xFFFD,
    0xFFFE,
    0xFFFF,
    0x10000,
    0x10FBF,
    0x10FC0,
    0x1F600,
    0x1FF80,
    0x1FFFE,
    0x1FFFF,
    0x20000,
    0x2A6DF,
    0x3FFFF,
    0x40000,
    0x40080,
    0xFFFFE,
    0xFFFFF,
    0x100000,
    0x10FF80,
    0x10FFFE,
    0x10FFFF,
]


def window_cp(rng: random.Random, w: int) -> int:
    lo, hi = O.win(w)  # type: ignore[misc]
    r = rng.random()
    if r < 0.2:
        return lo
    if r < 0.4:
        return hi
    return rng.randint(lo, hi)


def pick(rng: random.Random, wins: List[int]) -> int:
    r = rng.random()
    if r < 0.30:
        return window_cp(rng, rng.choice(wins))
    if r < 0.40:
        return rng.choice(EDGES) + rng.choice((-1, 0, 0, 0, 1))
    if r < 0.50:
        return rng.randint(0x20, 0x7E)
    if r < 0.56:
        return rng.choice((0x9, 0xA, 0xD, 0x20))
    if r < 0.66:
        return rng.randint(0x4E00, 0x9FFF)
    if r < 0.72:
        return rng.randint(0xAC00, 0xD7A3)
    if r < 0.80:
        return rng.randint(0x3000, 0x30FF)
    if r < 0.84:
        return rng.randint(0xFF00, 0xFFFF)
    if r < 0.88:
        return rng.randint(0xD800, 0xDFFF)
    if r < 0.92:
        return rng.randint(0x20000, 0x3FFFF)
    if r < 0.95:
        return rng.randint(0x80, 0x10FFFF)
    return rng.randint(0, 0x1F)


def clamp(c: int) -> int:
    return min(max(c, 0), 0x10FFFF)


def adversarial(rng: random.Random, length: int) -> List[int]:
    """Code points with no high surrogate directly followed by a low one."""
    wins = [rng.choice(WINDOWS) for _ in range(rng.randint(1, 4))]
    if rng.random() < 0.3:
        wins.append(rng.choice((0xFF80, 0x1FF80, 0x10FF80, 0xFF00, 0xFE80, 0x10F80, 0x1FF00)))
    out: List[int] = []
    while len(out) < length:
        mode = rng.random()
        if mode < 0.15:
            # a run of ideographs or syllables of a length around the cut-off of 16
            lo, hi = rng.choice(((0x4E00, 0x9FFF), (0xAC00, 0xD7A3)))
            n = rng.choice((rng.randint(0, 40), rng.randint(14, 18), rng.randint(0, 300)))
            out += [rng.randint(lo, hi) if rng.random() < 0.8 else rng.choice((lo, hi)) for _ in range(n)]
        elif mode < 0.30:
            # a run of one window (1-byte runs)
            w = rng.choice(wins)
            out += [window_cp(rng, w) for _ in range(rng.randint(1, 60))]
        elif mode < 0.38:
            # kana slot changes
            out += [
                rng.choice(
                    (
                        rng.randint(0x3041, 0x3096),
                        rng.randint(0x30A1, 0x30FC),
                        rng.randint(0x41, 0x7A),
                        0x30FB,
                        0x30FC,
                        rng.randint(0xFF00, 0xFF1F),
                        rng.randint(0x3000, 0x300F),
                    )
                )
                for _ in range(rng.randint(1, 20))
            ]
        else:
            out.append(clamp(pick(rng, wins)))
    out = out[:length]
    for i in range(1, len(out)):
        if 0xD800 <= out[i - 1] <= 0xDBFF and 0xDC00 <= out[i] <= 0xDFFF:
            out[i] = 0x61
    return out


def s(cps: List[int]) -> str:
    return "".join(map(chr, cps))
