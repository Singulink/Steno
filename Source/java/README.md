# Java Reference Implementation of the Steno Encoding

The Steno Encoding is a compact encoding of Unicode text whose bytes sort like the text. It was designed to be the ideal per-value
compression encoding for databases and key-value stores, which can sort, compare, range-scan and prefix-match Steno bytes as plain bytes,
without decoding them. It is specified in [Steno Encoding, version 1](https://github.com/Singulink/Steno/blob/main/FORMAT.md);
the [main README](https://github.com/Singulink/Steno/blob/main/README.md) compares it with UTF-8 and other encodings. The
documentation site has a [page on this implementation](https://www.singulink.com/Docs/Steno/articles/implementations/java.html)
and a [guide to storing values](https://www.singulink.com/Docs/Steno/articles/start/storing-values.html).

This folder is the Java reference implementation of the Steno Encoding, the Maven artifact `com.singulink:steno` (module
`com.singulink.steno`). It needs Java 17 or later. It has no dependencies, no native code and no `Unsafe`. It gives:

- `Steno`: one-shot encode and decode of whole values, code point comparison, and helpers on Steno bytes.
- `StenoCharset`: the encoding as a `java.nio.charset.Charset` named `x-steno`, for `String.getBytes`, `new String`, readers, writers and
  every other API that takes a charset.
- `StenoDecodeException`: the error for invalid Steno bytes, with the reason and the offset.

Steno bytes keep the order and the prefixes of the text, each value has exactly one encoding, and the byte 00 never occurs. There is no
byte below 08 inside a code point, so line feeds, tabs and spaces can be found at the byte level.

## Installation

> Not published yet: the coordinates below are the planned ones.

Maven:

```xml
<dependency>
    <groupId>com.singulink</groupId>
    <artifactId>steno</artifactId>
    <version>0.1.0</version>
</dependency>
```

Gradle:

```kotlin
implementation("com.singulink:steno:0.1.0")
```

In a modular application, add `requires com.singulink.steno;` to your `module-info.java`.

## Usage

Every example in this section is also a test, in `src/test/java/com/singulink/steno/ReadmeExamplesTest.java`.

### Encode and Decode

```java
import com.singulink.steno.Steno;

byte[] bytes = Steno.encode("Москва");
String text = Steno.decode(bytes);    // throws StenoDecodeException for invalid bytes

System.out.println(bytes.length);     // 7 (UTF-8 takes 12)
System.out.println(text);             // Москва
```

### Compare and Sort

The bytes sort like the text. Compare them as unsigned bytes with `Arrays.compareUnsigned`; Java has it already, so this library does not
add its own.

```java
byte[] zug = Steno.encode("Zug");
byte[] zurich = Steno.encode("Zürich");

Arrays.compareUnsigned(zug, zurich) < 0;          // true: the bytes of "Zug" sort first
Steno.compareCodePoints("Zug", "Zürich") < 0;     // true: the same order for strings in memory

List<String> names = new ArrayList<>(List.of("すごい😀", "すごい！"));
names.sort(Steno.CODE_POINT_ORDER);               // [すごい！, すごい😀]; String.compareTo would put the emoji first
```

`String.compareTo` compares UTF-16 code units. That is not code point order: where two strings first differ, a supplementary character
(such as an emoji, U+1F600) sorts before a character in U+E000..U+FFFF (such as U+FF01, fullwidth "！"), because its first code unit is a
surrogate in U+D800..U+DBFF. Steno bytes, `Steno.compareCodePoints` and `Steno.CODE_POINT_ORDER` put it after. Use them for strings that
must sort like their bytes.

### Encode into Your Own Buffer

```java
String text = "Ελληνικά";
byte[] buffer = new byte[Steno.maxEncodedLength(text.length())];
int length = Steno.encode(text, buffer, 0);       // 9
String back = Steno.decode(buffer, 0, length);
```

`Steno.maxEncodedLength(n)` is `3 * n` and `Steno.maxDecodedLength(n)` is `2 * n`; both are true bounds for a whole value. A destination
that is too small throws `IndexOutOfBoundsException` (`BufferOverflowException` for the `ByteBuffer` forms). There are also
`Steno.encode(CharSequence, ByteBuffer)` and `Steno.decode(ByteBuffer)`.

### Errors

```java
try {
    Steno.decode(new byte[] {0x48, 0x00});
} catch (StenoDecodeException e) {
    e.getReason();                 // Reason.ZERO_BYTE
    e.getReason().specName();      // "zero-byte"
    e.getOffset();                 // 1: where the code point that breaks the rule starts
}
```

### Streams and Other Charset APIs

```java
Charset steno = Charset.forName("x-steno");       // or StenoCharset.INSTANCE

byte[] bytes = "Grüße".getBytes(steno);
String text = new String(bytes, steno);

try (Writer writer = Files.newBufferedWriter(path, steno)) {
    writer.write("日本語のテキスト\n");
    writer.write("Line two\n");
}

try (BufferedReader reader = Files.newBufferedReader(path, steno)) {
    reader.readLine();                            // 日本語のテキスト
}
```

`new String(bytes, charset)` never throws: it replaces invalid data. Use `Steno.decode` when invalid data must be rejected:

```java
new String(new byte[] {0x48, 0x00}, StenoCharset.INSTANCE);   // "a�"
```

### Code Points

```java
byte[] bytes = Steno.encodeCodePoints(new int[] {0x61, 0xD800, 0x62, 0xDC00});   // 48 F8 C5 20 49 F8 C9 40
int[] codePoints = Steno.decodeCodePoints(bytes);

Steno.encodeCodePoints(new int[] {0xD800, 0xDC00});   // IllegalArgumentException: not a value
```

### ASCII Text

```java
Steno.isAsciiText(Steno.encode("Hello, world!\n"));   // true
Steno.isAsciiText(Steno.encode("Grüße"));             // false
```

`isAsciiText` decides from the bytes alone whether the value has only tab, line feed, carriage return and U+0020..U+007E.

## Text Model

Java strings are UTF-16, and every sequence of UTF-16 code units is a Steno value. A surrogate pair is one supplementary code point, and an
unpaired surrogate is a code point of its own (U+D800..U+DFFF). So every `String` encodes, encoding never fails, and decoding always gives a
`String` with exactly the same `char`s, unpaired surrogates included.

The code point API (`encodeCodePoints`, `decodeCodePoints`) takes and gives `int` code points. A high surrogate code point directly followed
by a low one is not a value (FORMAT.md, section 3), and `encodeCodePoints` rejects it. Such a sequence cannot be written as a `String`: the
two would be one surrogate pair.

## Errors

`Steno.decode` and `Steno.decodeCodePoints` throw `StenoDecodeException` for invalid Steno bytes. `getReason()` gives the
rule of the specification that the bytes break, as the enum `StenoDecodeException.Reason`:

| Reason | Rule | The bytes have |
|---|---|---|
| `ZERO_BYTE` | 1, `zero-byte` | a byte 00 |
| `UNUSED_LEAD` | 2, `unused-lead` | a lead byte that the current layout does not use |
| `TRUNCATED` | 3, `truncated` | an end in the middle of a code point |
| `PAST_RANGE` | 4, `past-range` | a code point past the end of its range |
| `SURROGATE_PAIR` | 5, `surrogate-pair` | a low surrogate code point directly after a high one |
| `INVALID_TRAIL` | 6, `invalid-trail` | a byte 01..07 where a trail byte is expected (trail bytes are 08..FF) |

The reason is the first rule that a decoder meets, in the order of the specification: for each code point, the lead byte (rules 1 and
2), then each trail byte as it is read (rule 3 before it is read, then rules 1 and 6), then the range (rule 4), and last rule 5.
`getOffset()` is where the code point that breaks the rule starts. `getInputLength()` is the number of bytes of that code point read up to
the defect.

`StenoDecodeException` is checked. It extends `java.nio.charset.MalformedInputException`, a `CharacterCodingException`, which is what the
charset API throws for the same kind of error. So one `catch (CharacterCodingException e)` handles both paths. Wrap it in an
`UncheckedIOException` where a checked exception does not fit.

## Streaming

A `CharsetEncoder` or `CharsetDecoder` of `StenoCharset` encodes or decodes one value between two calls of `reset()`. Over any split of the
value into pieces, it gives the same bytes or text as the one-shot methods for the whole value. That includes a split inside a surrogate
pair and a split inside the bytes of a code point. So a `Writer` or `Reader` reads or writes a whole stream as one value.

- **Encoder:** a high surrogate at the end of a piece is held for the next piece. `flush` ends the value, as for every Java charset: it
  writes a held high surrogate as an unpaired surrogate, and the encoder must be reset before it encodes again. `Writer.flush()` does not
  flush the encoder, so you can call it at any point of a stream. `CharsetEncoder.encode(CharBuffer)` resets first, so each call is one
  value.
- **Decoder:** the bytes of a code point that a piece ends in the middle of stay in the input buffer, as with the JDK's decoders. If the
  input ends there, that is malformed input (the `truncated` error). The decoder reports invalid data as malformed input
  (`CoderResult.malformedForLength`), without a reason. The state after invalid data is unknown, so the decoder stops there: it
  reports the error once, for the rest of the input buffer, and then takes and drops every later byte without another report until
  `reset()`. So with `REPLACE` (used by `new String(bytes, charset)`, `Charset.decode` and `InputStreamReader`) you get the text before the
  error and one U+FFFD, however the bytes arrive. With `REPORT` you get a `MalformedInputException`, and with `IGNORE` the text before the
  error.

## Thread Safety

The methods of `Steno` and `StenoCharset` are safe to call from many threads. They share a cache of layouts that does not lock: 1,024
entries in sets of 4, about 1.5 MB at most, with a hash chosen at random when the JVM starts. Each encoder and decoder also keeps the last
two layouts it used, so text that goes back and forth between a few states does not depend on the shared cache. Each thread that encodes
or decodes a short value keeps a working state of about 3 KB. An encoder or decoder object is not thread safe, as with every charset; use
one per thread.

Text that keeps moving to states it has not used before (for example, every code point from another rare script) builds a layout at
almost every code point. That costs about 400 ns per code point, against about 10 to 25 ns in states that are cached. The cost grows
linearly with the text, and the memory stays bounded.

## Before You Store Data

- **Store the name `x-steno` next to the bytes or in the schema.** Steno bytes carry no header or version marker, so nothing else tells a
  later reader how to decode them. `Steno.CHARSET_NAME` holds the name.
- **Keep each value apart: end it with a 00 byte or record its length.** The encoding is stateful: the bytes of a code point depend on the
  code points before it, so a value is decoded from its first byte, and the bytes of two values one after the other are not the bytes of the
  two texts together. The byte 00 never occurs in Steno bytes, so it can end a value.
- **Sort for people with a `java.text.Collator`, not with Steno bytes.** The bytes sort in code point order, which is not the order of any
  language: "Z" sorts before "a", and "Å" after "z".

The [guide to storing values](https://www.singulink.com/Docs/Steno/articles/start/storing-values.html) explains each point with
an example.

## Conformance

This implementation passes all 7,640 vectors of the specification's test vector file (valid, invalid and not-a-value).

Run the tests from this folder:

```
./mvnw verify
```

On Windows, use `mvnw.cmd verify`. The Maven Wrapper downloads Maven on the first run, so only a JDK 17 or later is needed. The default
run takes about 10 seconds, most of it Maven starting up. To also run the large vector file (145,573 vectors, not in the repository), set
`STENO_LARGE_VECTORS` to its path. `verify` also checks the compiler warnings (`-Xlint:all`), the Javadoc and, on JDK 21 and later, the
formatting (Palantir Java Format; `./mvnw spotless:apply` formats).

## Performance

This is scalar Java with word-at-a-time fast paths: no vector instructions. It builds the layout of each state once and keeps it in a
bounded cache. The code points that do not change the layout (ASCII, the main window, CJK ideographs, kana and Hangul syllables) go through
tight loops, and the rest through a slower general path. Values of ASCII text up to 256 chars are converted 8 bytes at a time, from the
bytes that the JDK keeps for such strings. Short values reuse a small per-thread working state (about 3 KB per thread), so that they
allocate only their result.

<!-- benchmarks:start -->
The speed figures are in `BENCHMARKS.md` after the first benchmark run.
<!-- benchmarks:end -->

The benchmarks use JMH, in the separate Maven project in `bench`, on the shared workloads of the repository (14 texts, each as a whole
document and as 16-character values). They compare the Steno Encoding with what a Java developer would use instead: the JDK's UTF-8 and
UTF-16, SCSU and BOCU-1 from ICU4J, and Deflate (`java.util.zip`), Zstandard (zstd-jni), Brotli (brotli4j) and LZ4 (lz4-java) on UTF-8.
These libraries are dependencies of the benchmark project only. Run the benchmarks on a quiet machine from this folder:

```
bench/run.sh
```

It takes about 25 minutes, writes `bench/results.csv`, `bench/sizes.csv`, `bench/libraries.csv` and `BENCHMARKS.md`, and fills the
figures above. JMH options after the command are
passed on, for example `bench/run.sh -p text=english` for one text. The default build does not build or run the benchmarks.

## License

MIT, the license of the repository. See [LICENSE.md](https://github.com/Singulink/Steno/blob/main/LICENSE.md).
