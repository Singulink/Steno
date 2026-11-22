"""The smallest tests for the findings of an adversarial review of the text-file and stream integration. Each states the behaviour that
the README or the platform's conventions lead a user to expect, or the refusal that replaced a silent corruption."""

from __future__ import annotations

import codecs
import copy
import io
import time
from pathlib import Path

import pytest

import singulink_steno as steno

# --- 1. tell() raises OverflowError at the end of a file that ends in a state with no compact number --------------------------------


def test_tell_after_reading_to_the_end_of_a_korean_file_with_a_greek_letter(tmp_path: Path) -> None:
    path = tmp_path / "a.txt"
    path.write_bytes(steno.encode("한 α"))  # Hangul, then a Greek letter: the pending window is neither the main nor the second window
    with open(path, encoding="x-steno") as f:
        assert f.read(10) == "한 α"
        assert f.read(10) == ""
        pos = f.tell()  # OverflowError: Python int too large to convert to C long
        f.seek(pos)
        assert f.read() == ""


# --- 2. tell() raises StenoDecodeError on a valid file after a seek and a short read ------------------------------------------------


def test_tell_after_seek_on_a_valid_korean_file(tmp_path: Path) -> None:
    # The C TextIOWrapper.tell() guesses skip_bytes = b2cratio * chars_to_skip from the ratio of the LAST chunk read, and decodes that many
    # bytes from the snapshot without a bound check. The Steno decoder's held bytes make cookies with bytes_to_feed > 0, so after a seek
    # into such a region the guess runs past the snapshot bytes into the NUL after the bytes object: the decoder sees 00 and tell() raises.
    path = tmp_path / "a.txt"
    path.write_bytes(steno.encode("한국 α abc def ghi jkl\n" + "한" * 20000))
    with open(path, encoding="x-steno") as f:
        marks = []
        for _ in range(25):
            marks.append(f.tell())
            f.read(1)
        f.seek(0)
        f.read(100)
        f.read(9000)  # the last chunk read is dense Hangul (about 2 bytes per character)
        for m in marks:
            f.seek(m)
            f.read(1)
            f.tell()  # StenoDecodeError: zero-byte


def test_tell_after_seek_small_chunks(tmp_path: Path) -> None:
    path = tmp_path / "a.txt"
    path.write_bytes(steno.encode("\rィ锲"))
    with open(path, encoding="x-steno") as f:
        f._CHUNK_SIZE = 5  # type: ignore[attr-defined]
        marks = []
        while True:
            marks.append(f.tell())
            if not f.read(1):
                break
        for m in marks:
            f.seek(m)
            f.read(1)
            f.tell()  # StenoDecodeError: zero-byte


# --- 3. Silent corruption when a text file is continued without a seek ---------------------------------------------------------------


def _decodes_to(path: Path, text: str) -> bool:
    try:
        return steno.decode(path.read_bytes()) == text
    except steno.StenoDecodeError:
        return False


def test_r_plus_and_w_plus_are_refused_by_the_package_open(tmp_path: Path) -> None:
    # The built-in open() gives a codec no way to refuse a write after a read (see the next test), so the package has its own open(), which
    # takes only the modes that cannot write wrong bytes.
    path = tmp_path / "a.txt"
    path.write_bytes(steno.encode("Москва"))
    for mode in ("r+", "w+", "a+", "x+", "rb", "r+t"):
        with pytest.raises(ValueError, match="takes the mode"):
            steno.open(path, mode)


def test_the_built_in_open_cannot_refuse_a_write_after_a_read(tmp_path: Path) -> None:
    # This documents the limit that the README states: with the built-in open() in mode r+ or w+, a write after a read writes bytes encoded
    # from the initial state, and nothing in the codec API can see it. The package's open() does not offer these modes.
    path = tmp_path / "a.txt"
    path.write_bytes(steno.encode("Москва"))
    with open(path, "r+", encoding="x-steno") as f:
        assert f.read() == "Москва"
        f.write("Москва")
    assert steno.decode(path.read_bytes()) != "МоскваМосква"


def test_seek_and_truncate_are_refused_when_writing(tmp_path: Path) -> None:
    path = tmp_path / "a.txt"
    with steno.open(path, "w") as f:
        f.write("Москва")
        with pytest.raises(io.UnsupportedOperation):
            f.seek(0)
        with pytest.raises(io.UnsupportedOperation):
            f.truncate(0)
        f.write("Москва")
    assert steno.decode(path.read_bytes()) == "МоскваМосква"


