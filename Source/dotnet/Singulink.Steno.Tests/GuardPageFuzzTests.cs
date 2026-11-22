using System.Text;
using PrefixClassName.MsTest;
using Shouldly;

namespace Singulink.Steno.Tests;

/// <summary>
/// Every entry point with its input and output flush against pages that cannot be touched, so that a read or write past either end of a span crashes
/// the run (Windows and Linux; see <see cref="GuardedMemory"/>).
/// </summary>
[PrefixTestClass]
public sealed class GuardPageFuzzTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void EveryEntryPoint_AtTheEdgesOfThePage()
    {
        if (!GuardedMemory.IsSupported)
            return;

        using var input = new GuardedMemory();
        using var output = new GuardedMemory();
        var random = new Random(1101);
        long count = 0;

        foreach (string text in FixedTexts(random))
        {
            Check(input, output, text);
            count++;
        }

        count += Fuzz.Run(_ => Check(input, output, KindText.Runs(random, 600)));
        TestContext.WriteLine($"{count} texts");
    }

    /// <summary>
    /// Runs of one kind after a lead-in that sets a context, ending in a lone high surrogate, a pair, a 3-byte char or a 2-byte char: of every length
    /// up to 70 in a long run, and in the suite of a sample of the lengths around the ends of the 4, 8, 16, 24, 32 and 48 blocks.
    /// </summary>
    private static IEnumerable<string> FixedTexts(Random random)
    {
        int[] edges = [0, 1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 23, 24, 25, 31, 32, 33, 47, 48, 49, 63, 64, 65, 70];

        string[] leads = [string.Empty, "é", "ЖЖ", "一", "あ", "ア", "가", "😀😁", "一あ", "一ア"];

        KindText.Kind[] kinds = [
            KindText.Kind.Ascii, KindText.Kind.Ideograph, KindText.Kind.Hangul, KindText.Kind.Hiragana, KindText.Kind.Katakana,
            KindText.Kind.Cyrillic, KindText.Kind.FullwidthLow, KindText.Kind.CjkPunct, KindText.Kind.TabLfCr,
        ];

        foreach (string lead in leads)
        {
            foreach (var kind in kinds)
            {
                var lengths = Fuzz.IsLong ? Enumerable.Range(0, 71) : edges.OrderBy(_ => random.Next()).Take(6);

                foreach (int n in lengths)
                {
                    string body = lead + KindText.Of(random, kind, 0x400, n);

                    yield return body;
                    yield return body + "\uD800";
                    yield return body + "😀";
                    yield return body + "ሴ";
                    yield return body + "«";
                }
            }
        }
    }

    private static void Check(GuardedMemory inPage, GuardedMemory outPage, string text)
    {
        var encoding = StenoEncoding.Instance;
        byte[] expected = FastReference.Encode(text);
        string what = Fuzz.Describe(text);

        if (text.Length * 2 > inPage.PageSize || expected.Length > outPage.PageSize || text.Length * 2 > outPage.PageSize || expected.Length > inPage.PageSize)
            return;

        foreach (bool inputAtEnd in (bool[])[true, false])
        {
            var chars = inputAtEnd ? inPage.AtEnd<char>(text.Length) : inPage.AtStart<char>(text.Length);
            text.AsSpan().CopyTo(chars);

            foreach (bool outputAtEnd in (bool[])[true, false])
            {
                var bytes = outputAtEnd ? outPage.AtEnd<byte>(expected.Length) : outPage.AtStart<byte>(expected.Length);
                encoding.GetBytes(chars, bytes).ShouldBe(expected.Length, what);
                bytes.SequenceEqual(expected).ShouldBeTrue(what);

                if (expected.Length > 0)
                {
                    Should.Throw<ArgumentException>(() =>
                        encoding.GetBytes(Chars(inPage, inputAtEnd, text.Length), Bytes(outPage, outputAtEnd, expected.Length - 1)));

                    try
                    {
                        encoding.GetBytes(chars, outputAtEnd ? outPage.AtEnd<byte>(expected.Length / 3) : outPage.AtStart<byte>(expected.Length / 3));
                    }
                    catch (ArgumentException)
                    {
                    }
                }

                encoding.GetByteCount(chars).ShouldBe(expected.Length, what);
                encoding.TryGetBytes(chars, bytes, out _).ShouldBeTrue(what);
            }

            // The encoder in two pieces, each at the end of the page.
            for (int cut = 0; cut <= text.Length; cut += 1 + (text.Length / 8))
            {
                var encoder = encoding.GetEncoder();
                var first = inPage.AtEnd<char>(cut);
                text.AsSpan(0, cut).CopyTo(first);
                var out1 = outPage.AtEnd<byte>(encoder.GetByteCount(first, false));
                var result = new List<byte>(out1[..encoder.GetBytes(first, out1, false)].ToArray());

                var second = inPage.AtEnd<char>(text.Length - cut);
                text.AsSpan(cut).CopyTo(second);
                var out2 = outPage.AtEnd<byte>(encoder.GetByteCount(second, true));
                result.AddRange(out2[..encoder.GetBytes(second, out2, true)].ToArray());

                Fuzz.Hex([.. result]).ShouldBe(Fuzz.Hex(expected), $"encoder cut at {cut}: {what}");
            }
        }

        foreach (bool inputAtEnd in (bool[])[true, false])
        {
            var bytes = inputAtEnd ? inPage.AtEnd<byte>(expected.Length) : inPage.AtStart<byte>(expected.Length);
            expected.AsSpan().CopyTo(bytes);

            foreach (bool outputAtEnd in (bool[])[true, false])
            {
                var chars = outputAtEnd ? outPage.AtEnd<char>(text.Length) : outPage.AtStart<char>(text.Length);
                encoding.GetChars(bytes, chars).ShouldBe(text.Length, what);
                chars.SequenceEqual(text).ShouldBeTrue(what);

                if (text.Length > 0)
                {
                    Should.Throw<ArgumentException>(() =>
                        encoding.GetChars(Bytes(inPage, inputAtEnd, expected.Length), Chars(outPage, outputAtEnd, text.Length - 1)));

                    try
                    {
                        encoding.GetChars(bytes, outputAtEnd ? outPage.AtEnd<char>(text.Length / 3) : outPage.AtStart<char>(text.Length / 3));
                    }
                    catch (ArgumentException)
                    {
                    }
                }

                encoding.GetCharCount(bytes).ShouldBe(text.Length, what);
            }

            // Cut off at the end of the page: rejected or a prefix, never a read past it; and the decoder in two pieces.
            for (int cut = 0; cut < expected.Length; cut += 1 + (expected.Length / 16))
            {
                var part = inPage.AtEnd<byte>(cut);
                expected.AsSpan(0, cut).CopyTo(part);

                try
                {
                    encoding.GetChars(part, outPage.AtEnd<char>(Math.Min(cut * 2, outPage.PageSize / 2)));
                }
                catch (DecoderFallbackException)
                {
                }

                var decoder = encoding.GetDecoder();
                var out1 = outPage.AtEnd<char>(decoder.GetCharCount(part, false));
                var result = new StringBuilder().Append(out1[..decoder.GetChars(part, out1, false)]);

                var rest = inPage.AtEnd<byte>(expected.Length - cut);
                expected.AsSpan(cut).CopyTo(rest);
                var out2 = outPage.AtEnd<char>(decoder.GetCharCount(rest, true));
                result.Append(out2[..decoder.GetChars(rest, out2, true)]);

                result.ToString().ShouldBe(text, $"decoder cut at {cut}: {what}");
            }

            // The checks and conversions that work on the bytes.
            bytes = inputAtEnd ? inPage.AtEnd<byte>(expected.Length) : inPage.AtStart<byte>(expected.Length);
            expected.AsSpan().CopyTo(bytes);
            StenoEncoding.IsAsciiText(bytes);

            if (!KindText.HasLoneSurrogate(text))
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(text);

                if (utf8.Length <= outPage.PageSize)
                {
                    var utf8Out = outPage.AtEnd<byte>(utf8.Length);
                    StenoEncoding.GetUtf8Bytes(bytes, utf8Out).ShouldBe(utf8.Length, what);
                    utf8Out.SequenceEqual(utf8).ShouldBeTrue(what);
                    StenoEncoding.GetUtf8ByteCount(bytes).ShouldBe(utf8.Length);

                    var utf8AtStart = outPage.AtStart<byte>(utf8.Length);
                    utf8.AsSpan().CopyTo(utf8AtStart);
                    var utf8AtEnd = outPage.AtEnd<byte>(utf8.Length);
                    utf8.AsSpan().CopyTo(utf8AtEnd);

                    var stenoOut = inPage.AtEnd<byte>(expected.Length);
                    StenoEncoding.GetBytesFromUtf8(utf8AtEnd, stenoOut).ShouldBe(expected.Length, what);
                    stenoOut.SequenceEqual(expected).ShouldBeTrue(what);

                    stenoOut = inPage.AtStart<byte>(expected.Length);
                    StenoEncoding.GetBytesFromUtf8(utf8AtStart, stenoOut).ShouldBe(expected.Length, what);
                    stenoOut.SequenceEqual(expected).ShouldBeTrue(what);
                    StenoEncoding.GetByteCountFromUtf8(utf8AtEnd).ShouldBe(expected.Length, what);

                    if (expected.Length > 0)
                    {
                        Should.Throw<ArgumentException>(() =>
                            StenoEncoding.GetBytesFromUtf8(outPage.AtEnd<byte>(utf8.Length), inPage.AtEnd<byte>(expected.Length - 1)));
                        StenoEncoding.TryGetBytesFromUtf8(utf8AtEnd, inPage.AtEnd<byte>(expected.Length - 1), out _).ShouldBeFalse();
                    }
                }
            }
        }

        // The comparer on texts at the ends of the pages.
        if (text.Length > 0)
        {
            var x = inPage.AtEnd<char>(text.Length);
            text.AsSpan().CopyTo(x);
            var y = outPage.AtEnd<char>(text.Length - 1);
            text.AsSpan(0, text.Length - 1).CopyTo(y);
            StenoEncoding.CompareCodePoints(x, y);
            StenoEncoding.CompareCodePoints(y, x);
        }

        static Span<char> Chars(GuardedMemory page, bool atEnd, int count) => atEnd ? page.AtEnd<char>(count) : page.AtStart<char>(count);

        static Span<byte> Bytes(GuardedMemory page, bool atEnd, int count) => atEnd ? page.AtEnd<byte>(count) : page.AtStart<byte>(count);
    }
}
