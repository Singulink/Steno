# Go Reference Implementation of the Steno Encoding

The package `steno` in the Go module `github.com/Singulink/Singulink.Steno/Ports/go` is the Go reference implementation of the Steno Encoding.

The Steno Encoding is a compact encoding of Unicode text whose bytes sort like the text. It was designed to be the ideal per-value compression
encoding for databases and key-value stores, which can sort, compare, range-scan and prefix-match Steno bytes as plain bytes, without decoding
them. The bytes keep code point order and prefixes, each value has exactly one encoding, and the byte `00` never occurs. No byte below `08`
occurs inside a code point, so line feeds, tabs and spaces can be found at the byte level.

- [Specification](https://github.com/Singulink/Singulink.Steno/blob/main/FORMAT.md): the Steno Encoding, version 1.
- [Main README](https://github.com/Singulink/Singulink.Steno/blob/main/README.md): the sizes and the comparison with other encodings.
- [The Go implementation on the documentation site](https://www.singulink.com/Docs/Steno/articles/implementations/go.html).
- [Storing values](https://www.singulink.com/Docs/Steno/articles/start/storing-values.html): what to know before you store data.

The package encodes and decodes the Steno Encoding, version 1, and passes all test vectors of the specification. It uses only the standard
library, with no cgo, no assembly and no `unsafe`, and it builds with Go 1.22 and later.

## Installation

> Not published yet: the module becomes available with the tag `Ports/go/v0.1.0`.

```
go get github.com/Singulink/Singulink.Steno/Ports/go@v0.1.0
```

The last element of the import path is `go`, and the package name is `steno`, so import it with a name:

```go
import steno "github.com/Singulink/Singulink.Steno/Ports/go"
```

## Usage

Every example below is an `Example` function in `example_test.go`, so `go test` runs it and checks its output.

### Encode and decode a string

```go
b, err := steno.Encode("Москва")
if err != nil {
	panic(err)
}
s, err := steno.Decode(b)
if err != nil {
	panic(err)
}
fmt.Println(len(b), len("Москва")) // 7 12
fmt.Println(s)                     // Москва

// The bytes sort like the text, so a store can compare them without decoding.
zug, _ := steno.Encode("Zug")
zurich, _ := steno.Encode("Zürich")
fmt.Println(bytes.Compare(zug, zurich) < 0, "Zug" < "Zürich") // true true
```

Go compares strings byte by byte in UTF-8, which is code point order, and `bytes.Compare` compares bytes as unsigned bytes. So the order of
Steno bytes under `bytes.Compare` is the order of the strings under `<`, `strings.Compare` and `sort.Strings`. The package needs no comparison
functions of its own.

### Append to a buffer

```go
text := "Grüße"
buf := make([]byte, 0, steno.MaxEncodedLen(len(text)))
buf, err := steno.AppendEncode(buf, text)
fmt.Printf("% X %v\n", buf, err) // 2E 59 A3 86 4C <nil>
```

`AppendEncode`, `AppendDecode` and the other `Append` functions append to `dst` and return the extended slice, as `strconv.AppendInt` does.
They write no byte past the bytes they append, so data in the spare capacity of `dst` stays as it is. On an error they return `dst` with its
length, and its bytes up to that length, unchanged; the spare capacity beyond it may have been written. `dst` must not overlap the input:
reuse a buffer with `buf[:0]` only when it is not the input. The maximum-size helpers are true bounds, tested on worst-case inputs:

| Function | Input | Bound |
|---|---|---|
| `MaxEncodedLen(n)` | `n` bytes of UTF-8 | `2n` Steno bytes |
| `MaxEncodedLenRunes(n)` | `n` code points | `4n` Steno bytes |
| `MaxEncodedLenUTF16(n)` | `n` UTF-16 code units | `3n` Steno bytes |
| `MaxDecodedLen(n)` | `n` Steno bytes | `4n` bytes of UTF-8 |
| `MaxDecodedLenRunes(n)` | `n` Steno bytes | `n` code points |
| `MaxDecodedLenUTF16(n)` | `n` Steno bytes | `2n` UTF-16 code units |

They panic if `n` is negative or the result overflows an `int`.

### Runes and UTF-16: every value

```go
// A lone surrogate is a code point of its own. A Go string cannot hold it, but a []rune can.
b, err := steno.EncodeRunes([]rune{'a', 0xD800, 'b'})
fmt.Printf("% X %v\n", b, err) // 48 F8 C5 20 49 <nil>

r, err := steno.DecodeRunes(b)
fmt.Printf("%U %v\n", r, err) // [U+0061 U+D800 U+0062] <nil>

// A high surrogate code point directly followed by a low one is not a value.
_, err = steno.EncodeRunes([]rune{0xD83D, 0xDE00})
fmt.Println(err) // steno: cannot encode: not-a-value at offset 1

// Decoding a surrogate code point to a string is an error, never a replacement.
_, err = steno.Decode(b)
fmt.Println(err) // steno: surrogate code point at offset 1 has no UTF-8 form
```

```go
// UTF-16 from another system, with a surrogate pair (U+1F600) and an unpaired surrogate.
u := []uint16{0x0048, 0xD83D, 0xDE00, 0xDC00}
b := steno.EncodeUTF16(u)
back, err := steno.DecodeUTF16(b)
fmt.Printf("%X %v\n", back, err) // [48 D83D DE00 DC00] <nil>
```

### Errors

```go
_, err := steno.Decode([]byte{0x2F, 0x50, 0x00})

var de *steno.DecodeError
if errors.As(err, &de) {
	fmt.Println(de.Reason, de.Offset, de.Reason == steno.ZeroByte) // zero-byte 2 true
}
fmt.Println(err) // steno: invalid data: zero-byte at offset 2
```

### Streaming

```go
// The pieces may split a UTF-8 sequence: "é" is C3 A9.
var enc steno.Encoder
var b []byte
for _, piece := range []string{"caf\xc3", "\xa9 crème"} {
	var err error
	if b, err = enc.AppendString(b, piece); err != nil {
		panic(err)
	}
}
b, err := enc.Flush(b)

whole, _ := steno.Encode("café crème")
fmt.Println(bytes.Equal(b, whole), err) // true <nil>

// Reset before the next value.
enc.Reset()
```

```go
b, _ := steno.Encode("東京")

// Bytes that arrive one at a time.
var dec steno.Decoder
var text []byte
for i := range b {
	var err error
	if text, err = dec.Append(text, b[i:i+1]); err != nil {
		panic(err)
	}
}
// Flush reports bytes of an unfinished code point.
fmt.Println(string(text), dec.Flush()) // 東京 <nil>
```

```go
var buf bytes.Buffer
w := steno.NewWriter(&buf)
fmt.Fprintf(w, "%s, %s!", "Привет", "мир")
if err := w.Close(); err != nil {
	panic(err)
}

text, err := io.ReadAll(steno.NewReader(&buf))
fmt.Println(string(text), err) // Привет, мир! <nil>
```

### ASCII text

```go
a, _ := steno.Encode("Hello\tworld\n")
b, _ := steno.Encode("Grüße")
fmt.Println(steno.IsASCIIText(a), steno.IsASCIIText(b)) // true false
```

`IsASCIIText` decides from the bytes alone whether the value has only tab, line feed, carriage return and U+0020..U+007E. The Steno bytes of
such text are not ASCII bytes: U+0020..U+007E are `07`..`65`.

## Text Model

A value of the Steno Encoding is a sequence of code points U+0000..U+10FFFF. Surrogate code points (U+D800..U+DFFF) are allowed as code points
of their own, but a high surrogate code point directly followed by a low one is not a value. Go strings are UTF-8, which has no form for a
surrogate code point, so the package has three forms of text:

- **Strings** (`Encode`, `Decode`, `AppendEncode`, `AppendDecode`). Encoding needs valid UTF-8. Anything else, including the UTF-8 form of a
  surrogate (`ED A0 80`), gives an `*EncodeError` with the reason `InvalidUTF8`. Nothing is replaced with U+FFFD. Decoding a value that has a
  surrogate code point gives a `*DecodeError` with the reason `LoneSurrogate`.
- **Runes** (`EncodeRunes`, `DecodeRunes`). A rune in U+D800..U+DFFF is a surrogate code point. A high surrogate rune directly followed by a
  low one is rejected with the reason `NotAValue`: combine such a pair into one rune first. A rune that is negative or above U+10FFFF is
  rejected with the reason `InvalidRune`.
- **UTF-16 code units** (`EncodeUTF16`, `DecodeUTF16`), as `[]uint16`. A high surrogate followed by a low surrogate is one code point, and
  every other code unit is the code point with its value. So every sequence of code units is a value, `EncodeUTF16` never fails, and
  `DecodeUTF16` gives back exactly the code units that were encoded.

Runes and UTF-16 have full fidelity: all valid Steno bytes decode in these forms.

## Errors

Decoding fails with a `*DecodeError`, which has a `Reason` and an `Offset`. Get it with `errors.As`. The reasons are the rejection rules of the
specification (§10.1), with the names of the test vectors (`Reason.String`). When bytes break several rules, the reason is the first one that a
decoder meets in the order of §9.2:

| Reason | The bytes have |
|---|---|
| `ZeroByte` | a byte `00`, as a lead byte or a trail byte |
| `UnusedLead` | a lead byte that the layout of the current state does not use |
| `Truncated` | an end in the middle of the encoding of a code point |
| `PastRange` | a code point beyond the last code point of its range, which includes every value above U+10FFFF |
| `SurrogatePair` | a low surrogate code point directly after a high surrogate code point |
| `InvalidTrail` | a byte `01`..`07` where a trail byte is expected (trail bytes are `08`..`FF`) |
| `LoneSurrogate` | not a rule of the format: a surrogate code point, which the functions that decode to UTF-8 cannot write |

`Offset` is where the encoding of the code point that broke the rule starts. Decoding to UTF-8 stops at the first surrogate code point, so for
bytes that also break a rule after it, the reason is `LoneSurrogate`. Use `DecodeRunes` to see every reason.

Encoding a value never fails. An `*EncodeError` only reports input that is not a value or not valid UTF-8, with the `Reason` `InvalidUTF8`,
`InvalidRune` or `NotAValue`, and the `Offset` in the input.

## Streaming

An `Encoder` takes a value in pieces (`AppendString`, `Append` for UTF-8 bytes, `AppendRunes`, `AppendUTF16`). A `Decoder` takes the bytes in
pieces and writes UTF-8 (`Append`), runes (`AppendRunes`) or UTF-16 (`AppendUTF16`). Their zero values are ready to use.

- **Any split gives the same result.** Over any split of the input, also inside a UTF-8 sequence, a surrogate pair or the bytes of one code
  point, they give exactly the bytes or text of the one-shot functions, or the same error. What a piece ends in is held back until the next
  call.
- **Flush does not start a new value.** `Encoder.Flush` writes a held high surrogate as a code point of its own; with the start of a UTF-8
  sequence held, it fails with `InvalidUTF8`. `Decoder.Flush` fails with `Truncated` if it holds the bytes of an unfinished code point. After
  either, the next call goes on with the same value.
- **Reset starts a new value.**
- **An error changes nothing.** A call that fails leaves the encoder or decoder as it was, and returns `dst` with its length, and its bytes up
  to that length, unchanged (as the `Append` functions do). The offsets of a `Decoder`
  count from the first byte after the last `Reset`.

`NewWriter` and `NewReader` adapt them to `io.Writer` (UTF-8 text in, Steno bytes out) and `io.Reader` (Steno bytes in, UTF-8 text out). Each
handles one value. On invalid UTF-8, `Writer.Write` writes the text before the bad sequence, returns its length as `n` and the position of
the sequence as the error's offset, and can be used again; if the sequence began in an earlier `Write`, the bytes held from that call are
dropped. `Writer.Close` checks that the text ended at the end of a UTF-8 sequence, and does not close the underlying writer. A `Reader` treats
the end of the underlying reader as the end of the value, and returns the text before a bad code point before it returns the error. It gives
up with `io.ErrNoProgress` after 100 reads in a row that return no bytes and no error, as `bufio.Reader` does.

## Before You Store Data

- **Record that the bytes are Steno bytes.** The bytes have no header or version marker. Store the name `x-steno` (the constant `steno.Name`)
  next to the data or in its schema.
- **Do not join encoded values.** The encoding is stateful: the bytes of a code point depend on the code points before it. Decoding starts at
  the first byte of a value, and the bytes of two values one after the other decode to other text. To keep several values in one buffer, end
  each with a `00` byte (it never occurs in Steno bytes) or record its length.
- **The order is code point order.** It is the order of Go's string comparison, not the order of any language: "Z" sorts before "a", and
  "Å" after "z". Use a collation, such as `golang.org/x/text/collate`, to sort for people.

The guide [Storing values](https://www.singulink.com/Docs/Steno/articles/start/storing-values.html) explains each point with an
example.

## Thread Safety

All functions of the package are safe to call from many goroutines at once. An `Encoder`, `Decoder`, `Writer` or `Reader` must not be used by
several goroutines at once; give each goroutine its own.

## Conformance

This implementation passes all 7,640 vectors of the specification's test vector file (valid, invalid and not-a-value), in every form of the
API: strings, runes, UTF-16 and the streaming decoder, with the exact reason for every invalid vector. `EncodeRunes` rejects the not-a-value
vectors; `EncodeUTF16` cannot be given them. It also passes the 145,573 vectors of the large vector file.

Run the tests from this folder:

```
go test ./...
```

The default run takes about 3 seconds. It reads `../../TestVectors/steno-v1-vectors.txt`, checks the package against a plain codec written
from the specification (`reference_test.go`), also at every offset around the 8-byte words of the fast paths, and checks the properties of the
specification on seeded random text: round trip, byte order equals code point order, prefixes, no `00` byte, and that all Steno bytes that are
accepted are the encoding of their value. To run the large vector
file, set `STENO_LARGE_VECTORS` to its path. To fuzz:

```
go test -run '^$' -fuzz '^FuzzDecode$' -fuzztime 30s
go test -run '^$' -fuzz '^FuzzRoundTrip$' -fuzztime 30s
```

## Performance

This is portable scalar Go with word-at-a-time fast paths. Printable ASCII is encoded and decoded 8 bytes at a time in a 64-bit word (and 4
UTF-16 code units at a time when encoding UTF-16). Code points of the main window, and the ideographs and Hangul syllables of the Chinese,
Japanese and Korean contexts, are encoded and decoded in place in the loop, without a state update. A change of script takes the general path,
one code point at a time. Each layout is built once and kept in a cache that has a fixed number of slots (1,024, about 1.2 MB at most), so
its memory is bounded; goroutines share it without a lock. The slot of a layout depends on a random seed of the process, so the text cannot be
chosen to make layouts share a slot, and each encoder and decoder also keeps its layout before the current one, so text that alternates
between two scripts needs no lookup in the shared cache.

The worst case is text that reaches a new state at almost every code point, which no cache can hold: for example, every character from a
window chosen at random among 104 windows above U+1000. Each such code point builds a layout. A layout is built in two steps for this
reason: its ranges, which are enough to encode or decode a code point, and its lookup tables, which are built only when the layout is used
again. On the machine of the measurements below, such text takes about 250 to 300 ns per code point to encode or decode, against about 2 ns
for ordinary Russian text and 14 ns for text that hops among 3 windows.

The benchmarks use the shared workloads of the repository (`Benchmarks/Workloads`): 14 texts, each as a whole document and as 16-character
values, as strings and as UTF-16, next to the standard library's UTF-8 conversion and the compressors a Go developer would use (Deflate,
Brotli, Zstandard and LZ4, in the separate module in `compare/`, so that this module keeps no dependencies). `go run ./bench` runs them (about
22 minutes) and writes `bench/results.csv`, `bench/sizes.csv`, `bench/libraries.csv`, `BENCHMARKS.md` and the block below.

<!-- benchmark summary: generated by go run ./bench -->

The figures are in `BENCHMARKS.md` after the first benchmark run.

<!-- end of benchmark summary -->

## License

MIT, the license of the repository. See [LICENSE.md](https://github.com/Singulink/Singulink.Steno/blob/main/LICENSE.md).
