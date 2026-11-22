"""The layout cache under several threads, with enough distinct layouts to force evictions, and memory over a long run.

Budget: STENO_FUZZ_SECONDS (default 3). Threads: STENO_THREADS (default 4).
"""

from __future__ import annotations

import gc
import os
import random
import threading
import time
import tracemalloc
from typing import List, Tuple

import singulink_steno as steno
from singulink_steno import _layout

from . import oracle as O
from .generators import WINDOWS, adversarial, s

BUDGET = float(os.environ.get("STENO_FUZZ_SECONDS", "1"))
THREADS = int(os.environ.get("STENO_THREADS", "4"))


def _hopping(rng: random.Random) -> List[int]:
    """Text that hops through many windows (each hop is a new layout key), with wide-context detours."""
    out: List[int] = []
    for _ in range(rng.randint(5, 40)):
        lo, hi = O.win(rng.choice(WINDOWS))  # type: ignore[misc]
        out += [rng.randint(lo, hi) for _ in range(rng.randint(1, 4))]
        if rng.random() < 0.2:
            out += adversarial(rng, rng.randint(1, 10))
    for i in range(1, len(out)):
        if 0xD800 <= out[i - 1] <= 0xDBFF and 0xDC00 <= out[i] <= 0xDFFF:
            out[i] = 0x61
    return out


def _corpus(seed: int, n: int) -> List[Tuple[str, bytes]]:
    rng = random.Random(seed)
    out = []
    for _ in range(n):
        cps = _hopping(rng)
        out.append((s(cps), O.encode(cps)))
    return out


def test_threads_with_evictions() -> None:
    corpus = _corpus(77, 300)
    errors: List[str] = []
    stop = time.monotonic() + BUDGET
    rounds = [0] * THREADS

    def work(k: int) -> None:
        rng = random.Random(k)
        while time.monotonic() < stop:
            text, data = corpus[rng.randrange(len(corpus))]
            try:
                if steno.encode(text) != data:
                    errors.append("encode " + ascii(text))
                if steno.decode(data) != text:
                    errors.append("decode " + ascii(text))
                if rng.random() < 0.2:
                    _layout.clear_layout_cache()
            except Exception as e:
                errors.append(repr(e))
            rounds[k] += 1

    ts = [threading.Thread(target=work, args=(k,)) for k in range(THREADS)]
    for t in ts:
        t.start()
    for t in ts:
        t.join()
    assert not errors, errors[:5]
    assert _layout.layout_cache_size() <= _layout.LAYOUT_CACHE_LIMIT
    print("rounds", sum(rounds))


def test_memory_stays_bounded() -> None:
    rng = random.Random(5)
    gc.collect()
    tracemalloc.start()
    try:
        samples = []
        stop = time.monotonic() + BUDGET
        i = 0
        while time.monotonic() < stop:
            cps = _hopping(rng)
            text = s(cps)
            data = steno.encode(text)
            assert steno.decode(data) == text
            dec = steno.Decoder()
            dec.decode(data[: len(data) // 2])
            enc = steno.Encoder()
            enc.encode(text[: len(text) // 2])
            i += 1
            if i % 500 == 0:
                gc.collect()
                samples.append(tracemalloc.get_traced_memory()[0])
        print("iterations", i, "traced MB", [round(x / 1e6, 1) for x in samples[:: max(1, len(samples) // 10)]])
        if len(samples) >= 6:
            # After the cache has filled, memory must not keep growing.
            third = len(samples) // 3
            assert samples[-1] < samples[third] * 1.3 + 2e6, samples
    finally:
        tracemalloc.stop()
