"""The codec ``x-steno`` for the codec machinery of the standard library, and the incremental encoder and decoder."""

from __future__ import annotations

import codecs
import hashlib
import io
import os
import threading
from typing import TYPE_CHECKING, Any, Dict, Optional, Tuple, Union

from . import _backend
from ._errors import ENCODING_NAME, StenoDecodeError, StenoDecodeErrorReason
from ._layout import COMPACT_LIMIT, STATE_NUMBER_LIMIT

if TYPE_CHECKING:
    from typing_extensions import Buffer

    BytesLike = Buffer
else:
    BytesLike = Union[bytes, bytearray, memoryview]


def as_bytes(data: BytesLike) -> bytes:
    """Gets the bytes of a bytes-like object. Raises TypeError for anything else (also for an int and a str)."""
    if isinstance(data, bytes):
        return data
    return bytes(memoryview(data))


def check_errors(errors: str) -> None:
    """Raises ValueError for an error handler other than ``strict``, the only one that the Steno Encoding can honour."""
    if errors != "strict":
        raise ValueError(
            f"the codec {ENCODING_NAME!r} supports only errors='strict', not {errors!r}: the encoding is stateful, and after invalid data "
            "the state is unknown, so no error handler can replace the bad part and go on"
        )


_CANNOT_CONTINUE = (
    f"the codec {ENCODING_NAME!r} cannot write here: the stream was continued at a position whose state the encoder does not know (a file "
    "opened for appending that is not empty, or a seek, a read or a header before the write). The encoding is stateful, so the bytes of new "
    "text depend on all the text before it; write the whole value from its start, for example with singulink_steno.open()"
)


class Encoder(codecs.IncrementalEncoder):
    """Encodes one value given in pieces: the incremental encoder of the codec ``x-steno``.

    Over any split of a string, the pieces give the bytes that :func:`singulink_steno.encode` gives for the whole string. The encoder never
    holds text back: each call returns all the bytes of its piece, because the bytes of a code point do not depend on the code points after
    it. So flushing a text file in the middle of a value is safe.

    - ``final=True`` changes nothing (there is nothing to write at the end), and it does not start a new value. :meth:`reset` starts one.
    - A high surrogate at the end of one piece followed by a low surrogate at the start of the next is not a value: the second call raises
      :class:`UnicodeEncodeError`, as one call with both would.
    - :meth:`getstate` returns a positive number that holds the whole state, and :meth:`setstate` takes it back. ``setstate(0)`` is how a
      text file says that it goes on in the middle of a stream whose state it does not know (a file opened for appending that is not empty,
      a seek to a position other than 0, a file opened over a stream positioned after a header). The encoder then refuses to write, with
      :class:`io.UnsupportedOperation`, until :meth:`reset` or :meth:`setstate` with a number of :meth:`getstate`. Reading such a file
      works.
    - Copies (``copy.copy``, ``copy.deepcopy``, ``pickle``) are independent encoders in the same state.

    A call that raises leaves the encoder as it was. Not to be shared between threads: an encoder holds the state of one value.
    """

    def __init__(self, errors: str = "strict") -> None:
        """Creates the encoder.

        Args:
            errors: The error handler; only ``"strict"`` is accepted.

        Raises:
            ValueError: ``errors`` is not ``"strict"``.
        """
        check_errors(errors)
        super().__init__(errors)
        self._core = _backend.EncoderCore()
        self._unknown = False

    def encode(self, input: str, final: bool = False) -> bytes:  # noqa: A002 (the name of the codecs API)
        """Encodes the next piece of the value.

        Args:
            input: The next piece of the string.
            final: True if this is the last piece. It changes nothing.

        Returns:
            The bytes of the piece.

        Raises:
            UnicodeEncodeError: The string has a high surrogate directly followed by a low one (also across two pieces).
            io.UnsupportedOperation: The encoder was told with ``setstate(0)`` that it goes on at a position whose state it does not know.
            TypeError: ``input`` is not a str.
        """
        if not isinstance(input, str):
            raise TypeError(f"{ENCODING_NAME} encodes str, not {type(input).__name__}")
        if self._unknown and input:
            raise io.UnsupportedOperation(_CANNOT_CONTINUE)
        return self._core.encode(input)

    def reset(self) -> None:
        """Starts a new value."""
        self._core.reset()
        self._unknown = False

    def getstate(self) -> int:
        """Returns a number that holds the whole state of the encoder. It is never 0."""
        return self._core.pack() + 1

    def setstate(self, state: int) -> None:  # type: ignore[override]
        """Sets the state from a number of :meth:`getstate`, or marks the state unknown with 0 (see the class).

        Raises:
            ValueError: ``state`` is not a number of :meth:`getstate`. The encoder is then unchanged.
            TypeError: ``state`` is not an int.
        """
        if not isinstance(state, int) or isinstance(state, bool):
            raise TypeError("the state of the x-steno encoder is an int")
        if state == 0:
            self._unknown = True
            return
        core = _backend.EncoderCore()
        core.unpack(state - 1)
        self._core = core
        self._unknown = False

    def __getstate__(self) -> Dict[str, Any]:
        return {"errors": self.errors, "state": self._core.pack(), "unknown": self._unknown}

    def __setstate__(self, state: Dict[str, Any]) -> None:
        self.errors = state["errors"]
        self.buffer = ""
        self._core = _backend.EncoderCore()
        self._core.unpack(state["state"])
        self._unknown = state["unknown"]


