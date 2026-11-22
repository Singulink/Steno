using System.Text;

namespace Singulink.Steno.Tests;

/// <summary>
/// Text generators for the fuzz tests: every window of FORMAT.md section 4, long runs, and the code points around every place where the layout
/// changes.
/// </summary>
internal static class KindText
{
    private const string AsciiOthers = "0123456789 !\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    private static readonly int[] SpecialPoints = [
        0x1FE00, 0x1FE01, 0x1FDFF, 0x1FE02, 0xFFFF, 0x10000, 0xFFFE, 0x10FFFF, 0xE000, 0xD7A3, 0xD7A4, 0xABFF, 0xAC00, 0x9FFF, 0xA000, 0x4DFF, 0x4E00,
        0x30FC, 0x30FA, 0x30FB, 0x3096, 0x3097, 0x3041, 0x3040, 0x30A0, 0x30A1, 0x20CF, 0x20D0, 0xEFF, 0xF00, 0xBF, 0xC0, 0x13F, 0x140, 0x7F, 0x7E,
        0x20, 0x1F, 0x0D, 0x0A, 0x09, 0x00, 0x36F, 0x370, 0x37F, 0x380, 0x4FF, 0x500, 0x52F, 0x530, 0x58F, 0x590, 0x5FF, 0x600, 0x1FFFF, 0x20000,
        0x3FFFF, 0x40000,
    ];

    private static readonly Kind[] AllKinds = Enum.GetValues<Kind>();

    /// <summary>
    /// Classes of code points.
    /// </summary>
    public enum Kind
    {
        Ascii,
        AsciiLetter,
        AsciiOther,
        Control,
        TabLfCr,
        Latin1Upper,
        AnyWindow,
        NearWindow,
        Ideograph,
        ExtA,
        ExtB,
        CjkPunct,
        Hiragana,
        KanaMark,
        Katakana,
        KatakanaDotProlong,
        Fullwidth,
        FullwidthLow,
        Hangul,
        LoneHigh,
        LoneLow,
        Pair,
        Emoji,
        SuppCut,
        HighPlane,
        GeneralPunct,
        Greek,
        Cyrillic,
        Bmp,
        Supp,
        Specials,
    }

    /// <summary>
    /// Gets the first code point of every window (7,356 of them).
    /// </summary>
    public static int[] Windows { get; } = BuildWindows();

    public static int WindowEnd(int start) => ReferenceSteno.WindowOf(start)!.Value.End;

    public static Kind RandomKind(Random random) => AllKinds[random.Next(AllKinds.Length)];

    public static int RandomWindow(Random random) => Windows[random.Next(Windows.Length)];

    /// <summary>
    /// Picks a code point of a kind; <paramref name="window"/> is used by the kinds of a window.
    /// </summary>
    public static int Pick(Random random, Kind kind, int window) => kind switch {
        Kind.Ascii => random.Next(0x20, 0x7F),
        Kind.AsciiLetter => random.Next(2) == 0 ? random.Next('A', 'Z' + 1) : random.Next('a', 'z' + 1),
        Kind.AsciiOther => AsciiOthers[random.Next(AsciiOthers.Length)],
        Kind.Control => random.Next(0, 0x20),
        Kind.TabLfCr => "\t\n\r"[random.Next(3)],
        Kind.Latin1Upper => random.Next(0x7F, 0xC0),
        Kind.AnyWindow => window + random.Next(WindowEnd(window) - window + 1),
        Kind.NearWindow => Math.Clamp(random.Next(2) == 0 ? window - random.Next(1, 300) : WindowEnd(window) + random.Next(1, 300), 0, 0x10FFFF),
        Kind.Ideograph => random.Next(0x4E00, 0xA000),
        Kind.ExtA => random.Next(0x3400, 0x4E00),
        Kind.ExtB => random.Next(0x20000, 0x2A6E0),
        Kind.CjkPunct => random.Next(0x3000, 0x3040),
        Kind.Hiragana => random.Next(0x3041, 0x3097),
        Kind.KanaMark => random.Next(2) == 0 ? random.Next(0x3097, 0x30A1) : 0x3040,
        Kind.Katakana => random.Next(0x30A1, 0x30FB),
        Kind.KatakanaDotProlong => random.Next(0x30FB, 0x3100),
        Kind.Fullwidth => random.Next(0xFF00, 0x10000),
        Kind.FullwidthLow => random.Next(0xFF00, 0xFF20),
        Kind.Hangul => random.Next(0xAC00, 0xD7A4),
        Kind.LoneHigh => random.Next(0xD800, 0xDC00),
        Kind.LoneLow => random.Next(0xDC00, 0xE000),
        Kind.Pair => random.Next(0x10000, 0x110000),
        Kind.Emoji => random.Next(0x1F300, 0x1FA00),
        Kind.SuppCut => random.Next(0x1FD00, 0x1FF00),
        Kind.HighPlane => random.Next(2) == 0 ? random.Next(0x10FF00, 0x110000) : random.Next(0x3FF00, 0x40100),
        Kind.GeneralPunct => random.Next(0x2000, 0x2100),
        Kind.Greek => random.Next(0x370, 0x400),
        Kind.Cyrillic => random.Next(0x400, 0x530),
        Kind.Bmp => random.Next(0, 0x10000),
        Kind.Supp => random.Next(0x10000, 0x110000),
        Kind.Specials => SpecialPoints[random.Next(SpecialPoints.Length)],
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static void Append(StringBuilder text, int c)
    {
        if (c < 0x10000)
            text.Append((char)c);
        else
            text.Append(char.ConvertFromUtf32(c));
    }

    /// <summary>
    /// Generates text in runs of a few kinds of code points, with run lengths that reach past the vector blocks (16, 32) and the slices (512, 1024).
    /// </summary>
    public static string Runs(Random random, int maxLength)
    {
        int length = random.Next(maxLength + 1);
        var text = new StringBuilder(length + 2);
        int kindCount = random.Next(1, 6);
        var kinds = new Kind[kindCount];
        int[] windows = new int[kindCount];

        for (int i = 0; i < kindCount; i++)
        {
            kinds[i] = RandomKind(random);
            windows[i] = RandomWindow(random);
        }

        while (text.Length < length)
        {
            int k = random.Next(kindCount);

            int run = random.Next(4) switch {
                0 => random.Next(1, 4),
                1 => random.Next(1, 20),
                2 => random.Next(1, 70),
                _ => random.Next(1, 400),
            };

            for (int i = 0; i < run && text.Length < length; i++)
                Append(text, Pick(random, kinds[k], windows[k]));
        }

        return text.ToString();
    }

    public static string Of(Random random, Kind kind, int window, int count)
    {
        var text = new StringBuilder();

        for (int i = 0; i < count; i++)
            Append(text, Pick(random, kind, window));

        return text.ToString();
    }

    public static bool HasLoneSurrogate(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                i++;
            else if (char.IsSurrogate(text[i]))
                return true;
        }

        return false;
    }

    public static string RemoveLoneSurrogates(string text)
    {
        var result = new StringBuilder(text.Length);

        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                result.Append(text, i, 2);
                i++;
            }
            else if (!char.IsSurrogate(text[i]))
            {
                result.Append(text[i]);
            }
        }

        return result.ToString();
    }

    private static int[] BuildWindows()
    {
        var list = new List<int>();
        int last = -1;

        for (int c = 0; c <= 0x10FFFF; c++)
        {
            if (ReferenceSteno.WindowOf(c) is { } window && window.Start != last)
            {
                list.Add(window.Start);
                last = window.Start;
            }
        }

        return [.. list];
    }
}
