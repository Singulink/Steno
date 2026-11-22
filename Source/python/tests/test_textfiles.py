"""Text files (io.TextIOWrapper, the C one and the one of _pyio) over x-steno: writing, reading, tell/seek with every newline mode and small chunk sizes.

The expected text comes from the same text file machinery over UTF-8 with surrogatepass, so newline translation is Python's own.
Budget: STENO_FUZZ_SECONDS (default 3 per test). Seed: STENO_SEED (default 1).
"""

from __future__ import annotations

import _pyio
import os
import random
import time
from pathlib import Path
from typing import List, Optional, Tuple

import pytest

import singulink_steno as steno

from . import oracle as O
from .generators import adversarial

BUDGET = float(os.environ.get("STENO_FUZZ_SECONDS", "1"))
SEED = int(os.environ.get("STENO_SEED", "1"))
NEWLINES: List[Optional[str]] = [None, "", "\n", "\r", "\r\n"]
OPENS = {"io": open, "_pyio": _pyio.open}


CPYTHON_TELL_BUG = {"count": 0}


def _tell(f) -> Optional[int]:  # type: ignore[no-untyped-def]
    """tell(). It must never raise on a valid file, with one exception that is not the codec's.

    _pyio.TextIOWrapper.tell() asserts that its guess of the bytes to skip (from the bytes-per-character ratio of the last chunk read) fits
    the bytes after the snapshot. After a seek to a position that needs bytes fed after it (which universal newlines make, holding a carriage
    return to see whether a line feed follows), that ratio is stale and the assertion can fail. It fails the same way with UTF-8 (585 times in
    44,872 seeks of a test with UTF-8 text, on Python 3.9 and 3.14), so it is a defect of _pyio, not of this codec; it is counted, not raised.
    The C TextIOWrapper reads past the end of its buffer in the same situation instead; the codec's decoder makes that case work.
    """
    try:
        return f.tell()  # type: ignore[no-any-return]
    except AssertionError:
        if not isinstance(f, _pyio.TextIOWrapper):
            raise
        CPYTHON_TELL_BUG["count"] += 1
        return None


def _text(rng: random.Random) -> str:
    cps = adversarial(rng, rng.choice((rng.randint(0, 30), rng.randint(0, 300), rng.randint(0, 3000))))
    # many line ends of every kind
    out = []
    for c in cps:
        out.append(chr(c))
        if rng.random() < 0.15:
            out.append(rng.choice(("\n", "\r", "\r\n", "\n\r")))
    return "".join(out)
    # no high+low pair after insertion (insertions are ASCII, so none can be made)


def _expected(tmp: Path, raw: bytes, text: str, newline: Optional[str]) -> str:
    p = tmp / "ref.txt"
    p.write_bytes(text.encode("utf-8", "surrogatepass"))
    with open(p, encoding="utf-8", errors="surrogatepass", newline=newline) as f:
        return f.read()


def test_write_every_newline_mode(tmp_path: Path) -> None:
    rng = random.Random(SEED)
    end = time.monotonic() + BUDGET
    p = tmp_path / "a.txt"
    while time.monotonic() < end:
        text = _text(rng)
        for nl in NEWLINES:
            with open(p, "w", encoding="x-steno", newline=nl) as f:
                i = 0
                while i < len(text):
                    j = min(len(text), i + rng.randint(0, 50))
                    f.write(text[i:j])
                    if rng.random() < 0.3:
                        f.flush()
                    i = j
            written = text.replace("\n", os.linesep if nl is None else (nl or "\n"))
            assert p.read_bytes() == O.encode([ord(c) for c in written]), (text, nl)


@pytest.mark.parametrize("impl", sorted(OPENS))
def test_tell_seek_fuzz(tmp_path: Path, impl: str) -> None:
    rng = random.Random(SEED + 1)
    end = time.monotonic() + BUDGET
    p = tmp_path / "a.txt"
    n = 0
    while time.monotonic() < end:
        text = _text(rng)
        raw = O.encode([ord(c) for c in text])
        p.write_bytes(raw)
        nl = rng.choice(NEWLINES)
        full = _expected(tmp_path, raw, text, nl)
        buffering = rng.choice((-1, 1, 2, 3, 7, 64, 8192))
        if buffering == 1:
            buffering = -1  # 1 means line buffering, only for writing
        with OPENS[impl](p, encoding="x-steno", newline=nl, buffering=buffering) as f:
            f._CHUNK_SIZE = rng.choice((1, 2, 3, 5, 8, 13, 64, 8192))  # type: ignore[attr-defined]
            marks: List[Tuple[int, int]] = []
            consumed = 0
            while True:
                pos = _tell(f)
                if pos is not None:
                    marks.append((pos, consumed))
                op = rng.random()
                if op < 0.4:
                    got = f.readline()
                elif op < 0.9:
                    got = f.read(rng.randint(1, 5))
                else:
                    got = f.read(rng.randint(1, 200))
                assert full[consumed : consumed + len(got)] == got, (text, nl, consumed)
                consumed += len(got)
                if not got:
                    pos = _tell(f)
                    if pos is not None:
                        marks.append((pos, consumed))
                    break
            assert consumed == len(full)
            for pos, at in rng.sample(marks, min(len(marks), 25)):
                f.seek(pos)
                if rng.random() < 0.5:
                    assert f.read() == full[at:], (text, nl, pos, at)
                else:
                    got = f.read(rng.randint(1, 4))
                    assert got == full[at : at + len(got)], (text, nl, pos, at)
                    # tell after a seek and a short read, then seek back to it
                    pos2 = _tell(f)
                    if pos2 is not None:
                        rest = f.read()
                        f.seek(pos2)
                        assert f.read() == rest
                n += 1
    print("seeks", n, "_pyio tell assertions", CPYTHON_TELL_BUG)


@pytest.mark.parametrize("impl", sorted(OPENS))
def test_tell_seek_at_eof_after_wide_context(tmp_path: Path, impl: str) -> None:
    # A file that ends in a state with no compact number: tell() at the end, after a readline that returned "", and seek to it.
    p = tmp_path / "a.txt"
    for text in ["한국 α", "한국 α\n", "東京 α", "東京 α\n", "abc\ud800", "abc\ud800\n", "\ud800", "한 α\ud800", "東 Ж\r\n"]:
        p.write_bytes(steno.encode(text))
        for nl in NEWLINES:
            full = _expected(tmp_path, b"", text, nl)
            with OPENS[impl](p, encoding="x-steno", newline=nl) as f:
                lines = []
                while True:
                    pos = f.tell()
                    line = f.readline()
                    lines.append((pos, line))
                    if not line:
                        break
                for _ in range(3):
                    pos = f.tell()
                    assert f.readline() == ""
                    lines.append((pos, ""))
                assert "".join(x for _, x in lines) == full
                for i, (pos, _) in enumerate(lines):
                    f.seek(pos)
                    assert f.read() == "".join(x for _, x in lines[i:]), (text, nl, i)
