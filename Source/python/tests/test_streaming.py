"""The incremental encoder and decoder: any split gives the one-shot result."""

from __future__ import annotations

import io
import random

import pytest

import singulink_steno as steno
from singulink_steno._layout import COMPACT_LIMIT

from .helpers import random_text

HIGH = chr(0xD800)
LOW = chr(0xDC00)


def split(rng: random.Random, n: int) -> list[int]:
    cuts = sorted(rng.sample(range(n + 1), min(n + 1, rng.randint(0, 6))))
    return [0, *cuts, n]


def encode_pieces(text: str, cuts: list[int]) -> bytes:
    enc = steno.Encoder()
    out = b"".join(enc.encode(text[a:b]) for a, b in zip(cuts, cuts[1:]))
    return out + enc.encode("", final=True)


def decode_pieces(data: bytes, cuts: list[int]) -> str:
    dec = steno.Decoder()
    out = "".join(dec.decode(data[a:b]) for a, b in zip(cuts, cuts[1:]))
    return out + dec.decode(b"", final=True)


def test_random_splits() -> None:
    rng = random.Random(10)
    for _ in range(400):
        text = random_text(rng, rng.randint(0, 50), pools=rng.randint(1, 4))
        data = steno.encode(text)
        assert encode_pieces(text, split(rng, len(text))) == data
        assert decode_pieces(data, split(rng, len(data))) == text


def test_one_unit_per_call() -> None:
    rng = random.Random(11)
    for _ in range(100):
        text = random_text(rng, rng.randint(0, 40), pools=3)
        data = steno.encode(text)
        assert encode_pieces(text, list(range(len(text) + 1))) == data
        assert decode_pieces(data, list(range(len(data) + 1))) == text


def test_split_inside_multibyte_sequences() -> None:
    text = "a\U0010fffd一\ud800b\U0001f600"
    data = steno.encode(text)
    for i in range(len(data) + 1):
        assert decode_pieces(data, [0, i, len(data)]) == text


def test_surrogate_pair_across_pieces_is_not_a_value() -> None:
    enc = steno.Encoder()
    first = enc.encode("a\ud83d")
    assert first == steno.encode("a\ud83d")
    with pytest.raises(UnicodeEncodeError):
        enc.encode("\ude00")
    # The call that raised changed nothing: the value can go on.
    assert first + enc.encode("b") == steno.encode("a\ud83db")


def test_surrogate_pair_across_pieces_in_the_decoder() -> None:
    high = steno.encode(HIGH)
    dec = steno.Decoder()
    assert dec.decode(high) == HIGH  # given out at once
    # The bytes of a low surrogate in the state after the high surrogate: the state does not change, so they are those of the initial layout.
    with pytest.raises(steno.StenoDecodeError) as e:
        dec.decode(steno.encode(LOW))
    assert e.value.reason is steno.StenoDecodeErrorReason.SURROGATE_PAIR
    assert e.value.offset == len(high)
    assert dec.decode(b"", final=True) == ""
    assert steno.decode(steno.encode(HIGH + " " + LOW)) == HIGH + " " + LOW


def test_final_with_unfinished_code_point_is_truncated() -> None:
    data = steno.encode("ab一")
    dec = steno.Decoder()
    assert dec.decode(data[:-1]) == "ab"
    with pytest.raises(steno.StenoDecodeError) as e:
        dec.decode(b"", final=True)
    assert e.value.reason == "truncated"
    assert e.value.offset == 2
    # The call that raised changed nothing.
    assert dec.decode(data[-1:], final=True) == "一"


def test_final_does_not_start_a_new_value_but_reset_does() -> None:
    enc = steno.Encoder()
    a = enc.encode("Моск", final=True)
    b = enc.encode("ва", final=True)
    assert a + b == steno.encode("Москва")
    enc.reset()
    assert enc.encode("ва") == steno.encode("ва")

    dec = steno.Decoder()
    data = steno.encode("Москва")
    assert dec.decode(data[:3], final=True) + dec.decode(data[3:], final=True) == "Москва"
    dec.reset()
    assert dec.decode(steno.encode("ва"), final=True) == "ва"


def test_decoder_error_leaves_the_decoder_unchanged() -> None:
    data = steno.encode("Москва")
    dec = steno.Decoder()
    assert dec.decode(data[:2]) == "М"
    with pytest.raises(steno.StenoDecodeError) as e:
        dec.decode(b"\xff")
    assert e.value.reason == "unused-lead"
    assert e.value.offset == 2
    assert dec.decode(data[2:], final=True) == "осква"


def test_a_zero_byte_without_final_is_raised_by_the_next_call() -> None:
    data = steno.encode("Москва")
    dec = steno.Decoder()
    assert dec.decode(data[:3] + b"\x00" + data[3:]) == "Мо"
    assert dec.getstate()[0].startswith(b"\x00")
    for _ in range(2):
        with pytest.raises(steno.StenoDecodeError) as e:
            dec.decode(b"")
        assert (e.value.reason, e.value.offset) == ("zero-byte", 3)
    dec.reset()
    assert dec.decode(data, final=True) == "Москва"
    # With final=True it is raised at once.
    with pytest.raises(steno.StenoDecodeError):
        steno.Decoder().decode(data[:3] + b"\x00", final=True)


