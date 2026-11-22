"""The choice of the backend: the module that does the encoding and the decoding.

The public functions and classes call only the names below, so a compiled backend can replace the pure Python one without a change to the
public API. A backend module provides:

- ``NAME``: its name, a ``str``.
- ``encode(text: str) -> bytes`` and ``decode(data: bytes) -> str``: one whole value. ``decode`` raises ``StenoDecodeError``; ``encode``
  raises ``UnicodeEncodeError`` for a string that is not a value.
- ``is_ascii_text(data: bytes) -> bool``.
- ``EncoderCore`` and ``DecoderCore``: the state of an incremental encoder and decoder, with the methods of the classes in ``_pure``.

Only the pure Python backend exists so far. The environment variable ``SINGULINK_STENO_BACKEND`` can name the backend to use; ``python``
is the only value that is accepted now.
"""

from __future__ import annotations

import os

from . import _pure

_requested = os.environ.get("SINGULINK_STENO_BACKEND", "").strip().lower()
if _requested not in ("", "python"):
    raise ImportError(
        f"singulink_steno: the backend {_requested!r} that SINGULINK_STENO_BACKEND names is not available; the only backend is 'python'"
    )

NAME = _pure.NAME
encode = _pure.encode
decode = _pure.decode
is_ascii_text = _pure.is_ascii_text
EncoderCore = _pure.EncoderCore
DecoderCore = _pure.DecoderCore
