using System.Globalization;
using System.Text;
using Singulink.Steno.Tests;

namespace Singulink.Steno.Benchmarks;

/// <summary>
/// Writes the test vectors of the byte-ordered format (Steno, version 1) from the reference codec, and prints the worked examples and the initial
/// layout of the specification.
/// </summary>
internal static class TestVectors
{
    private const int Seed = 20261120;

    /// <summary>
    /// The worked examples of the specification, in its order.
    /// </summary>
    public static readonly (string Name, string Text)[] Examples = [
        ("ascii", "Hi!\tOK\r\n"),
        ("latin", "Café crème"),
        ("cyrillic", "Мир, «да»!"),
        ("greek", "Καλή μέρα"),
        ("polish", "Łódź żółw"),
        ("amharic", "ሰላም ዓለም"),
        ("japanese", "東京でテストをするOK"),
        ("korean", "한국어 문장"),
        ("emoji", "Hi 😀👍"),
        ("lone-surrogate", "a\uD800b\uDC00"),
        ("cjk-then-latin", "漢字ŁŁa"),
    ];

    private static int _randomCount = 4000;

    public static int Run(string[] args)
    {
        if (args.Contains("--examples"))
            return PrintExamples();

        string path = GetOption(args, "--out") ?? Path.Combine(FindRepoRoot(), "TestVectors", "steno-v1-vectors.txt");

        // A larger file for testing other implementations has more random strings. The published file always has the default number.
        if (GetOption(args, "--random-count") is { } randomCount)
            _randomCount = int.Parse(randomCount, System.Globalization.CultureInfo.InvariantCulture);
        var coder = new OrderedLabCoder(OrderedLab.Reference);
        var valid = new List<(string Name, string Text)>();

        foreach (var example in Examples)
            valid.Add(($"example-{example.Name}", example.Text));

        valid.AddRange(Transitions());
        valid.AddRange(Boundaries(coder));
        valid.AddRange(Surrogates());
        valid.AddRange(Edges());

        int full = 0;

        foreach (string text in LabTexts.EndOfCodeSpaceTexts())
            valid.Add(($"end-of-code-space-{++full}", text));

        valid.AddRange(KanaSlotEdges());

        valid.AddRange(CorpusSample());
        valid.AddRange(RandomStrings());

        // One line per distinct text, in the order they came.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<string>();
        int validCount = 0;

        foreach ((string name, string text) in valid)
        {
            if (!seen.Add(text))
                continue;

            byte[] bytes = coder.Encode(text);

            if (coder.Decode(bytes) != text)
                throw new InvalidOperationException($"The reference does not round-trip vector {name}.");

            lines.Add($"valid\t{name}\t{CodePoints(text)}\t{Hex(bytes)}");
            validCount++;
        }

        int invalidCount = 0;

        foreach ((string name, byte[] bytes, string reason) in Invalid(coder))
        {
            lines.Add($"invalid\t{name}\t{Hex(bytes)}\t{reason}");
            invalidCount++;
        }

        int notValueCount = 0;

        foreach ((string name, int[] codePoints) in NotValues())
        {
            lines.Add($"not-a-value\t{name}\t{string.Join(' ', codePoints.Select(c => c.ToString("X4", CultureInfo.InvariantCulture)))}");
            notValueCount++;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" })
        {
            foreach (string line in Header(validCount, invalidCount, notValueCount))
                writer.WriteLine(line);

            foreach (string line in lines)
                writer.WriteLine(line);
        }

        Console.WriteLine($"Wrote {validCount:N0} valid, {invalidCount:N0} invalid and {notValueCount:N0} not-a-value vectors to {path} " +
            $"({new FileInfo(path).Length:N0} bytes).");
        Console.WriteLine($"{coder.CrowdedLayoutCount} layouts along the way had no room for their second window.");

        return CheckLibrary(path);
    }

    /// <summary>
    /// Checks the library against the vectors in a file: a second reading of the file, independent of the reference codec.
    /// </summary>
    private static int CheckLibrary(string path)
    {
        int passed = 0;
        int failed = 0;
        var steno = StenoEncoding.Instance;

        foreach (string line in File.ReadLines(path))
        {
            if (line.Length is 0 || line[0] is '#')
                continue;

            string[] fields = line.Split('\t');
            bool ok;

            // The library reads UTF-16, which cannot hold a sequence that is not a value.
            if (fields[0] is "not-a-value")
                continue;

            try
            {
                if (fields[0] is "valid")
                {
                    string text = fields[2] is "-" ? string.Empty : string.Concat(fields[2].Split(' ').Select(h => Text(int.Parse(h, NumberStyles.HexNumber))));
                    byte[] bytes = fields[3] is "-" ? [] : Convert.FromHexString(fields[3]);
                    ok = steno.GetBytes(text).AsSpan().SequenceEqual(bytes) && steno.GetString(bytes) == text;
                }
                else
                {
                    steno.GetString(fields[2] is "-" ? [] : Convert.FromHexString(fields[2]));
                    ok = false;
                }
            }
            catch (DecoderFallbackException ex)
            {
                ok = fields[0] is "invalid" && ex.Message.Contains($"({fields[3]})", StringComparison.Ordinal);
            }

            if (ok)
                passed++;
            else if (++failed <= 5)
                Console.WriteLine($"  the library disagrees: {fields[1]}");
        }

        Console.WriteLine($"The library agrees with {passed:N0} vectors and disagrees with {failed:N0}.");
        return failed is 0 ? 0 : 1;
    }

    private static string[] Header(int validCount, int invalidCount, int notValueCount) => [
        "# Test vectors for the Steno Encoding, version 1 (see FORMAT.md, section 12). Generated from the Steno reference implementation.",
        "#",
        "# Lines that start with '#' are comments, and empty lines are ignored. Every other line is one vector: fields separated by one tab.",
        "#",
        "#   valid <tab> name <tab> code points <tab> bytes",
        "#   invalid <tab> name <tab> bytes <tab> reason",
        "#   not-a-value <tab> name <tab> code points",
        "#",
        "# Code points are hexadecimal numbers without a prefix, in upper case, with at least 4 digits, separated by one space. Bytes are pairs of",
        "# hexadecimal digits in upper case without separators. An empty sequence is written as '-'. A name is a label without spaces.",
        "# Each vector is one whole value, encoded or decoded from the initial state.",
        "#",
        "# valid: encoding the code points must give exactly the bytes, and decoding the bytes must give exactly the code points. Code points",
        "# U+D800..U+DFFF are surrogate code points of their own; no valid vector has a high surrogate code point directly followed by a low one.",
        "#",
        "# invalid: decoding the bytes must fail. The reason is the rule of section 10.1 that the decoder meets first, reading from the start",
        "# in the order of section 9.2 (each trail byte is checked as it is read; the range check comes after all trail bytes are read):",
        "#   1 zero-byte       a byte 00, as a lead byte or a trail byte",
        "#   2 unused-lead     a lead byte other than 00 that the layout of the state does not use",
        "#   3 truncated       the bytes end before the last trail byte of a code point",
        "#   4 past-range      a code point beyond the last code point of its range (this includes any value above U+10FFFF)",
        "#   5 surrogate-pair  a low surrogate code point directly after a high surrogate code point",
        "#   6 invalid-trail   a byte 01..07 where a trail byte is expected (trail bytes are 08..FF)",
        "#",
        "# not-a-value: the code points are not a value (a high surrogate code point directly followed by a low one). An encoder whose input is",
        "# code points must reject them. An encoder that reads UTF-16 cannot be given them.",
        "#",
        $"# {validCount} valid, {invalidCount} invalid and {notValueCount} not-a-value vectors.",
        string.Empty,
    ];

    /// <summary>
    /// Code point sequences that are not values: each has a high surrogate code point directly followed by a low one.
    /// </summary>
    private static IEnumerable<(string Name, int[] CodePoints)> NotValues()
    {
        (string, int[])[] items = [
            ("pair", [0xD800, 0xDC00]),
            ("pair-last", [0xDBFF, 0xDFFF]),
            ("pair-emoji", [0xD83D, 0xDE00]),
            ("after-ascii", [0x61, 0xD800, 0xDC00]),
            ("before-ascii", [0xD800, 0xDC00, 0x62]),
            ("after-cyrillic", [0x416, 0x416, 0xD83D, 0xDE00]),
            ("in-cjk", [0x4E2D, 0xD840, 0xDC00, 0x4E2D]),
            ("in-hangul", [0xD55C, 0xDBFF, 0xDC00]),
            ("after-high", [0xD800, 0xD800, 0xDC00]),
            ("before-low", [0xD800, 0xDC00, 0xDC00]),
            ("two-pairs", [0xD800, 0xDC00, 0xD800, 0xDC00]),
            ("after-supplementary", [0x1F600, 0xD83D, 0xDE01]),
            ("late", [0x41, 0x42, 0x43, 0xE9, 0x3B1, 0xDB40, 0xDC01]),
        ];

        return items;
    }

    /// <summary>
    /// Texts for the state changes: the confidence, switches between the windows and the contexts, the kana slot, and leaving a wide context.
    /// </summary>
    private static IEnumerable<(string Name, string Text)> Transitions()
    {
        (string, string)[] items = [
            ("confidence-0-switch", "\u0416"),
            ("confidence-1-stray", "\u0416\u0416a\u00C0b"),
            ("confidence-2-strays", "\u0416\u0416\u0416\u0142\u0142\u0416\u0416"),
            ("confidence-2-then-switch", "\u0416\u0416\u0416\u0142\u0142\u0142\u0142"),
            ("confidence-saturates", "\u0416\u0416\u0416\u0416\u0416\u0416\u0142\u0142\u0142"),
            ("confidence-after-switch", "\u0416\u0142\u0416\u0416"),
            ("stray-long-sets-second", "\u0416\u0416\u0416\u1200\u0416\u0416\u1201"),
            ("stray-short-keeps-second", "\u0416\u0416\u0416\u1200\u0416\u0416\u0101\u0416\u1201"),
            ("switch-keeps-old-as-second", "\u0416\u1200\u1201\u0416"),
            ("switch-back-and-forth", "\u0416\u03B1\u0416\u03B1\u0416\u03B1"),
            ("neighbour-strays", "\u1200\u1200\u1280\u1300\u137F\u1100\u1200"),
            ("far-stray-supplementary", "\u0416\u0416\U0001F600\u0416\U0001F601"),
            ("supplementary-1-byte", "\U0001F600\U0001F601\U0001F602a\U0001F603"),
            ("supplementary-neighbours", "\U0001F600\U0001F600\U0001F680\U0001F500\U0001F600"),
            ("enter-cjk-ideograph", "a\u4E2Db"),
            ("enter-cjk-punctuation", "a\u3001b"),
            ("enter-cjk-hiragana", "a\u3042b"),
            ("enter-cjk-katakana", "a\u30A2b"),
            ("enter-cjk-middle-dot", "a\u30FB\u30FCb"),
            ("enter-cjk-extension-b", "a\U00020000\U0002A6DFb"),
            ("enter-cjk-from-hangul", "\uD55C\u4E2D"),
            ("enter-hangul", "a\uD55Cb"),
            ("enter-hangul-from-cjk", "\u4E2D\uD55C"),
            ("hangul-cjk-punctuation-stays", "\uD55C\u3001\u300C\uD55C"),
            ("hangul-kana-enters-cjk", "\uD55C\u3042"),
            ("hangul-fullwidth-stays", "\uD55C\uFF01\uFF21\uD55C"),
            ("cjk-fullwidth-stays", "\u4E2D\uFF01\uFF21\u4E2D"),
            ("slot-hiragana-to-katakana", "\u3042\u3044\u30A2\u30A4\u3042"),
            ("slot-katakana-to-letters", "\u30A2\u30A4abc\u30A2\u30A4"),
            ("slot-letters-to-hiragana", "\u4E2Dabc\u3042\u3044abc"),
            ("slot-marks-keep", "\u3042\u30FB\u30FC\u30A2\u30FB\u30FC"),
            ("slot-edges", "\u3040\u3041\u3096\u3097\u309F\u30A0\u30A1\u30FC\u30FD\u30FF"),
            ("slot-ascii-non-letters", "\u30421-[]{}~@`\u3042"),
            ("leave-cjk-two-chars", "\u4E2D\u0416\u0416"),
            ("leave-cjk-interrupted", "\u4E2D\u0416\u4E2D\u0416\u0416"),
            ("leave-cjk-other-window", "\u4E2D\u0416\u03B1\u0416\u0416"),
            ("leave-cjk-same-window", "\u4E2D\u0416\u0414\u0416"),
            ("leave-cjk-ascii-between", "\u4E2D\u0416a \u0416b"),
            ("leave-cjk-1-byte-window", "\u0142\u4E2D\u0142\u0142\u0142"),
            ("leave-hangul-two-chars", "\uD55C\u0416\u0416\uD55C"),
            ("leave-hangul-1-byte-window", "\u0416\uD55C\u0416\u0416\u0416"),
            ("wide-long-sets-second", "\u4E2D\u1200\u4E2D\u1200\u1200"),
            ("wide-then-general-second", "\u0416\u4E2D\u0142\u0142\u0416\u0416"),
            ("no-window-chars", "\u0416\u00A0\u00BF\u0370\u037F\u2000\u20CF\u20D0\u20FF\uD7A4\uE000\u0416"),
            ("controls", "\u0000\u0001\u0008\t\n\u000B\u000C\r\u000E\u001F\u007F"),
            ("near-zone", "\u0416\u0080\u00FF\u0100\u0300\u036F\u0380\u0500\u052F\u0530\u0590\u05FF\u0600\u0EFF\u0F00"),
            ("latin-windows", "\u00C0\u013F\u0140\u01BF\u01C0\u023F\u0240\u02BF\u02C0\u02FF\u0300"),
        ];

        return items;
    }

    /// <summary>
    /// For states of each kind: the first and last code point of every segment of the layout and their neighbours, after a text that reaches the state.
    /// </summary>
    private static IEnumerable<(string Name, string Text)> Boundaries(OrderedLabCoder coder)
    {
        (string, string)[] states = [
            ("initial", string.Empty),
            ("latin-140", "\u0142"),
            ("greek", "\u03B1"),
            ("cyrillic", "\u0416"),
            ("armenian", "\u0531"),
            ("hebrew", "\u05D0"),
            ("arabic", "\u0627"),
            ("devanagari", "\u0915"),
            ("thai", "\u0E01"),
            ("ethiopic", "\u1200"),
            ("ethiopic-second", "\u1200\u1200\u1200\u12C0"),
            ("cyrillic-second-ethiopic", "\u0416\u0416\u0416\u1200"),
            ("vietnamese", "\u1EA1"),
            ("symbols", "\u2190"),
            ("yi", "\uA000"),
            ("private-use", "\uE000"),
            ("variation-selectors", "\uFE00"),
            ("fullwidth-general", "\uFF21"),
            ("linear-b", "\U00010000"),
            ("emoji", "\U0001F600"),
            ("emoji-second", "\U0001F600\U0001F600\U0001F600\u1200"),
            ("plane-4", "\U00040000"),
            ("tags", "\U000E0000"),
            ("plane-16-end", "\U0010FF80"),
            ("end-of-code-space", "\uFE01\uFE02\uFE03\U000FFF85"),
            ("fullest", "\u1080\u1081\u1281"),
            ("second-adds-3", "\U00010200\U00010201\uFF01"),
            ("plane-1-cut", "\U0001FE00\U0001FE01"),
            ("hangul", "\uD55C"),
            ("hangul-after-cyrillic", "\u0416\uD55C"),
            ("hangul-second", "\uD55C\u1200"),
            ("cjk-letters", "\u4E2D"),
            ("cjk-hiragana", "\u3042"),
            ("cjk-katakana", "\u30A2"),
            ("cjk-after-greek", "\u03B1\u4E2D"),
            ("cjk-second", "\u4E2D\u1200"),
        ];

        foreach ((string name, string prefix) in states)
        {
            coder.Encode(prefix);
            var points = new SortedSet<int>();

            foreach ((var segment, _) in coder.CurrentLayout.Segments)
            {
                foreach (int c in (ReadOnlySpan<int>)[segment.Start - 1, segment.Start, segment.Start + 1, segment.End - 1, segment.End, segment.End + 1])
                {
                    if (c >= 0 && c <= 0x10FFFF)
                        points.Add(c);
                }
            }

            foreach (int c in points)
                yield return ($"boundary-{name}-{c:X4}", prefix + Text(c));
        }
    }

    /// <summary>
    /// The code points around the kana letters, after each kana slot, each followed by a hiragana, a katakana and an ASCII letter (whose bytes show
    /// where the slot went).
    /// </summary>
    private static IEnumerable<(string Name, string Text)> KanaSlotEdges()
    {
        (string, string)[] slots = [("letters", "\u4E2D"), ("hiragana", "\u3042"), ("katakana", "\u30A2")];
        int[] edges = [
            0x3000, 0x300F, 0x3010, 0x303F, 0x3040, 0x3041, 0x3096, 0x3097, 0x3099, 0x309F, 0x30A0, 0x30A1, 0x30FA, 0x30FB, 0x30FC,
            0x30FD, 0x30FF, 0x3100, 0xFF00, 0xFF1F, 0xFF20, 0xFF21,
        ];

        foreach ((string slot, string prefix) in slots)
        {
            foreach (int c in edges)
                yield return ($"kana-slot-{slot}-{c:X4}", prefix + Text(c) + "\u304B\u30AB" + "a\u304B");
        }
    }

    private static IEnumerable<(string Name, string Text)> Surrogates()
    {
        string[] lone = ["\uD800", "\uDBFF", "\uDC00", "\uDFFF", "\uD83D", "\uDE00"];
        string[] around = [string.Empty, "a", "\u0416", "\u4E2D", "\uD55C", "\u3042", "\U0001F600", "\uD800", "\uDC00"];
        int n = 0;

        foreach (string s in lone)
        {
            foreach (string before in around)
            {
                foreach (string after in around)
                {
                    string text = before + s + after;

                    // A high surrogate code unit followed by a low one is one code point, which is fine too: it is still a vector.
                    yield return ($"surrogate-{++n}", text);
                }
            }
        }

        yield return ("surrogate-high-high-low", "\uD800\U00010000");
        yield return ("surrogate-low-high", "\uDC00\uD800");
        yield return ("surrogate-separated", "\uD800a\uDC00");
    }

    private static IEnumerable<(string Name, string Text)> Edges()
    {
        int[] points = [
            0x0, 0x1, 0x7F, 0x80, 0xFF, 0x100, 0x7FF, 0x800, 0xEFF, 0xF00, 0x1FFF, 0x2000, 0xD7FF, 0xD800, 0xDFFF, 0xE000, 0xFEFF,
            0xFFFD, 0xFFFE, 0xFFFF, 0x10000, 0x10FBE, 0x10FBF, 0x10FC0, 0x10FC1, 0x1FDFF, 0x1FE00, 0x1FE01, 0x1FE02, 0x1FFFE, 0x1FFFF, 0x20000,
            0x20001, 0x3FFFF, 0x40000, 0xFFFFF, 0x100000, 0x10FFFE, 0x10FFFF,
        ];

        yield return ("empty", string.Empty);

        foreach (int c in points)
        {
            yield return ($"edge-{c:X4}", Text(c));
            yield return ($"edge-{c:X4}-twice", Text(c) + Text(c));
            yield return ($"edge-{c:X4}-between-ascii", "a" + Text(c) + "z");
        }

        yield return ("edge-all", string.Concat(points.Select(Text)));

        // Long values that start with U+FEFF, U+FFFE or U+FFFF: a byte order mark is an ordinary code point, and a value longer than the buffers of
        // an implementation (64 and 4,096 code units) must not be cut or misread there.
        (int First, int Length)[] starts = [(0xFEFF, 65), (0xFEFF, 4100), (0xFFFE, 65), (0xFFFF, 65), (0xFFFF, 300)];

        foreach ((int first, int length) in starts)
        {
            string text = Text(first) + "Byte order " + string.Concat(Enumerable.Repeat("Ελληνικά, Кириллица, 中文 and ASCII. ", (length / 20) + 1));
            yield return ($"start-{first:X4}-{length}", text[..length]);
        }

        yield return ("start-FEFF-ascii-5000", Text(0xFEFF) + new string('x', 5000));
    }

    /// <summary>
    /// A few hundred strings of the corpus, of mixed length (a fixed choice).
    /// </summary>
    private static IEnumerable<(string Name, string Text)> CorpusSample()
    {
        var random = new Random(Seed);
        var entries = Corpus.Load();
        int n = 0;

        foreach (var entry in entries.Where(e => e.Group is Corpus.UdhrParagraphs or Corpus.UdhrSnippets or Corpus.MixedLines or Corpus.ShortStrings))
        {
            for (int k = 0; k < 2; k++)
            {
                string unit = entry.Units[random.Next(entry.Units.Count)];

                if (unit.Length > 160)
                    unit = unit[..(char.IsHighSurrogate(unit[159]) ? 159 : 160)];

                yield return ($"corpus-{++n}", unit);
            }
        }
    }

    private static IEnumerable<(string Name, string Text)> RandomStrings()
    {
        var random = new Random(Seed);

        for (int i = 0; i < _randomCount; i++)
        {
            int maxLength = i % 20 == 0 ? 120 : 24;
            string text = i % 3 == 0 ? RandomText.Next(random, maxLength) : LabTexts.NextText(random, maxLength);
            yield return ($"random-{i + 1}", text);
        }
    }

    /// <summary>
    /// Bytes that a decoder must reject: after texts that reach states of each kind, each kind of defect; and seeded random changes to valid
    /// encodings. The reference decoder says whether and why each one fails.
    /// </summary>
    private static IEnumerable<(string Name, byte[] Bytes, string Reason)> Invalid(OrderedLabCoder coder)
    {
        string[] prefixes = [
            string.Empty, "a", "\u0416", "\u0416\u0416\u0416\u1200", "\u1200", "\U0001F600", "\u4E2D", "\u3042", "\u30A2", "\uD55C",
            "\uFE01\uFE02\uFE03\U000FFF85",
        ];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int n = 0;

        foreach (string prefix in prefixes)
        {
            byte[] head = coder.Encode(prefix);
            var segments = coder.CurrentLayout.Segments.ToList();
            var candidates = new List<byte[]>();
            bool[] used = new bool[256];

            foreach ((var segment, int firstLead) in segments)
            {
                int leads = LeadCount(segment);

                for (int i = 0; i < leads; i++)
                    used[firstLead + i] = true;

                if (segment.Bytes > 1)
                {
                    // A 0x00 trail byte, a sequence cut off after its lead byte, and the last lead byte with the highest trail bytes.
                    candidates.Add([.. head, (byte)firstLead, .. Enumerable.Repeat((byte)0, segment.Bytes - 1)]);
                    candidates.Add([.. head, (byte)firstLead]);
                    candidates.Add([.. head, (byte)(firstLead + leads - 1), .. Enumerable.Repeat((byte)0xFF, segment.Bytes - 1)]);

                    if (segment.Bytes > 2)
                        candidates.Add([.. head, (byte)firstLead, 0x01]);

                    // The bytes present already put the code point past its range, but the last trail byte is missing: that is truncated.
                    if (segment.Bytes > 2)
                        candidates.Add([.. head, (byte)(firstLead + leads - 1), .. Enumerable.Repeat((byte)0xFF, segment.Bytes - 2)]);
                }
            }

            candidates.Add([.. head, 0x00]);
            candidates.Add([.. head, 0x41, 0x00]);

            // Every byte 01..07 in every trail position of the first range of each size (in the other positions, the lowest trail byte 08).
            foreach (int size in (ReadOnlySpan<int>)[2, 3, 4])
            {
                int index = segments.FindIndex(s => s.Segment.Bytes == size);

                if (prefix.Length > 0 || index < 0)
                    continue;

                int lead = segments[index].FirstLead;

                for (int position = 1; position < size; position++)
                {
                    for (int b = 1; b < 8; b++)
                    {
                        byte[] sequence = [(byte)lead, .. Enumerable.Repeat((byte)0x08, size - 1)];
                        sequence[position] = (byte)b;
                        candidates.Add([.. head, .. sequence]);
                    }
                }
            }

            for (int b = 1; b < 256; b++)
            {
                if (!used[b])
                    candidates.Add([.. head, (byte)b]);
            }

            // A high and a low surrogate as two code points.
            byte[] withHigh = coder.Encode(prefix + "\uD83D");
            byte[] withLow = coder.Encode(prefix + "\uDE00");
            candidates.Add([.. withHigh, .. withLow.AsSpan(head.Length)]);
            candidates.Add([.. withHigh, .. withLow.AsSpan(head.Length), 0x41]);

            foreach (byte[] bytes in candidates)
            {
                if (Reject(coder, bytes) is { } reason && seen.Add(Hex(bytes)))
                    yield return ($"invalid-{++n}", bytes, reason);
            }
        }

        var random = new Random(Seed);

        for (int i = 0; i < 400; i++)
        {
            byte[] bytes = coder.Encode(LabTexts.NextText(random, 16));

            if (bytes.Length is 0)
                continue;

            bytes = random.Next(2) == 0 ? bytes[..random.Next(1, bytes.Length + 1)] : bytes;
            bytes[random.Next(bytes.Length)] = (byte)random.Next(256);

            if (Reject(coder, bytes) is { } reason && seen.Add(Hex(bytes)))
                yield return ($"invalid-random-{i + 1}", bytes, reason);
        }
    }

    /// <summary>
    /// Returns why the reference decoder rejects the bytes, or null if it decodes them.
    /// </summary>
    private static string? Reject(OrderedLabCoder coder, byte[] bytes)
    {
        try
        {
            coder.Decode(bytes);
            return null;
        }
        catch (IndexOutOfRangeException)
        {
            return "truncated";
        }
        catch (InvalidDataException ex) when (ex.Message.StartsWith("A lead byte", StringComparison.Ordinal))
        {
            // The first lead byte that the layout does not use: if it is 0x00, say so.
            return FirstUnusedLeadIsZero(coder, bytes) ? "zero-byte" : "unused-lead";
        }
        catch (InvalidDataException ex) when (ex.Message.StartsWith("A byte that", StringComparison.Ordinal))
        {
            return "zero-byte";
        }
        catch (InvalidDataException ex) when (ex.Message.StartsWith("A trail byte below", StringComparison.Ordinal))
        {
            return "invalid-trail";
        }
        catch (InvalidDataException ex) when (ex.Message.StartsWith("A sequence beyond", StringComparison.Ordinal))
        {
            return "past-range";
        }
        catch (InvalidDataException ex) when (ex.Message.StartsWith("A low surrogate", StringComparison.Ordinal))
        {
            return "surrogate-pair";
        }
    }

    /// <summary>
    /// Decodes as far as the bytes go and tells whether the sequence that failed starts with 0x00.
    /// </summary>
    private static bool FirstUnusedLeadIsZero(OrderedLabCoder coder, byte[] bytes)
    {
        // The longest prefix that decodes ends where the bad lead byte is.
        for (int length = bytes.Length - 1; length >= 0; length--)
        {
            if (Reject(coder, bytes[..length]) is null)
                return bytes[length] is 0;
        }

        return false;
    }

    /// <summary>
    /// Prints the worked examples with their traces, and the initial layout, as Markdown for the specification.
    /// </summary>
    private static int PrintExamples()
    {
        var coder = new OrderedLabCoder(OrderedLab.Reference);

        // The layouts after some texts: the initial state first.
        string[] states = [string.Empty, "ЖЖЖሀ", "あ", "한", "︁︂︃\U000FFF85", "ကခሀ", "𑈀", "𑈀𑈁𐺀"];

        foreach (string prefix in states)
        {
            coder.Encode(prefix);
            Console.WriteLine($"Layout after {CodePoints(prefix)}: {coder.DescribeState()}");
            Console.WriteLine();
            Console.WriteLine("| Range | Bytes | Lead bytes |");
            Console.WriteLine("|---|---|---|");

            foreach ((var segment, int firstLead) in coder.CurrentLayout.Segments)
            {
                int leads = LeadCount(segment);
                string leadText = leads is 1 ? $"{firstLead:X2}" : $"{firstLead:X2}..{firstLead + leads - 1:X2}";
                Console.WriteLine($"| U+{segment.Start:X4}..U+{segment.End:X4} | {segment.Bytes} | {leadText} |");
            }

            Console.WriteLine();
        }

        foreach ((string name, string text) in Examples)
        {
            Console.WriteLine();
            Console.WriteLine($"Example {name}: {CodePoints(text)}");
            Console.WriteLine($"Bytes: {Hex(coder.Encode(text))}");
            Console.WriteLine();
            Console.WriteLine("| # | Code point | Bytes | State after |");
            Console.WriteLine("|---|---|---|---|");

            int index = 0;
            int count = 0;
            byte[] previous = [];

            while (index < text.Length)
            {
                int c = OrderedLabCoder.ReadCodePoint(text, ref index);
                byte[] bytes = coder.Encode(text[..index]);
                string state = coder.DescribeState();
                Console.WriteLine($"| {++count} | U+{c:X4} | {Hex(bytes.AsSpan(previous.Length))} | {state} |");
                previous = bytes;
            }
        }

        return 0;
    }

    private static int LeadCount(OrderedPiece segment)
    {
        int perLead = 1;

        for (int i = 1; i < segment.Bytes; i++)
            perLead *= OrderedLab.Reference.Radix;

        return (segment.End - segment.Start + perLead) / perLead;
    }

    private static string Text(int c) => c is >= 0xD800 and <= 0xDFFF ? ((char)c).ToString() : char.ConvertFromUtf32(c);

    private static string CodePoints(string text)
    {
        if (text.Length is 0)
            return "-";

        var points = new List<string>();

        for (int index = 0; index < text.Length;)
            points.Add(OrderedLabCoder.ReadCodePoint(text, ref index).ToString("X4", CultureInfo.InvariantCulture));

        return string.Join(' ', points);
    }

    private static string Hex(ReadOnlySpan<byte> bytes) => bytes.Length is 0 ? "-" : Convert.ToHexString(bytes);

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FORMAT.md")))
                return directory.FullName;
        }

        return Directory.GetCurrentDirectory();
    }

    private static string? GetOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