class _StateTokens:
    """Numbers for the decoder states that have no compact number, so that every state number fits a text file's tell() cookie.

    A state with no compact number (in Chinese, Japanese or Korean text, right after a code point of a window that is neither the main nor
    the second window, or after a lone high surrogate) gets a token: a number from COMPACT_LIMIT up to 2**30 (SPACE = 99,616,168 values)
    that this process keeps in a table, with the whole state. The table keeps the last TOKEN_LIMIT states.

    The token is a keyed hash of the whole state, with a random key chosen when the process starts (the first free one of a few probes).
    So a token from another process matches an entry of this table only by chance, at most TOKEN_LIMIT / SPACE (about 1 in 1,520) when the
    table is full and far less when it holds few states; a token that is not in the table is refused with io.UnsupportedOperation. An entry is
    never replaced by a different state while it is in the table (but for a chance below 1 in 10**25), and a token that was dropped from the table can come back for a different
    state only if that state's hash happens to give the same number (about 1 in 100 million).
    """

    TOKEN_LIMIT = 65536
    SPACE = STATE_NUMBER_LIMIT - COMPACT_LIMIT
    PROBES = 8

    def __init__(self, key: Optional[bytes] = None) -> None:
        self._lock = threading.Lock()
        self._key = key if key is not None else os.urandom(16)
        self._by_token: Dict[int, int] = {}
        self._by_state: Dict[int, int] = {}

    def _candidate(self, state: int, probe: int) -> int:
        digest = hashlib.blake2b(b"%d/%d" % (state, probe), digest_size=8, key=self._key).digest()
        return COMPACT_LIMIT + int.from_bytes(digest, "little") % self.SPACE

    def token(self, state: int) -> int:
        with self._lock:
            token = self._by_state.get(state)
            if token is not None:
                return token
            if len(self._by_token) >= self.TOKEN_LIMIT:
                old = next(iter(self._by_token))
                del self._by_state[self._by_token.pop(old)]
            for probe in range(self.PROBES):
                token = self._candidate(state, probe)
                if token not in self._by_token:
                    self._by_token[token] = state
                    self._by_state[state] = token
                    return token
            # Every probe is taken by another state, a chance of about (TOKEN_LIMIT / SPACE) ** PROBES, below 1 in 10**25: give the first
            # probe to this state. A cookie of the state that had it would then restore this one; at that chance it is accepted.
            token = self._candidate(state, 0)
            del self._by_state[self._by_token.pop(token)]
            self._by_token[token] = state
            self._by_state[state] = token
            return token

    def state(self, token: int) -> Optional[int]:
        with self._lock:
            state = self._by_token.get(token)
        if state is None:
            return None
        # The entry must be one that this process's key gives for that state.
        if not any(self._candidate(state, probe) == token for probe in range(self.PROBES)):
            return None
        return state


_tokens = _StateTokens()

