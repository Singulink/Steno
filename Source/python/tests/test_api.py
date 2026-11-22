"""The public API: bounds, input types, garbage, threads, the layouts, the cache and the backend."""

from __future__ import annotations

import random
import subprocess
import sys
import threading

import pytest

import singulink_steno as steno
from singulink_steno import _layout

from . import reference
from .helpers import random_text

# --- Bounds ---


def test_max_lengths() -> None:
    assert steno.max_encoded_len(0) == 0
    assert steno.max_encoded_len(5) == 20
    assert steno.max_decoded_len(7) == 7
    with pytest.raises(ValueError):
        steno.max_encoded_len(-1)
    with pytest.raises(ValueError):
        steno.max_decoded_len(-1)


def test_max_lengths_hold_on_worst_cases() -> None:
    # Code points of 4 bytes each: far supplementary code points that keep switching between windows.
    worst = "".join(chr(0x40000 + i * 0x7000) for i in range(29))
    assert len(steno.encode(worst)) == steno.max_encoded_len(len(worst))
    rng = random.Random(30)
    for _ in range(300):
        text = random_text(rng, rng.randint(0, 30), pools=4)
        data = steno.encode(text)
        assert len(data) <= steno.max_encoded_len(len(text))
        assert len(text) <= steno.max_decoded_len(len(data))
    ascii_text = "abc"
    assert len(steno.decode(steno.encode(ascii_text))) == steno.max_decoded_len(len(steno.encode(ascii_text)))


# --- Input types ---


def test_input_types() -> None:
    data = steno.encode("Grüße")
    assert steno.decode(bytearray(data)) == "Grüße"
    assert steno.decode(memoryview(data)) == "Grüße"
    assert steno.is_ascii_text(bytearray(steno.encode("abc")))
    assert steno.is_ascii_text(memoryview(steno.encode("a\tb\r\n")))
    assert not steno.is_ascii_text(steno.encode("a\x00"))
    assert steno.is_ascii_text(b"")
    with pytest.raises(TypeError):
        steno.encode(b"abc")  # type: ignore[arg-type]
    with pytest.raises(TypeError):
        steno.decode("abc")  # type: ignore[arg-type]
    with pytest.raises(TypeError):
        steno.decode(5)  # type: ignore[arg-type]


def test_lone_surrogates_round_trip() -> None:
    text = "".join(chr(c) for c in (0xDC00, 0xD800, 0x61, 0xDBFF, 0xDBFF, 0x20, 0xDFFF, 0xDFFF, 0xD800))
    assert steno.decode(steno.encode(text)) == text


def test_strings_compare_in_code_point_order_like_their_bytes() -> None:
    a, b = "すごい！", "すごい" + chr(0x1F600)
    assert (a < b) == (steno.encode(a) < steno.encode(b))
    assert "Z" < "a" < "z" < "Å"
    assert steno.encode("Z") < steno.encode("a") < steno.encode("z") < steno.encode("Å")


# --- Garbage ---


def test_garbage_never_crashes() -> None:
    rng = random.Random(31)
    for _ in range(4000):
        data = bytes(rng.randint(0, 255) for _ in range(rng.randint(0, 20)))
        check_garbage(data)


def test_cut_and_mutated_values() -> None:
    rng = random.Random(32)
    for _ in range(300):
        data = bytearray(steno.encode(random_text(rng, rng.randint(1, 30), pools=3)))
        check_garbage(bytes(data[: rng.randint(0, len(data))]))
        for _ in range(3):
            data[rng.randrange(len(data))] = rng.randint(0, 255)
        check_garbage(bytes(data))


def check_garbage(data: bytes) -> None:
    try:
        text = steno.decode(data)
    except steno.StenoDecodeError as e:
        with pytest.raises(reference.RefError) as r:
            reference.decode(data)
        assert (e.reason, e.offset) == (r.value.reason, r.value.offset), data.hex()
        assert 0 <= e.start < max(1, len(e.object)) and e.start < e.end <= len(e.object)
        return
    assert steno.encode(text) == data
    assert [ord(c) for c in text] == reference.decode(data)


