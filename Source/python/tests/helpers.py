"""Shared helpers of the tests: the vector files and random text."""

from __future__ import annotations

import os
import random
from dataclasses import dataclass
from pathlib import Path
from typing import Iterator, List, Optional

HERE = Path(__file__).resolve().parent
VECTORS = HERE.parent.parent.parent / "TestVectors" / "steno-v1-vectors.txt"
WORKLOADS = HERE.parent.parent.parent / "Benchmarks" / "Workloads"


@dataclass
class Vector:
    kind: str
    name: str
    text: str
    data: bytes
    reason: Optional[str]


def _cps(field: str) -> str:
    return "" if field == "-" else "".join(chr(int(x, 16)) for x in field.split(" "))


def _bytes(field: str) -> bytes:
    return b"" if field == "-" else bytes.fromhex(field)


def read_vectors(path: Path) -> Iterator[Vector]:
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            if not line or line.startswith("#"):
                continue
            fields = line.split("\t")
            kind = fields[0]
            if kind == "valid":
                yield Vector(kind, fields[1], _cps(fields[2]), _bytes(fields[3]), None)
            elif kind == "invalid":
                yield Vector(kind, fields[1], "", _bytes(fields[2]), fields[3])
            elif kind == "not-a-value":
                yield Vector(kind, fields[1], _cps(fields[2]), b"", None)
            else:
                raise AssertionError(f"unknown kind of vector: {line!r}")


def large_vectors_path() -> Optional[Path]:
    name = os.environ.get("STENO_LARGE_VECTORS")
    return Path(name) if name else None


# Pools of code points for random text: scripts, their neighbours, controls, CJK, kana, Hangul, supplementary planes and lone surrogates.
POOLS: List[List[int]] = [
    list(range(0x20, 0x7F)),
    [0x09, 0x0A, 0x0D],
    [*list(range(32)), 127],
    list(range(0x80, 0x250)),
    list(range(0x300, 0x370)),
    list(range(0x370, 0x530)),
    list(range(0x530, 0x600)),
    list(range(0x600, 0x700)),
    list(range(0x900, 0x980)),
    list(range(0xE00, 0xE80)),
    list(range(0x1200, 0x1380)),
    list(range(0x1E00, 0x2100)),
    list(range(0x2100, 0x2200)),
    list(range(0x3000, 0x3100)),
    list(range(0x3100, 0x3400)),
    list(range(0x3400, 0x3500)),
    list(range(0x4E00, 0x4F00)) + list(range(0x9F00, 0xA000)),
    list(range(0xA000, 0xA100)) + list(range(0xAB00, 0xAC00)),
    list(range(0xAC00, 0xAD00)) + list(range(0xD700, 0xD7B0)),
    list(range(0xD800, 0xE000)),
    list(range(0xE000, 0xE100)),
    list(range(0xFE00, 0x10000)),
    list(range(0x10000, 0x10100)),
    list(range(0x1F300, 0x1F700)),
    list(range(0x1FD00, 0x1FF00)),
    list(range(0x20000, 0x20100)) + list(range(0x2A600, 0x2A700)),
    list(range(0x40000, 0x40100)),
    list(range(0xFFF00, 0x110000)),
]


def random_text(rng: random.Random, length: int, pools: int = 3, surrogates: bool = True) -> str:
    """Random text of the given number of code points, from a few pools, with no high surrogate directly followed by a low one."""
    chosen = [rng.choice(POOLS) for _ in range(pools)]
    if not surrogates:
        chosen = [p if p[0] != 0xD800 else POOLS[0] for p in chosen]
    out: List[int] = []
    for _ in range(length):
        pool = chosen[0] if rng.random() < 0.6 else rng.choice(chosen)
        c = rng.choice(pool)
        if out and 0xD800 <= out[-1] <= 0xDBFF and 0xDC00 <= c <= 0xDFFF:
            c = 0x61
        out.append(c)
    return "".join(map(chr, out))
