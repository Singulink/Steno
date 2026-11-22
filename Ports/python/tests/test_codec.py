"""The codec x-steno in the codec machinery of the standard library: str.encode, bytes.decode, open(), codecs.*."""

from __future__ import annotations

import codecs
import io
import pickle
import random
from pathlib import Path

import pytest

import singulink_steno as steno

from .helpers import WORKLOADS, random_text

HIGH = chr(0xD800)
PAIR = chr(0xD83D) + chr(0xDE00)  # a high surrogate followed by a low one: not a value
MIXED = ("Москва 東京 Zürich 서울 Ελλάδα " + chr(0x1F600) + " " + HIGH + " abc\n") * 50


def test_names() -> None:
    for name in ("x-steno", "X-STENO", "x_steno", "x steno"):
        assert codecs.lookup(name).name == "x-steno"
    assert steno.ENCODING_NAME == "x-steno"


def test_str_encode_and_bytes_decode() -> None:
    data = MIXED.encode("x-steno")
    assert data == steno.encode(MIXED)
    assert data.decode("x-steno") == MIXED
    assert bytearray(data).decode("x-steno") == MIXED
    assert codecs.decode(memoryview(data), "x-steno") == MIXED
    assert codecs.encode(MIXED, "x-steno") == data


def test_error_handlers_other_than_strict_are_rejected() -> None:
    with pytest.raises(ValueError, match="only errors='strict'"):
        "abc".encode("x-steno", "replace")
    with pytest.raises(ValueError, match="only errors='strict'"):
        b"\x48".decode("x-steno", "ignore")
    with pytest.raises(ValueError, match="only errors='strict'"):
        codecs.getincrementaldecoder("x-steno")("surrogateescape")


def test_decode_error_is_a_unicode_decode_error() -> None:
    with pytest.raises(UnicodeDecodeError) as e:
        b"\x2f\x00".decode("x-steno")
    err = e.value
    assert isinstance(err, steno.StenoDecodeError)
    assert err.reason is steno.StenoDecodeErrorReason.ZERO_BYTE
    assert err.reason == "zero-byte"
    assert (err.offset, err.start, err.end, err.object, err.encoding) == (1, 1, 2, b"\x2f\x00", "x-steno")
    assert "zero-byte" in str(err)
    copy = pickle.loads(pickle.dumps(err))
    assert (copy.reason, copy.offset, copy.start, copy.end, copy.object) == (err.reason, 1, 1, 2, err.object)


def test_not_a_value_is_a_unicode_encode_error() -> None:
    with pytest.raises(UnicodeEncodeError) as e:
        ("ab" + PAIR).encode("x-steno")
    assert (e.value.start, e.value.end, e.value.encoding) == (2, 4, "x-steno")
    # The fix that the message proposes.
    joined = ("ab" + PAIR).encode("utf-16", "surrogatepass").decode("utf-16")
    assert joined == "ab" + chr(0x1F600)
    assert steno.decode(joined.encode("x-steno")) == joined


def test_iterencode_and_iterdecode() -> None:
    pieces = ["Моск", "ва ", HIGH, "東京"]
    data = b"".join(codecs.iterencode(pieces, "x-steno"))
    assert data == steno.encode("".join(pieces))
    assert "".join(codecs.iterdecode([data[i : i + 1] for i in range(len(data))], "x-steno")) == "".join(pieces)


def test_text_file_round_trip(tmp_path: Path) -> None:
    path = tmp_path / "a.txt"
    with open(path, "w", encoding="x-steno", newline="") as f:
        f.write(MIXED)
    assert path.read_bytes() == steno.encode(MIXED)
    with open(path, encoding="x-steno", newline="") as f:
        assert f.read() == MIXED


def test_flush_in_the_middle_of_a_value(tmp_path: Path) -> None:
    path = tmp_path / "a.txt"
    text = "Москва, 東京 and Zürich" + HIGH
    with open(path, "w", encoding="x-steno", newline="") as f:
        for i, ch in enumerate(text):
            f.write(ch)
            if i % 3 == 0:
                f.flush()
                # What is on disk after a flush is a prefix of the bytes of the whole value.
                assert steno.encode(text).startswith(path.read_bytes())
    assert path.read_bytes() == steno.encode(text)
    with open(path, encoding="x-steno", newline="") as f:
        assert f.read() == text


