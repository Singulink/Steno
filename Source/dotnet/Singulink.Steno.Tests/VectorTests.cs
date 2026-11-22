using System.Text;
using PrefixClassName.MsTest;
using Shouldly;

namespace Singulink.Steno.Tests;

/// <summary>
/// The vectors of TestVectors/steno-v1-vectors.txt (FORMAT.md section 12), against the library and against the reference codec.
/// </summary>
[PrefixTestClass]
public sealed class VectorTests
{
    private static readonly string[] Reasons = ["zero-byte", "unused-lead", "truncated", "past-range", "surrogate-pair", "invalid-trail"];

    [TestMethod]
    public void FileHasTheVectorsItSays()
    {
        TestData.Vectors.Count(v => v.Kind is "valid").ShouldBe(6798);
        TestData.Vectors.Count(v => v.Kind is "invalid").ShouldBe(829);
        TestData.Vectors.Count(v => v.Kind is "not-a-value").ShouldBe(13);

        TestData.Vectors.Where(v => v.Kind is "invalid").Select(v => v.Reason).Distinct().Order().ShouldBe(Reasons.Order());
    }

    [TestMethod]
    public void ValidVectorsEncodeExactly()
    {
        foreach (var vector in TestData.Vectors.Where(v => v.Kind is "valid"))
        {
            string text = TestData.ToText(vector.CodePoints);
            byte[] bytes = StenoEncoding.Instance.GetBytes(text);

            TestData.Hex(bytes).ShouldBe(TestData.Hex(vector.Bytes), vector.Name);
            StenoEncoding.Instance.GetByteCount(text).ShouldBe(vector.Bytes.Length, vector.Name);
        }
    }

    [TestMethod]
    public void ValidVectorsDecodeExactly()
    {
        foreach (var vector in TestData.Vectors.Where(v => v.Kind is "valid"))
        {
            string text = StenoEncoding.Instance.GetString(vector.Bytes);

            TestData.ToCodePoints(text).ShouldBe(vector.CodePoints, vector.Name);
            StenoEncoding.Instance.GetCharCount(vector.Bytes).ShouldBe(text.Length, vector.Name);
        }
    }

    [TestMethod]
    public void ValidVectorsStreamOneUnitAtATime()
    {
        var encoder = StenoEncoding.Instance.GetEncoder();
        var decoder = StenoEncoding.Instance.GetDecoder();
        byte[] byteBuffer = new byte[8];
        char[] charBuffer = new char[8];

        foreach (var vector in TestData.Vectors.Where(v => v.Kind is "valid"))
        {
            encoder.Reset();
            decoder.Reset();

            string text = TestData.ToText(vector.CodePoints);
            var bytes = new List<byte>();

            for (int i = 0; i < text.Length; i++)
            {
                int written = encoder.GetBytes(text.AsSpan(i, 1), byteBuffer, flush: false);
                bytes.AddRange(byteBuffer.AsSpan(0, written));
            }

            bytes.AddRange(byteBuffer.AsSpan(0, encoder.GetBytes([], byteBuffer, flush: true)));
            TestData.Hex([.. bytes]).ShouldBe(TestData.Hex(vector.Bytes), vector.Name);

            var chars = new StringBuilder();

            for (int i = 0; i < vector.Bytes.Length; i++)
                chars.Append(charBuffer, 0, decoder.GetChars(vector.Bytes.AsSpan(i, 1), charBuffer, flush: false));

            chars.Append(charBuffer, 0, decoder.GetChars([], charBuffer, flush: true));
            chars.ToString().ShouldBe(text, vector.Name);
        }
    }

    [TestMethod]
    public void InvalidVectorsAreRejectedForTheirReason()
    {
        foreach (var vector in TestData.Vectors.Where(v => v.Kind is "invalid"))
        {
            var ex = Should.Throw<DecoderFallbackException>(() => StenoEncoding.Instance.GetString(vector.Bytes), vector.Name);
            TestData.ReasonOf(ex).ShouldBe(vector.Reason, $"{vector.Name}: {ex.Message}");

            ex = Should.Throw<DecoderFallbackException>(() => StenoEncoding.Instance.GetCharCount(vector.Bytes), vector.Name);
            TestData.ReasonOf(ex).ShouldBe(vector.Reason, vector.Name);

            // As with UTF8Encoding, Try only means that the destination may be too small: invalid data still throws.
            ex = Should.Throw<DecoderFallbackException>(() => StenoEncoding.Instance.TryGetChars(vector.Bytes, new char[1000], out _), vector.Name);
            TestData.ReasonOf(ex).ShouldBe(vector.Reason, vector.Name);
        }
    }

    [TestMethod]
    public void InvalidVectorsAreRejectedForTheirReasonWhenStreamed()
    {
        char[] buffer = new char[1000];

        foreach (var vector in TestData.Vectors.Where(v => v.Kind is "invalid"))
        {
            var decoder = StenoEncoding.Instance.GetDecoder();

            var ex = Should.Throw<DecoderFallbackException>(
                () => {
                    for (int i = 0; i < vector.Bytes.Length; i++)
                        decoder.GetChars(vector.Bytes.AsSpan(i, 1), buffer, flush: false);

                    decoder.GetChars([], buffer, flush: true);
                },
                vector.Name);

            TestData.ReasonOf(ex).ShouldBe(vector.Reason, $"{vector.Name}: {ex.Message}");
        }
    }

    [TestMethod]
    public void NotAValueVectorsAreRejectedByTheReference()
    {
        foreach (var vector in TestData.Vectors.Where(v => v.Kind is "not-a-value"))
        {
            Should.Throw<ArgumentException>(() => ReferenceSteno.Encode(vector.CodePoints), vector.Name);

            // As UTF-16, a high surrogate code unit followed by a low one is one supplementary code point: the library encodes it as that.
            string text = string.Concat(vector.CodePoints.Select(c => (char)c));
            TestData.Hex(StenoEncoding.Instance.GetBytes(text)).ShouldBe(TestData.Hex(TestData.ReferenceEncode(text)), vector.Name);
        }
    }

    [TestMethod]
    public void ReferenceAgreesWithTheVectors()
    {
        foreach (var vector in TestData.Vectors)
        {
            switch (vector.Kind)
            {
                case "valid":
                    ReferenceSteno.Hex(ReferenceSteno.Encode(vector.CodePoints)).ShouldBe(ReferenceSteno.Hex(vector.Bytes), vector.Name);
                    ReferenceSteno.Decode(vector.Bytes).ShouldBe(vector.CodePoints, vector.Name);
                    break;

                case "invalid":
                    TestData.ReferenceDecode(vector.Bytes).Reason.ShouldBe(vector.Reason, vector.Name);
                    break;
            }
        }
    }
}
