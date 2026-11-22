<div class="article">

# Working with Encoded Values

The bytes of the Steno Encoding (Steno for short) can be compared, sorted, searched by prefix or range, tested for equality, split on line feeds, tabs and spaces, and checked for ASCII text without decoding them. These are properties of the encoding, so they hold for the bytes of every implementation. This page shows each of them with byte arrays in memory, with the .NET reference implementation in C#. A database or a key-value store does the same on the bytes it holds.

### In Other Languages

The other reference implementations have the same operations:

| Implementation | Compare Steno bytes with | Prefix test on bytes | Strings in memory in the same order |
|---|---|---|---|
| [C](../implementations/c.md) | `memcmp`, then the shorter first | `memcmp` on the length of the prefix | `memcmp` on UTF-8; a loop of its own on code points or UTF-16 |
| [Go](../implementations/go.md) | `bytes.Compare` | `bytes.HasPrefix` | The language's own string comparison |
| [Java](../implementations/java.md) | `Arrays.compareUnsigned` | Compare the first bytes | `Steno.CODE_POINT_ORDER` |
| [Python](../implementations/python.md) | `<` on `bytes` | `bytes.startswith` | The language's own `str` comparison |
| [Rust](../implementations/rust.md) | `<` on byte slices | `starts_with` | The language's own `str` comparison |
| [TypeScript](../implementations/typescript.md) | `compareBytes` | `startsWith` | `compareCodePoints` |

### Byte Comparer for the .NET Examples

.NET compares byte spans with <xref:System.MemoryExtensions.SequenceCompareTo*?displayProperty=nameWithType>, which compares them as unsigned bytes. The examples on this page use it through this small comparer for byte arrays:

```csharp
sealed class ByteOrder : IComparer<byte[]>, IEqualityComparer<byte[]>
{
    public static readonly ByteOrder Instance = new();

    public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);

    public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

    public int GetHashCode(byte[] bytes)
    {
        var hash = new HashCode();
        hash.AddBytes(bytes);
        return hash.ToHashCode();
    }
}
```

Each example starts with `using Singulink.Steno;` and `var steno = StenoEncoding.Instance;`.

## Comparing and Sorting Bytes

Sorting the bytes sorts the texts, in code point order:

```csharp
string[] names = ["Zürich", "Århus", "Zug", "東京", "Aachen", "Москва"];

byte[][] keys = names.Select(name => steno.GetBytes(name)).ToArray();
Array.Sort(keys, ByteOrder.Instance);

foreach (byte[] key in keys)
    Console.WriteLine(steno.GetString(key));

// Aachen
// Zug
// Zürich
// Århus
// Москва
// 東京
```

A store that compares keys as unsigned bytes, as many key-value stores do, therefore keeps Steno keys in the order of their text with no help from the application.

Comparing the bytes is many times as fast as decoding both values first to compare their text, and a store that holds Steno bytes never has to decode them to compare them. [Comparing Without Decoding](../benchmarks/comparing.md) has the figures, text by text, against decoding both values and against comparing UTF-8 bytes.

## Same Order for Strings in Memory

Code that keeps strings next to the Steno bytes, such as a cache of an index, must order them the same way. In .NET, <xref:Singulink.Steno.StenoEncoding.CodePointComparer?displayProperty=nameWithType> is a <xref:System.StringComparer> in that order, and <xref:Singulink.Steno.StenoEncoding.CompareCodePoints*?displayProperty=nameWithType> compares two spans of characters:

```csharp
string[] names = ["Zürich", "Århus", "Zug", "東京", "Aachen", "Москва"];

Array.Sort(names, StenoEncoding.CodePointComparer);

Console.WriteLine(string.Join(", ", names));   // Aachen, Zug, Zürich, Århus, Москва, 東京
```

It works anywhere a comparer goes:

```csharp
var visits = new SortedDictionary<string, int>(StenoEncoding.CodePointComparer)
{
    ["Zürich"] = 3,
    ["Zug"] = 1,
    ["Århus"] = 2,
};

Console.WriteLine(string.Join(", ", visits.Keys));   // Zug, Zürich, Århus
```

It does not allocate. A `null` string comes first, as with <xref:System.StringComparer.Ordinal?displayProperty=nameWithType>, and equality is ordinal equality.

