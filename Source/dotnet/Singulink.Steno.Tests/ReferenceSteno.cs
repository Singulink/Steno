using System.Globalization;

namespace Singulink.Steno.Tests;

/// <summary>
/// A plain reference codec of Steno, version 1, written from FORMAT.md alone without looking at the library. The tests use it as the oracle.
/// </summary>
/// <remarks>
/// It builds the layout from the pieces of FORMAT.md section 5 again before every code point, so it is slow, but each step can be checked against
/// the text of the specification. It works on code points: a lone surrogate is a code point of its own. Do not make it share code with the library
/// or change its logic to agree with the library; a disagreement is a finding to report.
/// </remarks>
internal static class ReferenceSteno
{
    public enum Context
    {
        General,
        Cjk,
        Hangul,
    }

    public enum Slot
    {
        Letters,
        Hiragana,
        Katakana,
    }

    /// <summary>
    /// A window, identified by its first code point (section 4).
    /// </summary>
    public readonly record struct Window(int Start, int End)
    {
        public override string ToString() => $"U+{Start:X4}..U+{End:X4}";
    }

    public sealed class State
    {
        public Context Context { get; set; } = Context.General;

        public Window Main { get; set; } = new(0xC0, 0x13F);

        public Window? Second { get; set; }

        public Slot Slot { get; set; } = Slot.Letters;

        public int Confidence { get; set; }

        public Window? Pending { get; set; }

        public override string ToString() =>
            $"{Context} main={Main} second={Second?.ToString() ?? "none"} slot={Slot} conf={Confidence} pending={Pending?.ToString() ?? "none"}";
    }

    public readonly record struct Range(int Lo, int Hi, int Size, int FirstLead, int LeadCount)
    {
        public override string ToString() => $"U+{Lo:X4}..U+{Hi:X4} size {Size} leads {FirstLead:X2}..{FirstLead + LeadCount - 1:X2}";
    }

    // ---------- Section 4: windows ----------

    private static Window Block(int c)
    {
        int s = c - (c % 0x80);
        return new Window(s, s + 0x7F);
    }

    public static Window? WindowOf(int c)
    {
        if (c <= 0x00BF) return null;
        if (c <= 0x013F) return new Window(0x00C0, 0x013F);
        if (c <= 0x01BF) return new Window(0x0140, 0x01BF);
        if (c <= 0x023F) return new Window(0x01C0, 0x023F);
        if (c <= 0x02BF) return new Window(0x0240, 0x02BF);
        if (c <= 0x02FF) return new Window(0x02C0, 0x02FF);
        if (c <= 0x036F) return new Window(0x0300, 0x036F);
        if (c <= 0x037F) return null;
        if (c <= 0x04FF) return Block(c);
        if (c <= 0x052F) return new Window(0x0500, 0x052F);
        if (c <= 0x058F) return new Window(0x0530, 0x058F);
        if (c <= 0x05FF) return new Window(0x0590, 0x05FF);
        if (c <= 0x1FFF) return Block(c);
        if (c <= 0x20FF) return null;
        if (c <= 0x2FFF) return Block(c);
        if (c <= 0x30FF) return null;
        if (c <= 0x33FF) return Block(c);
        if (c <= 0x9FFF) return null;
        if (c <= 0xABFF) return Block(c);
        if (c <= 0xDFFF) return null;
        if (c <= 0x1FFFF) return Block(c);
        if (c <= 0x3FFFF) return null;
        if (c <= 0x10FFFF) return Block(c);
        throw new ArgumentOutOfRangeException(nameof(c));
    }

    // ---------- Section 6: layout ----------

