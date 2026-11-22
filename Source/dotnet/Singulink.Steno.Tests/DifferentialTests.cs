using PrefixClassName.MsTest;
using Shouldly;

namespace Singulink.Steno.Tests;

/// <summary>
/// The library against the reference codec: the bytes of text, the text of bytes, and the rejection of garbage.
/// </summary>
[PrefixTestClass]
public sealed class DifferentialTests
{
    // The reference codec builds a layout for every code point, so it gets the start of each long file; the library gets all of every file.
    private const int ReferenceCorpusChars = 3000;

    [TestMethod]
    [DynamicData(nameof(SampleTexts.TestData), typeof(SampleTexts))]
    public void SampleTexts_MatchReference(string name, string text)
    {
        CheckText(text, name);
        byte[] whole = TestData.ReferenceEncode(text);

        // Every prefix, to stop at every place in the text: its bytes are a prefix of the bytes of the whole text (FORMAT.md section 11.2).
        for (int i = 0; i <= text.Length; i++)
        {
            if (i > 0 && i < text.Length && char.IsHighSurrogate(text[i - 1]) && char.IsLowSurrogate(text[i]))
                continue;

            string prefix = text[..i];
            byte[] bytes = StenoEncoding.Instance.GetBytes(prefix);

            whole.AsSpan().StartsWith(bytes).ShouldBeTrue($"{name}, prefix of {i} chars");
            StenoEncoding.Instance.GetString(bytes).ShouldBe(prefix, name);
        }

        // Suffixes, to start at many places in the text.
        for (int i = 1; i < text.Length; i += 13)
        {
            if (!char.IsLowSurrogate(text[i]))
                CheckText(text[i..], name);
        }
    }

    [TestMethod]
    public void Corpus_MatchesReference()
    {
        foreach ((string name, string text) in TestData.Corpus)
        {
            string start = text[..Math.Min(text.Length, ReferenceCorpusChars)];

            if (start.Length > 0 && char.IsHighSurrogate(start[^1]))
                start = start[..^1];

            CheckText(start, name);

            // Each line as a value of its own, as the corpus is used for short fields.
            foreach (string line in start.Split('\n'))
                CheckText(line, name);
        }
    }

    [TestMethod]
    public void Corpus_RoundTripsWhole()
    {
        foreach ((string name, string text) in TestData.Corpus)
        {
            byte[] bytes = StenoEncoding.Instance.GetBytes(text);

            StenoEncoding.Instance.GetString(bytes).ShouldBe(text, name);
            StenoEncoding.Instance.GetByteCount(text).ShouldBe(bytes.Length, name);
            StenoEncoding.Instance.GetCharCount(bytes).ShouldBe(text.Length, name);

            foreach (string line in text.Split('\n'))
                StenoEncoding.Instance.GetString(StenoEncoding.Instance.GetBytes(line)).ShouldBe(line, name);
        }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public void RandomText_MatchesReference(int seed)
    {
        var random = new Random(seed);

        for (int i = 0; i < 1500; i++)
            CheckText(RandomText.NextMixed(random, maxLength: i % 10 == 0 ? 300 : 30), $"seed {seed}, text {i}");
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void RandomText_RoundTripsInLongRuns(int seed)
    {
        // Long single-script runs, which the vectorized paths take, with the reference on the whole.
        var random = new Random(seed);
        string[] runs = ["The quick brown fox, 1234.\r\n", "Déjà vu à côté ", "Привет, мир! ", "Γειά σου κόσμε ", "नमस्ते दुनिया ", "你好世界，", "안녕하세요 세계 ",
            "こんにちは世界、カタカナ。", "😀😂🎉🚀", "\t\n", "ሰላም ዓለም "];

        for (int i = 0; i < 60; i++)
        {
            var text = new System.Text.StringBuilder();

            while (text.Length < 400)
            {
                string run = runs[random.Next(runs.Length)];
                int repeat = random.Next(1, 30);

                for (int j = 0; j < repeat; j++)
                    text.Append(run);
            }

            CheckText(text.ToString(), $"seed {seed}, text {i}");
        }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public void Garbage_IsDecodedLikeReference(int seed)
    {
        var random = new Random(seed);

        for (int i = 0; i < 1500; i++)
        {
            byte[] bytes;

            switch (i % 4)
            {
                case 0:
                    // Random bytes, with the zero byte and the small byte values weighted up.
                    bytes = new byte[random.Next(0, 24)];

                    for (int j = 0; j < bytes.Length; j++)
                        bytes[j] = (byte)(random.Next(4) == 0 ? random.Next(0, 8) : random.Next(256));

                    break;

                case 1:
                    // A valid encoding cut off anywhere.
                    bytes = StenoEncoding.Instance.GetBytes(RandomText.NextMixed(random, maxLength: 30));
                    bytes = bytes[..random.Next(bytes.Length + 1)];
                    break;

                case 2:
                    // A valid encoding with bytes changed.
                    bytes = StenoEncoding.Instance.GetBytes(RandomText.NextMixed(random, maxLength: 30));

                    for (int changes = random.Next(1, 3); changes > 0 && bytes.Length > 0; changes--)
                        bytes[random.Next(bytes.Length)] = (byte)random.Next(256);

                    break;

                default:
                    // Valid encodings put together, so that the second one is read in the state that the first one leaves.
                    bytes = [
                        .. StenoEncoding.Instance.GetBytes(RandomText.NextMixed(random, maxLength: 12)),
                        .. StenoEncoding.Instance.GetBytes(RandomText.NextMixed(random, maxLength: 12)),
                    ];
                    break;
            }

            var expected = TestData.ReferenceDecode(bytes);
            var actual = TestData.LibraryDecode(bytes);

            actual.ShouldBe(expected, $"seed {seed}, case {i}: {TestData.Hex(bytes)}");

            if (expected.Text is not null)
                StenoEncoding.Instance.GetCharCount(bytes).ShouldBe(expected.Text.Length);
        }
    }

    private static void CheckText(string text, string name)
    {
        byte[] expected = TestData.ReferenceEncode(text);
        byte[] actual = StenoEncoding.Instance.GetBytes(text);

        if (!actual.AsSpan().SequenceEqual(expected))
        {
            int index = 0;

            while (index < Math.Min(actual.Length, expected.Length) && actual[index] == expected[index])
                index++;

            throw new ShouldAssertException($"{name}: the bytes differ from the reference at byte {index} of {expected.Length} " +
                $"(library {actual.Length} bytes) for {TestData.Describe(text)}");
        }

        StenoEncoding.Instance.GetByteCount(text).ShouldBe(expected.Length, name);
        StenoEncoding.Instance.GetString(expected).ShouldBe(text, name);
        TestData.ToText(ReferenceSteno.Decode(expected)).ShouldBe(text, name);
    }
}
