# Python Reference Implementation of the Steno Encoding

The package `singulink-steno` (import name `singulink_steno`) is the Python reference implementation of the Steno Encoding. It runs on
Python 3.9 and later, has no dependencies, and is written in pure Python, so it installs and runs everywhere Python does.

The Steno Encoding is a compact encoding of Unicode text whose bytes sort like the text. It was designed to be the ideal per-value compression
encoding for databases and key-value stores, which can sort, compare, range-scan and prefix-match Steno bytes as plain bytes, without decoding
them.

- [Specification](https://github.com/Singulink/Singulink.Steno/blob/main/FORMAT.md) (Steno Encoding, version 1)
- [Main README](https://github.com/Singulink/Singulink.Steno#readme), with the sizes and the comparisons with other encodings
- [This implementation on the documentation site](https://www.singulink.com/Docs/Steno/articles/implementations/python.html)
- [Guide to storing values](https://www.singulink.com/Docs/Steno/articles/start/storing-values.html)

## Install

> Not published yet: the package below is the planned name.

```sh
pip install singulink-steno
```

## Usage

The examples below are run as tests (`pytest` runs this file as a doctest).

### Encode and Decode

```pycon
>>> import singulink_steno as steno
>>> data = steno.encode("Москва")
>>> len(data), len("Москва".encode("utf-8"))
(7, 12)
>>> steno.decode(data)
'Москва'

```

`encode` takes a `str` and returns `bytes`. `decode` takes `bytes`, `bytearray`, `memoryview` or any other object with the buffer protocol
and returns a `str`. Both work on one whole value. The maximum-size helpers are true bounds: a string of `n` code points (Python's `len()` of a
`str`) encodes to at most `4n` bytes, and `n` bytes decode to at most `n` code points.

```pycon
>>> steno.max_encoded_len(6), steno.max_decoded_len(7)
(24, 7)

```

### Text Files: Use `singulink_steno.open`

A whole text file is one value. Open Steno text files with the package's own `open()`:

```pycon
>>> import os, tempfile
>>> path = os.path.join(tempfile.mkdtemp(), "cities.txt")
>>> with steno.open(path, "w") as f:
...     _ = f.write("Москва\n東京\n")
>>> with steno.open(path) as f:
...     f.readlines()
['Москва\n', '東京\n']

```

`singulink_steno.open(file, mode="r", buffering=-1, newline=None, closefd=True, opener=None)` returns a `StenoTextFile`, an
`io.TextIOWrapper` with the codec `x-steno`. It takes only the modes that cannot write wrong bytes:

| Mode | What it does |
|---|---|
| `"r"` | Reads. `tell()` and `seek()` work (see below). |
| `"w"` | Writes a new value: the file is created or emptied. |
| `"x"` | Writes a new value into a file that must not exist yet. |
| `"a"` | Writes a new value into a file that is empty or does not exist. A file that is not empty raises `io.UnsupportedOperation`. |

`"t"` may be added. `"b"` and the modes with `"+"` raise `ValueError`. A file open for writing refuses `seek()`, `truncate()` and
`reconfigure()` (except `line_buffering` and `write_through`) with `io.UnsupportedOperation`.

#### What the Built-in `open()` Does

The built-in `open(path, mode, encoding="x-steno")` works with the codec too, but Python gives a codec no way to refuse some of what a text
file does, so it is safe only for some modes:

| Built-in `open()` | Result |
|---|---|
| `"r"` | Safe: reads, with `tell()` and `seek()`. |
| `"w"`, `"x"`, or `"a"` on an empty file | Safe for writing a new value from start to end. |
| `"a"` on a file that is not empty | Fails loudly: the first `write()` raises `io.UnsupportedOperation`. |
| `"r+"`, `"w+"`, `"a+"`: a write after a read | **Not safe. Do not use.** The new text is encoded from the start of a value after the old bytes, and the file cannot be read back. Nothing in the codec API can see it. |
| a write after `seek()` to a position other than 0 | Fails loudly: the write raises `io.UnsupportedOperation`. |
| a write after `seek(0)` over old text | **Not safe.** It writes a new value over the start of the old one and leaves the rest of the old bytes. |
| a write after `reconfigure()`, also one that raised | **Not safe.** Python replaces the encoder, so the next write starts a new value in the middle. |

`codecs.open()`, `codecs.getreader()` and `codecs.getwriter()` give the codec's stream classes. They check the position of the stream: a
writer refuses to write unless the stream is where its last write left it (so appending to a file that is not empty, a seek, or a read before
the write is refused), and a reader refuses to read after the stream was moved anywhere but to the start of the value. The value of a reader
starts where the stream is at its first read, so a reader can read a value after a header.

#### Writing, Reading, `tell()` and `seek()`

- **Flushing** in the middle of a value is safe: the encoder never holds text back, so `flush()` writes a prefix of the final bytes and
  never adds or changes anything.
- **`tell()` and `seek()`.** `tell()` never raises on a valid file. A position from `tell()` is meant for the process that produced it:
  - `seek(0)` always works.
  - In the same process, `seek()` to a position from `tell()` restores it exactly. The one exception is a position in Chinese, Japanese or
    Korean text right after a character of an unrelated script (or after a lone high surrogate there). Its state is too large for the number
    that `tell()` stores, so the process remembers it, for the last 65,536 such positions; a `seek()` to one it no longer remembers raises
    `io.UnsupportedOperation`.
  - A position saved and used in another process restores exactly for almost all text. In the rare case above it is refused with
    `io.UnsupportedOperation`, but a chance remains that it is accepted wrongly: at most about 1 in 1,500 (when the process that reads has
    65,536 such states in its table), and much less when it has met few of them.
  - A program that must store positions across runs should store the byte offsets of the starts of values instead, and decode each value
    from its start.
- **A file that ends in the middle of a code point** raises the `truncated` error at the end of reading.
- **A file opened over a stream that already holds a header** (for example `io.TextIOWrapper(raw, encoding="x-steno")` after reading a
  header from `raw`) can be read; a write to it is refused.
- **Byte order marks:** the codec neither writes nor removes one. U+FEFF is an ordinary code point, also at the start of a value.

The [Python bug notes](#text-file-bugs-in-python-itself) below list two defects of Python's own text files that affect every codec.

### The Codec `x-steno`

Importing the package registers the codec `x-steno` with the codec machinery of the standard library, so the usual ways to encode and decode
work too:

```pycon
>>> "Zürich".encode("x-steno")
b'A\xa3YPJO'
>>> b"A\xa3YPJO".decode("x-steno")
'Zürich'

```

`codecs.lookup("x-steno")`, `codecs.getincrementalencoder`, `codecs.getincrementaldecoder`, `codecs.iterencode` and `codecs.iterdecode` all
work. The codec is registered when `singulink_steno` is first imported, as with other codecs that are not in the standard library; import
it once before you use the name.

Only the error handler `"strict"` is accepted. Any other (`"replace"`, `"ignore"`, `"surrogateescape"` and so on) raises `ValueError` at
once, also when a file is opened: the encoding is stateful, and after invalid bytes the state is unknown, so no handler could replace the
bad part and go on. The codec does not need `"surrogatepass"` either: lone surrogates are code points of their own in the Steno Encoding.

### Compare and Sort

Python compares `str` values in code point order and `bytes` values as unsigned bytes. So the order of Steno bytes under `<`, `sorted()` and
`min()` is the order of the strings under the same operators, and the package needs no comparison functions of its own:

```pycon
>>> steno.encode("Zug") < steno.encode("Zürich"), "Zug" < "Zürich"
(True, True)
>>> cities = ["Zürich", "Zug", "Århus", "Zagreb"]
>>> sorted(cities) == sorted(cities, key=steno.encode)
True
>>> steno.encode("Zürich").startswith(steno.encode("Zür"))
True

```

The bytes of a prefix of a text are a prefix of the bytes of the text, so `bytes.startswith` on Steno bytes answers a prefix query.

No byte below `08` occurs inside a code point, so line feeds, tabs and spaces can be found at the byte level: line feed is always the byte
`03`, tab `02`, carriage return `05` and space `07`. A line found this way still cannot be decoded on its own, because its bytes depend on the
text before it.

```pycon
>>> lines = ["Москва", "東京", "Zürich"]
>>> data = steno.encode("\n".join(lines))
>>> len(data.split(b"\x03"))
3

```

### ASCII Text

```pycon
>>> steno.is_ascii_text(steno.encode("Hello, world!")), steno.is_ascii_text(steno.encode("Zürich"))
(True, False)

```

`is_ascii_text` decides from the bytes alone, without decoding, whether a value has only tab, line feed, carriage return and
U+0020..U+007E. The bytes of such a value are not ASCII bytes: U+0020..U+007E are the bytes `07`..`65`.

### Incremental Encoding and Decoding

```pycon
>>> enc = steno.Encoder()
>>> data = enc.encode("Моск") + enc.encode("ва", final=True)
>>> data == steno.encode("Москва")
True
>>> dec = steno.Decoder()
>>> dec.decode(data[:2]) + dec.decode(data[2:], final=True)
'Москва'
>>> import codecs
>>> codecs.getincrementaldecoder("x-steno") is steno.Decoder
True

```

`Encoder` and `Decoder` are the incremental encoder and decoder of the codec (subclasses of `codecs.IncrementalEncoder` and
`codecs.IncrementalDecoder`). What they guarantee:

- Over any split of the input, the pieces give the bytes or the text of the one-shot functions for the whole value. Each call gives out every
  code point that its bytes complete; the decoder holds only the bytes of an unfinished code point, until the next call.
- `final=True` ends the input: the decoder raises the `truncated` error for held bytes of an unfinished code point. It does not start a new
  value, as with the codecs of the standard library; `reset()` does.
- A call that raises leaves the encoder or decoder as it was, `setstate()` included.
- `getstate()` and `setstate()` follow the contract of the `codecs` module. The decoder's state number is always below 2\*\*30, so that a
  text file's `tell()` can store it (see [Text Files](#writing-reading-tell-and-seek) for the one kind of state that only the process
  remembers). The encoder's state is a number that is never 0, because text files call `setstate(0)` to say "go on in the middle of a stream
  whose state is unknown"; after that the encoder refuses to write, with `io.UnsupportedOperation`, until `reset()`.
- `copy.copy()`, `copy.deepcopy()` and `pickle` give independent objects in the same state.

`StreamReader` and `StreamWriter` are the stream classes of the codec, for `codecs.open`, `codecs.getreader` and `codecs.getwriter` (see
[Text Files](#what-the-built-in-open-does)).

### Errors

```pycon
>>> try:
...     steno.decode(b"\x2f\x00")
... except steno.StenoDecodeError as e:
...     print(e.reason, e.offset)
zero-byte 1

```

Bytes that are not the Steno bytes of a value raise `StenoDecodeError`. It is a subclass of `UnicodeDecodeError` (and so of `ValueError`),
so code that handles decoding errors of any codec, such as `except UnicodeDecodeError` around `bytes.decode` or reading a text file, handles it
too. Its `reason` is a `StenoDecodeErrorReason`, the rule of the specification that the bytes broke: `ZERO_BYTE`, `UNUSED_LEAD`, `TRUNCATED`,
`PAST_RANGE`, `SURROGATE_PAIR` or `INVALID_TRAIL` (a byte `01`..`07` where a trail byte `08`..`FF` is expected), in the check order of the specification (sections 9.2 and 10.1). The members are strings
(`e.reason == "zero-byte"` works). Its `offset` is where the code point that broke the rule starts, counted from the first byte of the value.
For an incremental decoder it counts over all calls since the value started, or since the last `setstate()`; in a text file, that is since
the last `seek()` to a position other than the start. The attributes of `UnicodeDecodeError` (`object`, `start`, `end`) are set as well.
There is no replacement character, and the one-shot `decode` gives no partial result. An incremental decoder raises at once, with one
exception: a byte `00` in a call without `final=True` is raised by the next call, and the call that received it gives out the text before
it. Python's text files need this (see [the bug notes](#text-file-bugs-in-python-itself)); no wrong text comes out either way.

Encoding fails only for a string that is not a value (see the text model below), with `UnicodeEncodeError`.

## Text Model

A Python `str` is a sequence of code points, and it can hold lone surrogates (for example from the error handlers `surrogateescape` and
`surrogatepass`). Lone surrogates are code points of their own in the Steno Encoding, so they are encoded, and decoding gives exactly the code
points back, lone surrogates included:

```pycon
>>> text = "a" + chr(0xD800) + "b"
>>> steno.decode(steno.encode(text)) == text
True

```

The one kind of `str` that is not a value is one with a high surrogate directly followed by a low surrogate: the specification says that
such a sequence is not a value, because in UTF-16 the pair is one supplementary code point. `encode` raises `UnicodeEncodeError` for it rather
than join the pair silently. If the pair came from UTF-16 data, join it first:

```pycon
>>> pair = chr(0xD83D) + chr(0xDE00)
>>> steno.encode(pair)
Traceback (most recent call last):
  ...
UnicodeEncodeError: 'x-steno' codec can't encode characters in position 0-1: a high surrogate directly followed by a low surrogate is not a value...
>>> joined = pair.encode("utf-16", "surrogatepass").decode("utf-16")
>>> len(joined), steno.decode(steno.encode(joined)) == joined
(1, True)

```

The encoding does not normalize the text and does not add or remove a byte order mark.

## Before You Store Data

- **Store the name of the encoding with the bytes.** Steno bytes carry no header or version marker, so nothing in them says how to read them.
  Record `x-steno` (`singulink_steno.ENCODING_NAME`) in the schema or next to the data.
- **Store each value by itself.** The bytes of a code point depend on the code points before it, so decoding must start at the first byte of a
  value, and two values cannot be joined by joining their bytes. To keep several values in one buffer, end each with a `00` byte (which never
  occurs in Steno bytes) or record its length. For the same reason a text file in `x-steno` is one value and cannot be appended to.
- **Expect code point order, not alphabetical order.** The bytes sort in the order of the characters' Unicode numbers: "Z" sorts before "a", and
  "Å" after "z". Use it for keys, ranges and prefixes, not for lists that people read in their language's order.

The [guide to storing values](https://www.singulink.com/Docs/Steno/articles/start/storing-values.html) has more.

## Thread Safety

The functions `encode`, `decode` and `is_ascii_text` are safe to call from many threads at once. The layouts that the codec builds are kept in
a cache of at most 1,024 entries that all threads share; reading it takes no lock, and adding to it takes a short one. When it is full, the
oldest entry is dropped, and layouts do not refer to each other, so memory stays bounded. The table of decoder states that only the process
remembers (see [Text Files](#writing-reading-tell-and-seek)) is shared too, holds at most 65,536 states and takes a short lock. An
`Encoder`, `Decoder`, `StreamReader`, `StreamWriter` or text file holds the state of one value and must not be used by several threads at
once.

## Conformance

This implementation passes all 7,640 vectors of the specification's test vector file (valid, invalid and not-a-value), and the large vector
file of 145,573 vectors. To run the tests:

```sh
python -m venv .venv
.venv/bin/pip install -e . pytest hypothesis
.venv/bin/pytest
```

The tests read the vector file from `../../TestVectors/steno-v1-vectors.txt`. If the environment variable `STENO_LARGE_VECTORS` names the large
vector file, the tests run it too. The other tests compare random and adversarial text with two plain reference codecs written
independently from the specification, check the order and prefix properties, streaming over random splits, mutated and random bytes, text
files (flush, `tell` and `seek` in every newline mode and chunk size, with the C and the `_pyio` text files, append, read and write), the
stream classes, copies, threads and memory. The examples of this README are run as tests. The tests take about 20 seconds; the fuzz tests
run for longer with `STENO_FUZZ_SECONDS=<seconds per test>`, and `pytest -m slow` runs deterministic sweeps of about a minute.

## Performance

This is pure Python: no compiled code, no dependencies. It is much slower than the compiled implementations; expect a few million characters
per second for most text, and more where the C code of the standard library can do the work:

- Values of ASCII text (tab, line feed, carriage return and U+0020..U+007E only) are converted by one `str.encode("ascii")` or `bytes.decode`
  and one `bytes.translate`.
- Runs of characters that take 1 byte (ASCII and the main window, for example a run of Cyrillic or Greek letters and spaces) are found by a
  regular expression and converted by the charmap functions of the `codecs` module, so a document in one alphabet goes at C speed.
- Long runs of CJK ideographs and Hangul syllables (2 bytes each) are converted with integer arithmetic on all of them at once; short runs,
  and every other character, take one step of Python each.
- Each layout is built once and kept in a bounded cache; an encoder or decoder also keeps its last four layouts for itself.

The worst case is text that moves to another of many windows at almost every character, because then almost every character needs a layout
that is not in the cache. Building one takes about 7 microseconds, so such text goes at about 100,000 characters per second (about 10
microseconds per character, against about 1 for text that moves between a few windows at every character). A crafted megabyte of it takes
seconds, not minutes. Ordinary text in any language uses a few layouts.

`singulink_steno.backend` names the code that does the work. It is `"python"`, the only backend so far. The package is built so that a
compiled backend can take over later without a change to the API.

<!-- benchmarks:start -->
The measured figures are in `BENCHMARKS.md` after the first benchmark run.
<!-- benchmarks:end -->

The benchmarks use pyperf, in `bench`, on the shared workloads of the repository (14 texts, each as a whole document and as 16-character
values). They compare the Steno Encoding with what a Python developer would use instead: UTF-8 and UTF-16 (`str.encode` and `bytes.decode`),
and Deflate (`zlib`), Zstandard (`zstandard`), Brotli (`brotli`) and LZ4 (`lz4`) on UTF-8. Python has no SCSU or BOCU-1 implementation in
common use. These libraries are in the `bench` dependency group only. Run the benchmarks on a quiet machine from this folder:

```sh
pip install -e . --group bench
python bench/run_all.py
```

It takes about 25 minutes, writes `bench/results.csv`, `bench/sizes.csv`, `bench/libraries.csv` and `BENCHMARKS.md`, and fills the figures
above. It keeps the results of each text in `bench/raw`, so a run that was stopped goes on where it stopped; delete `bench/raw` to start
afresh. `python bench/run_all.py --smoke` checks the setup in about 20 seconds.

## Text File Bugs in Python Itself

Two defects of CPython's `TextIOWrapper.tell()` affect every codec, UTF-8 included. Both need a `seek()` to a position that `tell()` gave in
the middle of a carriage return and line feed (universal newlines), and a short read after it. Measured with UTF-8 on Python 3.9 and 3.14,
in 44,872 such seeks:

- The C text file (the built-in `open()`) reads past the end of its buffer and decodes the bytes there; a strict codec raises (28 to 35
  times with UTF-8). The Steno decoder is written so that this case works: the first byte past the buffer is always `00`, and the decoder
  shows it as held bytes instead of raising at once, so `tell()` steps back.
- `_pyio.TextIOWrapper.tell()` fails an `assert` (585 times with UTF-8, and the same with `x-steno`). Nothing in a codec can change that.

## License

MIT, the license of the repository. See [LICENSE.md](LICENSE.md).