_NOT_RESTORABLE = (
    f"the codec {ENCODING_NAME!r} cannot restore this decoder state: the position was taken (with tell() or getstate()) in a state that has "
    "no compact number, and this process no longer remembers it or never did. Seek to the start of the value, or to a position taken in "
    "this process"
)


class Decoder(codecs.IncrementalDecoder):
    """Decodes one value given in pieces: the incremental decoder of the codec ``x-steno``.

    Over any split of the bytes, the pieces give the text that :func:`singulink_steno.decode` gives for the whole value. Each call gives out
    every code point that its bytes complete; it holds only the bytes of an unfinished code point.

    - ``final=True`` ends the input: held bytes of an unfinished code point are then the ``truncated`` error. It does not start a new
      value; :meth:`reset` does.
    - Invalid bytes raise :class:`~singulink_steno.StenoDecodeError`, whose offset counts from the first byte of the value, or from the
      last :meth:`setstate`. A call that raises leaves the decoder as it was. One exception to "at once": a byte 00 in a call without
      ``final=True`` is raised by the next call (which then keeps raising until :meth:`reset` or :meth:`setstate`); the call that received it
      gives out the text before it. A text file's ``tell()`` needs this (see the comment in the code). No wrong text comes out either way.
    - :meth:`getstate` returns the held bytes and a state number below 2**30 (what a text file's ``tell()`` can store), 0 for the initial
      state. :meth:`setstate` restores the state exactly. Most states have a number of their own; the rare state that has none (in Chinese,
      Japanese or Korean text right after a code point of an unrelated window, or after a lone high surrogate there) gets a number that only
      this process remembers, for its last 65,536 such states. :meth:`setstate` with a number that this process does not remember raises
      :class:`io.UnsupportedOperation`.
    - Copies (``copy.copy``, ``copy.deepcopy``, ``pickle``) are independent decoders in the same state.

    Not to be shared between threads: a decoder holds the state of one value.
    """

    def __init__(self, errors: str = "strict") -> None:
        """Creates the decoder.

        Args:
            errors: The error handler; only ``"strict"`` is accepted.

        Raises:
            ValueError: ``errors`` is not ``"strict"``.
        """
        check_errors(errors)
        super().__init__(errors)
        self._core = _backend.DecoderCore()
        # A zero-byte error of a call that did not end the value, raised by the next call, and the bytes from its code point on.
        self._deferred: Optional[Tuple[StenoDecodeError, bytes]] = None

    def decode(self, input: BytesLike, final: bool = False) -> str:  # noqa: A002 (the name of the codecs API)
        """Decodes the next piece of the value.

        Args:
            input: The next piece of the bytes.
            final: True if this is the last piece.

        Returns:
            The text of the code points that the piece completes.

        Raises:
            StenoDecodeError: The bytes are not the Steno bytes of a value, or ``final`` is True and the bytes end in the middle of a code
                point (``truncated``).
        """
        data = as_bytes(input)
        deferred = self._deferred
        if deferred is not None:
            raise deferred[0]
        core = self._core
        try:
            return core.decode(data, final)
        except StenoDecodeError as e:
            if final or e.reason is not StenoDecodeErrorReason.ZERO_BYTE:
                raise
            # A byte 00 in a call that does not end the value: give out the text before the code point that holds it, and raise the error
            # with the next call. The bytes from that code point on are held, so getstate() shows them. This keeps tell() of a text file
            # working: CPython's TextIOWrapper.tell() can read past the end of its buffer, into the 00 byte that ends every bytes object,
            # and when getstate() then shows held bytes it steps back instead of failing.
            buf = core.partial + data
            good = core.decode(data[: max(0, e.start - len(core.partial))], False)
            core.drop_partial()
            self._deferred = (e, buf[e.start :])
            return good

    def reset(self) -> None:
        """Starts a new value: forgets the state and any held bytes."""
        self._core.reset()
        self._deferred = None

    def getstate(self) -> Tuple[bytes, int]:
        """Returns the state: the held bytes of an unfinished code point, and a state number below 2**30 (0 for the initial state)."""
        core = self._core
        number = core.compact_state()
        if number is None:
            number = _tokens.token(core.full_state())
        if self._deferred is not None:
            return (self._deferred[1], number)
        return (core.partial, number)

    def setstate(self, state: Tuple[bytes, int]) -> None:
        """Sets the state from a value of :meth:`getstate`.

        Raises:
            ValueError: The number is not a state number, or the bytes are more than an unfinished code point.
            io.UnsupportedOperation: The number is one that this process does not remember (see the class).
            StenoDecodeError: The bytes are not valid in that state.
            TypeError: The state is not a pair of bytes and an int.

        If it raises, the decoder is unchanged.
        """
        buf, number = state
        if not isinstance(number, int) or isinstance(number, bool):
            raise TypeError("the state number of the x-steno decoder is an int")
        data = as_bytes(buf)
        if not 0 <= number < STATE_NUMBER_LIMIT:
            raise ValueError("not an x-steno decoder state number")
        core = _backend.DecoderCore()
        if number < COMPACT_LIMIT:
            core.set_compact_state(number)
        else:
            full = _tokens.state(number)
            if full is None:
                raise io.UnsupportedOperation(_NOT_RESTORABLE)
            core.set_full_state(full)
        if data and core.decode(data, False):
            raise ValueError("the bytes of an x-steno decoder state are only the bytes of an unfinished code point")
        self._core = core
        self._deferred = None

    def __getstate__(self) -> Dict[str, Any]:
        core = self._core
        return {"errors": self.errors, "state": core.full_state(), "partial": core.partial, "base": core.base, "deferred": self._deferred}

    def __setstate__(self, state: Dict[str, Any]) -> None:
        self.errors = state["errors"]
        self._core = _backend.DecoderCore()
        self._core.set_full_state(state["state"], state["partial"], state["base"])
        self._deferred = state["deferred"]


