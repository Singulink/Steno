# .NET Reference Implementation of the Steno Encoding

The package `Singulink.Steno` is the .NET reference implementation of the Steno Encoding. Its `StenoEncoding` class is a
`System.Text.Encoding`, so it works with every .NET API that takes an encoding.

The Steno Encoding is a compact encoding of Unicode text whose bytes sort like the text. It was designed to be the ideal per-value compression
encoding for databases and key-value stores, which can sort, compare, range-scan and prefix-match Steno bytes as plain bytes, without decoding
them. The bytes keep code point order and prefixes, each value has exactly one encoding, and the byte `00` never occurs. No byte below `08`
occurs inside a code point, so line feeds, tabs and spaces can be found at the byte level.

- [Specification](https://github.com/Singulink/Steno/blob/main/FORMAT.md): the Steno Encoding, version 1.
- [Main README](https://github.com/Singulink/Steno/blob/main/README.md): the sizes and the comparison with other encodings.
- [The .NET implementation on the documentation site](https://www.singulink.com/Docs/Steno/articles/implementations/dotnet.html), with
  its [API reference](https://www.singulink.com/Docs/Steno/api/Singulink.Steno.html).
- [Storing values](https://www.singulink.com/Docs/Steno/articles/start/storing-values.html): what to know before you store data.

The library encodes and decodes the Steno Encoding, version 1, and passes all test vectors of the specification. It targets .NET 10, has no
dependencies, is AOT compatible, and uses vector instructions where the processor has them.

## Installation

```sh
dotnet add package Singulink.Steno
```

## Usage

Every example below was compiled and run against the library; the comments show what it printed.

### Encode and Decode

```cs
using Singulink.Steno;

var steno = StenoEncoding.Instance;

byte[] bytes = steno.GetBytes("Москва");
string text = steno.GetString(bytes);

Console.WriteLine(bytes.Length);   // 7 (UTF-8 takes 12)
Console.WriteLine(text);           // Москва

Span<byte> buffer = stackalloc byte[steno.GetMaxByteCount(6)];
int length = steno.GetBytes("Zürich", buffer);
Console.WriteLine(length);         // 6
```

Each call encodes or decodes one whole value. Every `Encoding` method works: arrays, strings, spans, counts and the `Try` forms.

### UTF-8 and ASCII Text

```cs
byte[] utf8 = "Grüße"u8.ToArray();

byte[] steno = StenoEncoding.GetBytesFromUtf8(utf8);    // UTF-8 to Steno, without a string
byte[] back = StenoEncoding.GetUtf8Bytes(steno);        // and back

Console.WriteLine($"{utf8.Length} -> {steno.Length}");                  // 7 -> 5
Console.WriteLine(back.AsSpan().SequenceEqual(utf8));                  // True
Console.WriteLine(StenoEncoding.IsAsciiText(steno));                    // False
Console.WriteLine(StenoEncoding.IsAsciiText(StenoEncoding.GetBytesFromUtf8("order-1234"u8)));   // True
```

`IsAsciiText` decides from the bytes alone, without decoding, whether a value has only tab, line feed, carriage return and U+0020..U+007E. The
Steno bytes of such a value are not ASCII bytes: U+0020..U+007E are the bytes `07`..`65`.

### Compare and Sort

```cs
var steno = StenoEncoding.Instance;

byte[] zug = steno.GetBytes("Zug");
byte[] zurich = steno.GetBytes("Zürich");

Console.WriteLine(zug.AsSpan().SequenceCompareTo(zurich) < 0);              // True: the bytes of "Zug" sort first
Console.WriteLine(StenoEncoding.CompareCodePoints("Zug", "Zürich") < 0);    // True: the same order for strings

string[] names = ["Zürich", "すごい😀", "Zug", "すごい！"];
Array.Sort(names, StenoEncoding.CodePointComparer);
Console.WriteLine(string.Join(", ", names));    // Zug, Zürich, すごい！, すごい😀
```

`SequenceCompareTo` compares bytes as unsigned bytes, which on Steno bytes is the code point order of the texts. For strings in memory, use
`CompareCodePoints` or `CodePointComparer`, not ordinal comparison: ordinal comparison compares UTF-16 code units, and where two strings first
differ in a supplementary character (such as an emoji) and a character in U+E000..U+FFFF, it puts the emoji first. With `StringComparer.Ordinal`,
the last two names above come out the other way round.

### Streams

```cs
var steno = StenoEncoding.Instance;

using (var writer = new StreamWriter(path, append: false, steno))
{
    writer.WriteLine("Первая строка");
    writer.Flush();                       // does not end the value: the file stays one value
    writer.WriteLine("Вторая строка");
}

using (var reader = new StreamReader(path, steno, detectEncodingFromByteOrderMarks: false))
{
    Console.WriteLine(reader.ReadLine());   // Первая строка
    Console.WriteLine(reader.ReadLine());   // Вторая строка
}
```

### Encoder and Decoder Objects

```cs
var steno = StenoEncoding.Instance;
byte[] whole = steno.GetBytes("Hi 😀");

var decoder = steno.GetDecoder();
var text = new StringBuilder();
char[] chars = new char[4];

foreach (byte b in whole)   // the bytes arrive one at a time
{
    int n = decoder.GetChars([b], chars, flush: false);
    text.Append(chars, 0, n);
}

decoder.GetChars([], chars, flush: true);   // throws if the bytes end inside a code point
Console.WriteLine(text);                     // Hi 😀

decoder.Reset();                             // the next value starts here
```

### Errors

```cs
try
{
    StenoEncoding.Instance.GetString([0x48, 0x00, 0x49]);
}
catch (DecoderFallbackException e)
{
    Console.WriteLine(e.Index);     // 1: the bad code point starts at byte 1
    Console.WriteLine(e.Message);   // The bytes are not valid Steno data (zero-byte): a byte 00, in the code point that starts at byte 1.
}
```

## Text Model

A .NET string is a sequence of UTF-16 code units. A high surrogate directly followed by a low surrogate is one supplementary code point, and
every other code unit, an unpaired surrogate included, is a code point of its own. So every string is a value, encoding never fails, and
decoding gives back exactly the code units that were encoded:

```cs
var steno = StenoEncoding.Instance;
string lone = "a\uD800b";                         // an unpaired surrogate

byte[] bytes = steno.GetBytes(lone);
Console.WriteLine(steno.GetString(bytes) == lone);   // True: it round-trips

try
{
    StenoEncoding.GetUtf8Bytes(bytes);              // UTF-8 has no form for it
}
catch (EncoderFallbackException e)
{
    Console.WriteLine(e.GetType().Name);           // EncoderFallbackException
}
```

UTF-8 has no form for an unpaired surrogate, so the UTF-8 methods throw for such a value. The encoding does not normalize the text, and it
neither writes nor removes a byte order mark: `GetPreamble()` is empty, and U+FEFF is an ordinary code point.

## Errors

- **Invalid Steno bytes** throw a `DecoderFallbackException`, whatever `DecoderFallback` is set to: after invalid data the state of the decoder is
  unknown, so nothing can replace the bad part and go on. The message names the rule of the specification that the bytes break (`zero-byte`,
  `unused-lead`, `truncated`, `past-range`, `surrogate-pair` or `invalid-trail`); it is meant for diagnosis, and its wording is not part of the
  contract. `Index` is where the bad code point starts, and `BytesUnknown` has its bytes up to the defect.
- **Encoding never uses the `EncoderFallback`**, since every string is a value.
- **Invalid UTF-8** given to `GetBytesFromUtf8` throws a `DecoderFallbackException`; a value with an unpaired surrogate given to
  `GetUtf8Bytes` throws an `EncoderFallbackException`.
- **A destination that is too short** throws an `ArgumentException`, as for every `Encoding`; the `Try` methods return `false` instead.

## Streaming

`GetEncoder()` and `GetDecoder()` give objects that take one value in pieces. Over any split of the value, they give the bytes or the text of the
one-shot methods for the whole value, also when a piece ends between the two halves of a surrogate pair or inside the bytes of a code point.

- **A flush does not end the value.** An encoder call with `flush: true` writes a held high surrogate as an unpaired surrogate, and the next
  call goes on with the same value. A decoder call with `flush: true` throws with the reason `truncated` if it holds the bytes of an unfinished
  code point, and does not end the value either. So `StreamWriter.Flush()` can be called at any point of a stream, except between the two
  halves of a surrogate pair.
- **`Reset()` starts a new value.**
- **A call that throws leaves the object as it was.**
- **`StreamReader`:** create it with `detectEncodingFromByteOrderMarks: false`. Steno bytes start with `EF BB BF`, the UTF-8 byte order mark,
  only for a text that starts with U+09B3 U+09CF (U+09B3 is not an assigned character), but no data should be able to switch the reader to
  another encoding.
- **`BinaryReader.ReadString`** decodes a string of up to 128 bytes as one whole value, but a longer one with its decoder in pieces, and the
  reader does not reset the decoder between strings. So a string longer than 128 bytes can come back as other text, or fail to decode, unless
  it is the first such string that the reader reads. Store such strings as byte arrays with their length instead: write the length and
  `GetBytes`, and read them back with `ReadBytes` and `GetString`.

## Maximum Lengths

| Method | Input | Bound |
|---|---|---|
| `GetMaxByteCount(n)` | `n` UTF-16 code units | `(n + 1) × 3` Steno bytes (the extra unit allows for a high surrogate held by an encoder) |
| `GetMaxCharCount(n)` | `n` Steno bytes | `2n` UTF-16 code units |
| `StenoEncoding.GetMaxByteCountFromUtf8(n)` | `n` UTF-8 bytes | `2n` Steno bytes |
| `StenoEncoding.GetMaxUtf8ByteCount(n)` | `n` Steno bytes | `4n` UTF-8 bytes |

They are true bounds for every input of the given length. `GetByteCount`, `GetCharCount`, `GetByteCountFromUtf8` and `GetUtf8ByteCount` give the
exact length instead, at the cost of a pass over the input.

## Before You Store Data

- **Record that the bytes are Steno bytes.** The bytes have no header or version marker. Store the name `x-steno` (`StenoEncoding.WebName`)
  next to the data or in its schema.
- **Store each value by itself.** The bytes of a code point depend on the code points before it, so decoding must start at the first byte of a
  value, and two values cannot be joined by joining their bytes. To keep several values in one buffer, end each with a `00` byte (which never
  occurs in Steno bytes) or record its length.
- **Expect code point order, not alphabetical order.** "Z" sorts before "a", and "Å" after "z". Use a collation, such as `CompareInfo`, to sort
  for people.

The guide [Storing values](https://www.singulink.com/Docs/Steno/articles/start/storing-values.html) explains each point with an example.

## Thread Safety

`StenoEncoding.Instance` and every static method are safe to call from many threads at once. An encoder or decoder object holds the state of one
value and must not be used by several threads at once.

## Building and Testing

The folder holds the whole .NET solution, `Singulink.Steno.slnx`, with five projects:

- `Singulink.Steno`: the library, which is the NuGet package.
- `Singulink.Steno.Tests`: its tests.
- `Singulink.Steno.Benchmarks`: the benchmark and report tool that measures the sizes and writes the tables and charts of the documentation.
- `Singulink.Steno.Benchmarks.Speed`: the BenchmarkDotNet benchmarks that the tool runs.
- `Singulink.Steno.Benchmarks.Tests`: tests of the report that the tool writes.

It needs the .NET 10 SDK (`global.json` pins 10.0.100 and rolls forward to later 10.0 releases). From this folder:

```sh
dotnet build
dotnet test
```

The tests read the specification's vector file from `TestVectors` at the root of the repository. They also compare the library with a plain
reference codec on random text, check the properties of the specification, streaming over random splits, buffers of exact size and too
small, and invalid data. The fuzz tests run for 1 second each; set `STENO_FUZZ_SECONDS` to run them longer. The CI also runs the tests with
the vector code turned off, to cover the paths that processors without it take:

```sh
DOTNET_EnableHWIntrinsic=0 dotnet test -c Release    # no vector instructions
DOTNET_EnableSSSE3=0 dotnet test -c Release          # vectors without the byte shuffle
```

## Benchmarks

The benchmark tool runs the BenchmarkDotNet benchmarks of the library on the shared workloads of every implementation, and turns the results
into the tables of the documentation. Run it in Release, on a quiet machine:

```sh
dotnet build -c Release
dotnet run -c Release --project Singulink.Steno.Benchmarks -- report --parts machine,speed,ascii,compare
```

The [README of the tool](https://github.com/Singulink/Steno/blob/main/Source/dotnet/Singulink.Steno.Benchmarks/README.md) has every command.
The figures are on the [documentation site](https://www.singulink.com/Docs/Steno/articles/implementations/dotnet.html#performance) after the
benchmark run.

## License

MIT, the license of the repository. See [LICENSE.md](https://github.com/Singulink/Steno/blob/main/LICENSE.md).
