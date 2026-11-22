# ASCII Fast Paths

**On ASCII text, the .NET reference implementation converts Steno bytes to UTF-8 2.3 times as fast as it decodes them to UTF-16, on a document.**

Text that has only printable ASCII, tab, line feed and carriage return takes 1 byte per character in Steno, the code point minus 0x19, so the .NET reference implementation checks it and converts it to and from UTF-8 in a single vectorized pass. The text is the English UDHR with every character that is not printable ASCII, tab or line feed taken out. Each operation is given as a share of the speed of a plain copy of the same bytes, rounded to what the error margins support ([how](../articles/benchmarks/machine.md#speed)); the copy's own speed, in millions of bytes (which here are characters) per second, gives the scale.

| Operation | Document, against the copy | 16-char values, against the copy |
|---|--:|--:|
| Copy of the bytes (Span.CopyTo) | 230,000 MB/s | 4,000 MB/s |
| StenoEncoding.IsAsciiText | 9.8% | 82% |
| Steno to UTF-8 (GetUtf8Bytes) | 6.5% | 50% |
| UTF-8 to Steno (GetBytesFromUtf8) | 6.5% | 49% |
| Steno to UTF-16 (GetChars), for comparison | 2.8% | 15% |