def test_codecs_open_append(tmp_path: Path) -> None:
    path = tmp_path / "a.txt"
    path.write_bytes(steno.encode("Москва"))
    try:
        with codecs.open(str(path), "a", "x-steno") as f:
            f.write("Москва")
    except io.UnsupportedOperation:
        return
    assert _decodes_to(path, "МоскваМосква"), steno.decode(path.read_bytes())


def test_reconfigure_is_refused_by_the_package_open(tmp_path: Path) -> None:
    # The built-in TextIOWrapper swaps in a fresh encoder before a failed reconfigure() raises, so a later write corrupts the file. The
    # package's text files refuse reconfigure() before it changes anything (except line_buffering and write_through).
    path = tmp_path / "a.txt"
    with steno.open(path, "w") as f:
        f.write("Москва")
        with pytest.raises(io.UnsupportedOperation):
            f.reconfigure(encoding="x-steno")
        with pytest.raises(io.UnsupportedOperation):
            f.reconfigure(newline="")
        f.reconfigure(line_buffering=True)
        f.write("Москва")
    assert steno.decode(path.read_bytes()) == "МоскваМосква"


# --- 4. A call that raises does not leave the decoder as it was (README: "A call that raises leaves the encoder or decoder as it was")


@pytest.mark.parametrize("bad_state", [(b"", -5), (b"", 10**20), (b"\x00", 0)])
def test_decoder_setstate_that_raises_resets_the_decoder(bad_state: tuple) -> None:  # type: ignore[type-arg]
    data = steno.encode("Москва")
    dec = steno.Decoder()
    first = dec.decode(data[:2])
    with pytest.raises((ValueError, steno.StenoDecodeError)):
        dec.setstate(bad_state)
    assert first + dec.decode(data[2:], final=True) == "Москва"  # gives 'МāĄýõó'


# --- 5. copy.copy() of an incremental decoder or encoder shares its state with the original ------------------------------------------


def test_copy_of_a_decoder_is_independent() -> None:
    data = steno.encode("aМαβγ")
    dec = steno.Decoder()
    assert dec.decode(data[:1]) == "a"
    other = copy.copy(dec)
    assert other.decode(data[1:], final=True) == "Мαβγ"
    assert dec.decode(data[1:], final=True) == "Мαβγ"  # gives 'Ͽฑkβγ': the copy moved the original's state


def test_copy_of_an_encoder_is_independent() -> None:
    enc = steno.Encoder()
    enc.encode("a")
    other = copy.copy(enc)
    other.encode("Мαβγ")
    assert enc.encode("Мαβγ") == steno.encode("aМαβγ")[1:]


def test_deepcopy_of_a_decoder() -> None:
    dec = steno.Decoder()
    dec.decode(steno.encode("Моск"))
    copy.deepcopy(dec)  # TypeError: cannot pickle 'EncodingMap' object (the stdlib's Python-level decoders can be deep-copied)


# --- 6. The decoder holds back text without bound ("a little later" in the README) ---------------------------------------------------


def test_decoder_holds_text_without_bound() -> None:
    text = "한국어 α\n" + "line of ascii text\n" * 20000
    data = steno.encode(text)
    dec = steno.Decoder()
    out = 0
    start = time.perf_counter()
    for i in range(0, len(data), 8192):
        out += len(dec.decode(data[i : i + 8192]))
    elapsed = time.perf_counter() - start
    # 380 KB fed: almost all of the text should have come out. It gives 4 characters, and takes about 1 s (byte-at-a-time search).
    assert out >= len(text) - 100, (out, len(text), elapsed)


# --- 7. StreamReader.read() raises 'truncated' in the middle of a stream -------------------------------------------------------------


def test_stream_reader_read_zero_in_the_middle() -> None:
    data = steno.encode("a東京b")  # 48 F8 50 E9 7C B4 49
    reader = codecs.getreader("x-steno")(io.BytesIO(data))
    assert reader.read(size=2, chars=1) == "a"  # leaves the lead byte F8 in bytebuffer
    assert reader.read(0) == ""  # StenoDecodeError: truncated, although the stream goes on
    assert reader.read() == "東京b"
