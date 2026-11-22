using System.Reflection;
using System.Text;
using PrefixClassName.MsTest;
using Shouldly;

namespace Singulink.Steno.Tests;

/// <summary>
/// The library with the classes of .NET that use an encoding (StreamWriter, StreamReader, BinaryWriter, BinaryReader), the identity of the encoding,
/// and the bound on the memory of the layout cache.
/// </summary>
[PrefixTestClass]
public sealed class IntegrationTests
{
    private static readonly StenoEncoding Steno = StenoEncoding.Instance;

    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// StreamWriter.Flush() flushes the encoder, which does not end the value: the stream stays the encoding of all that was written.
    /// </summary>
    [TestMethod]
    public void StreamWriterFlush_InTheMiddle_KeepsTheStreamOneValue()
    {
        var stream = new MemoryStream();

        using (var writer = new StreamWriter(stream, Steno, leaveOpen: true))
        {
            writer.Write("ЖЖ");
            writer.Flush();
            writer.Write("Ж");
        }

        Fuzz.Hex(stream.ToArray()).ShouldBe(Fuzz.Hex(Steno.GetBytes("ЖЖЖ")));
        stream.Position = 0;
        new StreamReader(stream, Steno, detectEncodingFromByteOrderMarks: false).ReadToEnd().ShouldBe("ЖЖЖ");
    }

    [TestMethod]
    public async Task StreamWriterFlushAsync_InTheMiddle_KeepsTheStreamOneValue()
    {
        var stream = new MemoryStream();

        await using (var writer = new StreamWriter(stream, Steno, leaveOpen: true))
        {
            await writer.WriteAsync("ЖЖ");
            await writer.FlushAsync();
            await writer.WriteAsync("Ж");
        }

        Fuzz.Hex(stream.ToArray()).ShouldBe(Fuzz.Hex(Steno.GetBytes("ЖЖЖ")));
    }

    /// <summary>
    /// A flush between the two halves of a pair writes the high surrogate as an unpaired surrogate; the low surrogate after it cannot be written.
    /// </summary>
    [TestMethod]
    public void StreamWriterFlush_BetweenTheHalvesOfAPair_Throws()
    {
        var stream = new MemoryStream();
        var writer = new StreamWriter(stream, Steno, leaveOpen: true);
        writer.Write("a\uD83D");
        writer.Flush();
        writer.Write('\uDE00');

        Should.Throw<EncoderFallbackException>(writer.Flush);
    }

    /// <summary>
    /// A stream that ends in the middle of a code point is not read without an error: StreamReader flushes the decoder at the end.
    /// </summary>
    [TestMethod]
    public void StreamReader_TruncatedStream_Throws()
    {
        byte[] bytes = Steno.GetBytes("aЖ");
        using var reader = new StreamReader(new MemoryStream(bytes, 0, bytes.Length - 1), Steno, detectEncodingFromByteOrderMarks: false);

        TestData.ReasonOf(Should.Throw<DecoderFallbackException>(reader.ReadToEnd)).ShouldBe("truncated");
    }

    /// <summary>
    /// A Steno value can start with EF BB BF, the UTF-8 byte order mark: U+09B3 U+09CF does. A StreamReader that looks for byte order marks (the
    /// default) reads it as UTF-8; the documentation says to turn that off.
    /// </summary>
    [TestMethod]
    public void StreamReader_DetectingByteOrderMarks_TakesSomeValuesForUtf8()
    {
        const string Text = "\u09B3\u09CF";
        byte[] bytes = Steno.GetBytes(Text);
        Fuzz.Hex(bytes.AsSpan(0, 3)).ShouldBe("EFBBBF");

        using var sniffing = new StreamReader(new MemoryStream(bytes), Steno);
        sniffing.ReadToEnd().ShouldNotBe(Text);
        sniffing.CurrentEncoding.ShouldBe(Encoding.UTF8);

        using var reader = new StreamReader(new MemoryStream(bytes), Steno, detectEncodingFromByteOrderMarks: false);
        reader.ReadToEnd().ShouldBe(Text);
    }

