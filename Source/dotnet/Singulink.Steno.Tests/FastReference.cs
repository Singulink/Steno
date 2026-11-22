using System.Collections.Concurrent;
using System.Text;

namespace Singulink.Steno.Tests;

/// <summary>
/// A fast oracle for the fuzz tests: the reference codec's own <see cref="ReferenceSteno.BuildLayout"/> and <see cref="ReferenceSteno.Update"/>
/// (nothing from the library), with the layouts memoized by the fields that FORMAT.md section 5 says the layout depends on, so that megabytes of text
/// can be checked.
/// </summary>
internal static class FastReference
{
    // The digits of FORMAT.md section 8.2: base 248, a trail byte is a digit plus 8.
    private const int Radix = 248;
    private const int TrailBias = 8;

    // Bounded: random window pairs reach millions of states (an unbounded cache took 4 GB in a long run).
    private const int MaxLayouts = 2000;

    private static readonly ConcurrentDictionary<(ReferenceSteno.Context, ReferenceSteno.Slot, int, int), FastLayout> _layouts = new();

    /// <summary>
    /// Encodes the code points of the text from the initial state.
    /// </summary>
    public static byte[] Encode(string text)
    {
        var state = new ReferenceSteno.State();
        var output = new List<byte>(text.Length * 2);

        foreach (int c in TestData.ToCodePoints(text))
            EncodeOne(state, c, output);

        return [.. output];
    }

    /// <summary>
    /// Encodes one code point in the state and updates the state. Returns the number of bytes.
    /// </summary>
    public static int EncodeOne(ReferenceSteno.State state, int c, List<byte>? output)
    {
        var layout = Get(state);
        var range = layout.Ranges[Find(layout, c)];
        long offset = c - range.Lo;
        int size = range.Size;
        long per = 1;

        for (int i = 1; i < size; i++)
            per *= Radix;

        if (output is not null)
        {
            long q = offset / per;
            long rest = offset - (q * per);
            output.Add(checked((byte)(range.FirstLead + q)));

            for (long p = per / Radix; p >= 1; p /= Radix)
            {
                long t = rest / p;
                rest -= t * p;
                output.Add(checked((byte)(t + TrailBias)));
            }
        }

        ReferenceSteno.Update(state, c, size);
        return size;
    }

    /// <summary>
    /// Decodes bytes from the initial state: the text, or the reason of the rejection.
    /// </summary>
    public static (string? Text, string? Reason) Decode(ReadOnlySpan<byte> bytes)
    {
        var state = new ReferenceSteno.State();
        var result = new StringBuilder(bytes.Length);
        int position = 0;
        int previous = -1;

        while (position < bytes.Length)
        {
            var layout = Get(state);
            byte lead = bytes[position];

            if (lead is 0)
                return (null, "zero-byte");

            int index = layout.RangeOfLead[lead];

            if (index < 0)
                return (null, "unused-lead");

            var range = layout.Ranges[index];
            int start = position++;
            long offset = lead - range.FirstLead;

            for (int i = 0; i < range.Size - 1; i++)
            {
                if (position >= bytes.Length)
                    return (null, "truncated");

                byte trail = bytes[position++];

                if (trail is 0)
                    return (null, "zero-byte");

                if (trail < TrailBias)
                    return (null, "invalid-trail");

                offset = (offset * Radix) + (trail - TrailBias);
            }

            long c = range.Lo + offset;

            if (c > range.Hi)
                return (null, "past-range");

            int codePoint = (int)c;

            if (previous is >= 0xD800 and <= 0xDBFF && codePoint is >= 0xDC00 and <= 0xDFFF)
                return (null, "surrogate-pair");

            if (codePoint < 0x10000)
                result.Append((char)codePoint);
            else
                result.Append(char.ConvertFromUtf32(codePoint));

            previous = codePoint;
            ReferenceSteno.Update(state, codePoint, position - start);
        }

        return (result.ToString(), null);
    }

    private static FastLayout Get(ReferenceSteno.State state)
    {
        var slot = state.Context == ReferenceSteno.Context.Cjk ? state.Slot : ReferenceSteno.Slot.Letters;
        var key = (state.Context, slot, state.Main.Start, state.Second?.Start ?? -1);

        // A hit first: the count of a ConcurrentDictionary takes all its locks, which made every lookup slow.
        if (_layouts.TryGetValue(key, out var known))
            return known;

        if (_layouts.Count > MaxLayouts)
            _layouts.Clear();

        return _layouts.GetOrAdd(key, _ => {
            var ranges = ReferenceSteno.BuildLayout(state).ToArray();
            int[] leads = new int[256];
            Array.Fill(leads, -1);

            for (int i = 0; i < ranges.Length; i++)
            {
                for (int b = ranges[i].FirstLead; b < ranges[i].FirstLead + ranges[i].LeadCount; b++)
                    leads[b] = i;
            }

            return new FastLayout(ranges, [.. ranges.Select(r => r.Lo)], leads);
        });
    }

    private static int Find(FastLayout layout, int c)
    {
        int i = Array.BinarySearch(layout.Los, c);
        return i >= 0 ? i : ~i - 1;
    }

    private sealed record FastLayout(ReferenceSteno.Range[] Ranges, int[] Los, int[] RangeOfLead);
}
