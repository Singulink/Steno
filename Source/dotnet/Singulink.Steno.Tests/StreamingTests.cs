using System.Text;
using PrefixClassName.MsTest;
using Shouldly;

namespace Singulink.Steno.Tests;

/// <summary>
/// The streaming encoder and decoder: any split of the input into chunks gives the one-shot result.
/// </summary>
[PrefixTestClass]
public sealed class StreamingTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public void RandomChunks_GiveOneShotResult(int seed)
    {
        var random = new Random(seed);
        var encoder = StenoEncoding.Instance.GetEncoder();
        var decoder = StenoEncoding.Instance.GetDecoder();

        for (int i = 0; i < 1500; i++)
        {
            string text = RandomText.NextMixed(random, maxLength: i % 10 == 0 ? 300 : 40);
            byte[] oneShot = StenoEncoding.Instance.GetBytes(text);

            // Chunk sizes from 0 up, some large enough for the vectorized paths.
            int maxChunk = random.Next(3) switch { 0 => 4, 1 => 16, _ => 120 };

            // A flush does not end the value: Reset does.
            encoder.Reset();
            decoder.Reset();

            TestData.Hex(Encode(encoder, text, random, maxChunk)).ShouldBe(TestData.Hex(oneShot), $"seed {seed}, text {i}: {TestData.Describe(text)}");
            Decode(decoder, oneShot, random, maxChunk).ShouldBe(text, $"seed {seed}, text {i}: {TestData.Describe(text)}");
        }
    }

    [TestMethod]
    public void EverySplitInTwo_GivesOneShotResult()
    {
        foreach ((string name, string text) in SampleTexts.All)
        {
            byte[] oneShot = StenoEncoding.Instance.GetBytes(text);
            byte[] bytes = new byte[StenoEncoding.Instance.GetMaxByteCount(text.Length)];
            char[] chars = new char[StenoEncoding.Instance.GetMaxCharCount(oneShot.Length)];

            // Splits between the two code units of a surrogate pair are among them (the emoji texts).
            for (int split = 0; split <= text.Length; split++)
            {
                var encoder = StenoEncoding.Instance.GetEncoder();
                int written = encoder.GetBytes(text.AsSpan(0, split), bytes, flush: false);
                written += encoder.GetBytes(text.AsSpan(split), bytes.AsSpan(written), flush: true);

                TestData.Hex(bytes.AsSpan(0, written)).ShouldBe(TestData.Hex(oneShot), $"{name}, split at char {split}");
            }

            // Splits inside the bytes of a code point are among them.
            for (int split = 0; split <= oneShot.Length; split++)
            {
                var decoder = StenoEncoding.Instance.GetDecoder();
                int written = decoder.GetChars(oneShot.AsSpan(0, split), chars, flush: false);
                written += decoder.GetChars(oneShot.AsSpan(split), chars.AsSpan(written), flush: true);

                new string(chars, 0, written).ShouldBe(text, $"{name}, split at byte {split}");
            }
        }
    }

    [TestMethod]
    public void SurrogatePairSplitAcrossChunks_IsOneCodePoint()
    {
        const string Text = "a😀b𠀀c";
        var encoder = StenoEncoding.Instance.GetEncoder();
        byte[] buffer = new byte[64];
        int written = 0;

        foreach (char c in Text)
            written += encoder.GetBytes([c], buffer.AsSpan(written), flush: false);

        written += encoder.GetBytes([], buffer.AsSpan(written), flush: true);
        TestData.Hex(buffer.AsSpan(0, written)).ShouldBe(TestData.Hex(StenoEncoding.Instance.GetBytes(Text)));
    }

    [TestMethod]
    public void HeldHighSurrogate_IsWrittenOnFlush()
    {
        foreach (string text in (string[])["\uD800", "a\uD83D", "😀\uDBFF", "абв\uD800"])
        {
            var encoder = StenoEncoding.Instance.GetEncoder();
            byte[] buffer = new byte[64];

            int written = encoder.GetBytes(text, buffer, flush: false);
            encoder.GetByteCount([], flush: true).ShouldBe(StenoEncoding.Instance.GetByteCount(text) - written);
            written += encoder.GetBytes([], buffer.AsSpan(written), flush: true);

            TestData.Hex(buffer.AsSpan(0, written)).ShouldBe(TestData.Hex(StenoEncoding.Instance.GetBytes(text)));
        }
    }

    [TestMethod]
    public void HeldHighSurrogate_FollowedByOtherChar_IsLone()
    {
        const string Text = "x\uD800y\uDBFF𐀀";
        var encoder = StenoEncoding.Instance.GetEncoder();
        byte[] buffer = new byte[64];
        int written = 0;

        foreach (char c in Text)
            written += encoder.GetBytes([c], buffer.AsSpan(written), flush: false);

        written += encoder.GetBytes([], buffer.AsSpan(written), flush: true);
        TestData.Hex(buffer.AsSpan(0, written)).ShouldBe(TestData.Hex(TestData.ReferenceEncode(Text)));
    }

    [TestMethod]
    public void FlushOnUnfinishedSequence_Fails()
    {
        // Every proper prefix of the bytes of a multi-byte code point.
        foreach (string text in (string[])["ж", "中", "😀", "￿", "\U0010FFFF", "aЖЖЖ\U0001F600"])
        {
            byte[] bytes = StenoEncoding.Instance.GetBytes(text);
            byte[] last = StenoEncoding.Instance.GetBytes(text.AsSpan(0, text.Length - (char.IsLowSurrogate(text[^1]) ? 2 : 1)));

            for (int cut = last.Length + 1; cut < bytes.Length; cut++)
            {
                var decoder = StenoEncoding.Instance.GetDecoder();
                char[] chars = new char[16];

                decoder.GetChars(bytes.AsSpan(0, cut), chars, flush: false).ShouldBe(StenoEncoding.Instance.GetCharCount(last));

                var ex = Should.Throw<DecoderFallbackException>(() => decoder.GetChars([], chars, flush: true));
                TestData.ReasonOf(ex).ShouldBe("truncated");

                Should.Throw<DecoderFallbackException>(() => StenoEncoding.Instance.GetDecoder().GetChars(bytes.AsSpan(0, cut), chars, flush: true));
                Should.Throw<DecoderFallbackException>(() => StenoEncoding.Instance.GetDecoder().GetCharCount(bytes.AsSpan(0, cut), flush: true));
            }
        }
    }

    [TestMethod]
    public void SurrogatePairRuleHoldsAcrossChunks()
    {
        // A low surrogate code point after a high one is not a value, even when the two are in different chunks. The bytes of such a pair, written
        // with the steps of the reference codec (whose encoder refuses it).
        foreach ((int high, int low, string before) in ((int, int, string)[])[(0xD800, 0xDC00, string.Empty), (0xDBFF, 0xDFFF, "Ж中"), (0xD83D, 0xDE00, "😀")])
        {
            var state = new ReferenceSteno.State();
            var output = new List<byte>();

            foreach (int c in (int[])[.. TestData.ToCodePoints(before), high, low])
            {
                int count = output.Count;
                ReferenceSteno.EncodeOne(ReferenceSteno.BuildLayout(state), c, output);
                ReferenceSteno.Update(state, c, output.Count - count);
            }

            byte[] invalid = [.. output];
            TestData.ReferenceDecode(invalid).Reason.ShouldBe("surrogate-pair");
            TestData.LibraryDecode(invalid).Reason.ShouldBe("surrogate-pair");

            CheckEverySplitIsRejected(invalid, "surrogate-pair");
        }
    }

    private static void CheckEverySplitIsRejected(byte[] invalid, string reason)
    {
        for (int split = 0; split <= invalid.Length; split++)
        {
            var decoder = StenoEncoding.Instance.GetDecoder();
            char[] chars = new char[16];

            var ex = Should.Throw<DecoderFallbackException>(() => {
                int written = decoder.GetChars(invalid.AsSpan(0, split), chars, flush: false);
                decoder.GetChars(invalid.AsSpan(split), chars.AsSpan(written), flush: true);
            });

            TestData.ReasonOf(ex).ShouldBe(reason, $"split at {split}");
        }
    }

    [TestMethod]
    public void Reset_StartsANewValue()
    {
        string text = "Привет 😀 中文";
        byte[] expected = StenoEncoding.Instance.GetBytes(text);

        var encoder = StenoEncoding.Instance.GetEncoder();
        byte[] buffer = new byte[64];
        encoder.GetBytes("Ελληνικά \uD83D", buffer, flush: false);
        encoder.Reset();

        TestData.Hex(buffer.AsSpan(0, encoder.GetBytes(text, buffer, flush: true))).ShouldBe(TestData.Hex(expected));

        var decoder = StenoEncoding.Instance.GetDecoder();
        char[] chars = new char[64];
        byte[] other = StenoEncoding.Instance.GetBytes("Ελληνικά 😀");
        decoder.GetChars(other.AsSpan(0, other.Length - 1), chars, flush: false);
        decoder.Reset();

        new string(chars, 0, decoder.GetChars(expected, chars, flush: true)).ShouldBe(text);
    }

    [TestMethod]
    public void Flush_DoesNotEndTheValue()
    {
        // Flushing after every piece, at any split, gives the bytes of the whole value: a flush only writes a held high surrogate.
        var random = new Random(11);

        foreach ((string name, string text) in SampleTexts.All)
        {
            byte[] expected = StenoEncoding.Instance.GetBytes(text);
            var encoder = StenoEncoding.Instance.GetEncoder();
            var decoder = StenoEncoding.Instance.GetDecoder();
            var bytes = new List<byte>();
            var chars = new StringBuilder();
            byte[] buffer = new byte[StenoEncoding.Instance.GetMaxByteCount(text.Length)];
            char[] charBuffer = new char[StenoEncoding.Instance.GetMaxCharCount(expected.Length)];

            for (int index = 0; index < text.Length;)
            {
                int count = Math.Min(random.Next(1, 30), text.Length - index);

                // Not between the two halves of a pair: that would write the high surrogate as an unpaired one.
                if (index + count < text.Length && char.IsLowSurrogate(text[index + count]))
                    count++;

                bytes.AddRange(buffer.AsSpan(0, encoder.GetBytes(text.AsSpan(index, count), buffer, flush: true)));
                index += count;
            }

            TestData.Hex([.. bytes]).ShouldBe(TestData.Hex(expected), name);

            for (int index = 0; index < expected.Length;)
            {
                int count = Math.Min(random.Next(1, 30), expected.Length - index);

                try
                {
                    chars.Append(charBuffer, 0, decoder.GetChars(expected.AsSpan(index, count), charBuffer, flush: true));
                }
                catch (DecoderFallbackException ex) when (TestData.ReasonOf(ex) is "truncated")
                {
                    // The piece ends in the middle of a code point: a flush says the input ends there, so that is an error, and the decoder is
                    // as it was. Give it the piece again without the flush.
                    chars.Append(charBuffer, 0, decoder.GetChars(expected.AsSpan(index, count), charBuffer, flush: false));
                }

                index += count;
            }

            chars.ToString().ShouldBe(text, name);
        }
    }

    [TestMethod]
    public void Flush_BetweenTheHalvesOfAPair_WritesTheHighSurrogateAlone()
    {
        var encoder = StenoEncoding.Instance.GetEncoder();
        byte[] buffer = new byte[16];

        int written = encoder.GetBytes("a\uD83D", buffer, flush: true);
        TestData.Hex(buffer.AsSpan(0, written)).ShouldBe(TestData.Hex(StenoEncoding.Instance.GetBytes("a\uD83D")));

        // The low surrogate after it would make the two code points of a pair, which is not a value.
        Should.Throw<EncoderFallbackException>(() => encoder.GetBytes("\uDE00", buffer, flush: true));
        Should.Throw<EncoderFallbackException>(() => encoder.GetByteCount("\uDE00", flush: true));

        // Anything else goes on with the value.
        written += encoder.GetBytes("b", buffer.AsSpan(written), flush: true);
        TestData.Hex(buffer.AsSpan(0, written)).ShouldBe(TestData.Hex(StenoEncoding.Instance.GetBytes("a\uD83Db")));

        encoder.Reset();
        TestData.Hex(buffer.AsSpan(0, encoder.GetBytes("\uDE00", buffer, flush: true))).ShouldBe(TestData.Hex(StenoEncoding.Instance.GetBytes("\uDE00")));
    }

    [TestMethod]
    public void FailedCall_LeavesStateAsItWas()
    {
        string text = "Ελληνικά και 中文 😀";
        byte[] expected = StenoEncoding.Instance.GetBytes(text);

        var encoder = StenoEncoding.Instance.GetEncoder();
        byte[] buffer = new byte[64];
        int written = encoder.GetBytes(text.AsSpan(0, 9), buffer, flush: false);

        Should.Throw<ArgumentException>(() => encoder.GetBytes(text.AsSpan(9), new byte[1], flush: true));
        written += encoder.GetBytes(text.AsSpan(9), buffer.AsSpan(written), flush: true);
        TestData.Hex(buffer.AsSpan(0, written)).ShouldBe(TestData.Hex(expected));

        var decoder = StenoEncoding.Instance.GetDecoder();
        char[] chars = new char[64];
        int charCount = decoder.GetChars(expected.AsSpan(0, 5), chars, flush: false);

        Should.Throw<ArgumentException>(() => decoder.GetChars(expected.AsSpan(5), new char[1], flush: true));
        Should.Throw<DecoderFallbackException>(() => decoder.GetChars([0], chars, flush: true));
        charCount += decoder.GetChars(expected.AsSpan(5), chars.AsSpan(charCount), flush: true);
        new string(chars, 0, charCount).ShouldBe(text);
    }

    [TestMethod]
    public void Convert_GivesOneShotResult()
    {
        var random = new Random(5);

        for (int i = 0; i < 300; i++)
        {
            string text = RandomText.NextMixed(random, maxLength: 100);
            byte[] expected = StenoEncoding.Instance.GetBytes(text);

            var encoder = StenoEncoding.Instance.GetEncoder();
            var bytes = new List<byte>();

            // Convert needs room for the bytes of one char at least, which with a high surrogate held over is GetMaxByteCount(1).
            byte[] buffer = new byte[random.Next(StenoEncoding.Instance.GetMaxByteCount(1), 20)];
            var rest = text.AsSpan();

            while (true)
            {
                encoder.Convert(rest, buffer, flush: true, out int charsUsed, out int bytesUsed, out bool completed);
                bytes.AddRange(buffer.AsSpan(0, bytesUsed));
                rest = rest[charsUsed..];

                if (completed)
                    break;
            }

            TestData.Hex([.. bytes]).ShouldBe(TestData.Hex(expected), TestData.Describe(text));

            var decoder = StenoEncoding.Instance.GetDecoder();
            var chars = new StringBuilder();
            char[] charBuffer = new char[random.Next(StenoEncoding.Instance.GetMaxCharCount(1), 20)];
            var restBytes = expected.AsSpan();

            while (true)
            {
                decoder.Convert(restBytes, charBuffer, flush: true, out int bytesUsed, out int charsUsed, out bool completed);
                chars.Append(charBuffer, 0, charsUsed);
                restBytes = restBytes[bytesUsed..];

                if (completed)
                    break;
            }

            chars.ToString().ShouldBe(text, TestData.Describe(text));
        }
    }

    [TestMethod]
    public void Streams_RoundTrip()
    {
        foreach ((string name, string text) in SampleTexts.All)
        {
            using var stream = new MemoryStream();

            using (var writer = new StreamWriter(stream, StenoEncoding.Instance, bufferSize: 64, leaveOpen: true))
            {
                foreach (string part in text.Chunk(37).Select(c => new string(c)))
                    writer.Write(part);
            }

            TestData.Hex(stream.ToArray()).ShouldBe(TestData.Hex(StenoEncoding.Instance.GetBytes(text)), name);
            stream.Position = 0;

            using var reader = new StreamReader(stream, StenoEncoding.Instance, detectEncodingFromByteOrderMarks: false, bufferSize: 64);
            reader.ReadToEnd().ShouldBe(text, name);
        }
    }

    // Encodes in random chunks, checking the count of each chunk first (which must not change the encoder) and the bound of the chunk.
    private static byte[] Encode(System.Text.Encoder encoder, string text, Random random, int maxChunk)
    {
        var bytes = new List<byte>();
        byte[] buffer = new byte[StenoEncoding.Instance.GetMaxByteCount(maxChunk)];

        for (int index = 0; ;)
        {
            int count = Math.Min(random.Next(0, maxChunk + 1), text.Length - index);
            bool flush = index + count == text.Length && random.Next(4) != 0;
            var chunk = text.AsSpan(index, count);

            int expected = encoder.GetByteCount(chunk, flush);
            int written = encoder.GetBytes(chunk, buffer, flush);

            written.ShouldBe(expected);
            written.ShouldBeLessThanOrEqualTo(StenoEncoding.Instance.GetMaxByteCount(count));

            bytes.AddRange(buffer.AsSpan(0, written));
            index += count;

            if (flush)
                return [.. bytes];
        }
    }

    private static string Decode(System.Text.Decoder decoder, byte[] bytes, Random random, int maxChunk)
    {
        var text = new StringBuilder();
        char[] buffer = new char[StenoEncoding.Instance.GetMaxCharCount(maxChunk)];

        for (int index = 0; ;)
        {
            int count = Math.Min(random.Next(0, maxChunk + 1), bytes.Length - index);
            bool flush = index + count == bytes.Length && random.Next(4) != 0;
            var chunk = bytes.AsSpan(index, count);

            int expected = decoder.GetCharCount(chunk, flush);
            int written = decoder.GetChars(chunk, buffer, flush);

            written.ShouldBe(expected);
            written.ShouldBeLessThanOrEqualTo(StenoEncoding.Instance.GetMaxCharCount(count));

            text.Append(buffer, 0, written);
            index += count;

            if (flush)
                return text.ToString();
        }
    }
}