def test_error_offsets_count_from_the_start_of_the_value() -> None:
    data = steno.encode("hello") + b"\xff"
    dec = steno.Decoder()
    dec.decode(data[:3])
    with pytest.raises(steno.StenoDecodeError) as e:
        dec.decode(data[3:])
    assert e.value.reason == "unused-lead"
    assert e.value.offset == 5
    assert e.value.object == data[3:]
    assert e.value.start == 2


def test_getstate_setstate_round_trip_at_every_byte() -> None:
    rng = random.Random(12)
    for _ in range(60):
        text = random_text(rng, rng.randint(1, 30), pools=3)
        data = steno.encode(text)
        dec = steno.Decoder()
        out = ""
        for i in range(len(data)):
            out += dec.decode(data[i : i + 1])
            buf, number = dec.getstate()
            assert 0 <= number < 2**30
            # A fresh decoder in that state, fed the rest, gives the rest of the text.
            other = steno.Decoder()
            other.setstate((buf, number))
            rest = other.decode(data[i + 1 :], final=True)
            assert out + rest == text
            # The state number with no bytes, fed the held bytes, gives the same state back, without output.
            third = steno.Decoder()
            third.setstate((b"", number))
            assert third.decode(buf) == ""
            assert third.getstate() == (buf, number)
        assert out + dec.decode(b"", final=True) == text


def test_initial_state_is_zero() -> None:
    assert steno.Decoder().getstate() == (b"", 0)
    dec = steno.Decoder()
    dec.decode(steno.encode("Hello"))
    assert dec.getstate() == (b"", 0)


def test_no_text_is_held_and_every_state_number_fits_30_bits() -> None:
    # Hangul, then a Greek letter: its window is neither the main nor the second window, so the state has no compact number until the next
    # Hangul syllable. The decoder still gives out all the text at once, and gives the state a number that this process remembers.
    text = "한국 α" + " abc" * 5 + "어"
    data = steno.encode(text)
    dec = steno.Decoder()
    first = dec.decode(data[:-2])
    assert first == text[:-1]
    buf, number = dec.getstate()
    assert buf == b""
    assert COMPACT_LIMIT <= number < 2**30
    other = steno.Decoder()
    other.setstate((b"", number))
    assert other.decode(data[-2:], final=True) == "어"
    assert first + dec.decode(data[-2:], final=True) == text
    # After final=True too.
    end = steno.Decoder()
    end.decode(steno.encode("한 α"), final=True)
    assert end.getstate()[1] < 2**30


def test_a_state_number_this_process_does_not_know_is_refused() -> None:
    dec = steno.Decoder()
    data = steno.encode("Москва")
    assert dec.decode(data[:2]) == "М"
    for number in (2**30 - 1, COMPACT_LIMIT + 12345):
        with pytest.raises(io.UnsupportedOperation, match="cannot restore"):
            dec.setstate((b"", number))
    assert dec.decode(data[2:], final=True) == "осква"


def test_encoder_state() -> None:
    enc = steno.Encoder()
    assert enc.getstate() == 1
    enc.encode("Моск一a\ud800")
    state = enc.getstate()
    other = steno.Encoder()
    other.setstate(state)
    assert other.encode("ва") == enc.encode("ва")
    # setstate(0): a text file goes on at a position whose state it does not know. Writing is refused, until reset().
    other.setstate(0)
    assert other.encode("") == b""
    with pytest.raises(io.UnsupportedOperation, match="cannot write here"):
        other.encode("a")
    other.reset()
    assert other.encode("a") == steno.encode("a")
    with pytest.raises(ValueError):
        other.setstate(10**30)
    assert other.encode("b") == steno.encode("ab")[1:]


def test_errors_other_than_strict_are_rejected() -> None:
    for cls in (steno.Encoder, steno.Decoder):
        with pytest.raises(ValueError, match="only errors='strict'"):
            cls("replace")


def test_decoder_accepts_bytes_like() -> None:
    data = steno.encode("Grüße")
    dec = steno.Decoder()
    assert dec.decode(bytearray(data[:2])) + dec.decode(memoryview(data)[2:], final=True) == "Grüße"
    with pytest.raises(TypeError):
        dec.decode("text")  # type: ignore[arg-type]


def test_a_state_number_from_another_process_is_refused(monkeypatch: pytest.MonkeyPatch) -> None:
    from singulink_steno import _codec

    text = "한국 α" + " abc" * 5 + "어"
    data = steno.encode(text)
    dec = steno.Decoder()
    first = dec.decode(data[:-2])
    _buf, number = dec.getstate()
    assert number >= COMPACT_LIMIT
    # The same process restores it.
    same = steno.Decoder()
    same.setstate((b"", number))
    assert first + same.decode(data[-2:], final=True) == text
    # Another process: another random key, and a table that never held the state.
    monkeypatch.setattr(_codec, "_tokens", _codec._StateTokens())
    with pytest.raises(io.UnsupportedOperation, match="cannot restore"):
        steno.Decoder().setstate((b"", number))
    # Another process whose table holds the same state under its own key: the old number is still refused.
    other = steno.Decoder()
    other.decode(data[:-2])
    assert other.getstate()[1] != number
    with pytest.raises(io.UnsupportedOperation):
        steno.Decoder().setstate((b"", number))
    # A table entry with a number that its key does not give for the state is refused too.
    _codec._tokens._by_token[number] = _codec._tokens._by_token[other.getstate()[1]]
    with pytest.raises(io.UnsupportedOperation):
        steno.Decoder().setstate((b"", number))
