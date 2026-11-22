<div class="article">

# .NET

The .NET reference implementation of the Steno Encoding is the `Singulink.Steno` package. It targets .NET 10, and its <xref:Singulink.Steno.StenoEncoding> class is a <xref:System.Text.Encoding>, so it works with every .NET API that takes an encoding. Its encoder, decoder and ASCII paths use vector instructions.

### Installation

```
dotnet add package Singulink.Steno
```

## First Example

```csharp
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

[Getting Started with .NET](dotnet-getting-started.md) goes on with spans, UTF-8 and streams, and [Encoding in Pieces with .NET](dotnet-encoding-in-pieces.md) covers the encoder and decoder objects.

## What Is Particular to .NET

| Feature | What it gives |
|---|---|
| <xref:Singulink.Steno.StenoEncoding> derives from <xref:System.Text.Encoding> | Works with <xref:System.IO.StreamWriter>, <xref:System.IO.StreamReader>, <xref:System.IO.BinaryWriter> and every other API that takes an encoding |
| <xref:Singulink.Steno.StenoEncoding.CompareCodePoints*> and <xref:Singulink.Steno.StenoEncoding.CodePointComparer> | The order of Steno bytes for strings in memory. .NET's ordinal comparison compares UTF-16 code units, which is another order where an emoji meets U+E000..U+FFFF |
| <xref:Singulink.Steno.StenoEncoding.GetBytesFromUtf8*> and <xref:Singulink.Steno.StenoEncoding.GetUtf8Bytes*> | Conversion between UTF-8 and Steno bytes without making a string |
| <xref:Singulink.Steno.StenoEncoding.IsAsciiText*> | Whether a value is ASCII text, from its bytes alone |

<xref:Singulink.Steno.StenoEncoding.WebName> is `x-steno`, the name to record next to the data. Invalid bytes throw a <xref:System.Text.DecoderFallbackException>, whatever <xref:System.Text.Encoding.DecoderFallback> is set to; its message names the rule of the specification that the bytes break, and <xref:System.Text.DecoderFallbackException.Index?displayProperty=nameWithType> says where the bad code point starts.

## Readers, Writers and Encoder Objects

Three settings of .NET's own classes need care with any stateful encoding. Each is easy to get right.

#### StreamReader: Turn Off Byte Order Mark Detection

A <xref:System.IO.StreamReader> looks for a byte order mark by default and, when it finds one, switches to that encoding. Steno bytes never start with the byte order mark of UTF-16 or UTF-32, and they start with `EF BB BF`, the UTF-8 byte order mark, only for a text that starts with U+09B3 U+09CF, where U+09B3 is not an assigned character. Still, create every reader with `detectEncodingFromByteOrderMarks: false`, so that no data can switch it to another encoding:

```csharp
using var reader = new StreamReader(stream, steno, detectEncodingFromByteOrderMarks: false);
```

#### BinaryReader: Store Long Strings as Byte Arrays

<xref:System.IO.BinaryReader.ReadString*?displayProperty=nameWithType> decodes a string of up to 128 bytes as one whole value, so such strings are safe. A longer one goes through the reader's decoder in pieces, and the reader does not reset the decoder between strings, so a later long string can come back as other text, or fail to decode:

```csharp
string text = string.Concat(Enumerable.Repeat("Ελλάδα ", 30));   // 211 Steno bytes

var stream = new MemoryStream();

using (var writer = new BinaryWriter(stream, steno, leaveOpen: true))
{
    writer.Write(text);
    writer.Write(text);
}

stream.Position = 0;
using var reader = new BinaryReader(stream, steno);

string first = reader.ReadString();
string second = reader.ReadString();

Console.WriteLine(first == text);    // True
Console.WriteLine(second == text);   // False
Console.WriteLine(second[..12]);     // Ͽ΃λλάδα Ελλά
```

Store such strings as byte arrays with their length: write the length and <xref:Singulink.Steno.StenoEncoding.GetBytes*>, and read them back with <xref:System.IO.BinaryReader.ReadBytes*> and <xref:Singulink.Steno.StenoEncoding.GetString*>.

#### Flush and Reset

The encoder from <xref:Singulink.Steno.StenoEncoding.GetEncoder*> and the decoder from <xref:Singulink.Steno.StenoEncoding.GetDecoder*> take one value in pieces. A flush does not end that value; <xref:System.Text.Encoder.Reset*?displayProperty=nameWithType> and <xref:System.Text.Decoder.Reset*?displayProperty=nameWithType> start a new one:

```csharp
var encoder = steno.GetEncoder();
byte[] buffer = new byte[16];

int count = encoder.GetBytes("Ж", buffer, flush: true);
Console.WriteLine(Convert.ToHexString(buffer, 0, count));   // E9EE

count = encoder.GetBytes("Ж", buffer, flush: true);
Console.WriteLine(Convert.ToHexString(buffer, 0, count));   // 80: the same value goes on

encoder.Reset();
count = encoder.GetBytes("Ж", buffer, flush: true);
Console.WriteLine(Convert.ToHexString(buffer, 0, count));   // E9EE: a new value
```

| | Encoder | Decoder |
|---|---|---|
| **`flush: true`** | Writes a high surrogate that it holds from the call before as an unpaired surrogate. The next call goes on with the same value. | Says that the input ends here: throws a <xref:System.Text.DecoderFallbackException> with the reason `truncated` if it holds the bytes of an unfinished character. The next call goes on with the same value. |
| **`Reset`** | Starts a new value. | Starts a new value. |
| **A call that throws** | Leaves the encoder as it was. | Leaves the decoder as it was. |
| **Threads** | Not thread safe. | Not thread safe. |

<xref:System.IO.StreamWriter.Flush*?displayProperty=nameWithType> can therefore be called anywhere in a stream, except between the two halves of a surrogate pair: there it writes the high half on its own, and the next write throws an <xref:System.Text.EncoderFallbackException> when it reaches the low half.

## Performance

<!-- The figures are generated by the benchmark tool into Docs/benchmarks/impl-dotnet.md; regenerate them there, never edit them here. -->

[!include[](../../benchmarks/impl-dotnet.md)]

It encodes and decodes runs of 1-byte characters, and checks and converts ASCII text, with vector instructions; [ASCII Fast Paths](../benchmarks/ascii.md) has those figures. [Implementations Side by Side](../benchmarks/implementations.md) puts the implementations together.

## Next Steps

- [Getting Started with .NET](dotnet-getting-started.md) - Strings, spans, UTF-8 and streams.
- [Working with Encoded Values](../start/encoded-values.md) - Sorting, prefix and range queries, composite keys and hashing on the bytes.

## More

The [README of the .NET reference implementation](https://github.com/Singulink/Steno/blob/main/Source/dotnet/README.md) has every example, the text model, the errors, streaming, thread safety, and how to build, test and benchmark it.

## API Reference

The API reference of the .NET reference implementation is generated with this site: the [`Singulink.Steno` namespace](../../api/Singulink.Steno.yml), with every member and its exact guarantees. [API References](../../api/index.md) lists the API reference of every implementation.

</div>
