using System.Text;
using PrefixClassName.MsTest;
using Shouldly;

namespace Singulink.Steno.Tests;

/// <summary>
/// The contract of the public API: argument checks like those of <see cref="UTF8Encoding"/>, identity, fallbacks, and the entry points agreeing.
/// </summary>
[PrefixTestClass]
public sealed unsafe class ApiTests
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [TestMethod]
    public void Identity()
    {
        StenoEncoding.Instance.ShouldBeSameAs(StenoEncoding.Instance);
        StenoEncoding.Instance.EncodingName.ShouldBe("Steno");
        StenoEncoding.Instance.WebName.ShouldBe("x-steno");
        StenoEncoding.Instance.IsSingleByte.ShouldBeFalse();
        StenoEncoding.Instance.GetPreamble().ShouldBeEmpty();
        StenoEncoding.Instance.Preamble.IsEmpty.ShouldBeTrue();
    }

    [TestMethod]
    public void Empty()
    {
        StenoEncoding.Instance.GetBytes(string.Empty).ShouldBeEmpty();
        StenoEncoding.Instance.GetBytes(ReadOnlySpan<char>.Empty).ShouldBeEmpty();
        StenoEncoding.Instance.GetByteCount(string.Empty).ShouldBe(0);
        StenoEncoding.Instance.GetString([]).ShouldBe(string.Empty);
        StenoEncoding.Instance.GetString(ReadOnlySpan<byte>.Empty).ShouldBe(string.Empty);
        StenoEncoding.Instance.GetCharCount([]).ShouldBe(0);
        StenoEncoding.Instance.GetChars([]).ShouldBeEmpty();
        StenoEncoding.IsAsciiText([]).ShouldBeTrue();
        StenoEncoding.GetUtf8Bytes([]).ShouldBeEmpty();
        StenoEncoding.GetBytesFromUtf8([]).ShouldBeEmpty();
    }

    [TestMethod]
    public void PrintableAsciiIsShiftedAscii()
    {
        // Printable ASCII is 1 byte each, U+0020..U+007E as 07..65 (FORMAT.md section 14.1).
        const string Text = "The quick brown fox jumps over the lazy dog.\r\n\t0123456789 !\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";
        byte[] bytes = StenoEncoding.Instance.GetBytes(Text);

        bytes.Length.ShouldBe(Text.Length);

        for (int i = 0; i < Text.Length; i++)
            bytes[i].ShouldBe((byte)(Text[i] switch { '\t' => 0x02, '\n' => 0x03, '\r' => 0x05, _ => Text[i] - 0x19 }));
    }

    [TestMethod]
    public void EncodingIgnoresEncoderFallback()
    {
        // Every UTF-16 string is a value: lone surrogates are encoded, never replaced or rejected.
        var encoding = (Encoding)StenoEncoding.Instance.Clone();
        encoding.EncoderFallback = EncoderFallback.ExceptionFallback;

        foreach (string text in (string[])["\uD800", "a\uDC00b", "\uDBFF\uDBFF", "\uDFFF\uD800"])
        {
            byte[] bytes = encoding.GetBytes(text);
            TestData.Hex(bytes).ShouldBe(TestData.Hex(TestData.ReferenceEncode(text)));
            encoding.GetString(bytes).ShouldBe(text);
        }
    }

    [TestMethod]
    public void DecodingThrowsWhateverTheDecoderFallback()
    {
        byte[] invalid = [0x48, 0x00];

        DecoderFallback[] fallbacks = [DecoderFallback.ReplacementFallback, DecoderFallback.ExceptionFallback, new DecoderReplacementFallback("?")];

        foreach (var fallback in fallbacks)
        {
            var encoding = (Encoding)StenoEncoding.Instance.Clone();
            encoding.DecoderFallback = fallback;

            var ex = Should.Throw<DecoderFallbackException>(() => encoding.GetString(invalid));
            TestData.ReasonOf(ex).ShouldBe("zero-byte");
            ex.Message.ShouldContain("byte 1");

            Should.Throw<DecoderFallbackException>(() => encoding.GetCharCount(invalid));
            Should.Throw<DecoderFallbackException>(() => encoding.GetChars(invalid));
            Should.Throw<DecoderFallbackException>(() => encoding.GetDecoder().GetChars(invalid, 0, invalid.Length, new char[8], 0, flush: true));
        }
    }

    [TestMethod]
    public void RejectionDetails_AreFilledIn()
    {
        // A lead byte 00 at byte 1: the byte, and where the code point starts.
        var ex = Should.Throw<DecoderFallbackException>(() => StenoEncoding.Instance.GetString([0x48, 0x00]));
        ex.BytesUnknown.ShouldBe([(byte)0x00]);
        ex.Index.ShouldBe(1);

        // A trail byte 00: the bytes of the code point up to it.
        byte[] cyrillic = StenoEncoding.Instance.GetBytes("aЖ");
        ex = Should.Throw<DecoderFallbackException>(() => StenoEncoding.Instance.GetString([.. cyrillic[..2], 0x00]));
        ex.BytesUnknown.ShouldBe([cyrillic[1], (byte)0x00]);
        ex.Index.ShouldBe(1);

        // Cut off.
        ex = Should.Throw<DecoderFallbackException>(() => StenoEncoding.Instance.GetString(cyrillic[..2]));
        TestData.ReasonOf(ex).ShouldBe("truncated");
        ex.BytesUnknown.ShouldBe([cyrillic[1]]);
        ex.Index.ShouldBe(1);

        // A streaming decoder that completes a code point from bytes of the call before reports where it starts as a negative index, as UTF-8 does.
        var decoder = StenoEncoding.Instance.GetDecoder();
        char[] chars = new char[8];
        decoder.GetChars(cyrillic.AsSpan(0, 2), chars, flush: false).ShouldBe(1);
        ex = Should.Throw<DecoderFallbackException>(() => decoder.GetChars([0x00], chars, flush: false));
        ex.BytesUnknown.ShouldBe([cyrillic[1], (byte)0x00]);
        ex.Index.ShouldBe(-1);

        // Counting in slices reports the index in the whole input.
        byte[] longInput = [.. StenoEncoding.Instance.GetBytes(new string('a', 3000)), 0x00];
        Should.Throw<DecoderFallbackException>(() => StenoEncoding.Instance.GetCharCount(longInput)).Index.ShouldBe(3000);

        // Invalid UTF-8, also in the slices of a long input.
        byte[] utf8 = [.. Encoding.UTF8.GetBytes(new string('é', 1000)), 0xFF];
        var utf8Error = Should.Throw<DecoderFallbackException>(() => StenoEncoding.GetBytesFromUtf8(utf8));
        utf8Error.Index.ShouldBe(2000);
        utf8Error.BytesUnknown.ShouldBe([(byte)0xFF]);
    }

    [TestMethod]
    public void MaxUtf8Counts_HoldOnWorstCases()
    {
        // ASCII controls take 2 Steno bytes for 1 UTF-8 byte: the bound of GetMaxByteCountFromUtf8, exactly.
        byte[] controls = [.. Enumerable.Range(0, 300).Select(i => (byte)(i % 9))];
        StenoEncoding.GetByteCountFromUtf8(controls).ShouldBe(StenoEncoding.GetMaxByteCountFromUtf8(controls.Length));

        // Supplementary code points of the main window take 1 Steno byte for 4 UTF-8 bytes: the bound of GetMaxUtf8ByteCount, but for the first one.
        string emoji = string.Concat(Enumerable.Range(0, 300).Select(i => char.ConvertFromUtf32(0x1F600 + (i % 0x50))));
        byte[] steno = StenoEncoding.Instance.GetBytes(emoji);
        int utf8Count = StenoEncoding.GetUtf8ByteCount(steno);

        utf8Count.ShouldBeLessThanOrEqualTo(StenoEncoding.GetMaxUtf8ByteCount(steno.Length));
        utf8Count.ShouldBeGreaterThan(StenoEncoding.GetMaxUtf8ByteCount(steno.Length) - 12);

        StenoEncoding.GetMaxUtf8ByteCount(0).ShouldBe(0);
        StenoEncoding.GetMaxByteCountFromUtf8(0).ShouldBe(0);
        Should.Throw<ArgumentOutOfRangeException>(() => StenoEncoding.GetMaxUtf8ByteCount(-1)).ParamName.ShouldBe("byteCount");
        Should.Throw<ArgumentOutOfRangeException>(() => StenoEncoding.GetMaxUtf8ByteCount((int.MaxValue / 4) + 1));
        Should.Throw<ArgumentOutOfRangeException>(() => StenoEncoding.GetMaxByteCountFromUtf8(-1)).ParamName.ShouldBe("utf8ByteCount");
        Should.Throw<ArgumentOutOfRangeException>(() => StenoEncoding.GetMaxByteCountFromUtf8((int.MaxValue / 2) + 1));
        Should.Throw<ArgumentOutOfRangeException>(() => StenoEncoding.Instance.GetMaxByteCount(int.MaxValue / 3)).ParamName.ShouldBe("charCount");
        StenoEncoding.Instance.GetMaxByteCount((int.MaxValue / 3) - 1).ShouldBe(int.MaxValue - 1);
    }

    [TestMethod]
    public void RejectionMessagesNameTheReason()
    {
        (byte[] Bytes, string Reason, string Words)[] cases = [
            ([0x00], "zero-byte", "byte 00"),
            ([0x01, 0x00], "zero-byte", "byte 00"),
            ([0x01, 0x03], "invalid-trail", "trail byte"),
            ([0xFF], "unused-lead", "lead byte"),
            ([0x01], "truncated", "end"),
            ([0x01, 0xFF], "past-range", "beyond"),
        ];

        foreach ((byte[] bytes, string reason, string words) in cases)
        {
            var ex = Should.Throw<DecoderFallbackException>(() => StenoEncoding.Instance.GetString(bytes));
            TestData.ReasonOf(ex).ShouldBe(reason);
            ex.Message.ShouldContain(words, Case.Insensitive);
        }
    }

    [TestMethod]
    public void ArgumentChecks_MatchUtf8Encoding()
    {
        char[] chars = "abc".ToCharArray();
        byte[] bytes = [0x48, 0x49, 0x4A];

        Func<Encoding, object?>[] calls = [
            e => e.GetByteCount((char[])null!),
            e => e.GetByteCount((string)null!),
            e => e.GetByteCount((char[])null!, 0, 0),
            e => e.GetByteCount(chars, -1, 1),
            e => e.GetByteCount(chars, 0, -1),
            e => e.GetByteCount(chars, 1, 3),
            e => e.GetByteCount(chars, 4, 0),
            e => e.GetByteCount((char*)null, 0),
            e => e.GetByteCount((char*)null, 1),
            e => {
                fixed (char* c = chars)
                    return e.GetByteCount(c, -1);
            },
            e => e.GetBytes((string)null!),
            e => e.GetBytes((char[])null!),
            e => e.GetBytes((char[])null!, 0, 0),
            e => e.GetBytes(chars, -1, 1),
            e => e.GetBytes(chars, 0, 4),
            e => e.GetBytes(chars, 0, 1, null!, 0),
            e => e.GetBytes((char[])null!, 0, 1, new byte[8], 0),
            e => e.GetBytes(chars, 0, 1, new byte[8], -1),
            e => e.GetBytes(chars, 0, 1, new byte[8], 9),
            e => e.GetBytes(chars, -1, 1, new byte[8], 0),
            e => e.GetBytes(chars, 0, -1, new byte[8], 0),
            e => e.GetBytes(chars, 2, 2, new byte[8], 0),
            e => e.GetBytes("abc", 0, 1, null!, 0),
            e => e.GetBytes((string)null!, 0, 1, new byte[8], 0),
            e => e.GetBytes("abc", -1, 1, new byte[8], 0),
            e => e.GetBytes("abc", 0, 4, new byte[8], 0),
            e => e.GetBytes("abc", 0, 1, new byte[8], -1),
            e => e.GetBytes("abc", 0, 1, new byte[8], 9),
            e => e.GetBytes("abc", 0, 3, new byte[2], 0),
            e => e.GetBytes(chars, 0, 3, new byte[3], 1),
            e => {
                byte* b = stackalloc byte[8];
                return e.GetBytes(null, 1, b, 8);
            },
            e => {
                fixed (char* c = chars)
                    return e.GetBytes(c, 1, null, 8);
            },
            e => {
                byte* b = stackalloc byte[8];

                fixed (char* c = chars)
                    return e.GetBytes(c, -1, b, 8);
            },
            e => {
                byte* b = stackalloc byte[8];

                fixed (char* c = chars)
                    return e.GetBytes(c, 1, b, -1);
            },
            e => e.GetCharCount((byte[])null!),
            e => e.GetCharCount(null!, 0, 0),
            e => e.GetCharCount(bytes, -1, 1),
            e => e.GetCharCount(bytes, 0, -1),
            e => e.GetCharCount(bytes, 2, 2),
            e => e.GetCharCount((byte*)null, 0),
            e => e.GetChars((byte[])null!),
            e => e.GetChars((byte[])null!, 0, 0),
            e => e.GetChars(bytes, -1, 1),
            e => e.GetChars(bytes, 0, 4),
            e => e.GetChars(bytes, 0, 1, null!, 0),
            e => e.GetChars((byte[])null!, 0, 1, new char[8], 0),
            e => e.GetChars(bytes, 0, 1, new char[8], -1),
            e => e.GetChars(bytes, 0, 1, new char[8], 9),
            e => e.GetChars(bytes, -1, 1, new char[8], 0),
            e => e.GetChars(bytes, 0, 3, new char[2], 0),
            e => e.GetChars(bytes, 0, 3, new char[3], 1),
            e => {
                char* c = stackalloc char[8];
                return e.GetChars(null, 1, c, 8);
            },
            e => {
                fixed (byte* b = bytes)
                    return e.GetChars(b, 1, null, 8);
            },
            e => e.GetString((byte[])null!),
            e => e.GetString(null!, 0, 0),
            e => e.GetString(bytes, -1, 1),
            e => e.GetString(bytes, 0, -1),
            e => e.GetString(bytes, 1, 3),
            e => e.GetMaxByteCount(-1),
            e => e.GetMaxCharCount(-1),
            e => e.GetEncoder().GetByteCount(null!, 0, 0, true),
            e => e.GetEncoder().GetByteCount(chars, -1, 1, true),
            e => e.GetEncoder().GetByteCount(chars, 0, 4, true),
            e => e.GetEncoder().GetBytes(null!, 0, 0, new byte[8], 0, true),
            e => e.GetEncoder().GetBytes(chars, 0, 1, null!, 0, true),
            e => e.GetEncoder().GetBytes(chars, 0, 1, new byte[8], 9, true),
            e => e.GetEncoder().GetBytes(chars, 0, 3, new byte[2], 0, true),
            e => e.GetDecoder().GetCharCount(null!, 0, 0),
            e => e.GetDecoder().GetCharCount(bytes, 2, 2),
            e => e.GetDecoder().GetChars(null!, 0, 0, new char[8], 0),
            e => e.GetDecoder().GetChars(bytes, 0, 1, null!, 0),
            e => e.GetDecoder().GetChars(bytes, 0, 1, new char[8], 9),
            e => e.GetDecoder().GetChars(bytes, 0, 3, new char[2], 0, true),
            e => Convert(e.GetEncoder(), null!, 0, 0, new byte[4], 0, 4),
            e => Convert(e.GetEncoder(), chars, 0, 1, null!, 0, 4),
            e => Convert(e.GetEncoder(), chars, -1, 1, new byte[4], 0, 4),
            e => Convert(e.GetEncoder(), chars, 0, -1, new byte[4], 0, 4),
            e => Convert(e.GetEncoder(), chars, 0, 4, new byte[4], 0, 4),
            e => Convert(e.GetEncoder(), chars, 0, 1, new byte[4], -1, 4),
            e => Convert(e.GetEncoder(), chars, 0, 1, new byte[4], 0, -1),
            e => Convert(e.GetEncoder(), chars, 0, 1, new byte[4], 1, 4),
            e => Convert(e.GetEncoder(), chars, 0, 3, new byte[4], 0, 0),
            e => Convert(e.GetDecoder(), null!, 0, 0, new char[4], 0, 4),
            e => Convert(e.GetDecoder(), bytes, 0, 1, null!, 0, 4),
            e => Convert(e.GetDecoder(), bytes, -1, 1, new char[4], 0, 4),
            e => Convert(e.GetDecoder(), bytes, 0, 4, new char[4], 0, 4),
            e => Convert(e.GetDecoder(), bytes, 0, 1, new char[4], 1, 4),
            e => Convert(e.GetDecoder(), bytes, 0, 3, new char[4], 0, 0),
            e => {
                fixed (char* c = chars)
                    e.GetEncoder().Convert(c, 1, null, 4, true, out _, out _, out _);

                return null;
            },
            e => {
                byte* b = stackalloc byte[4];
                e.GetEncoder().Convert(null, 1, b, 4, true, out _, out _, out _);
                return null;
            },
            e => {
                fixed (byte* b = bytes)
                    e.GetDecoder().Convert(b, 1, null, 4, true, out _, out _, out _);

                return null;
            },
            e => {
                char* c = stackalloc char[4];
                e.GetDecoder().Convert(null, 0, c, 4, true, out _, out _, out _);
                return null;
            },
            e => e.GetEncoder().GetBytes((char*)null, 0, (byte*)null, 0, true),
            e => e.GetDecoder().GetChars((byte*)null, 0, (char*)null, 0, true),
            e => e.GetEncoder().GetByteCount((char*)null, 0, true),
            e => e.GetDecoder().GetCharCount((byte*)null, 0, true),
            e => e.GetDecoder().GetCharCount(bytes, -1, 1, true),
            e => e.GetDecoder().GetChars(bytes, 0, 3, new char[2], 0, false),
            e => e.GetEncoder().GetBytes(chars, 0, 3, new byte[2], 0, false),
            e => e.GetString((byte*)null, 0),
            e => e.GetString((byte*)null, 1),
        ];

        var mismatches = new List<string>();

        for (int i = 0; i < calls.Length; i++)
        {
            var expected = Catch(() => calls[i](Utf8));
            var actual = Catch(() => calls[i](StenoEncoding.Instance));

            var expectedArgument = expected.ShouldBeAssignableTo<ArgumentException>($"call {i}")!;

            // Encoding.GetByteCount(string) names its parameter s, which UTF8Encoding renames to chars; the library keeps the name of Encoding.
            if (i is 1 && actual is ArgumentNullException { ParamName: "s" })
                continue;

            if (actual?.GetType() != expected!.GetType() || ((ArgumentException)actual).ParamName != expectedArgument.ParamName)
            {
                mismatches.Add($"call {i}: expected {expected.GetType().Name} ({((ArgumentException)expected).ParamName}), " +
                    $"got {actual?.GetType().Name ?? "no exception"} ({(actual as ArgumentException)?.ParamName})");
            }
        }

        mismatches.ShouldBeEmpty();
    }

    [TestMethod]
    public void AllEntryPointsAgree()
    {
        var encoding = (Encoding)StenoEncoding.Instance;

        foreach ((string name, string text) in SampleTexts.All)
        {
            byte[] expected = StenoEncoding.Instance.GetBytes(text.AsSpan());

            encoding.GetBytes(text).ShouldBe(expected, name);
            encoding.GetBytes(text.ToCharArray()).ShouldBe(expected, name);
            encoding.GetBytes(text.ToCharArray(), 0, text.Length).ShouldBe(expected, name);
            encoding.GetByteCount(text).ShouldBe(expected.Length, name);
            encoding.GetByteCount(text.ToCharArray()).ShouldBe(expected.Length, name);
            encoding.GetByteCount(text.AsSpan()).ShouldBe(expected.Length, name);
            encoding.GetByteCount(text.ToCharArray(), 1, text.Length - 1).ShouldBe(encoding.GetByteCount(text[1..]), name);

            byte[] buffer = new byte[expected.Length + 2];
            encoding.GetBytes(text, 0, text.Length, buffer, 1).ShouldBe(expected.Length, name);
            buffer.AsSpan(1, expected.Length).SequenceEqual(expected).ShouldBeTrue(name);

            fixed (char* c = text)
            fixed (byte* b = buffer)
            {
                encoding.GetByteCount(c, text.Length).ShouldBe(expected.Length, name);
                encoding.GetBytes(c, text.Length, b, buffer.Length).ShouldBe(expected.Length, name);
            }

            buffer.AsSpan(0, expected.Length).SequenceEqual(expected).ShouldBeTrue(name);

            encoding.GetString(expected).ShouldBe(text, name);
            encoding.GetString(expected, 0, expected.Length).ShouldBe(text, name);
            encoding.GetString(expected.AsSpan()).ShouldBe(text, name);
            StenoEncoding.Instance.GetString(expected.AsSpan()).ShouldBe(text, name);
            encoding.GetCharCount(expected).ShouldBe(text.Length, name);
            encoding.GetCharCount(expected.AsSpan()).ShouldBe(text.Length, name);
            new string(encoding.GetChars(expected)).ShouldBe(text, name);
            new string(encoding.GetChars(expected, 0, expected.Length)).ShouldBe(text, name);

            char[] chars = new char[text.Length + 2];
            encoding.GetChars(expected, 0, expected.Length, chars, 2).ShouldBe(text.Length, name);
            new string(chars, 2, text.Length).ShouldBe(text, name);

            fixed (byte* b = expected)
            fixed (char* c = chars)
            {
                encoding.GetCharCount(b, expected.Length).ShouldBe(text.Length, name);
                encoding.GetChars(b, expected.Length, c, chars.Length).ShouldBe(text.Length, name);
            }

            new string(chars, 0, text.Length).ShouldBe(text, name);
        }
    }

    [TestMethod]
    public void CountsDoNotDependOnLength()
    {
        // Counting runs the codec in slices through a small buffer: lengths around the slices, and long text.
        var random = new Random(6);

        foreach (int length in (int[])[1023, 1024, 1025, 2047, 2048, 2049, 5000, 100_000])
        {
            string text = RandomText.NextMixed(random, maxLength: length);

            while (text.Length < length)
                text += RandomText.NextMixed(random, maxLength: length);

            text = text[..length];
            byte[] bytes = StenoEncoding.Instance.GetBytes(text);

            StenoEncoding.Instance.GetByteCount(text).ShouldBe(bytes.Length);
            StenoEncoding.Instance.GetCharCount(bytes).ShouldBe(text.Length);
            StenoEncoding.Instance.GetEncoder().GetByteCount(text, flush: true).ShouldBe(bytes.Length);
            StenoEncoding.Instance.GetDecoder().GetCharCount(bytes, flush: true).ShouldBe(text.Length);
            StenoEncoding.Instance.GetString(bytes).ShouldBe(text);
        }
    }

    private static object? Convert(System.Text.Encoder encoder, char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex, int byteCount)
    {
        encoder.Convert(chars, charIndex, charCount, bytes, byteIndex, byteCount, true, out _, out _, out _);
        return null;
    }

    private static object? Convert(System.Text.Decoder decoder, byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex, int charCount)
    {
        decoder.Convert(bytes, byteIndex, byteCount, chars, charIndex, charCount, true, out _, out _, out _);
        return null;
    }

    private static Exception? Catch(Func<object?> call)
    {
        try
        {
            call();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
