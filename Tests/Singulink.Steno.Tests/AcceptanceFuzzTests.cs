using System.Text;
using PrefixClassName.MsTest;
using Shouldly;

namespace Singulink.Steno.Tests;

/// <summary>
/// What the decoder accepts, against the fast oracle: every byte string must decode to the text whose encoding it is, or be rejected for the reason
/// that the oracle gives.
/// </summary>
[PrefixTestClass]
public sealed class AcceptanceFuzzTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Every byte string of 1 and 2 bytes, and every one of 3 bytes after a sample of the lead bytes that start long sequences in the initial layout
    /// (all of the sample in a long run).
    /// </summary>
    [TestMethod]
    public void EveryShortByteString()
    {
        for (int a = 0; a < 256; a++)
            Fuzz.CheckDecoding([(byte)a], "1 byte");

        for (int a = 0; a < 256; a++)
        {
            for (int b = 0; b < 256; b++)
                Fuzz.CheckDecoding([(byte)a, (byte)b], "2 bytes");
        }

        int[] leads = Fuzz.IsLong ? [0x01, 0x66, 0xE7, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0xFA, 0x07] : [0x01, 0xF7, 0xF9];

        foreach (int a in leads)
        {
            for (int b = 0; b < 256; b++)
            {
                for (int c = 0; c < 256; c++)
                    Fuzz.CheckDecoding([(byte)a, (byte)b, (byte)c], "3 bytes");
            }
        }
    }

    /// <summary>
    /// Valid encodings with a byte changed, inserted or deleted, the end cut, or another encoding spliced in; also decoded in two pieces.
    /// </summary>
    [TestMethod]
    [DataRow(1201)]
    [DataRow(1202)]
    public void MutatedEncodings(int seed)
    {
        var random = new Random(seed);
        long accepted = 0;

        long count = Fuzz.Run(i => {
            string text = KindText.Runs(random, random.Next(2) == 0 ? 30 : 300);
            var bytes = new List<byte>(FastReference.Encode(text));

            for (int m = random.Next(1, 4); m > 0; m--)
            {
                int at = bytes.Count == 0 ? 0 : random.Next(bytes.Count);

                switch (random.Next(7))
                {
                    case 0 when bytes.Count > 0:
                        bytes[at] = (byte)random.Next(256);
                        break;
                    case 1 when bytes.Count > 0:
                        bytes[at] = (byte)(bytes[at] + (random.Next(2) == 0 ? 1 : -1));
                        break;
                    case 2:
                        bytes.Insert(at, (byte)random.Next(256));
                        break;
                    case 3 when bytes.Count > 0:
                        bytes.RemoveAt(at);
                        break;
                    case 4 when bytes.Count > 0:
                        bytes.RemoveRange(at, bytes.Count - at);
                        break;
                    case 5:
                        byte[] other = FastReference.Encode(KindText.Runs(random, 30));
                        bytes.InsertRange(at, other.AsSpan(0, random.Next(other.Length + 1)).ToArray());
                        break;
                    default:
                        if (bytes.Count > 0)
                            bytes[at] = (byte)random.Next(1, 256);

                        break;
                }
            }

            byte[] input = [.. bytes];
            Fuzz.CheckDecoding(input, $"seed {seed}, text {i}");

            // In two pieces, the verdict is the same.
            int cut = random.Next(input.Length + 1);
            var decoder = StenoEncoding.Instance.GetDecoder();
            bool valid = FastReference.Decode(input).Text is not null;
            bool streamed;

            try
            {
                char[] chars = new char[(input.Length * 2) + 4];
                int written = decoder.GetChars(input.AsSpan(0, cut), chars, false);
                decoder.GetChars(input.AsSpan(cut), chars.AsSpan(written), true);
                streamed = true;
            }
            catch (DecoderFallbackException)
            {
                streamed = false;
            }

            streamed.ShouldBe(valid, $"{Fuzz.Hex(input)} cut at {cut}");

            if (valid)
                accepted++;
        });

        TestContext.WriteLine($"{count} mutated encodings, {accepted} of them accepted");
    }

    /// <summary>
    /// Byte strings after prefixes that set the wide contexts, biased to the bytes of the wide blocks (the CJK and Hangul pair decoder), with damage.
    /// </summary>
    [TestMethod]
    public void RandomBytesAfterWidePrefix()
    {
        var random = new Random(1301);
        string[] prefixes = [string.Empty, "一", "가", "一あ", "一ア", "😀😀", "ЖЖ", "一Ł"];

        long count = Fuzz.Run(i => {
            byte[] prefix = FastReference.Encode(prefixes[random.Next(prefixes.Length)] + KindText.Runs(random, 60));
            var tail = new List<byte>(FastReference.Encode(prefixes[random.Next(prefixes.Length)] + KindText.Runs(random, 100)));

            for (int damage = random.Next(4); damage > 0 && tail.Count > 0; damage--)
                tail[random.Next(tail.Count)] = (byte)random.Next(256);

            Fuzz.CheckDecoding([.. prefix, .. tail], $"byte string {i}");
        });

        TestContext.WriteLine($"{count} byte strings");
    }
}