def test_tell_and_seek(tmp_path: Path) -> None:
    rng = random.Random(20)
    lines = [random_text(rng, rng.randint(0, 30), pools=2).replace("\n", " ").replace("\r", " ") + "\n" for _ in range(120)]
    lines += ["한국 α abc\n", HIGH + "\n", "東京 é\n"] * 5
    text = "".join(lines)
    path = tmp_path / "a.txt"
    path.write_bytes(steno.encode(text))
    with open(path, encoding="x-steno", newline="") as f:
        positions = []
        read = []
        while True:
            positions.append(f.tell())
            line = f.readline()
            if not line:
                break
            read.append(line)
        assert "".join(read) == text
        for i in rng.sample(range(len(read)), 40):
            f.seek(positions[i])
            assert f.readline() == read[i]
            assert f.readline() == (read[i + 1] if i + 1 < len(read) else "")
        f.seek(0)
        assert f.read() == text


def test_tell_and_seek_with_universal_newlines(tmp_path: Path) -> None:
    text = "Москва\r\n東京\r\n서울 α x\r\n" * 200
    path = tmp_path / "a.txt"
    path.write_bytes(steno.encode(text))
    with open(path, encoding="x-steno") as f:
        f.read(1000)
        pos = f.tell()
        rest = f.read()
        f.seek(pos)
        assert f.read() == rest


def test_built_in_open_appending_and_writing_after_a_seek_fail_loudly(tmp_path: Path) -> None:
    path = tmp_path / "a.txt"
    with open(path, "w", encoding="x-steno") as f:
        f.write("Москва")
    with open(path, "a", encoding="x-steno") as f, pytest.raises(io.UnsupportedOperation, match="cannot write here"):
        f.write("Москва")
    assert steno.decode(path.read_bytes()) == "Москва"
    with open(path, "r+", encoding="x-steno") as f:
        assert f.read(3) == "Мос"
        f.seek(0, io.SEEK_END)
        with pytest.raises(io.UnsupportedOperation):
            f.write("Мос")
    assert steno.decode(path.read_bytes()) == "Москва"
    # An empty file can be opened for appending: the value starts at the start.
    empty = tmp_path / "b.txt"
    with open(empty, "a", encoding="x-steno") as f:
        f.write("Zürich")
    assert empty.read_bytes() == steno.encode("Zürich")


def test_reading_a_value_after_a_header_of_a_writable_stream(tmp_path: Path) -> None:
    path = tmp_path / "a.bin"
    path.write_bytes(b"HDR" + steno.encode("Москва"))
    with open(path, "r+b") as raw:
        assert raw.read(3) == b"HDR"
        f = io.TextIOWrapper(raw, encoding="x-steno")  # the encoder is told that the state here is unknown: reading works
        assert f.read() == "Москва"
        with pytest.raises(io.UnsupportedOperation):
            f.write("x")
        f.detach()


def test_package_open(tmp_path: Path) -> None:
    path = tmp_path / "a.txt"
    with steno.open(path, "w") as f:
        assert isinstance(f, steno.StenoTextFile)
        f.write("Москва\n")
        f.flush()
        f.write("東京\n")
    with steno.open(path) as f:
        pos = f.tell()
        assert f.readline() == "Москва\n"
        assert f.read() == "東京\n"
        f.seek(pos)
        assert f.read() == "Москва\n東京\n"
    with pytest.raises(io.UnsupportedOperation, match="cannot be appended"):
        steno.open(path, "a")
    with pytest.raises(FileExistsError):
        steno.open(path, "x")
    new = tmp_path / "b.txt"
    with steno.open(new, "a") as f:
        f.write("Zürich")
    with steno.open(tmp_path / "c.txt", "xt") as f:
        f.write("Zürich")
    assert new.read_bytes() == (tmp_path / "c.txt").read_bytes() == steno.encode("Zürich")


def test_text_file_truncated_at_the_end(tmp_path: Path) -> None:
    path = tmp_path / "a.txt"
    path.write_bytes(steno.encode("abc一")[:-1])
    with open(path, encoding="x-steno") as f, pytest.raises(steno.StenoDecodeError) as e:
        f.read()
    assert e.value.reason == "truncated"


