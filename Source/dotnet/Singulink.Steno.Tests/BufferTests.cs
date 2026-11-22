using System.Text;
using PrefixClassName.MsTest;
using Shouldly;

namespace Singulink.Steno.Tests;

/// <summary>
/// Destinations of every size: exact sizes are enough, one less fails without writing past the destination, counts are what is written, the maximum
/// counts are bounds, and nothing is read or written outside the spans (guard pages).
/// </summary>
[PrefixTestClass]
public sealed unsafe class BufferTests
{
    private const byte Sentinel = 0xAB;

    [TestMethod]
    public void ExactSize_IsEnough_AndOneLess_Fails()
    {
        var random = new Random(1);

        for (int i = 0; i < 1500; i++)
        {
            string text = RandomText.NextMixed(random, maxLength: i % 10 == 0 ? 200 : 40);
            byte[] expected = StenoEncoding.Instance.GetBytes(text);
            int n = expected.Length;

            CheckBytes(n, d => StenoEncoding.Instance.GetBytes(text.AsSpan(), d), expected, "bytes");
            CheckBytes(n, d => {
                byte[] array = new byte[d.Length];
                int written = StenoEncoding.Instance.GetBytes(text.ToCharArray(), 0, text.Length, array, 0);
                array.AsSpan(0, written).CopyTo(d);
                return written;
            }, expected, "bytes");
            CheckBytes(n, d => StenoEncoding.Instance.GetEncoder().GetBytes(text.AsSpan(), d, flush: true), expected, "bytes");
            CheckBytes(n, d => {
                byte empty = 0;

                // An empty span pins as a null pointer, which the pointer overloads refuse, as UTF8Encoding's do.
                fixed (char* c = text)
                fixed (byte* b = d)
                    return StenoEncoding.Instance.GetBytes(c, text.Length, b is null ? &empty : b, d.Length);
            }, expected, "bytes");
            CheckTry(n, d => StenoEncoding.Instance.TryGetBytes(text.AsSpan(), d, out int written) ? written : -1, expected);

            CheckChars(text.Length, d => StenoEncoding.Instance.GetChars(expected.AsSpan(), d), text);
            CheckChars(text.Length, d => StenoEncoding.Instance.GetDecoder().GetChars(expected.AsSpan(), d, flush: true), text);
            CheckChars(text.Length, d => {
                byte emptyBytes = 0;
                char emptyChars = '\0';

                fixed (byte* b = expected)
                fixed (char* c = d)
                    return StenoEncoding.Instance.GetChars(b is null ? &emptyBytes : b, expected.Length, c is null ? &emptyChars : c, d.Length);
            }, text);

            char[] tryChars = new char[text.Length + 1];
            StenoEncoding.Instance.TryGetChars(expected, tryChars.AsSpan(0, text.Length), out int charsWritten).ShouldBeTrue();
            charsWritten.ShouldBe(text.Length);

            if (text.Length > 0)
                StenoEncoding.Instance.TryGetChars(expected, tryChars.AsSpan(0, text.Length - 1), out _).ShouldBeFalse();

            if (!HasLoneSurrogate(text))
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(text);
                CheckBytes(utf8.Length, d => StenoEncoding.GetUtf8Bytes(expected, d), utf8, "utf8");
                CheckBytes(n, d => StenoEncoding.GetBytesFromUtf8(utf8, d), expected, "bytes");
            }
        }
    }

    [TestMethod]
    public void ArrayOverloads_WriteAtTheirIndex()
    {
        var random = new Random(2);

        for (int i = 0; i < 300; i++)
        {
            string text = RandomText.NextMixed(random, maxLength: 60);
            byte[] expected = StenoEncoding.Instance.GetBytes(text);

            byte[] bytes = new byte[expected.Length + 5];
            bytes.AsSpan().Fill(Sentinel);
            StenoEncoding.Instance.GetBytes(text, 0, text.Length, bytes, 3).ShouldBe(expected.Length);
            bytes.AsSpan(3, expected.Length).SequenceEqual(expected).ShouldBeTrue();
            bytes.AsSpan(3 + expected.Length).ToArray().ShouldAllBe(b => b == Sentinel);

            if (expected.Length > 0)
            {
                Should.Throw<ArgumentException>(() => StenoEncoding.Instance.GetBytes(text, 0, text.Length, bytes, bytes.Length - expected.Length + 1))
                    .ParamName.ShouldBe("bytes");
            }

            char[] chars = new char[text.Length + 5];
            StenoEncoding.Instance.GetChars(expected, 0, expected.Length, chars, 2).ShouldBe(text.Length);
            new string(chars, 2, text.Length).ShouldBe(text);

            if (text.Length > 0)
            {
                Should.Throw<ArgumentException>(() => StenoEncoding.Instance.GetChars(expected, 0, expected.Length, chars, chars.Length - text.Length + 1))
                    .ParamName.ShouldBe("chars");
            }
        }
    }

    [TestMethod]
    public void MaxByteCount_HoldsOnWorstCase()
    {
        string worst = WorstBytesPerChar(300);
        byte[] bytes = StenoEncoding.Instance.GetBytes(worst);

        // The text is close to the bound of 3 bytes per char: the bound is not loose by much, and it holds.
        bytes.Length.ShouldBeGreaterThan(worst.Length * 5 / 2);
        bytes.Length.ShouldBeLessThanOrEqualTo(StenoEncoding.Instance.GetMaxByteCount(worst.Length));

        // With a high surrogate held over from the call before, the call writes it too.
        foreach (string held in (string[])["\uD800", "\uDBFF"])
        {
            var encoder = StenoEncoding.Instance.GetEncoder();
            byte[] buffer = new byte[StenoEncoding.Instance.GetMaxByteCount(worst.Length)];
            encoder.GetBytes(held, buffer, flush: false).ShouldBe(0);

            int written = encoder.GetBytes(worst, buffer, flush: true);
            written.ShouldBeLessThanOrEqualTo(StenoEncoding.Instance.GetMaxByteCount(worst.Length));
            encoder.GetBytes([], buffer, flush: false).ShouldBe(0);

            // The worst single char after a held high surrogate: the high surrogate and a char of 3 bytes, against a bound of 3 for 1 char.
            encoder.GetBytes(held, buffer, flush: false);
            encoder.GetBytes(worst.AsSpan(0, 1), buffer, flush: true).ShouldBeLessThanOrEqualTo(StenoEncoding.Instance.GetMaxByteCount(1));
            encoder.GetBytes(held, buffer, flush: false);
            encoder.GetBytes([], buffer, flush: true).ShouldBeLessThanOrEqualTo(StenoEncoding.Instance.GetMaxByteCount(0));
        }

        StenoEncoding.Instance.GetMaxByteCount(0).ShouldBe(3);
        Should.Throw<ArgumentOutOfRangeException>(() => StenoEncoding.Instance.GetMaxByteCount(-1));
        Should.Throw<ArgumentOutOfRangeException>(() => StenoEncoding.Instance.GetMaxByteCount(int.MaxValue / 3));
    }

    [TestMethod]
    public void MaxCharCount_HoldsOnWorstCase()
    {
        // Supplementary code points of 1 byte: 2 chars for each byte.
        var worst = new StringBuilder();

        for (int i = 0; i < 200; i++)
            worst.Append(char.ConvertFromUtf32(0x1F600 + (i % 0x50)));

        string text = worst.ToString();
        byte[] bytes = StenoEncoding.Instance.GetBytes(text);

        text.Length.ShouldBeGreaterThan(bytes.Length * 19 / 10);
        text.Length.ShouldBeLessThanOrEqualTo(StenoEncoding.Instance.GetMaxCharCount(bytes.Length));

        // With the bytes of an unfinished code point held over: every split, each call within the bound for the bytes it was given.
        for (int split = 0; split <= bytes.Length; split++)
        {
            var decoder = StenoEncoding.Instance.GetDecoder();
            char[] chars = new char[text.Length];

            int first = decoder.GetChars(bytes.AsSpan(0, split), chars, flush: false);
            first.ShouldBeLessThanOrEqualTo(StenoEncoding.Instance.GetMaxCharCount(split));

            int second = decoder.GetChars(bytes.AsSpan(split), chars.AsSpan(first), flush: true);
            second.ShouldBeLessThanOrEqualTo(StenoEncoding.Instance.GetMaxCharCount(bytes.Length - split));
        }

        StenoEncoding.Instance.GetMaxCharCount(0).ShouldBe(0);
        Should.Throw<ArgumentOutOfRangeException>(() => StenoEncoding.Instance.GetMaxCharCount(-1));
        Should.Throw<ArgumentOutOfRangeException>(() => StenoEncoding.Instance.GetMaxCharCount((int.MaxValue / 2) + 1));
    }

    [TestMethod]
    public void MaxCounts_HoldOnRandomText()
    {
        var random = new Random(3);

        for (int i = 0; i < 2000; i++)
        {
            string text = RandomText.NextMixed(random, maxLength: 60);
            byte[] bytes = StenoEncoding.Instance.GetBytes(text);

            bytes.Length.ShouldBeLessThanOrEqualTo(StenoEncoding.Instance.GetMaxByteCount(text.Length));
            text.Length.ShouldBeLessThanOrEqualTo(StenoEncoding.Instance.GetMaxCharCount(bytes.Length));
        }
    }

    [TestMethod]
    public void GuardPages_NothingOutsideTheSpans()
    {
        if (!GuardedMemory.IsSupported)
            return;

        using var input = new GuardedMemory();
        using var output = new GuardedMemory();
        var random = new Random(4);
        int maxChars = Math.Min(input.PageSize / 2, 1300) / 3;

        for (int i = 0; i < 1500; i++)
        {
            // Long runs of one script now and then, which the vectorized paths take.
            string text = i % 3 == 0 ? Run(random, maxChars) : RandomText.NextMixed(random, maxLength: maxChars);

            if (text.Length > maxChars)
                text = text[..maxChars];

            if (text.Length > 0 && char.IsHighSurrogate(text[^1]))
                text = text[..^1];

            byte[] expected = StenoEncoding.Instance.GetBytes(text);
            bool atEnd = i % 2 == 0;

            var chars = atEnd ? input.AtEnd<char>(text.Length) : input.AtStart<char>(text.Length);
            text.AsSpan().CopyTo(chars);

            var bytes = atEnd ? output.AtEnd<byte>(expected.Length) : output.AtStart<byte>(expected.Length);
            StenoEncoding.Instance.GetBytes(chars, bytes).ShouldBe(expected.Length);
            bytes.SequenceEqual(expected).ShouldBeTrue();
            StenoEncoding.Instance.GetByteCount(chars).ShouldBe(expected.Length);

            var encoded = atEnd ? input.AtEnd<byte>(expected.Length) : input.AtStart<byte>(expected.Length);
            expected.CopyTo(encoded);

            var decoded = atEnd ? output.AtEnd<char>(text.Length) : output.AtStart<char>(text.Length);
            StenoEncoding.Instance.GetChars(encoded, decoded).ShouldBe(text.Length);
            decoded.SequenceEqual(text).ShouldBeTrue();
            StenoEncoding.Instance.GetCharCount(encoded).ShouldBe(text.Length);
            StenoEncoding.IsAsciiText(encoded).ShouldBe(IsPrintableAscii(text));

            // Cut off, so that the last code point is unfinished at the end of the span.
            if (expected.Length > 1)
            {
                var cut = atEnd ? input.AtEnd<byte>(expected.Length - 1) : input.AtStart<byte>(expected.Length - 1);
                expected.AsSpan(0, expected.Length - 1).CopyTo(cut);

                var result = TestData.LibraryDecode(cut);
                result.ShouldBe(TestData.LibraryDecode(expected.AsSpan(0, expected.Length - 1).ToArray()));
            }

            if (!HasLoneSurrogate(text))
            {
                expected.CopyTo(encoded);
                byte[] utf8 = Encoding.UTF8.GetBytes(text);
                var utf8Out = atEnd ? output.AtEnd<byte>(utf8.Length) : output.AtStart<byte>(utf8.Length);
                StenoEncoding.GetUtf8Bytes(encoded, utf8Out).ShouldBe(utf8.Length);
                utf8Out.SequenceEqual(utf8).ShouldBeTrue();

                var utf8In = atEnd ? input.AtEnd<byte>(utf8.Length) : input.AtStart<byte>(utf8.Length);
                utf8.CopyTo(utf8In);
                var stenoOut = atEnd ? output.AtEnd<byte>(expected.Length) : output.AtStart<byte>(expected.Length);
                StenoEncoding.GetBytesFromUtf8(utf8In, stenoOut).ShouldBe(expected.Length);
                stenoOut.SequenceEqual(expected).ShouldBeTrue();
            }

            // Every destination one byte or char too small, ending at the guard page.
            if (expected.Length > 0)
            {
                text.AsSpan().CopyTo(input.AtEnd<char>(text.Length));
                Should.Throw<ArgumentException>(() => StenoEncoding.Instance.GetBytes(input.AtEnd<char>(text.Length), output.AtEnd<byte>(expected.Length - 1)));
                expected.CopyTo(input.AtEnd<byte>(expected.Length));
                Should.Throw<ArgumentException>(() => StenoEncoding.Instance.GetChars(input.AtEnd<byte>(expected.Length), output.AtEnd<char>(text.Length - 1)));
            }
        }
    }

    private static string Run(Random random, int maxLength)
    {
        string[] runs = ["The quick brown fox. ", "Привет мир ", "你好世界", "안녕하세요 ", "ひらがなカタカナ", "😀😂🎉", "Ελληνικά "];
        string run = runs[random.Next(runs.Length)];
        var text = new StringBuilder();
        int length = random.Next(maxLength + 1);

        while (text.Length < length)
            text.Append(run);

        return text.ToString(0, length);
    }

    private static bool HasLoneSurrogate(string text) => !Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text)).Equals(text, StringComparison.Ordinal);

    private static bool IsPrintableAscii(string text) => text.All(c => c is '\t' or '\n' or '\r' or (>= ' ' and <= '~'));

    // Checks a byte destination of exactly the size, and one byte less, inside a larger buffer so that a write past it shows.
    private static void CheckBytes(int size, Func<Span<byte>, int> write, byte[] expected, string paramName)
    {
        byte[] buffer = new byte[size + 16];
        buffer.AsSpan().Fill(Sentinel);

        write(buffer.AsSpan(0, size)).ShouldBe(expected.Length);
        buffer.AsSpan(0, size).SequenceEqual(expected).ShouldBeTrue();
        buffer.AsSpan(size).ToArray().ShouldAllBe(b => b == Sentinel);

        if (size > 0)
        {
            buffer.AsSpan().Fill(Sentinel);
            Should.Throw<ArgumentException>(() => write(buffer.AsSpan(0, size - 1))).ParamName.ShouldBe(paramName);
            buffer.AsSpan(size - 1).ToArray().ShouldAllBe(b => b == Sentinel);
        }
    }

    private static void CheckTry(int size, Func<Span<byte>, int> write, byte[] expected)
    {
        byte[] buffer = new byte[size + 16];
        buffer.AsSpan().Fill(Sentinel);

        write(buffer.AsSpan(0, size)).ShouldBe(expected.Length);
        buffer.AsSpan(0, size).SequenceEqual(expected).ShouldBeTrue();

        if (size > 0)
        {
            buffer.AsSpan().Fill(Sentinel);
            write(buffer.AsSpan(0, size - 1)).ShouldBe(-1);
            buffer.AsSpan(size - 1).ToArray().ShouldAllBe(b => b == Sentinel);
        }
    }

    private static void CheckChars(int size, Func<Span<char>, int> write, string expected)
    {
        char[] buffer = new char[size + 16];
        buffer.AsSpan().Fill('ꯍ');

        write(buffer.AsSpan(0, size)).ShouldBe(expected.Length);
        new string(buffer, 0, size).ShouldBe(expected);
        buffer.AsSpan(size).ToArray().ShouldAllBe(c => c == 'ꯍ');

        if (size > 0)
        {
            buffer.AsSpan().Fill('ꯍ');
            Should.Throw<ArgumentException>(() => write(buffer.AsSpan(0, size - 1))).ParamName.ShouldBe("chars");
            buffer.AsSpan(size - 1).ToArray().ShouldAllBe(c => c == 'ꯍ');
        }
    }

    // Text that takes as many bytes per char as the encoder can be made to write, found with the reference codec: from each state, the code point
    // below U+10000 (or lone surrogate) that takes the most bytes, among a sample of every block.
    private static string WorstBytesPerChar(int length)
    {
        int[] candidates = [.. Enumerable.Range(0, 0x100).Select(i => (i * 0x100) + 0x55).Where(c => c is < 0xD800 or > 0xDFFF), 0xD800, 0xDC00, 0xFFFF];
        var state = new ReferenceSteno.State();
        var codePoints = new List<int>();
        var output = new List<byte>();

        for (int i = 0; i < length; i++)
        {
            var layout = ReferenceSteno.BuildLayout(state);
            int best = candidates[0], bestSize = 0;

            foreach (int c in candidates)
            {
                if (codePoints.Count > 0 && codePoints[^1] is >= 0xD800 and <= 0xDBFF && c is >= 0xDC00 and <= 0xDFFF)
                    continue;

                output.Clear();
                ReferenceSteno.EncodeOne(layout, c, output);

                if (output.Count > bestSize)
                    (best, bestSize) = (c, output.Count);
            }

            codePoints.Add(best);
            ReferenceSteno.Update(state, best, bestSize);
        }

        return TestData.ToText(codePoints);
    }
}