    public static List<Range> BuildLayout(State s)
    {
        // Segments covering 0..10FFFF; size 0 means "no piece covers it".
        var segs = new List<(int Lo, int Hi, int Size)> { (0, 0x10FFFF, 0) };

        void Piece(int lo, int hi, int size)
        {
            if (lo > hi) return;
            var next = new List<(int Lo, int Hi, int Size)>();

            foreach (var g in segs)
            {
                if (g.Hi < lo || g.Lo > hi)
                {
                    next.Add(g);
                    continue;
                }

                if (g.Lo < lo) next.Add((g.Lo, lo - 1, g.Size));
                if (g.Hi > hi) next.Add((hi + 1, g.Hi, g.Size));
            }

            next.Add((lo, hi, size));
            next.Sort((a, b) => a.Lo.CompareTo(b.Lo));
            segs = next;
        }

        int m0 = s.Main.Start, m1 = s.Main.End;

        // 6.2 piece 1
        if (s.Context == Context.General)
        {
            Piece(Math.Max(0x80, m0 - Radix), m0 - 1, 2);
            if (m1 < 0x10FFFF) Piece(m1 + 1, Math.Min(0x10FFFF, m1 + Radix), 2);
        }

        // step 2
        Piece(0x00, 0x1F, 2);
        Piece(0x09, 0x0A, 1);
        Piece(0x0D, 0x0D, 1);

        // step 3
        if (s.Context == Context.Cjk && s.Slot != Slot.Letters)
        {
            Piece(0x20, 0x40, 1);
            Piece(0x41, 0x7E, 2);
        }
        else
        {
            Piece(0x20, 0x7E, 1);
        }

        // step 4
        Piece(0x7F, 0xBF, 2);

        // step 5
        if (s.Context != Context.Cjk) Piece(0x80, 0xEFF, 2);

        // step 6
        Piece(0x2000, 0x20CF, 2);

        // step 7
        if (s.Second is Window sw) Piece(sw.Start, sw.End, 2);

        // step 8
        switch (s.Context)
        {
            case Context.General:
                Piece(m0, m1, 1);
                break;
            case Context.Hangul:
                Piece(m0, m1, 2);
                Piece(0x3000, 0x303F, 2);
                Piece(0xAC00, 0xD7A3, 2);
                Piece(0xFF00, 0xFFFF, 2);
                break;
            case Context.Cjk:
                Piece(m0, m1, 2);
                Piece(0x3000, 0x300F, 1);
                Piece(0x3010, 0x30FF, 2);
                if (s.Slot == Slot.Hiragana) Piece(0x3041, 0x3096, 1);
                if (s.Slot == Slot.Katakana) Piece(0x30A1, 0x30FC, 1);
                Piece(0x4E00, 0x9FFF, 2);
                Piece(0xFF00, 0xFFFF, 2);
                if (s.Slot == Slot.Letters) Piece(0xFF00, 0xFF1F, 1);
                break;
        }

        // 6.3: merge adjacent uncovered segments into stretches, split into parts, and size them.
        var sized = new List<(int Lo, int Hi, int Size)>();
        int i = 0;

        while (i < segs.Count)
        {
            if (segs[i].Size != 0)
            {
                sized.Add(segs[i]);
                i++;
                continue;
            }

            int lo = segs[i].Lo, hi = segs[i].Hi;
            i++;

            while (i < segs.Count && segs[i].Size == 0)
            {
                hi = segs[i].Hi;
                i++;
            }

            foreach (var (plo, phi) in Cut(lo, hi))
            {
                long n = (long)phi - plo + 1;
                int size = n <= Radix ? 2 : n <= (long)Radix * Radix ? 3 : 4;
                sized.Add((plo, phi, size));
            }
        }

        // 6.4 / 6.5: ranges are maximal runs of same size; then lead bytes.
        var ranges = new List<Range>();
        int lead = 1;
        int j = 0;

        while (j < sized.Count)
        {
            int lo = sized[j].Lo, hi = sized[j].Hi, size = sized[j].Size;
            j++;

            while (j < sized.Count && sized[j].Size == size && sized[j].Lo == hi + 1)
            {
                hi = sized[j].Hi;
                j++;
            }

            long n = (long)hi - lo + 1;
            long per = PowRadix(size - 1);
            int leads = (int)((n + per - 1) / per);
            ranges.Add(new Range(lo, hi, size, lead, leads));
            lead += leads;
        }

        if (lead - 1 > 254) throw new InvalidOperationException("layout needs more than 254 lead bytes");
        return ranges;
    }

    // 6.3: the runs U+0000..U+10FBF, U+10FC0..U+1FFFF and U+20000..U+10FFFF; a stretch is cut only at their boundaries.
    private static IEnumerable<(int Lo, int Hi)> Cut(int lo, int hi)
    {
        int[] bounds = [0x10FC0, 0x20000];
        int start = lo;

        foreach (int b in bounds)
        {
            if (start < b && hi >= b)
            {
                yield return (start, b - 1);
                start = b;
            }
        }

        yield return (start, hi);
    }

    // 8.2: digits are base 248; a trail byte is a digit plus 8, so trail bytes are 08..FF.
    private const int Radix = 248;
    private const int TrailBias = 8;

    private static long PowRadix(int e)
    {
        long r = 1;
        for (int i = 0; i < e; i++) r *= Radix;
        return r;
    }

    // ---------- Section 8.2: one code point ----------

    public static void EncodeOne(List<Range> layout, int c, List<byte> output)
    {
        Range r = layout.Single(x => x.Lo <= c && c <= x.Hi);
        long o = c - r.Lo;
        int k = r.Size;
        long q = o / PowRadix(k - 1);
        long rest = o - (q * PowRadix(k - 1));
        output.Add(checked((byte)(r.FirstLead + q)));

        for (int i = k - 2; i >= 0; i--)
        {
            long p = PowRadix(i);
            long t = rest / p;
            rest -= t * p;
            output.Add(checked((byte)(t + TrailBias)));
        }
    }

    // ---------- Section 7 ----------

    private static bool In(int c, int lo, int hi) => lo <= c && c <= hi;

