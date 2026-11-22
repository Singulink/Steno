<div class="article">

# Getting Started with .NET

This page installs the .NET reference implementation, the .NET library that implements the Steno Encoding (Steno for short), and goes through the common ways to encode and decode with it: strings, spans, UTF-8 without strings, and streams.

### Installation

Install the `Singulink.Steno` package, the .NET reference implementation, from NuGet. It targets .NET 10.

```
dotnet add package Singulink.Steno
```

## Encoding and Decoding Text

<xref:Singulink.Steno.StenoEncoding.Instance?displayProperty=nameWithType> is the shared instance of the .NET reference implementation's encoding. It is thread safe, and every method encodes or decodes its input as one whole value.

```csharp
using Singulink.Steno;

var steno = StenoEncoding.Instance;

byte[] bytes = steno.GetBytes("Καλημέρα");
string text = steno.GetString(bytes);

Console.WriteLine(bytes.Length);    // 9 (UTF-8 takes 16)
Console.WriteLine(text);            // Καλημέρα
```

<xref:Singulink.Steno.StenoEncoding> is an <xref:System.Text.Encoding>, so the other members of an encoding work too, such as <xref:Singulink.Steno.StenoEncoding.GetByteCount*> and <xref:Singulink.Steno.StenoEncoding.GetChars*>. The other examples on this page start the same way, with `using Singulink.Steno;` and the `steno` variable.

> [!IMPORTANT]
> The Steno Encoding has no header and no version marker. Store the name of the format, `x-steno` (<xref:Singulink.Steno.StenoEncoding.WebName>), with the data or in its schema. See [Storing Steno Values](../start/storing-values.md#record-the-format-in-the-schema).

#### Spans

The span overloads encode into a buffer that you provide. <xref:Singulink.Steno.StenoEncoding.GetMaxByteCount*> and <xref:Singulink.Steno.StenoEncoding.GetMaxCharCount*> give a size that is always enough:

```csharp
ReadOnlySpan<char> name = "Zürich";

Span<byte> bytes = stackalloc byte[steno.GetMaxByteCount(name.Length)];   // stackalloc only for short input; rent an array for long text
int byteCount = steno.GetBytes(name, bytes);

Span<char> chars = stackalloc char[steno.GetMaxCharCount(byteCount)];
int charCount = steno.GetChars(bytes[..byteCount], chars);

Console.WriteLine(byteCount);                       // 6
Console.WriteLine(chars[..charCount].ToString());   // Zürich
```

<xref:Singulink.Steno.StenoEncoding.GetByteCount*> gives the exact size, but it has to encode the text to find it. A buffer of the maximum size is usually cheaper. <xref:Singulink.Steno.StenoEncoding.TryGetBytes*> and <xref:Singulink.Steno.StenoEncoding.TryGetChars*> return `false` when the buffer is too short.

## UTF-8 In and Out

<xref:Singulink.Steno.StenoEncoding.GetBytesFromUtf8*> and <xref:Singulink.Steno.StenoEncoding.GetUtf8Bytes*> convert between UTF-8 and Steno without making a string:

```csharp
ReadOnlySpan<byte> utf8 = "Grüße aus Köln"u8;

byte[] stenoBytes = StenoEncoding.GetBytesFromUtf8(utf8);
byte[] utf8Again = StenoEncoding.GetUtf8Bytes(stenoBytes);

Console.WriteLine($"{utf8.Length} -> {stenoBytes.Length}");   // 17 -> 14
Console.WriteLine(utf8Again.AsSpan().SequenceEqual(utf8));    // True
```

Both have span forms that write into a buffer, with <xref:Singulink.Steno.StenoEncoding.GetMaxByteCountFromUtf8*> and <xref:Singulink.Steno.StenoEncoding.GetMaxUtf8ByteCount*> for its size, and `Try` forms. Invalid UTF-8 throws a <xref:System.Text.DecoderFallbackException>. A UTF-8 byte order mark at the start is the character U+FEFF of the value, and is kept. ASCII text takes a fast path: see [ASCII Fast Paths](../benchmarks/ascii.md).

> [!NOTE]
> Steno can hold an unpaired surrogate, as a .NET string can, but UTF-8 cannot. Converting such a value to UTF-8 throws an <xref:System.Text.EncoderFallbackException>.

## Streams

<xref:System.IO.StreamWriter> and <xref:System.IO.StreamReader> take the encoding like any other. Everything written to one stream is one value:

```csharp
var stream = new MemoryStream();

using (var writer = new StreamWriter(stream, steno, leaveOpen: true))
{
    writer.WriteLine("Первая строка");
    writer.WriteLine("Вторая строка");
}

stream.Position = 0;

using var reader = new StreamReader(stream, steno, detectEncodingFromByteOrderMarks: false);

Console.WriteLine(reader.ReadLine());   // Первая строка
Console.WriteLine(reader.ReadLine());   // Вторая строка
```

> [!TIP]
> Create every <xref:System.IO.StreamReader> with `detectEncodingFromByteOrderMarks: false`, so that no data can switch the reader to another encoding. See [Readers, Writers and Encoder Objects](dotnet.md#readers-writers-and-encoder-objects), which also covers <xref:System.IO.BinaryReader>.

Since the stream is one value, it must be read from its first byte. To keep many values that are read on their own, such as the rows of a table, encode each one by itself. To encode or decode one value in pieces without a stream, see [Encoding in Pieces](dotnet-encoding-in-pieces.md).

## Next Steps

- [Storing Steno Values](../start/storing-values.md) - How to integrate the encoding into a store.
- [Working with Encoded Values](../start/encoded-values.md) - Sorting, prefix and range queries, composite keys and hashing on the bytes.
- [Encoding in Pieces](dotnet-encoding-in-pieces.md) - The encoder and the decoder, for one value given in pieces.
- [API Documentation](../../api/Singulink.Steno.yml) - Every member of the .NET reference implementation, with its exact guarantees.

</div>
