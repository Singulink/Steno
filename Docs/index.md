<div class="article">

# Singulink Steno Encoding

The **Steno Encoding** is a compact encoding of Unicode text whose bytes sort like the text. It was designed to be the ideal per-value compression encoding for databases and key-value stores, which can sort, compare, range-scan and prefix-match Steno bytes as plain bytes, without decoding them.

Steno is designed to be the better choice wherever you would otherwise reach for SCSU or BOCU-1, the two Unicode compression schemes. It is smaller than both over every group of values measured, and it is the only one of the three with all five properties that a store needs: bytes that sort like the text, prefixes that stay prefixes, one encoding per text, no 00 byte, and line feeds, tabs and spaces that can be found in the bytes without decoding.

This site documents the Steno Encoding and its reference implementations in .NET, Rust, C, Go, Java, TypeScript and Python.

### Why Use It

- **Stored text gets smaller.** Alphabets such as Greek, Cyrillic, Hebrew, Arabic, Hindi and Thai take about 1 byte per character, and Chinese, Japanese and Korean about 2. Over the UDHR in 47 languages cut into 16-character values, that is 1.11 bytes per character against 1.92 for UTF-8, and on short values the Steno Encoding is smaller than every other encoding and compressor measured.
- **The store works on the bytes.** Steno bytes sort like the text, keep prefixes, have one encoding per text and never contain the byte 00, so a store compares, sorts, range-scans, prefix-matches and hashes them as they are. Formats without these properties must be decoded first, and in the .NET measurements that takes many times as long as comparing the bytes.
- **Lines and fields stay visible.** Line feeds, tabs and spaces are bytes of their own (03, 02 and 07), never part of another character, so encoded text splits into lines and fields without decoding, and Steno values fit into line- and tab-delimited data.
- **It drops into existing stores.** A store that sorts keys as unsigned bytes keeps Steno keys in the order of their text, with no comparator plug-in and no change inside the store.
- **It is open.** Anyone may implement it, without permission or payment, from the [specification](articles/reference/specification.md) and its test vectors.

```csharp
byte[] bytes = StenoEncoding.Instance.GetBytes("Москва");
Console.WriteLine(bytes.Length);    // 7 (UTF-8 takes 12)
```

### How It Compares for Stored Values

| | UTF-8 | UTF-16 | SCSU | BOCU-1 | **Steno** |
|---|---|---|---|---|---|
| Bytes sort like the text | Yes | Not with emoji and other supplementary characters | No | Yes | **Yes** |
| Prefix of the text, prefix of the bytes | Yes | Yes | No | Yes | **Yes** |
| One encoding per text | Yes | Yes | No | Yes | **Yes** |
| No 00 byte | Not with U+0000 | No | No | Not with U+0000 | **Yes** |
| Line feed, tab and space bytes never part of another character | Yes | No | No | Yes | **Yes** |

The Steno Encoding is the only one of these with all five. Over each group of values measured, UTF-8 is 38% to 81% larger than Steno, UTF-16 58% to 92% larger, SCSU 0.6% to 7% larger and BOCU-1 1% to 12% larger.

<!-- speed: after each benchmark run, the paragraph in Docs/benchmarks/headline.md goes here, before the links below. -->

**Using one of them today?** Each has a page on why to choose Steno over it, and when to stay with it: [UTF-8](articles/comparisons/utf-8.md) · [UTF-16](articles/comparisons/utf-16.md) · [SCSU](articles/comparisons/scsu.md) · [BOCU-1](articles/comparisons/bocu-1.md) · [general compressors](articles/comparisons/compressors.md) · [all trade-offs at a glance](articles/comparisons/trade-offs.md).

## Where to Go Next

Here are the pages to read next, in order:

- [Why the Steno Encoding](articles/start/introduction.md) - The fuller case for stored values, with the numbers, and the right tool for each job.
- [Implementations](articles/implementations/dotnet.md) - Pick your language and start: .NET, Rust, C, Go, Java, TypeScript or Python.
- [Trade-offs at a Glance](articles/comparisons/trade-offs.md) - The whole record against each alternative, and why the design accepts each exception.
- [Storing Steno Values](articles/start/storing-values.md) - How to integrate the encoding into a store.

Also on this site: the [benchmarks](articles/benchmarks/overview.md) with every measurement, and the [API references](api/index.md) of every implementation. For questions, issues and contributions, use the [Discord server](https://discord.gg/EkQhJFsBu6) and the [GitHub repository](https://github.com/Singulink/Singulink.Steno).

</div>
