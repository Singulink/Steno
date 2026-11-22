"""singulink_steno.open(): text files in the Steno Encoding that refuse what would write wrong bytes."""

from __future__ import annotations

import builtins
import io
import os
from typing import IO, Any, Callable, Optional, Union

from ._errors import ENCODING_NAME

_MODES = {"r": "rb", "w": "wb", "x": "xb", "a": "ab"}

_NO_SEEK_FOR_WRITING = (
    "a Steno text file that is open for writing cannot seek or truncate: the bytes of new text depend on all the text before it, so the "
    "value must be written from its start to its end in one go"
)


class StenoTextFile(io.TextIOWrapper):
    """A text file in the Steno Encoding, as :func:`singulink_steno.open` returns it.

    It is an :class:`io.TextIOWrapper` with the codec ``x-steno`` that is open either for reading or for writing, never both, and that
    refuses with :class:`io.UnsupportedOperation` what would write wrong bytes: ``seek()`` and ``truncate()`` while it is open for writing,
    and ``reconfigure()`` of anything but ``line_buffering`` and ``write_through``. Opened for reading, ``tell()`` and ``seek()`` work as
    for any text file (see the README for the one kind of position that cannot always be restored).
    """

    def seek(self, cookie: int, whence: int = 0) -> int:
        """Moves in a file that is open for reading. Refused for a file that is open for writing."""
        if self.writable():
            raise io.UnsupportedOperation(_NO_SEEK_FOR_WRITING)
        return super().seek(cookie, whence)

    def truncate(self, pos: Optional[int] = None) -> int:
        """Refused: a Steno value cannot be cut in the middle and written on."""
        raise io.UnsupportedOperation(_NO_SEEK_FOR_WRITING)

    def reconfigure(self, **kwargs: Any) -> None:
        """Changes ``line_buffering`` or ``write_through``; anything else is refused.

        A change of the encoding, the error handler or the newline mode in the middle of a value would leave text that cannot be read back.
        """
        allowed = {"line_buffering", "write_through"}
        refused = [k for k in kwargs if k not in allowed]
        if refused:
            raise io.UnsupportedOperation(
                f"a Steno text file can reconfigure only line_buffering and write_through, not {', '.join(refused)}"
            )
        super().reconfigure(**kwargs)


def open(  # noqa: A001 (the name is that of the built-in on purpose)
    file: Union[str, bytes, os.PathLike[str], os.PathLike[bytes], int],
    mode: str = "r",
    buffering: int = -1,
    newline: Optional[str] = None,
    closefd: bool = True,
    opener: Optional[Callable[[str, int], int]] = None,
) -> StenoTextFile:
    """Opens a text file in the Steno Encoding, in a mode that cannot write wrong bytes.

    The whole file is one value. The modes are:

    - ``"r"``: read. ``tell()`` and ``seek()`` work.
    - ``"w"``: write a new value (the file is created or emptied).
    - ``"x"``: write a new value into a file that must not exist yet.
    - ``"a"``: write a new value into a file that is empty or does not exist; a file that is not empty is refused, because text cannot be
      added to a Steno value without knowing the state at its end.

    ``"t"`` may be added; ``"b"`` and ``"+"`` are refused (reading and writing one Steno file at once cannot work: see the README). The
    encoding is always ``x-steno`` and the error handler ``strict``. ``buffering``, ``newline``, ``closefd`` and ``opener`` mean what they mean
    for the built-in :func:`open`.

    Returns:
        A :class:`StenoTextFile`.

    Raises:
        ValueError: The mode is not one of the above.
        io.UnsupportedOperation: Mode ``"a"`` on a file that is not empty.
    """
    letters = mode.replace("t", "")
    if letters not in _MODES or len(mode) - len(letters) > 1:
        raise ValueError(
            f"singulink_steno.open() takes the mode 'r', 'w', 'x' or 'a' (with an optional 't'), not {mode!r}: a Steno file is read or "
            "written as one value from its start, never both at once"
        )
    binary: IO[bytes] = builtins.open(file, _MODES[letters], buffering=-1 if buffering == 1 else buffering, closefd=closefd, opener=opener)  # noqa: SIM115
    try:
        if letters == "a" and binary.tell() != 0:
            raise io.UnsupportedOperation(
                "a Steno file that is not empty cannot be appended to: the bytes of new text depend on all the text before it; read the "
                "file, then write the whole value anew"
            )
        return StenoTextFile(binary, encoding=ENCODING_NAME, errors="strict", newline=newline, line_buffering=buffering == 1)
    except BaseException:
        binary.close()
        raise