def _tell(stream: Any) -> Optional[int]:
    """The position of a binary stream, or None if it has none."""
    try:
        return int(stream.tell())
    except (AttributeError, OSError, ValueError):
        return None


class StreamWriter(codecs.StreamWriter):
    """The stream writer of the codec ``x-steno`` (for ``codecs.open`` and ``codecs.getwriter``).

    Everything written is one value, from the position of the stream at the first write, which must be 0. If the stream has a position, the
    writer checks before each write that the stream is where the last write left it; a stream that is not at position 0 at the first write
    (``codecs.open(path, "a", ...)`` on a file that is not empty, for example), or that was moved by a seek or a read, is refused with
    :class:`io.UnsupportedOperation`, because the bytes of new text depend on all the text before it. :meth:`seek` is refused too.
    :meth:`reset` starts a new value after the bytes written so far; it writes nothing.
    """

    def __init__(self, stream: Any, errors: str = "strict") -> None:
        """Creates the writer over a binary stream. Raises ValueError if ``errors`` is not ``"strict"``."""
        check_errors(errors)
        super().__init__(stream, errors)
        self._core = _backend.EncoderCore()
        self._expected: Optional[int] = 0

    def encode(self, input: str, errors: str = "strict") -> Tuple[bytes, int]:  # noqa: A002 (the name of the codecs API)
        """Encodes the next piece of the value."""
        check_errors(errors)
        if not isinstance(input, str):
            raise TypeError(f"{ENCODING_NAME} encodes str, not {type(input).__name__}")
        return self._core.encode(input), len(input)

    def write(self, object: str) -> None:  # noqa: A002 (the name of the codecs API)
        """Encodes the text and writes it to the stream."""
        pos = _tell(self.stream)
        if pos is not None and pos != self._expected:
            raise io.UnsupportedOperation(_CANNOT_CONTINUE)
        data, _ = self.encode(object, self.errors)
        self.stream.write(data)
        if pos is not None:
            self._expected = pos + len(data)

    def writelines(self, list: Any) -> None:  # noqa: A002 (the name of the codecs API)
        """Writes the strings one after another."""
        self.write("".join(list))

    def reset(self) -> None:
        """Starts a new value after the bytes written so far (the stream must still be where the last write left it)."""
        self._core.reset()

    def seek(self, offset: int, whence: int = 0) -> None:
        """Refused: a Steno value cannot be written from the middle (see the class)."""
        raise io.UnsupportedOperation(_CANNOT_CONTINUE)