@pytest.mark.parametrize("length", [65, 4097, 20000])
def test_long_value_that_starts_with_a_byte_order_mark(length: int, tmp_path: Path) -> None:
    rng = random.Random(length)
    text = "﻿" + random_text(rng, length, pools=3)
    data = steno.encode(text)
    assert steno.decode(data) == text
    assert data.decode("x-steno") == text
    dec = steno.Decoder()
    assert "".join(dec.decode(data[i : i + 7]) for i in range(0, len(data), 7)) + dec.decode(b"", final=True) == text
    assert "".join(codecs.iterdecode([data[:3], data[3:]], "x-steno")) == text
    path = tmp_path / "bom.txt"
    with open(path, "w", encoding="x-steno", newline="") as f:
        f.write(text)
    assert path.read_bytes() == data
    with open(path, encoding="x-steno", newline="") as f:
        assert f.read() == text
    with codecs.open(str(path), "r", "x-steno") as f:
        assert f.read() == text


def test_stream_reader_and_writer(tmp_path: Path) -> None:
    buf = io.BytesIO()
    writer = codecs.getwriter("x-steno")(buf)
    for piece in ["Моск", "ва\n", "東京\n", HIGH]:
        writer.write(piece)
    assert buf.getvalue() == steno.encode("Москва\n東京\n" + HIGH)
    reader = codecs.getreader("x-steno")(io.BytesIO(buf.getvalue()))
    assert reader.readline() == "Москва\n"
    assert reader.read() == "東京\n" + HIGH
    reader = codecs.getreader("x-steno")(io.BytesIO(buf.getvalue()[:-1]))
    with pytest.raises(steno.StenoDecodeError):
        reader.read()
    path = tmp_path / "c.txt"
    with codecs.open(str(path), "w", "x-steno") as f:
        f.write(MIXED)
    with codecs.open(str(path), "r", "x-steno") as f:
        assert f.read() == MIXED


def test_workload_documents_through_a_text_file(tmp_path: Path) -> None:
    for name in ("russian", "japanese", "korean", "vietnamese"):
        text = (WORKLOADS / f"{name}.document.txt").read_text(encoding="utf-8")
        path = tmp_path / f"{name}.txt"
        with open(path, "w", encoding="x-steno", newline="") as f:
            f.write(text)
        with open(path, encoding="x-steno", newline="") as f:
            assert f.read() == text


def test_stream_writer_refuses_to_write_where_the_state_is_unknown(tmp_path: Path) -> None:
    path = tmp_path / "a.txt"
    path.write_bytes(steno.encode("Москва"))
    with codecs.open(str(path), "r+", "x-steno") as f:
        assert f.read() == "Москва"
        with pytest.raises(io.UnsupportedOperation):
            f.write("x")  # after a read
    with codecs.open(str(path), "w", "x-steno") as f:
        f.write("Моск")
        with pytest.raises(io.UnsupportedOperation):
            f.writer.seek(0)
        f.seek(0)  # StreamReaderWriter.seek moves the stream itself
        with pytest.raises(io.UnsupportedOperation):
            f.write("x")
    buf = io.BytesIO(b"header")
    buf.seek(0, io.SEEK_END)
    with pytest.raises(io.UnsupportedOperation):
        codecs.getwriter("x-steno")(buf).write("x")


def test_stream_reader_refuses_to_read_from_the_middle() -> None:
    data = steno.encode("Москва 東京")
    stream = io.BytesIO(data)
    reader = codecs.getreader("x-steno")(stream)
    assert reader.read(4) == "Моск"
    with pytest.raises(io.UnsupportedOperation):
        reader.seek(2)
    reader.seek(0)
    assert reader.read() == "Москва 東京"
    stream.seek(3)
    with pytest.raises(io.UnsupportedOperation):
        reader.read()
    # A value may start after a header: the first read sets the start.
    reader = codecs.getreader("x-steno")(io.BytesIO(b"HDR" + data))
    reader.stream.read(3)
    assert reader.read() == "Москва 東京"


def test_copies_and_pickles_are_independent() -> None:
    import copy
    import pickle

    data = steno.encode("aМαβγ東京")
    dec = steno.Decoder()
    dec.decode(data[:2])
    for other in (copy.copy(dec), copy.deepcopy(dec), pickle.loads(pickle.dumps(dec))):
        assert other.decode(data[2:], final=True) == "aМαβγ東京"[1:] or other.getstate() is not None
    assert dec.decode(data[2:], final=True) == steno.decode(data)[1:]
    enc = steno.Encoder()
    enc.encode("aМ")
    for other in (copy.copy(enc), copy.deepcopy(enc), pickle.loads(pickle.dumps(enc))):
        assert other.encode("αβγ") == steno.encode("aМαβγ")[len(steno.encode("aМ")) :]
    assert enc.encode("αβγ") == steno.encode("aМαβγ")[len(steno.encode("aМ")) :]
