using System.Text;
using PrefixClassName.MsTest;
using Shouldly;

namespace Singulink.Steno.Tests;

/// <summary>
/// The properties of the format on random text (FORMAT.md section 11): order, prefixes, no zero byte; and the checks and conversions that work on
/// the bytes, against plain definitions.
/// </summary>
[PrefixTestClass]
public sealed class PropertyTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void BytesSortInCodePointOrder(int seed)
    {
        var random = new Random(seed);

        for (int i = 0; i < 3000; i++)
        {
            (string x, string y) = NextPair(random);
            int expected = Math.Sign(CompareByCodePoints(x, y));

            byte[] bx = StenoEncoding.Instance.GetBytes(x);
            byte[] by = StenoEncoding.Instance.GetBytes(y);

            Math.Sign(bx.AsSpan().SequenceCompareTo(by)).ShouldBe(expected, $"{TestData.Describe(x)} against {TestData.Describe(y)}");
        }
    }

    [TestMethod]
    public void SortedByBytes_IsSortedByCodePoints()
    {
        var random = new Random(7);
        var texts = Enumerable.Range(0, 2000).Select(_ => NextPair(random).X).ToList();

        var byBytes = texts.Select(t => (Text: t, Bytes: StenoEncoding.Instance.GetBytes(t))).ToList();
        byBytes.Sort((a, b) => a.Bytes.AsSpan().SequenceCompareTo(b.Bytes));

        for (int i = 1; i < byBytes.Count; i++)
            CompareByCodePoints(byBytes[i - 1].Text, byBytes[i].Text).ShouldBeLessThanOrEqualTo(0);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void CompareCodePoints_HasTheOrderOfTheBytes(int seed)
    {
        var random = new Random(seed);

        for (int i = 0; i < 4000; i++)
        {
            (string x, string y) = NextPair(random);

            int expected = Math.Sign(StenoEncoding.Instance.GetBytes(x).AsSpan().SequenceCompareTo(StenoEncoding.Instance.GetBytes(y)));
            int actual = StenoEncoding.CompareCodePoints(x, y);
            string message = $"{TestData.Describe(x)} against {TestData.Describe(y)}";

            Math.Sign(actual).ShouldBe(expected, message);
            Math.Sign(StenoEncoding.CodePointComparer.Compare(x, y)).ShouldBe(expected, message);
            Math.Sign(StenoEncoding.CompareCodePoints(y, x)).ShouldBe(-expected, message);

            // It differs from ordinal order exactly in the documented cases: without unpaired surrogates, where the texts first differ in a
            // supplementary char against one in U+E000..U+FFFF.
            int ordinal = Math.Sign(string.CompareOrdinal(x, y));
            (ordinal != expected).ShouldBe(OrdinalDiffers(x, y), message);

            if (!HasLoneSurrogate(x) && !HasLoneSurrogate(y))
                (ordinal != expected).ShouldBe(IsSupplementaryAgainstHighBmp(x, y), message);
        }
    }

    [TestMethod]
    public void CompareCodePoints_Examples()
    {
        // U+FF01 against U+1F600: before in code point order, after in ordinal order.
        StenoEncoding.CompareCodePoints("すごい！", "すごい😀").ShouldBeLessThan(0);
        string.CompareOrdinal("すごい！", "すごい😀").ShouldBeGreaterThan(0);
        StenoEncoding.Instance.GetBytes("すごい！").AsSpan().SequenceCompareTo(StenoEncoding.Instance.GetBytes("すごい😀")).ShouldBeLessThan(0);

        (string X, string Y, int Sign)[] cases = [
            (string.Empty, string.Empty, 0),
            (string.Empty, "a", -1),
            ("abc", "abc", 0),
            ("abc", "abd", -1),
            ("ab", "abc", -1),
            ("\uD83D", "😀", -1), // A high surrogate as a code point is below every supplementary code point.
            ("\uD83Dx", "😀", -1),
            ("\uD83D￿", "😀", -1),
            ("😀", "😁", -1),
            ("😀a", "😀b", -1),
            ("\uDC00", "", -1),
            ("\uDC00", "😀", -1),
            ("￿", "𐀀", -1),
            ("\uD800\uD800", "𐀀", -1), // Lone high, lone high against U+10000.
            ("\uD800\uDBFF", "𐀀", -1),
        ];

        foreach ((string x, string y, int sign) in cases)
        {
            Math.Sign(StenoEncoding.CompareCodePoints(x, y)).ShouldBe(sign, $"{TestData.Describe(x)} against {TestData.Describe(y)}");
            Math.Sign(StenoEncoding.CompareCodePoints(y, x)).ShouldBe(-sign);
            Math.Sign(StenoEncoding.Instance.GetBytes(x).AsSpan().SequenceCompareTo(StenoEncoding.Instance.GetBytes(y))).ShouldBe(sign);
        }
    }

    [TestMethod]
    public void CodePointComparer_HandlesNullLikeOrdinal()
    {
        var comparer = StenoEncoding.CodePointComparer;

        foreach (string? x in (string?[])[null, string.Empty, "a", "😀"])
        {
            foreach (string? y in (string?[])[null, string.Empty, "a", "😀"])
            {
                if (x is null || y is null)
                    Math.Sign(comparer.Compare(x, y)).ShouldBe(Math.Sign(StringComparer.Ordinal.Compare(x, y)));

                comparer.Equals(x, y).ShouldBe(StringComparer.Ordinal.Equals(x, y));
            }
        }

        comparer.GetHashCode("abc").ShouldBe(comparer.GetHashCode(new string("abc".AsSpan())));
        Should.Throw<ArgumentNullException>(() => comparer.GetHashCode(null!));

        string[] sorted = [.. new[] { "すごい😀", null, "すごい！", "a", string.Empty }.Order(comparer)!];
        sorted.ShouldBe([null!, string.Empty, "a", "すごい！", "すごい😀"]);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void PrefixOfText_HasPrefixOfBytes(int seed)
    {
        var random = new Random(seed);

        for (int i = 0; i < 400; i++)
        {
            string text = RandomText.NextMixed(random, maxLength: 60);
            byte[] bytes = StenoEncoding.Instance.GetBytes(text);

            for (int length = 0; length <= text.Length; length++)
            {
                if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length]))
                    continue;

                bytes.AsSpan().StartsWith(StenoEncoding.Instance.GetBytes(text.AsSpan(0, length))).ShouldBeTrue(TestData.Describe(text));
            }
        }
    }

    [TestMethod]
    public void NoZeroByte()
    {
        var random = new Random(3);

        for (int i = 0; i < 5000; i++)
        {
            byte[] bytes = StenoEncoding.Instance.GetBytes(RandomText.NextMixed(random, maxLength: 60));
            bytes.AsSpan().IndexOf((byte)0).ShouldBe(-1);
        }

        // Every code unit, alone and after each of a few others.
        var all = new StringBuilder();

        for (int c = 0; c <= 0xFFFF; c++)
            all.Append((char)c).Append("aЖ中가"[c % 4]);

        StenoEncoding.Instance.GetBytes(all.ToString()).AsSpan().IndexOf((byte)0).ShouldBe(-1);
    }

    [TestMethod]
    public void IsAsciiText_MatchesPlainDefinition()
    {
        var random = new Random(4);
        char[] ascii = [.. Enumerable.Range(0, 0x80).Select(c => (char)c)];

        for (int i = 0; i < 3000; i++)
        {
            string text;

            if (i % 3 == 0)
            {
                // Printable ASCII of every length up to a few blocks, with a single other char sometimes.
                char[] chars = new char[random.Next(0, 70)];

                for (int j = 0; j < chars.Length; j++)
                    chars[j] = (char)random.Next(0x20, 0x7F);

                if (chars.Length > 0 && random.Next(2) == 0)
                    chars[random.Next(chars.Length)] = random.Next(2) == 0 ? ascii[random.Next(ascii.Length)] : (char)random.Next(0x80, 0x3000);

                text = new string(chars);
            }
            else
            {
                text = RandomText.NextMixed(random, maxLength: 40);
            }

            byte[] bytes = StenoEncoding.Instance.GetBytes(text);
            StenoEncoding.IsAsciiText(bytes).ShouldBe(IsPrintableAscii(text), TestData.Describe(text));
        }

        // On any bytes, valid or not: true exactly when they decode to printable ASCII.
        for (int i = 0; i < 3000; i++)
        {
            byte[] bytes = new byte[random.Next(0, 40)];

            for (int j = 0; j < bytes.Length; j++)
                bytes[j] = (byte)(random.Next(8) == 0 ? random.Next(256) : random.Next(1, 0x70));

            (string? text, _) = TestData.LibraryDecode(bytes);
            StenoEncoding.IsAsciiText(bytes).ShouldBe(text is not null && IsPrintableAscii(text), TestData.Hex(bytes));
        }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void Utf8Conversions_MatchGoingThroughAString(int seed)
    {
        var random = new Random(seed);

        for (int i = 0; i < 1500; i++)
        {
            string text = (i % 4) switch {
                0 => new string('x', random.Next(0, 80)) + (random.Next(2) == 0 ? "\r\n\t~" : string.Empty),
                1 => RandomText.NextMixed(random, maxLength: 2000), // Longer than the slices that the conversions go through.
                _ => RandomText.NextMixed(random, maxLength: 40),
            };

            byte[] steno = StenoEncoding.Instance.GetBytes(text);
            byte[]? utf8 = TryGetStrictUtf8(text);

            if (utf8 is null)
            {
                // A lone surrogate has no UTF-8 form.
                Should.Throw<EncoderFallbackException>(() => StenoEncoding.GetUtf8Bytes(steno));
                Should.Throw<EncoderFallbackException>(() => StenoEncoding.GetUtf8ByteCount(steno));
                Should.Throw<EncoderFallbackException>(() => StenoEncoding.GetUtf8Bytes(steno, new byte[text.Length * 3]));
                continue;
            }

            StenoEncoding.GetUtf8ByteCount(steno).ShouldBe(utf8.Length);
            TestData.Hex(StenoEncoding.GetUtf8Bytes(steno)).ShouldBe(TestData.Hex(utf8), TestData.Describe(text));

            StenoEncoding.GetByteCountFromUtf8(utf8).ShouldBe(steno.Length);
            TestData.Hex(StenoEncoding.GetBytesFromUtf8(utf8)).ShouldBe(TestData.Hex(steno), TestData.Describe(text));
        }
    }

    [TestMethod]
    public void Utf8Conversions_RejectInvalidInput()
    {
        var random = new Random(9);
        var strict = new UTF8Encoding(false, true);

        // Invalid UTF-8 is rejected, never replaced.
        byte[][] invalidUtf8 = [[0x80], [0xC0, 0x80], [0xED, 0xA0, 0x80], [0xF4, 0x90, 0x80, 0x80], [0x61, 0xE2, 0x82], [0xFF], [0x61, 0xF0, 0x9F, 0x98]];

        foreach (byte[] utf8 in invalidUtf8)
        {
            Should.Throw<DecoderFallbackException>(() => StenoEncoding.GetBytesFromUtf8(utf8));
            Should.Throw<DecoderFallbackException>(() => StenoEncoding.GetByteCountFromUtf8(utf8));
            Should.Throw<DecoderFallbackException>(() => StenoEncoding.GetBytesFromUtf8(utf8, new byte[64]));
        }

        for (int i = 0; i < 2000; i++)
        {
            byte[] utf8 = new byte[random.Next(0, i % 10 == 0 ? 1500 : 20)];

            for (int j = 0; j < utf8.Length; j++)
                utf8[j] = (byte)(random.Next(3) == 0 ? random.Next(0x80, 0x100) : random.Next(0x20, 0x7F));

            string? text;

            try
            {
                text = strict.GetString(utf8);
            }
            catch (DecoderFallbackException)
            {
                text = null;
            }

            if (text is null)
                Should.Throw<DecoderFallbackException>(() => StenoEncoding.GetBytesFromUtf8(utf8));
            else
                TestData.Hex(StenoEncoding.GetBytesFromUtf8(utf8)).ShouldBe(TestData.Hex(StenoEncoding.Instance.GetBytes(text)));
        }

        // Invalid Steno is rejected for its reason. (The vectors are shorter than the slices that the conversion decodes before it writes UTF-8, so a
        // surrogate decoded before the defect does not stop it first.)
        foreach (var vector in TestData.Vectors.Where(v => v.Kind is "invalid"))
        {
            var ex = Should.Throw<DecoderFallbackException>(() => StenoEncoding.GetUtf8Bytes(vector.Bytes), vector.Name);
            TestData.ReasonOf(ex).ShouldBe(vector.Reason, vector.Name);
        }
    }

    // Pairs of texts that share a prefix often and then differ, from small alphabets that have the cases that order depends on.
    private static (string X, string Y) NextPair(Random random)
    {
        string[] alphabets = [
            "ab￿！😀𐀀\U0010FFFF\uD800􏰀\uDFFF",
            "aZ~\t\n\r\u0000\u007FéЖ中가あア",
        ];

        string Next(int length)
        {
            string alphabet = alphabets[random.Next(alphabets.Length)];
            var text = new StringBuilder();

            for (int i = 0; i < length; i++)
                text.Append(alphabet[random.Next(alphabet.Length)]);

            return text.ToString();
        }

        string prefix = random.Next(3) == 0 ? RandomText.NextMixed(random, maxLength: 20) : Next(random.Next(0, 6));
        return (prefix + Next(random.Next(0, 4)), prefix + Next(random.Next(0, 4)));
    }

    // Code point order from the definition: the code points of the texts, compared one by one.
    private static int CompareByCodePoints(string x, string y)
    {
        var cx = TestData.ToCodePoints(x);
        var cy = TestData.ToCodePoints(y);

        for (int i = 0; i < Math.Min(cx.Count, cy.Count); i++)
        {
            if (cx[i] != cy[i])
                return cx[i].CompareTo(cy[i]);
        }

        return cx.Count.CompareTo(cy.Count);
    }

    // Where the code points of the texts first differ, one is supplementary and the other in U+E000..U+FFFF.
    private static bool IsSupplementaryAgainstHighBmp(string x, string y)
    {
        var cx = TestData.ToCodePoints(x);
        var cy = TestData.ToCodePoints(y);
        int i = 0;

        while (i < cx.Count && i < cy.Count && cx[i] == cy[i])
            i++;

        if (i == cx.Count || i == cy.Count)
            return false;

        return (cx[i] >= 0x10000 && cy[i] is >= 0xE000 and <= 0xFFFF) || (cy[i] >= 0x10000 && cx[i] is >= 0xE000 and <= 0xFFFF);
    }

    // The documented cases where ordinal order differs, from the definition: where the code points first differ, a supplementary one against
    // U+E000..U+FFFF, against an unpaired surrogate above its high surrogate, or against an unpaired surrogate equal to its high surrogate that is
    // followed by a code unit in U+E000..U+FFFF.
    private static bool OrdinalDiffers(string x, string y)
    {
        var cx = TestData.ToCodePoints(x);
        var cy = TestData.ToCodePoints(y);
        int i = 0;

        while (i < cx.Count && i < cy.Count && cx[i] == cy[i])
            i++;

        if (i == cx.Count || i == cy.Count)
            return false;

        if (cx[i] < 0x10000 && cy[i] >= 0x10000)
            (cx, cy) = (cy, cx);
        else if (!(cx[i] >= 0x10000 && cy[i] < 0x10000))
            return false;

        int high = 0xD800 + ((cx[i] - 0x10000) >> 10);
        int other = cy[i];

        if (other >= 0xE000 || (other is >= 0xD800 and <= 0xDFFF && other > high))
            return true;

        return other == high && i + 1 < cy.Count && cy[i + 1] is >= 0xE000 and <= 0xFFFF;
    }

    private static bool HasLoneSurrogate(string text) => TestData.ToCodePoints(text).Any(c => c is >= 0xD800 and <= 0xDFFF);

    private static bool IsPrintableAscii(string text) => text.All(c => c is '\t' or '\n' or '\r' or (>= ' ' and <= '~'));

    private static byte[]? TryGetStrictUtf8(string text)
    {
        try
        {
            return new UTF8Encoding(false, true).GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            return null;
        }
    }
}