class StreamReader(codecs.StreamReader):
    """The stream reader of the codec ``x-steno`` (for ``codecs.open`` and ``codecs.getreader``).

    The value starts at the position of the stream at the first read. A read that reaches the end of the stream in the middle of a code point
    raises the ``truncated`` error. If the stream has a position, the reader checks before each read that the stream is where the last read
    left it, or at the start of the value (then it starts over); any other move, by a seek or a write, is refused with
    :class:`io.UnsupportedOperation`, because decoding cannot start in the middle of a value. :meth:`reset` starts a new value.
    """

    def __init__(self, stream: Any, errors: str = "strict") -> None:
        """Creates the reader over a binary stream. Raises ValueError if ``errors`` is not ``"strict"``."""
        check_errors(errors)
        super().__init__(stream, errors)
        self._core = _backend.DecoderCore()
        self._start: Optional[int] = None
        self._expected: Optional[int] = None

    def decode(self, input: BytesLike, errors: str = "strict") -> Tuple[str, int]:  # noqa: A002 (the name of the codecs API)
        """Decodes the next bytes. The bytes of an unfinished code point are left to the base class, which passes them in again."""
        check_errors(errors)
        data = as_bytes(input)
        text = self._core.decode(data, False)
        held = len(self._core.partial)
        self._core.drop_partial()
        return text, len(data) - held

    def read(self, size: int = -1, chars: int = -1, firstline: bool = False) -> str:
        """Reads and decodes; see :meth:`codecs.StreamReader.read`."""
        pos = _tell(self.stream)
        if pos is not None:
            if self._start is None:
                self._start = pos
                self._expected = pos
            elif pos != self._expected:
                if pos != self._start:
                    raise io.UnsupportedOperation(
                        f"the codec {ENCODING_NAME!r} cannot read from here: the stream was moved to a position in the middle of the value, "
                        "and decoding must start at its first byte"
                    )
                self._restart()
        text = super().read(size, chars, firstline)
        held: bytes = getattr(self, "bytebuffer", b"")
        if held and (not text or (size < 0 and chars < 0)):
            # Bytes of an unfinished code point are left. If the stream has ended, that is the truncated error.
            more = self.stream.read(1)
            if more:
                self.bytebuffer = held + more
            else:
                self._core.decode(held, True)
        if pos is not None:
            self._expected = _tell(self.stream)
        return text

    def _restart(self) -> None:
        super().reset()
        self._core.reset()

    def reset(self) -> None:
        """Forgets the buffers and starts a new value at the current position. It does not move in the stream."""
        self._restart()
        self._start = None
        self._expected = None

    def seek(self, offset: int, whence: int = 0) -> None:
        """Moves to the start of the value (only there: see the class) and starts over."""
        self.stream.seek(offset, whence)
        pos = _tell(self.stream)
        if self._start is not None and pos != self._start:
            raise io.UnsupportedOperation(
                f"the codec {ENCODING_NAME!r} can seek only to the start of the value: decoding cannot start in the middle of a value"
            )
        self._restart()
        self._expected = pos


def _encode(input: str, errors: str = "strict") -> Tuple[bytes, int]:  # noqa: A002 (the name of the codecs API)
    check_errors(errors)
    if not isinstance(input, str):
        raise TypeError(f"{ENCODING_NAME} encodes str, not {type(input).__name__}")
    return _backend.encode(input), len(input)


def _decode(input: BytesLike, errors: str = "strict") -> Tuple[str, int]:  # noqa: A002 (the name of the codecs API)
    check_errors(errors)
    data = as_bytes(input)
    return _backend.decode(data), len(data)


CODEC_INFO = codecs.CodecInfo(
    name=ENCODING_NAME,
    encode=_encode,
    decode=_decode,
    incrementalencoder=Encoder,
    incrementaldecoder=Decoder,
    streamreader=StreamReader,
    streamwriter=StreamWriter,
)


def search(name: str) -> Optional[codecs.CodecInfo]:
    """The search function that ``codecs.register`` takes: finds ``x-steno`` (also written ``x_steno`` or in upper case)."""
    if name.lower().replace("_", "-").replace(" ", "-") == ENCODING_NAME:
        return CODEC_INFO
    return None


_registered = False


def register() -> None:
    """Registers the codec ``x-steno``. Calling it again does nothing."""
    global _registered
    if not _registered:
        codecs.register(search)
        _registered = True