> [!WARNING]
> Do not use ordinal comparison for this: the strings then sort differently from their bytes, without an error. Ordinal comparison puts a supplementary character, such as an emoji, before the characters U+E000..U+FFFF, and the bytes put it after them. See [Matching the Order in Memory](storing-values.md#matching-the-order-in-memory).

## Composite Keys

The byte 00 never occurs in an encoding, so it can end a field inside a key. A key made of several fields, each ended with 00, sorts field by field:

```csharp
static byte[] MakeKey(string lastName, string firstName)
{
    var steno = StenoEncoding.Instance;
    byte[] buffer = new byte[steno.GetMaxByteCount(lastName.Length) + 1 + steno.GetMaxByteCount(firstName.Length)];

    int length = steno.GetBytes(lastName, buffer);
    buffer[length++] = 0x00;                                 // the separator
    length += steno.GetBytes(firstName, buffer.AsSpan(length));
    return buffer[..length];
}

static (string LastName, string FirstName) ReadKey(ReadOnlySpan<byte> key)
{
    var steno = StenoEncoding.Instance;

    int separator = key.IndexOf((byte)0x00);
    return (steno.GetString(key[..separator]), steno.GetString(key[(separator + 1)..]));
}
```

```csharp
var keys = new List<byte[]>
{
    MakeKey("Nguyen", "Anh"),
    MakeKey("Ng", "Zoe"),
    MakeKey("Łukasiewicz", "Jan"),
    MakeKey("Ng", "Wei"),
};

keys.Sort(ByteOrder.Instance);

foreach (byte[] key in keys)
    Console.WriteLine(ReadKey(key));

// (Ng, Wei)
// (Ng, Zoe)
// (Nguyen, Anh)
// (Łukasiewicz, Jan)
```

"Ng" comes before "Nguyen" because its 00 is smaller than any byte of "Nguyen" at the same place. Each field is a value of its own: encode each one with its own call, and decode each one from its first byte. The last field needs no 00 after it. A field of fixed width, such as a big-endian integer, needs no 00 either, since its width says where it ends.

## Prefix Queries

The encoding of a prefix of a text is a prefix of the encoding of the text. In a sorted list, every key that starts with some text is in one run, which starts where the bytes of that text would go:

```csharp
string[] names = ["Müllner", "Mueller", "Münster", "Müller", "Muller", "Mülheim", "Maier"];

var keys = names.Select(name => steno.GetBytes(name)).ToList();
keys.Sort(ByteOrder.Instance);

byte[] prefix = steno.GetBytes("Mül");

int i = keys.BinarySearch(prefix, ByteOrder.Instance);
if (i < 0)
    i = ~i;   // the first key at or after the prefix

for (; i < keys.Count && keys[i].AsSpan().StartsWith(prefix); i++)
    Console.WriteLine(steno.GetString(keys[i]));

// Mülheim
// Müller
// Müllner
```

The prefix guarantee is about code points. A text cut between the two halves of a surrogate pair, such as an emoji, is not a code point prefix, and its bytes do not start the bytes of the full text. Cut the text of a prefix query at a code point boundary; see [Prefix Queries](storing-values.md#prefix-queries).

A prefix query on the first field of a composite key works the same way. The same property lets a store compress sorted keys by the bytes they share with the key before them.

## Range Queries

Because the bytes sort like the text, a range of bytes is a range of text. The keys from the bytes of "Mu" up to the bytes of "Mü" are exactly the texts from "Mu" up to "Mü" in code point order:

```csharp
string[] names = ["Müllner", "Mueller", "Münster", "Müller", "Muller", "Mülheim", "Maier"];

var keys = names.Select(name => steno.GetBytes(name)).ToList();
keys.Sort(ByteOrder.Instance);

// Every name from "Mu" up to, but not including, "Mü":
byte[] from = steno.GetBytes("Mu");
byte[] to = steno.GetBytes("Mü");

foreach (byte[] key in keys)
{
    if (ByteOrder.Instance.Compare(key, from) >= 0 && ByteOrder.Instance.Compare(key, to) < 0)
        Console.WriteLine(steno.GetString(key));
}

// Mueller
// Muller
```

A store with a sorted index seeks to the first bound and reads until the second, as in the prefix query.

## Equality and Hashing

A text has exactly one encoding, so equal bytes mean equal text and different bytes mean different text. Bytes can be compared and hashed as they are:

```csharp
byte[] a = steno.GetBytes("Straße");
byte[] b = steno.GetBytes("Straße");
byte[] c = steno.GetBytes("Strasse");

Console.WriteLine(a.AsSpan().SequenceEqual(b));   // True: the same text
Console.WriteLine(a.AsSpan().SequenceEqual(c));   // False: another text

var counts = new Dictionary<byte[], int>(ByteOrder.Instance) { [a] = 1 };
Console.WriteLine(counts.ContainsKey(b));         // True
```

<xref:System.HashCode> gives a different hash in each process. For a hash that is stored, use a hash function with a fixed output, such as the ones in the System.IO.Hashing package.

"Equal text" means the same code points, which for .NET strings is ordinal equality. The Steno Encoding does not normalize text:

```csharp
string composed = "caf\u00E9";      // é as one code point
string decomposed = "cafe\u0301";   // e, then a combining acute accent

byte[] composedBytes = steno.GetBytes(composed);

Console.WriteLine(composedBytes.AsSpan().SequenceEqual(steno.GetBytes(decomposed)));               // False
Console.WriteLine(composedBytes.AsSpan().SequenceEqual(steno.GetBytes(decomposed.Normalize())));   // True
```

Normalize text before encoding it where such forms must be equal, for example with <xref:System.String.Normalize*?displayProperty=nameWithType>.

## Line Feeds, Tabs and Spaces

The bytes 01 to 07 never occur inside a character: each only ever starts one, and is a control character or the space. So the byte 03 is always a line feed, 02 a tab, 05 a carriage return and 07 a space, wherever it occurs. A program finds them in the bytes without decoding:

```csharp
byte[] text = steno.GetBytes("Москва\nZürich\n東京");

Console.WriteLine(text.AsSpan().Count((byte)0x03) + 1);   // 3 lines
```

The same property keeps Steno values safe inside data that is split on line feeds or tabs, such as one value per line or tab-separated fields, as long as the values themselves contain no line feed or tab. Each line is then a value of its own and decodes on its own:

```csharp
string[] cities = ["Москва", "Zürich", "東京"];
var data = new List<byte>();

foreach (string city in cities)
{
    data.AddRange(steno.GetBytes(city));
    data.Add(0x03);   // a line feed ends each value
}

ReadOnlySpan<byte> span = data.ToArray();

foreach (Range line in span[..^1].Split((byte)0x03))
    Console.WriteLine(steno.GetString(span[line]));

// Москва
// Zürich
// 東京
```

What this does not give: a line of a longer text cannot be decoded on its own, because the bytes of each character depend on the text before it, across line feeds too. Finding the lines of an encoded text works; decoding one of them needs the text before it. An application that wants to decode each line on its own encodes each line as a value of its own, as above.

## Checking for ASCII Text

In the .NET reference implementation, <xref:Singulink.Steno.StenoEncoding.IsAsciiText*> tells from the bytes alone whether a value has only printable ASCII, tab, line feed and carriage return:

```csharp
byte[] plain = steno.GetBytes("order-1234");
byte[] accented = steno.GetBytes("caf\u00E9");

Console.WriteLine(StenoEncoding.IsAsciiText(plain));      // True
Console.WriteLine(StenoEncoding.IsAsciiText(accented));   // False
Console.WriteLine(plain[0]);                              // 86, not 111 ('o'): the bytes are not ASCII bytes
```

Such a value takes 1 byte per character in Steno, as in UTF-8, but the bytes differ: a printable character is its code point minus 0x19. The reference implementation converts such a value to or from UTF-8 on a fast path; see [ASCII Fast Paths](../benchmarks/ascii.md).

## Next Steps

- [Storing Steno Values](storing-values.md) - How to integrate the encoding into a store.
- [Encoding in Pieces with .NET](../implementations/dotnet-encoding-in-pieces.md) - The encoder and the decoder, for one value given in pieces.
- [Trade-offs at a Glance](../comparisons/trade-offs.md) - Size and speed against UTF-8, UTF-16, SCSU, BOCU-1 and compressors.
- [Specification](../reference/specification.md) - The format, with the argument for each property.

</div>
