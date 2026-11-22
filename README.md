# Singulink Steno Encoding

[![Chat on Discord](https://img.shields.io/discord/906246067773923490)](https://discord.gg/EkQhJFsBu6)
[![Build and Test](https://github.com/Singulink/Singulink.Steno/workflows/build%20and%20test/badge.svg)](https://github.com/Singulink/Singulink.Steno/actions?query=workflow%3A%22build+and+test%22)
[![View documentation](https://img.shields.io/badge/View-Documentation-blue)](https://www.singulink.com/Docs/Steno/)

The **Steno Encoding** is a compact encoding of Unicode text whose bytes sort like the text. It was designed to be the ideal per-value
compression encoding for databases and key-value stores, which can sort, compare, range-scan and prefix-match Steno bytes as plain bytes,
without decoding them.

Steno is designed to be the better choice wherever you would otherwise reach for SCSU or BOCU-1, the two Unicode compression schemes. It is
smaller than both over every group of values measured, and it is the only one of the three with all five properties that a store needs: bytes
that sort like the text, prefixes that stay prefixes, one encoding per text, no 00 byte, and line feeds, tabs and spaces that can be found in
the bytes without decoding.

This repository holds the specification, its test vectors and seven reference implementations: .NET, Rust, C, Go, Java, TypeScript and
Python. See [Implementations](#implementations).

## Why Use It

**Stored text gets smaller.** Alphabets such as Greek, Cyrillic, Hebrew, Arabic, Hindi and Thai take about 1 byte per character, and
Chinese, Japanese and Korean about 2. Over the UDHR in 47 languages, cut into 16-character values, that is 1.11 bytes per character against
1.92 for UTF-8. On short values, of up to 16 characters, the Steno Encoding is smaller than every other encoding and compressor measured: UTF-8,
UTF-16, SCSU, BOCU-1, and UTF-8 compressed with four general compressors at two levels each.

**The store works on the bytes.** Steno bytes sort like the text, in code point order (the order of the characters' Unicode numbers); they
keep prefixes, have one encoding per text and never contain the byte 00. So a store compares, sorts, range-scans, prefix-matches and hashes
them as they are. Formats without these properties, such as SCSU or compressed text, must be decoded first, and in the .NET measurements
decoding first takes many times as long as comparing the bytes.

**Lines and fields stay visible.** Line feeds, tabs and spaces are bytes of their own (03, 02 and 07), never part of another character. So
encoded text splits into lines and fields without decoding, and Steno values fit into line- and tab-delimited data.

**It drops into existing stores.** A store that sorts keys as unsigned bytes, as many ordered key-value stores do by default, keeps Steno keys
in the order of their text with no comparator plug-in and no change inside the store. The encoder sits at the edges, where text enters and
leaves.

**It is open.** Anyone may implement the Steno Encoding, in any language and for any purpose, without permission or payment; Singulink holds
no patents on it (see the [specification](https://github.com/Singulink/Singulink.Steno/blob/main/FORMAT.md#16-intellectual-property)). The
format is frozen: bytes written by any correct implementation decode to the same text with any other.

```cs
using Singulink.Steno;

var steno = StenoEncoding.Instance;

byte[] bytes = steno.GetBytes("Москва");
string text = steno.GetString(bytes);

Console.WriteLine(bytes.Length);    // 7 (UTF-8 takes 12)
Console.WriteLine(text);            // Москва

// The bytes sort like the text, so a store can compare them without decoding:
byte[] zug = steno.GetBytes("Zug");
byte[] zurich = steno.GetBytes("Zürich");

Console.WriteLine(zug.AsSpan().SequenceCompareTo(zurich) < 0);            // True: the bytes of "Zug" sort first
Console.WriteLine(StenoEncoding.CompareCodePoints("Zug", "Zürich") < 0);  // True: the same order for strings in memory
```

This is the .NET reference implementation. The README of each other implementation has the same examples in its language.

## Implementations

Seven reference implementations pass every test vector of the specification. Each has a page on the documentation site, and its folder has its
README, with the full API, the tests and the benchmarks.

### .NET

`dotnet add package Singulink.Steno` [![NuGet](https://img.shields.io/nuget/v/Singulink.Steno.svg)](https://www.nuget.org/packages/Singulink.Steno/)
· .NET 10, vectorized · [Page](https://www.singulink.com/Docs/Steno/articles/implementations/dotnet.html) · [Source](https://github.com/Singulink/Singulink.Steno/tree/main/Source/Singulink.Steno)

The example above is .NET. `StenoEncoding` is a `System.Text.Encoding`, so streams, readers and writers take it too.

### Rust

`singulink-steno = "0.1"` (not published yet) · no dependencies, `no_std` support, vector code on x86-64 and ARM64 ·
[Page](https://www.singulink.com/Docs/Steno/articles/implementations/rust.html) · [Folder](https://github.com/Singulink/Singulink.Steno/tree/main/Ports/rust)

```rust
let bytes = singulink_steno::encode("Москва");    // Vec<u8>: 7 bytes, where UTF-8 takes 12
let text = singulink_steno::decode(&bytes)?;      // String: "Москва"
assert!(singulink_steno::encode("Zug") < singulink_steno::encode("Zürich"));   // byte slices sort like the text
```

The crate `singulink-steno-capi` builds a C library over it. Its functions are the one-shot functions of the C reference implementation below,
with the same names, so a C program can link either library.

### C

`steno.h` and `steno.c` (not published yet) · C99, nothing but the standard library, no allocation, with SSE2 and NEON code ·
[Page](https://www.singulink.com/Docs/Steno/articles/implementations/c.html) · [Folder](https://github.com/Singulink/Singulink.Steno/tree/main/Ports/c)

```c
uint8_t bytes[64];
char text[64];
size_t n, m;

steno_encode_utf8((const uint8_t *)"Москва", 12, bytes, sizeof bytes, &n);   /* 7 bytes, where UTF-8 takes 12 */
steno_decode_utf8(bytes, n, (uint8_t *)text, sizeof text, &m);             /* "Москва", 12 bytes */
```

### Go

`go get github.com/Singulink/Singulink.Steno/Ports/go@v0.1.0` (not published yet) · standard library only, with word-at-a-time fast
paths · [Page](https://www.singulink.com/Docs/Steno/articles/implementations/go.html) · [Folder](https://github.com/Singulink/Singulink.Steno/tree/main/Ports/go)

```go
b, err := steno.Encode("Москва")   // 7 bytes, where UTF-8 takes 12
s, err := steno.Decode(b)          // "Москва"
// bytes.Compare on Steno bytes gives the order of the strings under <
```

### Java

`com.singulink:steno:0.1.0` (not published yet) · Java 17 or later, no dependencies, with word-at-a-time fast paths ·
[Page](https://www.singulink.com/Docs/Steno/articles/implementations/java.html) · [Folder](https://github.com/Singulink/Singulink.Steno/tree/main/Ports/java)

```java
Charset steno = Charset.forName("x-steno");    // a java.nio Charset, for every API that takes one
byte[] bytes = "Москва".getBytes(steno);       // 7 bytes, where UTF-8 takes 12
String text = Steno.decode(bytes);             // "Москва"; throws StenoDecodeException for invalid bytes
```

### TypeScript

`npm install @singulink/steno` (not published yet) · one ES module for Node.js, Deno, Bun and browsers, with fast paths ·
[Page](https://www.singulink.com/Docs/Steno/articles/implementations/typescript.html) · [Folder](https://github.com/Singulink/Singulink.Steno/tree/main/Ports/typescript)

```js
import { compareBytes, decode, encode } from '@singulink/steno';

const bytes = encode('Москва');                         // Uint8Array: 7 bytes, where UTF-8 takes 12
decode(bytes);                                          // 'Москва'
compareBytes(encode('Zug'), encode('Zürich'));          // -1: the bytes sort like the text
```

### Python

`pip install singulink-steno` (not published yet) · pure Python 3.9 or later, no dependencies · [Page](https://www.singulink.com/Docs/Steno/articles/implementations/python.html) ·
[Folder](https://github.com/Singulink/Singulink.Steno/tree/main/Ports/python)

```python
import singulink_steno                   # registers the codec x-steno
data = "Москва".encode("x-steno")        # 7 bytes, where UTF-8 takes 12
data.decode("x-steno")                   # 'Москва'; singulink_steno.open() for text files
```

## How It Compares for Stored Values

| | UTF-8 | UTF-16 | SCSU | BOCU-1 | **Steno** |
|---|---|---|---|---|---|
| Bytes sort like the text | Yes | Not with emoji and other supplementary characters | No | Yes | **Yes** |
| Prefix of the text, prefix of the bytes | Yes | Yes | No | Yes | **Yes** |
| One encoding per text | Yes | Yes | No | Yes | **Yes** |
| No 00 byte | Not with U+0000 | No | No | Not with U+0000 | **Yes** |
| Line feed, tab and space bytes never part of another character | Yes | No | No | Yes | **Yes** |

The Steno Encoding is the only one of these with all five. Against UTF-8, the encoding everyone knows, on 16-character values of the UDHR
in 47 languages, in bytes per character:

| Script | Steno | UTF-8 | UTF-8 is larger by |
|---|--:|--:|--:|
| Latin | 1.01 | 1.06 | 4% |
| Cyrillic | 1.09 | 1.85 | 70% |
| Greek | 1.07 | 1.84 | 73% |
| Hebrew | 1.07 | 1.82 | 70% |
| Arabic script | 1.21 | 1.82 | 50% |
| Indic scripts | 1.07 | 2.73 | 155% |
| Thai and Lao | 1.07 | 2.92 | 174% |
| Chinese | 2.01 | 2.96 | 47% |
| Japanese | 1.58 | 2.97 | 88% |
| Korean | 1.82 | 2.48 | 37% |

Over each group of values measured, UTF-16 is 58% to 92% larger than Steno, SCSU 0.6% to 7% larger and BOCU-1 1% to 12% larger.

![Encoded size against UTF-8, by language (UDHR, 16-character values)](https://raw.githubusercontent.com/Singulink/Singulink.Steno/main/Docs/benchmarks/charts/size-vs-utf8-udhr-16char.svg)

<!-- speed:start: after each benchmark run, replace this paragraph with the one in Docs/benchmarks/headline.md. -->
Encoding and decoding take tens of nanoseconds per short value in the .NET reference implementation. UTF-8 transcodes short values
faster; a store that works on the bytes pays the transcoding only when text enters or leaves it
([Cost per Value and Speed](https://www.singulink.com/Docs/Steno/articles/benchmarks/speed.html)).
<!-- speed:end -->

**Using UTF-8, UTF-16, SCSU, BOCU-1 or a compressor today?** Each has a page on why to choose Steno over it, and when to stay with it:
[UTF-8](https://www.singulink.com/Docs/Steno/articles/comparisons/utf-8.html) ·
[UTF-16](https://www.singulink.com/Docs/Steno/articles/comparisons/utf-16.html) ·
[SCSU](https://www.singulink.com/Docs/Steno/articles/comparisons/scsu.html) ·
[BOCU-1](https://www.singulink.com/Docs/Steno/articles/comparisons/bocu-1.html) ·
[general compressors](https://www.singulink.com/Docs/Steno/articles/comparisons/compressors.html) ·
[all trade-offs at a glance](https://www.singulink.com/Docs/Steno/articles/comparisons/trade-offs.html).

To put Steno values into a database or key-value store, see
[Storing Steno Values](https://www.singulink.com/Docs/Steno/articles/start/storing-values.html).

## Conformance

Conformance is defined in §10 of the [specification](https://github.com/Singulink/Singulink.Steno/blob/main/FORMAT.md), and the
[test vector file](https://github.com/Singulink/Singulink.Steno/blob/main/TestVectors/steno-v1-vectors.txt) is the test for it. All seven
implementations above pass every vector in it. If you write another implementation, open an issue to have it listed here.

## Further Reading

Here are some links to get you started:

- [Why the Steno Encoding](https://www.singulink.com/Docs/Steno/articles/start/introduction.html) - The case for stored values,
  with the numbers, and the right tool for each job.
- [Trade-offs at a Glance](https://www.singulink.com/Docs/Steno/articles/comparisons/trade-offs.html) - The whole record
  against each alternative, and why the design accepts each exception.
- [Benchmarks](https://www.singulink.com/Docs/Steno/articles/benchmarks/overview.html) - Every measurement and how it was made.
- [Specification](https://github.com/Singulink/Singulink.Steno/blob/main/FORMAT.md) - The Steno Encoding, version 1.
- [API References](https://www.singulink.com/Docs/Steno/api/index.html) - Where the API reference of each implementation
  is.
- [Chat on Discord](https://discord.gg/EkQhJFsBu6) - Have questions or want to discuss the Steno Encoding? This is the place for all
  Singulink project discussions.

### About Singulink

We are a small team of engineers and designers dedicated to building beautiful, functional and well-engineered software solutions. We offer very competitive rates as well as fixed-price contracts and welcome inquiries to discuss any custom development / project support needs you may have.

This package is part of our **Singulink Libraries** collection. Visit https://github.com/Singulink to see our full list of publicly available libraries and other open-source projects.
