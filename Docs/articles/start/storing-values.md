<div class="article">

# Storing Steno Values

How to put Steno Encoding values into a database or a key-value store, and why the format works the way it does. Everything on this page holds for every implementation; each implementation's page adds what is particular to its language.

### In Short

| Do this | Because |
|---|---|
| Record the format name `x-steno` in the schema or next to the data | A value has zero bytes of overhead: no header, no version, no marker |
| Keep one value per field; to put several in one buffer, end each with 00 (or with a line feed or tab, if no value contains one) or record its length | Each value is self-contained, and its bytes depend on the characters before them in the same value |
| Use your implementation's code point comparison for strings in memory | Steno bytes sort in code point order, the order of UTF-8 and UTF-32 |
| Cut the text of a prefix query at a code point boundary | The prefix guarantee is about code points |
| Normalize text before encoding where equivalent forms must be equal | One encoding per text means one encoding per sequence of code points |
| Sort for people with a collation | Code point order is the order of Unicode numbers, not of a language |

## Record the Format in the Schema

A Steno value is its encoded characters and nothing else: no header, no version number, no byte order mark. That is why a short value stays small: "Zug" is 3 bytes, where a format with a header would add bytes to every value. The name of the format therefore goes where the store keeps facts about a column or a key space: in the schema, a column type, or a header of your own. Its name is `x-steno`, and every reference implementation has it as a constant.

The format, Steno Encoding, version 1, is frozen: bytes written by any correct implementation decode to the same text with any other, now and later. A different format would have a different name.

## One Value per Field

The Steno Encoding is stateful: it keeps a small state with a **window** of up to 128 code points, usually the letters of one script, which take 1 byte each. That state is what makes the bytes small, and it starts fresh in every value, so each value is self-contained and decodes on its own, from its first byte.

It follows that two values are not joined by joining their bytes. The bytes of a second value are written for the starting state, and decoding them after a first value reads them in another state:

```text
"α"                        E9 89
"αα"                       E9 89 9B
"α" twice, bytes joined    E9 89 E9 89    decodes to "αϿΟ"
```

So keep one value per field, which is what a store does anyway. To keep several values in one buffer, end each with a delimiter byte or record its length:

