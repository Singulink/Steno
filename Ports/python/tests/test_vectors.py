"""The test vectors of the specification (FORMAT.md section 12)."""

from __future__ import annotations

from collections import Counter
from pathlib import Path

import pytest

import singulink_steno as steno

from .helpers import VECTORS, Vector, large_vectors_path, read_vectors


def check_vector(v: Vector) -> None:
    if v.kind == "valid":
        assert steno.encode(v.text) == v.data, v.name
        assert steno.decode(v.data) == v.text, v.name
        assert steno.is_ascii_text(v.data) == all(c in "\t\n\r" or " " <= c <= "~" for c in v.text), v.name
    elif v.kind == "invalid":
        with pytest.raises(steno.StenoDecodeError) as e:
            steno.decode(v.data)
        assert e.value.reason == v.reason, v.name
        assert e.value.reason is steno.StenoDecodeErrorReason(v.reason)
    else:
        with pytest.raises(UnicodeEncodeError):
            steno.encode(v.text)


def test_published_vectors() -> None:
    counts: Counter[str] = Counter()
    for v in read_vectors(VECTORS):
        check_vector(v)
        counts[v.kind] += 1
    # The counts of the header of the vector file.
    assert counts == {"valid": 6798, "invalid": 829, "not-a-value": 13}


def test_published_vectors_through_the_codec_machinery() -> None:
    for v in read_vectors(VECTORS):
        if v.kind == "valid":
            assert v.text.encode("x-steno") == v.data
            assert v.data.decode("x-steno") == v.text
        elif v.kind == "invalid":
            with pytest.raises(UnicodeDecodeError) as e:
                v.data.decode("x-steno")
            assert isinstance(e.value, steno.StenoDecodeError)
            assert e.value.reason == v.reason
        else:
            with pytest.raises(UnicodeEncodeError):
                v.text.encode("x-steno")


def test_published_vectors_one_byte_and_one_code_point_at_a_time() -> None:
    for v in read_vectors(VECTORS):
        if v.kind == "valid":
            enc = steno.Encoder()
            assert b"".join(enc.encode(c) for c in v.text) + enc.encode("", final=True) == v.data
            dec = steno.Decoder()
            text = "".join(dec.decode(v.data[i : i + 1]) for i in range(len(v.data))) + dec.decode(b"", final=True)
            assert text == v.text, v.name
        elif v.kind == "invalid":
            dec = steno.Decoder()
            with pytest.raises(steno.StenoDecodeError) as e:
                for i in range(len(v.data)):
                    dec.decode(v.data[i : i + 1])
                dec.decode(b"", final=True)
            assert e.value.reason == v.reason, v.name
        else:
            enc = steno.Encoder()
            with pytest.raises(UnicodeEncodeError):
                for c in v.text:
                    enc.encode(c)


@pytest.mark.skipif(large_vectors_path() is None, reason="STENO_LARGE_VECTORS is not set")
def test_large_vectors() -> None:
    path = large_vectors_path()
    assert isinstance(path, Path)
    counts: Counter[str] = Counter()
    for v in read_vectors(path):
        check_vector(v)
        counts[v.kind] += 1
    print(dict(counts))
    assert sum(counts.values()) == 145573