    public static void Update(State s, int c, int n)
    {
        // Step 1
        if (c <= 0x7F)
        {
            if (s.Context == Context.Cjk && (In(c, 0x41, 0x5A) || In(c, 0x61, 0x7A))) s.Slot = Slot.Letters;
            return;
        }

        // Step 2
        if (In(c, 0xAC00, 0xD7A3))
        {
            s.Pending = null;
            s.Context = Context.Hangul;
            return;
        }

        // Step 3
        if (In(c, 0x3040, 0x30FF) || In(c, 0x3400, 0x9FFF) || In(c, 0x20000, 0x3FFFF) ||
            (In(c, 0x3000, 0x303F) && s.Context != Context.Hangul))
        {
            if (s.Context != Context.Cjk)
            {
                s.Context = Context.Cjk;
                s.Slot = Slot.Letters;
            }

            s.Pending = null;
            if (In(c, 0x3041, 0x3096)) s.Slot = Slot.Hiragana;
            else if (In(c, 0x30A1, 0x30FA)) s.Slot = Slot.Katakana;
            return;
        }

        // Step 4
        if (s.Context != Context.General && In(c, 0xFF00, 0xFFFF)) return;

        // Step 5
        Window? wn = WindowOf(c);
        if (wn is not Window w) return;

        // Step 6
        if (s.Context == Context.General && w.Start == s.Main.Start)
        {
            s.Confidence = Math.Min(s.Confidence + 1, 2);
            return;
        }

        // Step 7 (n is the size k of c's range: 3 or 4)
        if (n > 2) s.Second = w;

        // Step 8
        if (s.Context == Context.General)
        {
            if (s.Confidence > 0)
            {
                s.Confidence--;
                return;
            }
        }
        else
        {
            if (s.Pending is not Window p || p.Start != w.Start)
            {
                s.Pending = w;
                return;
            }
        }

        // Step 9
        if (s.Main.Start != w.Start) s.Second = s.Main;
        else if (s.Second is Window sw && sw.Start == w.Start) s.Second = null;
        s.Main = w;
        s.Context = Context.General;
        s.Pending = null;
        s.Confidence = 0;
    }

    // ---------- Encoder / decoder ----------

    public static byte[] Encode(IReadOnlyList<int> cps, Action<int, byte[], State>? trace = null)
    {
        for (int i = 0; i < cps.Count; i++)
        {
            if (cps[i] < 0 || cps[i] > 0x10FFFF) throw new ArgumentException($"not a code point: {cps[i]:X}", nameof(cps));
            if (i > 0 && In(cps[i - 1], 0xD800, 0xDBFF) && In(cps[i], 0xDC00, 0xDFFF))
                throw new ArgumentException("not a value: high surrogate code point followed by low surrogate code point", nameof(cps));
        }

        var s = new State();
        var output = new List<byte>();

        foreach (int c in cps)
        {
            var layout = BuildLayout(s);
            int before = output.Count;
            EncodeOne(layout, c, output);
            int n = output.Count - before;
            Update(s, c, n);
            trace?.Invoke(c, [.. output.Skip(before)], s);
        }

        return [.. output];
    }

    public static List<int> Decode(byte[] bytes)
    {
        var s = new State();
        var result = new List<int>();
        int pos = 0;

        while (pos < bytes.Length)
        {
            var layout = BuildLayout(s);
            byte b = bytes[pos];
            if (b == 0) throw new ReferenceStenoException("zero-byte", $"lead byte 00 at {pos}");
            Range? found = null;

            foreach (var r in layout)
            {
                if (r.FirstLead <= b && b < r.FirstLead + r.LeadCount)
                {
                    found = r;
                    break;
                }
            }

            if (found is not Range rg) throw new ReferenceStenoException("unused-lead", $"lead byte {b:X2} at {pos}");
            int start = pos;
            pos++;
            long o = b - rg.FirstLead;

            for (int i = 0; i < rg.Size - 1; i++)
            {
                if (pos >= bytes.Length) throw new ReferenceStenoException("truncated", $"at {pos}");
                byte u = bytes[pos];
                if (u == 0) throw new ReferenceStenoException("zero-byte", $"trail byte 00 at {pos}");
                if (u < TrailBias) throw new ReferenceStenoException("invalid-trail", $"trail byte {u:X2} at {pos}");
                o = (o * Radix) + (u - TrailBias);
                pos++;
            }

            long c = rg.Lo + o;
            if (c > rg.Hi) throw new ReferenceStenoException("past-range", $"code point {c:X} past {rg} at {start}");
            int cp = (int)c;

            if (result.Count > 0 && In(result[^1], 0xD800, 0xDBFF) && In(cp, 0xDC00, 0xDFFF))
                throw new ReferenceStenoException("surrogate-pair", $"at {start}");

            result.Add(cp);
            Update(s, cp, pos - start);
        }

        return result;
    }

    public static string Hex(IEnumerable<byte> b) => string.Concat(b.Select(x => x.ToString("X2", CultureInfo.InvariantCulture)));

    public static string Cps(IEnumerable<int> c)
    {
        var l = c.Select(x => x.ToString("X4", CultureInfo.InvariantCulture)).ToList();
        return l.Count == 0 ? "-" : string.Join(' ', l);
    }
}

/// <summary>
/// The error of <see cref="ReferenceSteno.Decode(byte[])"/>, with the reason of FORMAT.md section 10.1 that the decoder met.
/// </summary>
internal sealed class ReferenceStenoException(string reason, string detail) : Exception(reason + ": " + detail)
{
    public string Reason { get; } = reason;
}
