"""The Python reference implementation of the Steno Encoding.

The Steno Encoding is a compact encoding of Unicode text whose bytes sort like the text. This package encodes and decodes the Steno
Encoding, version 1. Importing it registers the codec ``x-steno``, so ``text.encode("x-steno")``, ``data.decode("x-steno")`` and
``open(path, encoding="x-steno")`` work too.

Example:
    >>> import singulink_steno
    >>> data = singulink_steno.encode("Москва")
    >>> len(data), len("Москва".encode("utf-8"))
    (7, 12)
    >>> singulink_steno.decode(data)
    'Москва'
"""

from __future__ import annotations

from . import _backend, _codec
from ._codec import BytesLike, Decoder, Encoder, StreamReader, StreamWriter
from ._errors import ENCODING_NAME, StenoDecodeError, StenoDecodeErrorReason
from ._file import StenoTextFile, open  # noqa: A004 (singulink_steno.open is the module's own open)

__all__ = [
    "ENCODING_NAME",
    "Decoder",
    "Encoder",
    "StenoDecodeError",
    "StenoDecodeErrorReason",
    "StenoTextFile",
    "StreamReader",
    "StreamWriter",
    "__version__",
    "backend",
    "decode",
    "encode",
    "is_ascii_text",
    "max_decoded_len",
    "max_encoded_len",
    "open",
]

__version__ = "0.1.0"

backend: str = _backend.NAME
"""The name of the backend that does the work: ``"python"`` for the pure Python backend, the only one so far."""


def encode(text: str) -> bytes:
    """Encodes a string as one value.

    Every string is a value unless it has a high surrogate directly followed by a low surrogate. Lone surrogates are code points of their own
    and are encoded as such.

    Args:
        text: The text.

    Returns:
        The Steno bytes of the text.

    Raises:
        UnicodeEncodeError: The string has a high surrogate directly followed by a low surrogate, which is not a value (FORMAT.md section 3).
        TypeError: ``text`` is not a str.

    Example:
        >>> encode("Zürich").hex(" ")
        '41 a3 59 50 4a 4f'
    """
    if not isinstance(text, str):
        raise TypeError(f"encode() takes a str, not {type(text).__name__}")
    return _backend.encode(text)


def decode(data: BytesLike) -> str:
    """Decodes the Steno bytes of one value.

    Args:
        data: The bytes: ``bytes``, ``bytearray``, ``memoryview`` or another object with the buffer protocol.

    Returns:
        The text, with exactly the code points that were encoded, lone surrogates included.

    Raises:
        StenoDecodeError: The bytes are not the Steno bytes of a value.
        TypeError: ``data`` is not bytes-like.

    Example:
        >>> decode(bytes.fromhex("41a359504a4f"))
        'Zürich'
    """
    return _backend.decode(_codec.as_bytes(data))


def max_encoded_len(length: int) -> int:
    """Returns the most bytes that a string of ``length`` code points can encode to: ``4 * length``.

    No code point takes more than 4 bytes (FORMAT.md section 1.3). Python's ``len()`` of a str counts code points.

    Raises:
        ValueError: ``length`` is negative.
        TypeError: ``length`` is not an int.
    """
    _check_length(length)
    return 4 * length


def max_decoded_len(length: int) -> int:
    """Returns the most code points (``len()`` of a str) that ``length`` Steno bytes can decode to: ``length``.

    Every code point takes at least 1 byte.

    Raises:
        ValueError: ``length`` is negative.
        TypeError: ``length`` is not an int.
    """
    _check_length(length)
    return length


def _check_length(length: int) -> None:
    if not isinstance(length, int) or isinstance(length, bool):
        raise TypeError(f"length must be an int, not {type(length).__name__}")
    if length < 0:
        raise ValueError("length must not be negative")


def is_ascii_text(data: BytesLike) -> bool:
    """Returns True if a value holds only tab, line feed, carriage return and U+0020..U+007E, decided from its Steno bytes alone.

    It does not decode the bytes; it checks that every byte is 02, 03, 05 or 07..65 (FORMAT.md section 14.1). Such bytes are not ASCII
    bytes: U+0020..U+007E are the bytes 07..65.

    Example:
        >>> is_ascii_text(encode("Hello, world!")), is_ascii_text(encode("Zürich"))
        (True, False)
    """
    return _backend.is_ascii_text(_codec.as_bytes(data))


_codec.register()