    /// <summary>
    /// BinaryWriter.Write(string) writes each string as a value of its own, with its length in front; BinaryReader.ReadString reads a string of up to
    /// 128 bytes as a whole value, so short strings in a row round-trip, whatever the strings before them leave the decoder in.
    /// </summary>
    [TestMethod]
    public void BinaryReader_ShortNonAsciiStringsInARow_RoundTrip()
    {
        string[] values = ["Мир", "Καλη", "Мир", "東京で", "テスト", "한국", "Łódź", "ЖЖ"];

        // The bytes of each string read differently after the string before it as one value: so a decoder that kept its state would fail.
        for (int i = 1; i < values.Length; i++)
            TestData.LibraryDecode([.. Steno.GetBytes(values[i - 1]), .. Steno.GetBytes(values[i])]).Text.ShouldNotBe(values[i - 1] + values[i]);

        var stream = new MemoryStream();

        using (var writer = new BinaryWriter(stream, Steno, leaveOpen: true))
        {
            foreach (string value in values)
                writer.Write(value);

            writer.Write("Мир");
            writer.Write('ж');
            writer.Write("Καλη");
        }

        stream.Position = 0;
        using var reader = new BinaryReader(stream, Steno);

        foreach (string value in values)
            reader.ReadString().ShouldBe(value);

        reader.ReadString().ShouldBe("Мир");
        reader.ReadChar().ShouldBe('ж');
        reader.ReadString().ShouldBe("Καλη");
    }

    /// <summary>
    /// BinaryWriter writes long strings (it encodes those over about 21,000 chars with its encoder in pieces) as the length-prefixed whole values.
    /// </summary>
    [TestMethod]
    public void BinaryWriter_LongStrings_AreWholeValues()
    {
        string[] values = [new string('Ж', 30_000) + "一", "Ελληνικά " + new string('λ', 30_000), new string('Ж', 30_000) + "一"];
        var stream = new MemoryStream();
        var expected = new MemoryStream();

        using (var writer = new BinaryWriter(stream, Steno, leaveOpen: true))
        using (var prefixed = new BinaryWriter(expected, Encoding.UTF8, leaveOpen: true))
        {
            foreach (string value in values)
            {
                writer.Write(value);

                byte[] bytes = Steno.GetBytes(value);
                prefixed.Write7BitEncodedInt(bytes.Length);
                prefixed.Write(bytes);
            }
        }

        stream.ToArray().AsSpan().SequenceEqual(expected.ToArray()).ShouldBeTrue();
    }

    /// <summary>
    /// The trap that the documentation of GetDecoder describes: BinaryReader.ReadString decodes a string of more than 128 bytes with its decoder in
    /// pieces and never resets it, so a second such string is read in the state that the first one left. If this starts to fail, .NET changed and the
    /// documentation must be updated.
    /// </summary>
    [TestMethod]
    public void BinaryReader_SecondLongString_IsReadInTheStateOfTheFirst()
    {
        string first = new('Ж', 300);
        string second = new string('λ', 300) + "あ";
        var stream = new MemoryStream();

        using (var writer = new BinaryWriter(stream, Steno, leaveOpen: true))
        {
            writer.Write(first);
            writer.Write(second);
        }

        stream.Position = 0;
        using var reader = new BinaryReader(stream, Steno);

        reader.ReadString().ShouldBe(first);

        string? read;

        try
        {
            read = reader.ReadString();
        }
        catch (DecoderFallbackException)
        {
            read = null;
        }

        read.ShouldNotBe(second);
    }

    /// <summary>
    /// A Decoder.Convert that throws on invalid bytes leaves the decoder as it was, also when it has converted pieces of the input before.
    /// </summary>
    [TestMethod]
    public void DecoderConvert_ThatThrows_LeavesTheDecoderAsItWas()
    {
        const string Text = "ЖЖЖЖ";
        byte[] valid = Steno.GetBytes(Text);
        var decoder = Steno.GetDecoder();

        Should.Throw<DecoderFallbackException>(() => decoder.Convert([.. valid, 0x00], new char[4], true, out _, out _, out _));

        char[] chars = new char[16];
        new string(chars, 0, decoder.GetChars(valid, chars, true)).ShouldBe(Text);
    }

    /// <summary>
    /// Encoder.Convert with a high surrogate held over and 3 to 5 bytes of room makes progress as UTF8Encoding's does: it writes the held high
    /// surrogate on its own when the next char does not pair it.
    /// </summary>
    [TestMethod]
    public void EncoderConvert_WithAHeldHighSurrogate_MakesProgressLikeUtf8()
    {
        foreach (int room in (int[])[3, 4, 5])
        {
            var steno = Steno.GetEncoder();
            var utf8 = new UTF8Encoding(false, false).GetEncoder();
            steno.GetBytes("\uD800", new byte[8], false);
            utf8.GetBytes("\uD800", new byte[8], false);

            steno.Convert("ሴ", new byte[room], true, out int stenoChars, out int stenoBytes, out bool stenoCompleted);
            utf8.Convert("ሴ", new byte[room], true, out int utf8Chars, out _, out bool utf8Completed);

            (stenoChars, stenoCompleted).ShouldBe((utf8Chars, utf8Completed), $"room {room}");
            stenoBytes.ShouldBe(Steno.GetByteCount("\uD800"), $"room {room}");
        }
    }

