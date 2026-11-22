"""The codecs that the benchmarks measure, and the workloads.

Each codec has an encode loop and a decode loop that call the library directly, one value after another, and a function that prepares the
inputs of the decode loop. The alternatives are what a Python developer would use instead of the Steno Encoding:

- UTF-8 and UTF-16: str.encode and bytes.decode of the standard library.
- Deflate: zlib.compress and zlib.decompress of the standard library (the zlib format), at level 1 and 9.
- Zstandard: the package zstandard, with one ZstdCompressor and one ZstdDecompressor that are made once and reused for every value, as its
  documentation advises; at level 1 and 19.
- Brotli: the package brotli (Google's binding), brotli.compress and brotli.decompress, at quality 1 and 11. It has no reusable object for
  one-shot calls.
- LZ4: the package lz4, lz4.block.compress and lz4.block.decompress (the block format with the size in front, the API for small inputs).
- SCSU and BOCU-1: none. Python has no implementation in common use. PyICU binds ICU4C, whose converters have both, but PyPI has only its
  source, which needs ICU and a C++ compiler to build; it is not a package that Python developers reach for to store text.

Compressors encode a str by UTF-8 encoding and then compressing it, and decode by decompressing and UTF-8 decoding, because that is what a
program that stores text does.
"""

from __future__ import annotations

import sys
import zlib
from pathlib import Path
from time import perf_counter
from typing import Any, Callable, Dict, List, Optional, Tuple

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "src"))

import singulink_steno as steno  # noqa: E402

WORKLOADS = HERE.parent.parent.parent / "Benchmarks" / "Workloads"

TEXTS = [
    "english",
    "french",
    "polish",
    "vietnamese",
    "russian",
    "greek",
    "arabic",
    "urdu",
    "hindi",
    "thai",
    "amharic",
    "chinese",
    "japanese",
    "korean",
]

# The texts on which the alternatives are measured, so that the full run fits in about 25 minutes.
SUBSET = ["english", "french", "russian", "arabic", "hindi", "thai", "amharic", "chinese", "japanese", "korean"]

SCSU_BOCU_NOTE = (
    "SCSU and BOCU-1 are not measured: Python has no implementation of them in common use. PyICU binds ICU4C, whose converters have both, "
    "but PyPI has only its source, which needs ICU and a C++ compiler to build (it does not install on this machine)."
)


def load(name: str, kind: str) -> List[str]:
    """Loads a workload: the document (the file without its final LF) or its values (one per line)."""
    content = (WORKLOADS / f"{name}.{kind}.txt").read_bytes().decode("utf-8")
    if "\r" in content:
        raise ValueError(f"{name}.{kind}.txt has CR characters; check out the repository with LF line endings")
    if not content.endswith("\n"):
        raise ValueError(f"{name}.{kind}.txt does not end with LF")
    body = content[:-1]
    return [body] if kind == "document" else body.split("\n")


