<div class="article">

# Encoding in Pieces with .NET

The encoder and the decoder of the .NET reference implementation take one value in pieces, for code that receives text or bytes a part at a time. Most code does not need them: <xref:Singulink.Steno.StenoEncoding.GetBytes*> and <xref:Singulink.Steno.StenoEncoding.GetString*> take a whole value, and <xref:System.IO.StreamWriter> and <xref:System.IO.StreamReader> use the encoder and the decoder for you.

Each example starts with `using Singulink.Steno;` and `var steno = StenoEncoding.Instance;`.

## Encoder

<xref:Singulink.Steno.StenoEncoding.GetEncoder*> returns an encoder for one value. However the text is split, the encoder writes the same bytes as <xref:Singulink.Steno.StenoEncoding.GetBytes*> on the whole text:

```csharp
using System.Buffers;

var encoder = steno.GetEncoder();
var output = new ArrayBufferWriter<byte>();

foreach (string piece in new[] { "Добрый ", "день, ", "мир" })
{
    Span<byte> span = output.GetSpan(steno.GetMaxByteCount(piece.Length));
    output.Advance(encoder.GetBytes(piece, span, flush: false));
}

// At the end of the value, flush writes a high surrogate that the encoder may still hold:
Span<byte> last = output.GetSpan(steno.GetMaxByteCount(0));
output.Advance(encoder.GetBytes(ReadOnlySpan<char>.Empty, last, flush: true));
encoder.Reset();   // the next call starts a new value

byte[] whole = steno.GetBytes("Добрый день, мир");
Console.WriteLine(output.WrittenSpan.SequenceEqual(whole));   // True
```

A high surrogate at the end of a piece is held until the next call, which may start with its low surrogate.

## Decoder

<xref:Singulink.Steno.StenoEncoding.GetDecoder*> does the same for decoding. A piece may end inside the bytes of a character; the decoder holds those bytes until the next call:

```csharp
using System.Text;

byte[] bytes = steno.GetBytes("Добрый день, мир");

var decoder = steno.GetDecoder();
var text = new StringBuilder();
char[] chars = new char[steno.GetMaxCharCount(5)];

foreach (byte[] piece in bytes.Chunk(5))   // pieces of 5 bytes, which may end inside a character
{
    int charCount = decoder.GetChars(piece, chars, flush: false);
    text.Append(chars, 0, charCount);
}

// At the end of the input, flush throws if the bytes stop inside a character.
text.Append(chars, 0, decoder.GetChars(ReadOnlySpan<byte>.Empty, chars, flush: true));

Console.WriteLine(text);   // Добрый день, мир
```

## Starting the Next Value

> [!WARNING]
> A flush does not end the value. An encoder or a decoder that is used again after `flush: true` goes on with the same value. Call <xref:System.Text.Encoder.Reset*?displayProperty=nameWithType> or <xref:System.Text.Decoder.Reset*?displayProperty=nameWithType> before the next value.

The encoder and the decoder are not thread safe. A call that throws leaves them as they were. [Flush and Reset](dotnet.md#flush-and-reset) has the exact rules, with an example.

## Next Steps

- [Storing Steno Values](../start/storing-values.md) - How to integrate the encoding into a store.
- [Working with Encoded Values](../start/encoded-values.md) - Sorting, prefix and range queries, composite keys and hashing on the bytes.

</div>