    /// <summary>
    /// The identity of the encoding: the members that look up code page data do not throw for code page 0, and the encoding is equal only to a Steno
    /// encoding.
    /// </summary>
    [TestMethod]
    public void Identity()
    {
        Steno.CodePage.ShouldBe(0);
        Steno.BodyName.ShouldBe("x-steno");
        Steno.HeaderName.ShouldBe("x-steno");
        Steno.WindowsCodePage.ShouldBe(0);
        Steno.IsBrowserDisplay.ShouldBeFalse();
        Steno.IsBrowserSave.ShouldBeFalse();
        Steno.IsMailNewsDisplay.ShouldBeFalse();
        Steno.IsMailNewsSave.ShouldBeFalse();
        Steno.IsReadOnly.ShouldBeTrue();
        Steno.EncoderFallback.ShouldBe(EncoderFallback.ExceptionFallback);
        Steno.DecoderFallback.ShouldBe(DecoderFallback.ExceptionFallback);

        var clone = (Encoding)Steno.Clone();
        clone.IsReadOnly.ShouldBeFalse();
        clone.DecoderFallback = DecoderFallback.ReplacementFallback;
        Steno.Equals(clone).ShouldBeTrue();
        clone.Equals(Steno).ShouldBeTrue();
        Steno.GetHashCode().ShouldBe(clone.GetHashCode());

        var other = new OtherCodePageZeroEncoding();
        Steno.Equals(other).ShouldBeFalse();
        Steno.Equals(Encoding.UTF8).ShouldBeFalse();
        Steno.Equals(null).ShouldBeFalse();

        // There is no public constructor: Instance is the way to get one.
        typeof(StenoEncoding).GetConstructors(BindingFlags.Public | BindingFlags.Instance).ShouldBeEmpty();
    }

    /// <summary>
    /// The memory that the layout cache keeps stays bounded over texts with many windows (the layouts that the cache evicts are collected), and no
    /// layout, tables or transition holds a reference to another layout.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void LayoutMemory_IsBounded_OnGeneratedTexts()
    {
        // Structurally: what a layout remembers of its transitions names the layout that they lead to, and does not hold it. The only reference
        // to a layout in a layout is to its tables (its twin, with the same key and id, see Layout.Tables), and the tables are a layout.
        var codec = typeof(StenoEncoding).Assembly.GetType("Singulink.Steno.StenoCodec")!;
        var layoutType = codec.GetNestedType("Layout", BindingFlags.NonPublic | BindingFlags.Public)!;
        var tablesType = codec.GetNestedType("Tables", BindingFlags.NonPublic | BindingFlags.Public)!;
        var transitionType = codec.GetNestedType("Transition", BindingFlags.NonPublic | BindingFlags.Public)!;

        layoutType.IsAssignableFrom(tablesType).ShouldBeTrue();

        foreach (var type in (Type[])[layoutType, tablesType, transitionType])
        {
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            fields.Where(f => f.Name != "Tables" && IsLayout(f.FieldType)).ShouldBeEmpty($"{type.Name} holds a layout");
        }

        bool IsLayout(Type type) => layoutType.IsAssignableFrom(type) || (type.IsArray && layoutType.IsAssignableFrom(type.GetElementType()));

        // By measurement: retained memory after each round of texts.
        var random = new Random(7);
        var marks = new List<long>();

        for (int round = 0; round < 8; round++)
        {
            for (int i = 0; i < 1500; i++)
                Steno.GetString(Steno.GetBytes(KindText.Runs(random, 300)));

            marks.Add(GC.GetTotalMemory(forceFullCollection: true) / 1_048_576);
        }

        TestContext.WriteLine($"retained MB after each 1,500 texts: {string.Join(", ", marks)}");
        (marks[^1] - marks[1]).ShouldBeLessThan(20, $"retained memory grows: {string.Join(", ", marks)} MB");
    }

    private sealed class OtherCodePageZeroEncoding : Encoding
    {
        public override int GetByteCount(char[] chars, int index, int count) => count;

        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) => 0;

        public override int GetCharCount(byte[] bytes, int index, int count) => count;

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) => 0;

        public override int GetMaxByteCount(int charCount) => charCount;

        public override int GetMaxCharCount(int byteCount) => byteCount;
    }
}