def utf16_units(values: List[str]) -> int:
    return sum(len(v.encode("utf-16-le")) // 2 for v in values)


Loop = Callable[[int, List[Any]], float]


class Codec:
    def __init__(
        self,
        name: str,
        library: str,
        version: str,
        encode_loop: Loop,
        decode_loop: Loop,
        encode_one: Callable[[str], bytes],
        decode_one: Callable[[bytes], str],
    ) -> None:
        self.name = name
        self.library = library
        self.version = version
        self.encode_loop = encode_loop
        self.decode_loop = decode_loop
        self.encode_one = encode_one
        self.decode_one = decode_one


def _steno_encode(loops: int, values: List[Any]) -> float:
    encode = steno.encode
    t0 = perf_counter()
    for _ in range(loops):
        for v in values:
            encode(v)
    return perf_counter() - t0


def _steno_decode(loops: int, values: List[Any]) -> float:
    decode = steno.decode
    t0 = perf_counter()
    for _ in range(loops):
        for v in values:
            decode(v)
    return perf_counter() - t0


def _utf8_encode(loops: int, values: List[Any]) -> float:
    t0 = perf_counter()
    for _ in range(loops):
        for v in values:
            v.encode("utf-8")
    return perf_counter() - t0


def _utf8_decode(loops: int, values: List[Any]) -> float:
    t0 = perf_counter()
    for _ in range(loops):
        for v in values:
            v.decode("utf-8")
    return perf_counter() - t0


def _utf16_encode(loops: int, values: List[Any]) -> float:
    t0 = perf_counter()
    for _ in range(loops):
        for v in values:
            v.encode("utf-16-le")
    return perf_counter() - t0


def _utf16_decode(loops: int, values: List[Any]) -> float:
    t0 = perf_counter()
    for _ in range(loops):
        for v in values:
            v.decode("utf-16-le")
    return perf_counter() - t0


def _deflate(level: int) -> Tuple[Loop, Loop]:
    compress = zlib.compress
    decompress = zlib.decompress

    def enc(loops: int, values: List[Any]) -> float:
        t0 = perf_counter()
        for _ in range(loops):
            for v in values:
                compress(v.encode("utf-8"), level)
        return perf_counter() - t0

    def dec(loops: int, values: List[Any]) -> float:
        t0 = perf_counter()
        for _ in range(loops):
            for v in values:
                decompress(v).decode("utf-8")
        return perf_counter() - t0

    return enc, dec


def _compressor(compress: Callable[[bytes], bytes], decompress: Callable[[bytes], bytes]) -> Tuple[Loop, Loop]:
    def enc(loops: int, values: List[Any]) -> float:
        t0 = perf_counter()
        for _ in range(loops):
            for v in values:
                compress(v.encode("utf-8"))
        return perf_counter() - t0

    def dec(loops: int, values: List[Any]) -> float:
        t0 = perf_counter()
        for _ in range(loops):
            for v in values:
                decompress(v).decode("utf-8")
        return perf_counter() - t0

    return enc, dec


def _version(module: str) -> Optional[str]:
    try:
        from importlib.metadata import version

        return version(module)
    except Exception:
        return None


def all_codecs() -> Dict[str, Codec]:
    """The codecs, by the name that the CSV files use. Alternatives whose package is missing are left out."""
    py = f"Python {sys.version.split()[0]}"
    out: Dict[str, Codec] = {
        "steno": Codec(
            "steno",
            "singulink_steno (this package) encode / decode",
            f"{steno.__version__} ({steno.backend} backend)",
            _steno_encode,
            _steno_decode,
            steno.encode,
            steno.decode,
        ),
        "utf8": Codec(
            "utf8",
            'str.encode("utf-8") / bytes.decode("utf-8")',
            py,
            _utf8_encode,
            _utf8_decode,
            lambda s: s.encode("utf-8"),
            lambda b: b.decode("utf-8"),
        ),
        "utf16": Codec(
            "utf16",
            'str.encode("utf-16-le") / bytes.decode("utf-16-le")',
            py,
            _utf16_encode,
            _utf16_decode,
            lambda s: s.encode("utf-16-le"),
            lambda b: b.decode("utf-16-le"),
        ),
    }
    for name, level in (("deflate-fast", 1), ("deflate-max", 9)):
        enc, dec = _deflate(level)
        out[name] = Codec(
            name,
            f"zlib.compress / zlib.decompress (standard library, zlib format), level {level}",
            f"zlib {zlib.ZLIB_RUNTIME_VERSION}",
            enc,
            dec,
            lambda s, lv=level: zlib.compress(s.encode("utf-8"), lv),
            lambda b: zlib.decompress(b).decode("utf-8"),
        )
    try:
        import zstandard

        for level in (1, 19):
            cctx = zstandard.ZstdCompressor(level=level)
            dctx = zstandard.ZstdDecompressor()
            enc, dec = _compressor(cctx.compress, dctx.decompress)
            out[f"zstd-{level}"] = Codec(
                f"zstd-{level}",
                f"zstandard ZstdCompressor(level={level}).compress / ZstdDecompressor().decompress, reused",
                f"zstandard {zstandard.__version__} (libzstd {zstandard.ZSTD_VERSION[0]}.{zstandard.ZSTD_VERSION[1]}.{zstandard.ZSTD_VERSION[2]})",
                enc,
                dec,
                lambda s, c=cctx: c.compress(s.encode("utf-8")),
                lambda b, d=dctx: d.decompress(b).decode("utf-8"),
            )
    except ImportError:
        pass
    try:
        import brotli

        for quality in (1, 11):

            def bc(b: bytes, q: int = quality) -> bytes:
                return brotli.compress(b, quality=q)

            enc, dec = _compressor(bc, brotli.decompress)
            out[f"brotli-{quality}"] = Codec(
                f"brotli-{quality}",
                f"brotli.compress(quality={quality}) / brotli.decompress",
                f"brotli {_version('brotli')}",
                enc,
                dec,
                lambda s, f=bc: f(s.encode("utf-8")),
                lambda b: brotli.decompress(b).decode("utf-8"),
            )
    except ImportError:
        pass
    try:
        import lz4.block

        enc, dec = _compressor(lz4.block.compress, lz4.block.decompress)
        out["lz4"] = Codec(
            "lz4",
            "lz4.block.compress / lz4.block.decompress (block format with the size in front)",
            f"lz4 {_version('lz4')}",
            enc,
            dec,
            lambda s: lz4.block.compress(s.encode("utf-8")),
            lambda b: lz4.block.decompress(b).decode("utf-8"),
        )
    except ImportError:
        pass
    return out


SMOKE_CODECS = ["steno", "utf8", "zstd-1", "lz4"]


def codecs_for(text: str, smoke: bool = False) -> List[str]:
    """The codecs measured on a text: all on the texts of SUBSET, Steno and UTF-8 and UTF-16 on the others, a few in a smoke run."""
    if smoke:
        return SMOKE_CODECS
    names = list(all_codecs())
    return names if text in SUBSET else ["steno", "utf8", "utf16"]


def write_lf(path: Path, text: str) -> None:
    """Writes UTF-8 text with LF line endings."""
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
