"""Differential fuzzing against the second oracle (tests/oracle.py).

Budget: STENO_FUZZ_SECONDS (default 3 per test). Seed: STENO_SEED (default 1).
"""

from __future__ import annotations

import io
import os
import random
import time
from typing import List, Optional, Tuple

import singulink_steno as steno

from . import oracle as O
from .generators import adversarial, s

BUDGET = float(os.environ.get("STENO_FUZZ_SECONDS", "1"))
SEED = int(os.environ.get("STENO_SEED", "1"))


def _deadline() -> float:
    return time.monotonic() + BUDGET


def _cuts(rng: random.Random, n: int) -> List[int]:
    r = rng.random()
    if r < 0.15:
        return list(range(n + 1))
    k = rng.randint(0, 12)
    cuts = sorted(rng.choice(range(n + 1)) for _ in range(k))  # duplicates give empty pieces
    return [0, *cuts, n]


def _length(rng: random.Random) -> int:
    return rng.choice((rng.randint(0, 8), rng.randint(0, 60), rng.randint(0, 400)))


def test_encode_decode_against_oracle() -> None:
    rng = random.Random(SEED)
    end = _deadline()
    n = 0
    while time.monotonic() < end:
        cps = adversarial(rng, _length(rng))
        text = s(cps)
        want = O.encode(cps)
        got = steno.encode(text)
        assert got == want, (cps, got.hex(), want.hex())
        assert steno.decode(want) == text
        assert want.decode("x-steno") == text
        assert text.encode("x-steno") == want
        # incremental encoder
        cuts = _cuts(rng, len(text))
        enc = steno.Encoder()
        pieces = b"".join(enc.encode(text[a:b]) for a, b in zip(cuts, cuts[1:])) + enc.encode("", final=True)
        assert pieces == want, cps
        # incremental decoder
        cuts = _cuts(rng, len(want))
        dec = steno.Decoder()
        out = "".join(dec.decode(want[a:b]) for a, b in zip(cuts, cuts[1:])) + dec.decode(b"", final=True)
        assert out == text, (cps, cuts)
        n += 1
    print("cases", n)


def _oracle_decode(data: bytes) -> Tuple[Optional[str], Optional[Tuple[str, int]]]:
    try:
        return s(O.decode(data)), None
    except O.OErr as e:
        return None, (e.reason, e.offset)


def _impl_decode(data: bytes) -> Tuple[Optional[str], Optional[Tuple[str, int]]]:
    try:
        return steno.decode(data), None
    except steno.StenoDecodeError as e:
        return None, (e.reason.value, e.offset)


def _mutate(rng: random.Random, data: bytes) -> bytes:
    b = bytearray(data)
    for _ in range(rng.randint(1, 3)):
        r = rng.random()
        if r < 0.4 and b:
            b[rng.randrange(len(b))] = rng.choice((0, rng.randint(1, 7), 8, 0xFE, 0xFF, rng.randint(0, 255)))
        elif r < 0.6:
            b.insert(rng.randint(0, len(b)), rng.randint(0, 255))
        elif r < 0.8 and b:
            del b[rng.randrange(len(b))]
        else:
            b = b[: rng.randint(0, len(b))]
    return bytes(b)


def test_decoder_on_bad_input_against_oracle() -> None:
    rng = random.Random(SEED + 1000)
    end = _deadline()
    n = 0
    while time.monotonic() < end:
        cps = adversarial(rng, rng.randint(0, 40))
        data = _mutate(rng, O.encode(cps))
        want = _oracle_decode(data)
        got = _impl_decode(data)
        assert got == want, (data.hex(" "), got, want)
        # Incremental, random splits: same text, or the same reason and offset.
        cuts = _cuts(rng, len(data))
        dec = steno.Decoder()
        out = []
        try:
            for a, b in zip(cuts, cuts[1:]):
                out.append(dec.decode(data[a:b]))
            out.append(dec.decode(b"", final=True))
            inc: Tuple[Optional[str], Optional[Tuple[str, int]]] = ("".join(out), None)
        except steno.StenoDecodeError as e:
            inc = (None, (e.reason.value, e.offset))
        assert inc == want, (data.hex(" "), cuts, inc, want)
        n += 1
    print("cases", n)


def test_every_byte_at_every_position_of_short_encodings() -> None:
    rng = random.Random(SEED + 2000)
    end = _deadline()
    n = 0
    while time.monotonic() < end:
        cps = adversarial(rng, rng.randint(1, 6))
        data = O.encode(cps)
        i = rng.randrange(len(data) + 1)
        for v in range(256):
            for mutated in (data[:i] + bytes([v]) + data[i + 1 :], data[:i] + bytes([v]) + data[i:]):
                assert _impl_decode(mutated) == _oracle_decode(mutated), mutated.hex(" ")
                n += 1
    print("cases", n)


def test_decoder_state_round_trip_at_every_position() -> None:
    rng = random.Random(SEED + 3000)
    end = _deadline()
    n = 0
    while time.monotonic() < end:
        cps = adversarial(rng, rng.randint(1, 50))
        text = s(cps)
        data = O.encode(cps)
        dec = steno.Decoder()
        out = ""
        step = rng.choice((1, 1, 2, 3, 7))
        for i in range(0, len(data), step):
            out += dec.decode(data[i : i + step])
            buf, number = dec.getstate()
            assert isinstance(number, int) and 0 <= number < 2**30, (text, i, number)
            other = steno.Decoder()
            other.setstate((buf, number))
            assert out + other.decode(data[i + step :], final=True) == text, (cps, i)
            # The newline decoder that text files put around the decoder doubles the number.
            nl = io.IncrementalNewlineDecoder(steno.Decoder(), translate=False)
            nl.setstate((buf, number * 2))
            assert out + nl.decode(data[i + step :], final=True) == text
            n += 1
        assert out + dec.decode(b"", final=True) == text
    print("positions", n)


def test_encoder_state_round_trip_at_every_position() -> None:
    rng = random.Random(SEED + 4000)
    end = _deadline()
    while time.monotonic() < end:
        cps = adversarial(rng, rng.randint(1, 50))
        text = s(cps)
        want = O.encode(cps)
        enc = steno.Encoder()
        out = b""
        for i in range(len(text)):
            out += enc.encode(text[i])
            st = enc.getstate()
            other = steno.Encoder()
            other.setstate(st)
            assert out + other.encode(text[i + 1 :], final=True) == want
