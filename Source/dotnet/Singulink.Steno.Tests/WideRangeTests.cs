using System.Text;
using PrefixClassName.MsTest;
using Shouldly;

namespace Singulink.Steno.Tests;

/// <summary>
/// Every char of the wide ranges (the CJK ideographs U+4E00..U+9FFF and the Hangul syllables U+AC00..U+D7A3) through the vectorized wide blocks,
/// against the same char encoded on its own: the division by 248 and the pair arithmetic of the wide blocks must be exact for every one.
/// </summary>
[PrefixTestClass]
public sealed class WideRangeTests
{
    [TestMethod]
    [DataRow(0x4E00, 0x9FFF, "中")]
    [DataRow(0xAC00, 0xD7A3, "가")]
    public void EveryCharOfTheWideRange_InBlocksAndAlone(int first, int last, string context)
    {
        var steno = StenoEncoding.Instance;
        byte[] prefix = steno.GetBytes(context);

        // The bytes of each char on its own after the char that sets the context (a value too short for the wide blocks), without the prefix.
        var alone = new Dictionary<int, byte[]>();

        for (int c = first; c <= last; c++)
            alone[c] = steno.GetBytes(context + (char)c)[prefix.Length..];

        // All of them in one value, in runs long enough for the wide blocks (the context char keeps the state where it is).
        var text = new StringBuilder(context);
        var expected = new List<byte>(prefix);

        for (int c = first; c <= last; c++)
        {
            text.Append((char)c);
            expected.AddRange(alone[c]);
        }

        byte[] bytes = steno.GetBytes(text.ToString());
        bytes.AsSpan().SequenceEqual(expected.ToArray()).ShouldBeTrue("the wide blocks encode some char differently");
        steno.GetString(bytes).ShouldBe(text.ToString());

        // Every char also decodes on its own.
        for (int c = first; c <= last; c++)
            steno.GetString([.. prefix, .. alone[c]]).ShouldBe(context + (char)c);
    }
}