- **00** is always safe: it never occurs in an encoding. A composite key ended field by field with 00 also sorts field by field (see [Composite Keys](encoded-values.md#composite-keys)).
- **03 (line feed) or 02 (tab)** is safe between values that contain no line feed or tab: these bytes occur in an encoding only as those characters, never inside another one. So Steno values can be kept one per line, or as tab-separated fields (see [Line Feeds, Tabs and Spaces](encoded-values.md#line-feeds-tabs-and-spaces)).

To build one value from pieces, use your implementation's encoder object.

## Matching the Order in Memory

Steno bytes sort in code point order, the same order as UTF-8 and UTF-32 bytes. Where a program keeps strings next to the bytes, for example as a cache of an index, it must order them the same way:

| Implementation | Strings in memory are | Use |
|---|---|---|
| C | UTF-8, UTF-16 or code points, as the program keeps them | `memcmp` on UTF-8; code points and UTF-16 need a comparison loop of their own (`memcmp` on `uint32_t` values is not numeric order on little-endian machines) |
| .NET | UTF-16 | <xref:Singulink.Steno.StenoEncoding.CodePointComparer?displayProperty=nameWithType> or <xref:Singulink.Steno.StenoEncoding.CompareCodePoints*?displayProperty=nameWithType> |
| Go | UTF-8 | The language's own string comparison |
| Java | UTF-16 | `Steno.CODE_POINT_ORDER` or `Steno.compareCodePoints` |
| Python | Code points | The language's own string comparison |
| Rust | UTF-8 | The language's own string comparison |
| TypeScript | UTF-16 | `compareCodePoints` |

In the languages whose strings are UTF-16, the built-in ordinal comparison compares code units, and it differs from code point order in one case. Where two strings first differ, if one has a supplementary character (such as an emoji) and the other a character in U+E000..U+FFFF (such as fullwidth punctuation), ordinal comparison puts the emoji first and code point order puts it last. The code point comparisons in the table give the order of the bytes.

Code point order is not the order of any language either. "Z" sorts before "a", and "Å" after "z". Sort for people with a collation.

## Prefix Queries

The encoding of a prefix of a value is a prefix of its encoding, and a prefix here means a prefix of code points. A text cut between the two halves of a surrogate pair, such as an emoji, is not a code point prefix: its last code point is the lone high half, a code point of its own. So `"a\uD83D"` is a prefix of `"a😀"` in UTF-16, but its Steno bytes are not a prefix of the bytes of `"a😀"`. Cut the text of a prefix query at a code point boundary.

## Equality Means the Same Code Points

A text has exactly one encoding, so equal bytes mean the same sequence of code points. The encoding does not normalize text: "é" as one code point and "e" followed by a combining accent are two texts with two encodings. Where such forms must be equal, normalize the text, for example to NFC, before encoding it.

## Invalid Bytes Are Rejected

Bytes that are not valid Steno data break one of six rules of the specification: `zero-byte`, `unused-lead`, `truncated`, `past-range`, `surrogate-pair` or `invalid-trail` ([§10.1](../reference/specification.md#101-rejection-rules)). Decoding such bytes in one call gives an error that names the rule. After invalid bytes a decoder cannot know its state, so it cannot replace them and go on; what a streaming reader or a charset of your language does at that point is on its implementation's page.

Encoding a value never fails. Input that is not a value, such as invalid UTF-8 or a high surrogate code point directly followed by a low one, is rejected.

Text with an unpaired surrogate, which a UTF-16 string can hold, is a value like any other, but it has no UTF-8 form; decoding such a value to UTF-8 fails.

## How Size Depends on the Text

The first character of a value in a new script takes 2 to 4 bytes, and the characters of that window after it take 1 byte each. Chinese ideographs and Korean syllables work the same way, with 3 bytes for the first and 2 for each one after it:

```text
"Ж"           2 bytes
"ЖЖ"          3 bytes
"ЖЖЖ"         4 bytes
"Москва"      7 bytes
"ሰ"           3 bytes
"ሰላም"         5 bytes
"😀"          3 bytes
"😀😀"        4 bytes
"東"          3 bytes
"東京"        5 bytes
"こんにちは"  7 bytes
```

No code point takes more than 4 bytes, and none below U+20000 (which includes the emoji) more than 3. Text that changes script often pays the first-character cost at each change, and it is the slowest text to encode and decode; text in one script, the common case for a field, pays it once per value. [Trade-offs at a Glance](../comparisons/trade-offs.md) shows where this costs the most and why the design accepts it.

## Readers, Writers and Encoder Objects

Each language's own stream and charset classes have a few settings to get right with a stateful encoding. Each implementation's page lists them:

- [C](../implementations/c.md#encoding-in-pieces), [Go](../implementations/go.md), [Rust](../implementations/rust.md) and [TypeScript](../implementations/typescript.md): how their encoder and decoder objects end a value;
- [.NET](../implementations/dotnet.md#readers-writers-and-encoder-objects): `StreamReader` and byte order marks, `BinaryReader.ReadString`, flush and reset;
- [Java](../implementations/java.md#what-is-particular-to-java): `new String` replaces invalid data, a charset encoder's `flush` ends the value, and a decoder that reported an error must be reset before it is used again;
- [Python](../implementations/python.md#what-is-particular-to-python): text files through the package's own `open()`, flushing, and which modes of the built-in `open()` are safe.

## Next Steps

- [Working with Encoded Values](encoded-values.md) - Sorting, prefix and range queries, composite keys and hashing on the bytes.
- [Specification](../reference/specification.md) - The exact rules of the format.

</div>
