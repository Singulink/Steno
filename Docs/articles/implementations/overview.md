<div class="article">

# Implementations

The Steno Encoding has seven reference implementations. They encode the same bytes, pass every test vector of the [specification](../reference/specification.md), and each is written for the way its language handles text. Pick yours:

| Implementation | Package | What it is |
|---|---|---|
| [C](c.md) | `steno.h` and `steno.c` | C99 in two files, nothing but the standard library, no allocation; the same C interface as the Rust implementation's C library |
| [.NET](dotnet.md) | `Singulink.Steno` | A `System.Text.Encoding`, for every .NET API that takes an encoding |
| [Go](go.md) | `github.com/Singulink/Steno/Source/go` | Strings, runes and UTF-16, with `io.Writer` and `io.Reader` adapters; standard library only |
| [Java](java.md) | `com.singulink:steno` | A `java.nio.charset.Charset` named `x-steno`, and one-shot methods |
| [Python](python.md) | `singulink-steno` | A codec named `x-steno` for `str.encode` and `bytes.decode`, and its own `open()` for text files; pure Python |
| [Rust](rust.md) | `singulink-steno` | `str`, UTF-16 and code points, `no_std` support, and a C library over it |
| [TypeScript](typescript.md) | `@singulink/steno` | One ES module for Node.js, Deno, Bun and browsers, with stream classes |

Each page shows how to add the implementation to a project, a first example, what is particular to its language, and its performance. [API References](../../api/index.md) lists where the API reference of each one is, and [Working with Encoded Values](../start/encoded-values.md) shows how each one compares and searches the bytes.

The Steno Encoding is open: anyone may implement it, from the specification and its test vectors. An implementation that passes every vector writes the same bytes as these.

</div>