# --- Threads ---


def test_threads_agree_with_one_thread() -> None:
    rng = random.Random(33)
    texts = [random_text(rng, rng.randint(0, 60), pools=3) for _ in range(300)]
    expected = [steno.encode(t) for t in texts]
    _layout.clear_layout_cache()
    failures: list[str] = []

    def work(seed: int) -> None:
        order = list(range(len(texts)))
        random.Random(seed).shuffle(order)
        for _ in range(3):
            for i in order:
                if steno.encode(texts[i]) != expected[i] or steno.decode(expected[i]) != texts[i]:
                    failures.append(f"{seed}:{i}")

    threads = [threading.Thread(target=work, args=(s,)) for s in range(6)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    assert failures == []
    assert _layout.layout_cache_size() <= _layout.LAYOUT_CACHE_LIMIT


# --- Layouts and the cache ---


def ranges_of(layout: _layout.Layout) -> list[tuple[int, int, int, int]]:
    return [(lo, hi, k, f) for lo, hi, k, f in zip(layout.lo, layout.hi, layout.size, layout.first)]


def test_layouts_agree_with_the_reference() -> None:
    rng = random.Random(34)
    states = [
        (_layout.GENERAL, 0, 0x00C0, -1),
        (_layout.GENERAL, 0, 0xFE00, 0xFFF80),
        (_layout.CJK, 1, 0x00C0, -1),
        (_layout.HANGUL, 0, 0x00C0, -1),
    ]
    for _ in range(200):
        ctx = rng.choice([_layout.GENERAL, _layout.CJK, _layout.HANGUL])
        main = rng.choice(_layout.WINDOWS)
        second = rng.choice([-1, *[w for w in rng.sample(_layout.WINDOWS, 3) if w != main]])
        states.append((ctx, rng.randint(0, 2) if ctx == _layout.CJK else 0, main, second))
    for ctx, slot, main, second in states:
        mine = ranges_of(_layout.Layout(ctx, slot, main, second))
        ref_state = reference.State(
            context=[reference.GENERAL, reference.CJK, reference.HANGUL][ctx],
            main=(main, _layout.window_end(main)),
            second=None if second < 0 else (second, _layout.window_end(second)),
            slot=[reference.LETTERS, reference.HIRAGANA, reference.KATAKANA][slot],
        )
        ref = [(r.lo, r.hi, r.size, r.first_lead) for r in reference.build_layout(ref_state)]
        assert mine == ref


def test_the_fullest_layout_of_the_specification() -> None:
    # FORMAT.md section 11.1: 254 lead bytes.
    layout = _layout.Layout(_layout.GENERAL, 0, 0xFE00, 0xFFF80)
    assert layout.lead_count == 254


def test_windows() -> None:
    assert len(_layout.WINDOWS) == 7356
    for c in range(0, 0x110000, 7):
        w = _layout.window_start(c)
        ref = reference.window_of(c)
        assert (None if w < 0 else (w, _layout.window_end(w))) == ref


def test_cache_is_bounded() -> None:
    _layout.clear_layout_cache()
    for main in _layout.WINDOWS[: _layout.LAYOUT_CACHE_LIMIT + 50]:
        _layout.get_layout(_layout.GENERAL, 0, main, -1)
    assert _layout.layout_cache_size() == _layout.LAYOUT_CACHE_LIMIT
    # Every layout is still built correctly after the cache dropped some.
    text = "".join(chr(w) + chr(w + 1) for w in _layout.WINDOWS[:600:7])
    assert steno.encode(text) == reference.encode([ord(c) for c in text])


def test_state_numbers_round_trip() -> None:
    rng = random.Random(35)
    st = _layout.State()
    other = _layout.State()
    assert _layout.pack_compact(st, False) == 0
    assert _layout.pack_full(st) == 0
    for _ in range(3000):
        c = ord(random_text(rng, 1, pools=1))
        st.update(c, st.layout.size[st.layout.find(c)])
        number = _layout.pack_full(st)
        _layout.unpack_full(other, number)
        assert _layout.pack_full(other) == number
        assert other.layout is not None and ranges_of(other.layout) == ranges_of(st.layout)
        for high in (False, True):
            compact = _layout.pack_compact(st, high)
            if compact is not None:
                assert 0 <= compact < _layout.COMPACT_LIMIT < 2**30
                assert _layout.unpack_compact(other, compact) is high
                assert _layout.pack_full(other) == number
    with pytest.raises(ValueError):
        _layout.unpack_compact(other, _layout.COMPACT_LIMIT)


# --- The backend ---


def test_backend() -> None:
    assert steno.backend == "python"
    assert steno.__version__ == "0.1.0"


def test_backend_can_be_chosen_and_an_unknown_one_fails() -> None:
    code = "import singulink_steno; print(singulink_steno.backend)"
    ok = subprocess.run(
        [sys.executable, "-c", code],
        env={"SINGULINK_STENO_BACKEND": "python", "SYSTEMROOT": _systemroot()},
        capture_output=True,
        text=True,
        check=False,
    )
    assert ok.stdout.strip() == "python", ok.stderr
    bad = subprocess.run(
        [sys.executable, "-c", code],
        env={"SINGULINK_STENO_BACKEND": "native", "SYSTEMROOT": _systemroot()},
        capture_output=True,
        text=True,
        check=False,
    )
    assert bad.returncode != 0
    assert "not available" in bad.stderr


def _systemroot() -> str:
    import os

    return os.environ.get("SYSTEMROOT", "")


def test_invalid_trail_and_its_order() -> None:
    cases = [
        (b"\x01\x07", "invalid-trail"),
        (b"\x01\x00", "zero-byte"),
        (b"\x01", "truncated"),
        (b"\xf6\x08\x05", "invalid-trail"),  # a 3-byte code point of the initial layout
        (b"\xf6\x05\x00", "invalid-trail"),  # the first bad trail byte decides
        (b"\xf6\x08", "truncated"),
    ]
    for data, reason in cases:
        with pytest.raises(steno.StenoDecodeError) as e:
            steno.decode(data)
        assert e.value.reason == reason, data.hex()
        assert e.value.offset == 0
    # An incremental decoder reports a bad trail byte at once, before the code point is complete.
    dec = steno.Decoder()
    with pytest.raises(steno.StenoDecodeError) as e:
        dec.decode(b"\xf6\x05")
    assert e.value.reason is steno.StenoDecodeErrorReason.INVALID_TRAIL


def test_max_lengths_take_only_ints() -> None:
    for bad in (2.5, True, "3", None):
        with pytest.raises(TypeError):
            steno.max_encoded_len(bad)  # type: ignore[arg-type]
        with pytest.raises(TypeError):
            steno.max_decoded_len(bad)  # type: ignore[arg-type]


def test_text_that_hops_between_windows_is_not_much_slower() -> None:
    # Text whose every code point is in another of many windows misses the layout cache at almost every code point. Each code point then
    # builds a layout. Compare with text that changes window at every code point too, but among four windows, whose layouts stay cached: the
    # same slow path without the builds. Measured about 9 times on a busy machine (10 us against 1.1 us per code point); before layouts were
    # built cheaply it was over 100 times. The bound leaves room for a noisy machine.
    import time

    rng = random.Random(36)
    hop = "".join(chr(rng.choice(_layout.WINDOWS) + rng.randrange(16)) for _ in range(3000))
    hop = "".join(c for c in hop if not 0xD800 <= ord(c) <= 0xDFFF)
    few = "".join(chr(rng.choice(_layout.WINDOWS[100:104]) + rng.randrange(16)) for _ in range(3000))

    def per_code_point(text: str) -> float:
        data = steno.encode(text)
        best = 1e9
        for _ in range(5):
            t0 = time.perf_counter()
            steno.encode(text)
            steno.decode(data)
            best = min(best, time.perf_counter() - t0)
        return best / len(text)

    _layout.clear_layout_cache()
    ratio = per_code_point(hop) / per_code_point(few)
    assert ratio < 25, ratio
