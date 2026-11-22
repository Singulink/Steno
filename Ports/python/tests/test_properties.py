"""Properties on seeded random text, and agreement with the plain reference codec (FORMAT.md section 13)."""

from __future__ import annotations

import random

import pytest
from hypothesis import given, settings
from hypothesis import strategies as hs

import singulink_steno as steno
from singulink_steno import _pure

from . import reference
from .helpers import POOLS, random_text


def cps(text: str) -> list[int]:
    return [ord(c) for c in text]


def test_random_text_agrees_with_the_reference() -> None:
    rng = random.Random(1)
    for _ in range(300):
        text = random_text(rng, rng.randint(0, 40), pools=rng.randint(1, 4))
        data = steno.encode(text)
        assert data == reference.encode(cps(text)), ascii(text)
        assert steno.decode(data) == text


@pytest.mark.parametrize("ctx_text", ["一", "가", "あ", "ア"])
def test_long_wide_runs_agree_with_the_reference(ctx_text: str) -> None:
    # Runs long enough for the integer arithmetic, in the layouts of the wide contexts, with other scripts between them.
    rng = random.Random(2)
    blocks = [(0x4E00, 0x9FFF), (0xAC00, 0xD7A3)]
    for _ in range(40):
        parts = [ctx_text]
        for _ in range(rng.randint(1, 4)):
            lo, hi = rng.choice(blocks)
            parts.append("".join(chr(rng.randint(lo, hi)) for _ in range(rng.choice([1, 5, 15, 16, 17, 40]))))
            parts.append(random_text(rng, rng.randint(0, 3)))
        text = "".join(parts)
        data = steno.encode(text)
        assert data == reference.encode(cps(text)), ascii(text)
        assert steno.decode(data) == text


def test_wide_block_edges() -> None:
    for lo, hi in [(0x4E00, 0x9FFF), (0xAC00, 0xD7A3)]:
        text = "".join(chr(c) for c in list(range(lo, lo + 300)) + list(range(hi - 300, hi + 1)))
        data = steno.encode(text)
        assert data == reference.encode(cps(text))
        assert steno.decode(data) == text


def test_whole_blocks_round_trip() -> None:
    for lo, hi in [(0x4E00, 0x9FFF), (0xAC00, 0xD7A3)]:
        text = "".join(map(chr, range(lo, hi + 1)))
        assert steno.decode(steno.encode(text)) == text
        assert steno.decode(steno.encode(text[::-1])) == text[::-1]


def test_order_prefix_and_no_zero_byte() -> None:
    rng = random.Random(3)
    texts = []
    for _ in range(400):
        base = random_text(rng, rng.randint(0, 12), pools=2)
        texts.append(base)
        # Neighbours in order: a common prefix and then a difference.
        texts.append(base + random_text(rng, rng.randint(0, 4), pools=1))
    texts = list(dict.fromkeys(texts))
    pairs = [(t, steno.encode(t)) for t in texts]
    for t, b in pairs:
        assert 0 not in b
        for i in range(len(t) + 1):
            prefix = t[:i]
            # A prefix that ends between a high and a low surrogate is still a value here: Python strings hold code points.
            assert b.startswith(steno.encode(prefix))
    assert [t for t, _ in sorted(pairs, key=lambda p: p[1])] == sorted(texts)


def test_reencoding_accepted_bytes_gives_the_same_bytes() -> None:
    rng = random.Random(4)
    accepted = 0
    for _ in range(3000):
        data = bytes(rng.randint(1, 255) for _ in range(rng.randint(0, 12)))
        try:
            text = steno.decode(data)
        except steno.StenoDecodeError:
            continue
        accepted += 1
        assert steno.encode(text) == data
    assert accepted > 100


def test_ascii_fast_paths_match_the_general_path() -> None:
    rng = random.Random(5)
    for _ in range(200):
        text = "".join(chr(rng.choice([*range(0x20, 0x7F), 0x09, 0x0A, 0x0D, 0x00, 0x1F, 0x7F, 0x0B])) for _ in range(rng.randint(0, 30)))
        data = steno.encode(text)
        assert data == reference.encode(cps(text))
        out = bytearray()
        _pure._encode(_pure.State(), text, out)
        assert bytes(out) == data
        parts: list[str] = []
        _pure._decode(_pure.State(), data, True, False, 0, parts)
        assert "".join(parts) == text
        assert steno.decode(data) == text


code_points = hs.integers(0, 0x10FFFF)


@settings(max_examples=150, deadline=None)
@given(hs.lists(hs.one_of(code_points, hs.sampled_from(POOLS[0]), hs.sampled_from(POOLS[16])), max_size=30))
def test_hypothesis_round_trip(values: list[int]) -> None:
    # Make it a value: split every high surrogate directly followed by a low one.
    fixed: list[int] = []
    for c in values:
        if fixed and 0xD800 <= fixed[-1] <= 0xDBFF and 0xDC00 <= c <= 0xDFFF:
            fixed.append(0x20)
        fixed.append(c)
    text = "".join(map(chr, fixed))
    data = steno.encode(text)
    assert data == reference.encode(fixed)
    assert steno.decode(data) == text


def test_division_by_248_of_the_wide_paths_is_exact() -> None:
    m = _pure.WIDE_DIV_MUL
    assert all((x * m) >> 24 == x // 248 for x in range(0x10000))
    # The largest offset in a block is at most 0xD7A3, so the product stays in a 32-bit lane.
    assert 0xD7A3 * m < 2**32


def test_bytes_below_08_start_a_code_point_and_are_controls_or_space() -> None:
    rng = random.Random(6)
    for _ in range(300):
        text = random_text(rng, rng.randint(0, 40), pools=rng.randint(1, 4))
        data = steno.encode(text)
        # The start of each code point: encode each prefix.
        starts = {len(steno.encode(text[:i])) for i in range(len(text))}
        for i, b in enumerate(data):
            if b < 8:
                assert i in starts, (ascii(text), i)
                c = ord(steno.decode(data[: i + 1 + (1 if b in (1, 4, 6) else 0)])[-1])
                assert c < 0x20 or c == 0x20, (ascii(text), i)
        assert all(b >= 8 for i, b in enumerate(data) if i not in starts)


def test_splitting_the_bytes_on_line_feeds() -> None:
    rng = random.Random(7)
    for _ in range(200):
        lines = [random_text(rng, rng.randint(0, 12), pools=2).replace("\n", "") for _ in range(rng.randint(1, 6))]
        text = "\n".join(lines)
        data = steno.encode(text)
        pieces = data.split(b"\x03")
        assert len(pieces) == len(lines)
        # Each piece holds the bytes of exactly the code points of its line, encoded in the state of the whole value.
        pos = 0
        for line, piece in zip(lines, pieces):
            start = len(steno.encode(text[:pos]))
            end = len(steno.encode(text[: pos + len(line)]))
            assert data[start:end] == piece
            pos += len(line) + 1
