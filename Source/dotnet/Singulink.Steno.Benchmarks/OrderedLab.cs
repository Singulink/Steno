using System.Globalization;
using System.Text;

namespace Singulink.Steno.Benchmarks;

/// <summary>
/// Explores byte-ordered formats: encodings whose bytes sort the way the code points of the text do, like BOCU-1. Each candidate is a real codec here
/// (it encodes to bytes and decodes back), and the lab checks the round trip, the byte order and that the encoding of a prefix is a prefix of the
/// encoding. It then compares the sizes with ICU's BOCU-1 and SCSU and with Steno (the library), and checks that candidate Z, which became Steno,
/// version 1, gives the bytes of the library exactly.
/// </summary>
/// <remarks>
/// A candidate has no tags and its encoder makes no choices. Its state is a function of the text encoded so far, and each state has a layout: a split
/// of the code points into ranges that take 1 to 4 bytes each, with lead bytes handed out in code point order. That alone gives the byte order.
/// </remarks>
internal static class OrderedLab
{
    private static readonly OrderedOptions[] _candidates = [
        new("A") { Description = "a window becomes the 1-byte window at its first char" },
        new("J") { Description = "as A, but a used 1-byte window survives one char of another window; 2 bytes at most below U+0800; 6 recent windows at 2 bytes", Confidence = 1, NearZoneEnd = 0x7FF },
        new("P") { Description = "as J, with 4 recent windows, and byte 0x00 is never used", Confidence = 1, NearZoneEnd = 0x7FF, Recents = 4, ReserveZero = true },
        new("V") { Description = "as P, but with 2 bytes for the chars next to the 1-byte window and for the window that was it before, not for recent windows", Confidence = 1, NearZoneEnd = 0x7FF, Recents = 0, ReserveZero = true, ShortPunctuation = true, HotNeighbors = true, PreviousHot = true },
        new("W") { Description = "as V, with 2 bytes at most below U+0F00", Confidence = 1, NearZoneEnd = 0xEFF, Recents = 0, ReserveZero = true, ShortPunctuation = true, HotNeighbors = true, PreviousHot = true, CompactControls = true },
        new("X") { Description = "as W, but the second 2-byte window is the one of the last char outside the 1-byte window", Confidence = 1, NearZoneEnd = 0xEFF, Recents = 1, ReserveZero = true, ShortPunctuation = true, HotNeighbors = true, CompactControls = true },
        new("X2") { Description = "as X, but a 1-byte window survives up to 2 chars of other windows, earned one per char of its own", Confidence = 2, NearZoneEnd = 0xEFF, Recents = 1, ReserveZero = true, ShortPunctuation = true, HotNeighbors = true, CompactControls = true },
        new("Z") { Description = "as X2, but only a char that took more than 2 bytes makes its window the second 2-byte window", Confidence = 2, NearZoneEnd = 0xEFF, Recents = 1, ReserveZero = true, ShortPunctuation = true, HotNeighbors = true, CompactControls = true, LastOnlyIfLong = true },
        new("Z6") { Description = "as Z, but no byte below 0x06 is ever a trail byte, so the bytes of tab, line feed and carriage return mean nothing else", Confidence = 2, NearZoneEnd = 0xEFF, Recents = 1, ReserveZero = true, ShortPunctuation = true, HotNeighbors = true, CompactControls = true, LastOnlyIfLong = true, TrailFloor = 6 },
        new("Z7") { Description = "as Z6, with the 3-byte part after the BMP placed so that it ends with plane 1 (emoji keep 3 bytes)", Confidence = 2, NearZoneEnd = 0xEFF, Recents = 1, ReserveZero = true, ShortPunctuation = true, HotNeighbors = true, CompactControls = true, LastOnlyIfLong = true, TrailFloor = 6, PlaneOneCut = true },
        new("Z8") { Description = "as Z7, with no trail byte below 0x07: every byte below it starts a C0 control", Confidence = 2, NearZoneEnd = 0xEFF, Recents = 1, ReserveZero = true, ShortPunctuation = true, HotNeighbors = true, CompactControls = true, LastOnlyIfLong = true, PlaneOneCut = true, TrailFloor = 7 },
        new("Z9") { Description = "as Z7, with no trail byte below 0x08: the byte of the space means nothing else either", Confidence = 2, NearZoneEnd = 0xEFF, Recents = 1, ReserveZero = true, ShortPunctuation = true, HotNeighbors = true, CompactControls = true, LastOnlyIfLong = true, PlaneOneCut = true, TrailFloor = 8 },
        new("X7") { Description = "as X, with 2 bytes at most below U+0800 only", Confidence = 1, NearZoneEnd = 0x7FF, Recents = 1, ReserveZero = true, ShortPunctuation = true, HotNeighbors = true, CompactControls = true },
    ];

    /// <summary>
    /// Gets the options of the candidate that is Steno, version 1: Z9, which is Z with no trail byte below 0x08 and the cuts of the stretches
    /// moved so that plane 1 takes at most 3 bytes. The library implements it; Z, which it replaced, stays in the lab as history.
    /// </summary>
    internal static OrderedOptions Reference => _candidates.First(c => c.Name is "Z9");

    /// <summary>
    /// Gets the options of a candidate by its name.
    /// </summary>
    internal static OrderedOptions Candidate(string name) => _candidates.First(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Gets the strings that <see cref="Run"/> checks the order and the prefixes over: every distinct string of the corpus, with the paragraphs also cut
    /// into pieces of 8 and of 4 chars.
    /// </summary>
    internal static string[] GetOrderCheckStrings()
    {
        var corpus = Corpus.Load();
        var entries = corpus.ToList();

        foreach (int size in (ReadOnlySpan<int>)[8, 4])
        {
            entries.AddRange(entries
                .Where(e => e.Group == Corpus.UdhrParagraphs)
                .Select(e => new CorpusEntry($"UDHR, {size}-char snippets", e.Name, [.. e.Units.SelectMany(u => Chunk(u, size))]))
                .ToList());
        }

        return [.. entries.Concat(corpus).SelectMany(e => e.Units).Distinct(StringComparer.Ordinal)];
    }

    public static int Run(string[] args)
    {
        bool details = args.Contains("--details");
        string? filter = null;
        int filterIndex = Array.IndexOf(args, "--filter");

        if (filterIndex >= 0 && filterIndex + 1 < args.Length)
            filter = args[filterIndex + 1];

        string? folder = null;
        int folderIndex = Array.IndexOf(args, "--corpus");

        if (folderIndex >= 0 && folderIndex + 1 < args.Length)
            folder = args[folderIndex + 1];

        var candidates = _candidates;
        int candidatesIndex = Array.IndexOf(args, "--candidates");

        if (candidatesIndex >= 0 && candidatesIndex + 1 < args.Length)
        {
            string[] names = args[candidatesIndex + 1].Split(',');
            candidates = [.. _candidates.Where(c => names.Contains(c.Name, StringComparer.OrdinalIgnoreCase))];
        }

        List<CorpusEntry> entries;

        if (folder is not null)
        {
            entries = LoadFolder(folder);
        }
        else
        {
            entries = Corpus.Load().ToList();

            // Very short values: the paragraphs cut into pieces of 8 and of 4 chars.
            foreach (int size in (ReadOnlySpan<int>)[8, 4])
            {
                entries.AddRange(entries
                    .Where(e => e.Group == Corpus.UdhrParagraphs)
                    .Select(e => new CorpusEntry($"UDHR, {size}-char snippets", e.Name, [.. e.Units.SelectMany(u => Chunk(u, size))]))
                    .ToList());
            }
        }

        if (filter is not null)
            entries = [.. entries.Where(e => e.Group.Contains(filter, StringComparison.OrdinalIgnoreCase) || e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))];

        var references = new List<Codec>();

        if (Codecs.Bocu1 is not null && Codecs.Scsu is not null)
        {
            references.Add(Codecs.Bocu1);
            references.Add(Codecs.Scsu);
        }
        else
        {
            Console.WriteLine("ICU (icu.dll) is not available: the BOCU-1 and SCSU columns are omitted.");
        }

        references.Add(Codecs.Steno);
        int stenoColumn = references.Count - 1;

        Console.WriteLine("Encoded sizes in bytes. The lettered columns are byte-ordered candidates:");

        foreach (var candidate in candidates)
            Console.WriteLine($"  {candidate.Name}: {candidate.Description}");

        int columnCount = references.Count + candidates.Length;
        int failures = 0;
        long zChecked = 0;
        long zMismatches = 0;

        foreach (var group in entries.GroupBy(e => e.Group))
        {
            var rows = group.ToList();
            long[][] sizes = [.. rows.Select(_ => new long[columnCount])];

            for (int r = 0; r < rows.Count; r++)
            {
                foreach (string unit in rows[r].Units)
                {
                    for (int c = 0; c < references.Count; c++)
                        sizes[r][c] += references[c].Encode(unit).Length;
                }
            }

            Parallel.For(0, rows.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) }, r => {
                for (int c = 0; c < candidates.Length; c++)
                {
                    var coder = new OrderedLabCoder(candidates[c]);
                    bool isZ = candidates[c] == Reference;

                    foreach (string unit in rows[r].Units)
                    {
                        byte[] encoded = coder.Encode(unit);

                        if (coder.Decode(encoded) != unit)
                            Interlocked.Increment(ref failures);

                        // The reference candidate is Steno, version 1: a free extra oracle for the library.
                        if (isZ)
                        {
                            Interlocked.Increment(ref zChecked);

                            if (!encoded.AsSpan().SequenceEqual(StenoEncoding.Instance.GetBytes(unit)))
                                Interlocked.Increment(ref zMismatches);
                        }

                        sizes[r][references.Count + c] += encoded.Length;
                    }
                }
            });

            Console.WriteLine();
            Console.WriteLine($"== {group.Key} ==");
            Console.WriteLine();
            Console.WriteLine($"{"Name",-24}" + string.Concat(references.Select(c => $" {c.Name,9}")) + string.Concat(candidates.Select(c => $" {c.Name,9}")));

            if (details)
            {
                for (int r = 0; r < rows.Count; r++)
                    Console.WriteLine($"{(rows[r].Name.Length <= 24 ? rows[r].Name : rows[r].Name[..23] + "…"),-24}" + string.Concat(sizes[r].Select(s => $" {s,9}")));
            }

            long[] totals = [.. Enumerable.Range(0, columnCount).Select(c => sizes.Sum(s => s[c]))];

            Console.WriteLine($"{"TOTAL",-24}" + string.Concat(totals.Select(s => $" {s,9}")));
            Console.WriteLine($"{"  against Steno",-24}" + string.Concat(totals.Select(s => $" {Percent(s, totals[stenoColumn]),9}")));

            if (references.Count > 1)
                Console.WriteLine($"{"  against BOCU-1",-24}" + string.Concat(totals.Select(s => $" {Percent(s, totals[0]),9}")));
        }

        Console.WriteLine();
        Console.WriteLine(failures is 0 ? "Every candidate decoded every encoding back to its text." : $"{failures} encodings did NOT decode back to their text.");

        if (zChecked > 0)
        {
            Console.WriteLine(zMismatches is 0
                ? $"Candidate {Reference.Name} gave the bytes of the library (Steno, version 1) for all {zChecked:N0} values."
                : $"Candidate {Reference.Name} and the library (Steno, version 1) DISAGREE on {zMismatches:N0} of {zChecked:N0} values.");
        }

        // The order and prefix checks always run over every distinct string of the corpus: what a filter leaves would be too little to mean much.
        string[] strings = [.. (folder is null ? entries.Concat(Corpus.Load()) : entries).SelectMany(e => e.Units).Distinct(StringComparer.Ordinal)];

        foreach (var candidate in candidates)
        {
            var coder = new OrderedLabCoder(candidate);
            (string Text, byte[] Bytes)[] encoded = [.. strings.Select(s => (s, coder.Encode(s)))];
            Array.Sort(encoded, (x, y) => x.Bytes.AsSpan().SequenceCompareTo(y.Bytes));

            int orderErrors = 0;
            int prefixErrors = 0;

            for (int i = 1; i < encoded.Length; i++)
            {
                if (CompareCodePoints(encoded[i - 1].Text, encoded[i].Text) >= 0)
                    orderErrors++;
            }

            for (int i = 0; i < strings.Length; i += 25)
            {
                string text = strings[i];
                byte[] whole = coder.Encode(text);

                for (int length = 1; length < Math.Min(text.Length, 48); length++)
                {
                    if (char.IsLowSurrogate(text[length]) && char.IsHighSurrogate(text[length - 1]))
                        continue;

                    if (!whole.AsSpan().StartsWith(coder.Encode(text[..length])))
                        prefixErrors++;
                }
            }

            failures += orderErrors + prefixErrors;

            if (candidate.Recents <= 1 && candidate.CacheSize is 0 && candidate.NearZoneEnd is not 0)
            {
                int most = coder.GetMostByteValues(out int stateCount, out string worst, out int mostWithoutSecond);
                Console.WriteLine($"{candidate.Name}: {stateCount} states listed; the fullest layout takes {most} of 256 byte values ({worst}), " +
                    $"and {mostWithoutSecond} without a second 2-byte window.");

                if (most > 256)
                    failures++;
            }

            Console.WriteLine($"{candidate.Name}: {strings.Length} strings sorted by their bytes, {orderErrors} out of code point order; {prefixErrors} prefixes that did not encode to a prefix; " +
                $"{coder.LayoutCount} layouts, {coder.CrowdedLayoutCount} of them left something out for lack of lead bytes.");
        }

        return failures is 0 && zMismatches is 0 ? 0 : 1;
    }

    /// <summary>
    /// Loads the text files of a folder as a corpus. The part of a file name before the first underscore names the entry that the file belongs to,
    /// which makes "en_01.txt" and "en_02.txt" two documents of the entry "en".
    /// </summary>
    private static List<CorpusEntry> LoadFolder(string folder)
    {
        var files = Directory.EnumerateFiles(folder, "*.txt")
            .Order(StringComparer.Ordinal)
            .Select(path => (Name: Path.GetFileNameWithoutExtension(path).Split('_')[0], Lines: ReadLines(path)))
            .Where(f => f.Lines.Length > 0)
            .GroupBy(f => f.Name)
            .ToList();

        var entries = new List<CorpusEntry>();

        entries.AddRange(files.Select(g => new CorpusEntry("Folder, whole file", g.Key, [.. g.Select(f => string.Join('\n', f.Lines))])));
        entries.AddRange(files.Select(g => new CorpusEntry("Folder, each paragraph on its own", g.Key, [.. g.SelectMany(f => f.Lines)])));

        foreach (int size in (ReadOnlySpan<int>)[16, 8, 4])
            entries.AddRange(files.Select(g => new CorpusEntry($"Folder, {size}-char snippets", g.Key, [.. g.SelectMany(f => f.Lines).SelectMany(l => Chunk(l, size))])));

        return entries;

        static string[] ReadLines(string path)
        {
            return [
                .. File.ReadAllText(path, Encoding.UTF8)
                    .Normalize(NormalizationForm.FormC)
                    .Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0),
            ];
        }
    }

    private static IEnumerable<string> Chunk(string line, int maxChars)
    {
        int start = 0;

        while (start < line.Length)
        {
            int length = Math.Min(maxChars, line.Length - start);

            if (start + length < line.Length && char.IsHighSurrogate(line[start + length - 1]))
                length--;

            string piece = line.Substring(start, length).Trim();
            start += length;

            if (piece.Length > 0)
                yield return piece;
        }
    }

    private static string Percent(long value, long reference)
        => reference == 0 ? "n/a" : ((value - reference) * 100.0 / reference).ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%";

    private static int CompareCodePoints(string x, string y)
    {
        int i = 0;
        int j = 0;

        while (i < x.Length && j < y.Length)
        {
            int a = OrderedLabCoder.ReadCodePoint(x, ref i);
            int b = OrderedLabCoder.ReadCodePoint(y, ref j);

            if (a != b)
                return a.CompareTo(b);
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}

/// <summary>
/// The settings of one byte-ordered candidate.
/// </summary>
internal sealed record OrderedOptions(string Name)
{
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Gets the number of recently used windows that take 2 bytes per char.
    /// </summary>
    public int Recents { get; init; } = 6;

    /// <summary>
    /// Gets the number of chars of one window that must come in a row (ASCII aside) before it becomes the 1-byte window.
    /// </summary>
    public int SwitchAfter { get; init; } = 1;

    /// <summary>
    /// Gets the same for the 1-byte slot of Japanese text, which goes to hiragana, katakana or the ASCII letters.
    /// </summary>
    public int SlotSwitchAfter { get; init; } = 1;

    /// <summary>
    /// Gets the number of chars of other windows that a 1-byte window survives, earned one at a time by chars of its own. 0 turns this off.
    /// </summary>
    public int Confidence { get; init; }

    /// <summary>
    /// Gets the same for the 1-byte slot of Japanese text.
    /// </summary>
    public int SlotConfidence { get; init; }

    /// <summary>
    /// Gets the last code point of the stretch after ASCII that never takes more than 2 bytes per char, outside Chinese and Japanese text. 0 leaves
    /// that to the recently used windows.
    /// </summary>
    public int NearZoneEnd { get; init; }

    /// <summary>
    /// Gets a value indicating whether a char outside the 1-byte window only makes its window the second 2-byte window if it took more than 2 bytes
    /// itself. A char that takes 2 bytes anyway then leaves the layout as it is.
    /// </summary>
    public bool LastOnlyIfLong { get; init; }

    /// <summary>
    /// Gets a value indicating whether the chars next to the 1-byte window take 2 bytes: as many as one lead byte holds, on each side.
    /// </summary>
    public bool HotNeighbors { get; init; }

    /// <summary>
    /// Gets a value indicating whether the window that was the 1-byte window before takes 2 bytes per char.
    /// </summary>
    public bool PreviousHot { get; init; }

    /// <summary>
    /// Gets a value indicating whether only tab, line feed and carriage return have a byte of their own among the controls, which takes one byte
    /// value less than giving the two controls between them one too.
    /// </summary>
    public bool CompactControls { get; init; }

    /// <summary>
    /// Gets a value indicating whether the 2-byte range of general punctuation ends at U+20CF, where one lead byte still holds it without byte 0x00.
    /// </summary>
    public bool ShortPunctuation { get; init; }

    /// <summary>
    /// Gets a value indicating whether byte 0x00 is kept out of the encoded form, so that it can end a value inside a key of several values. That
    /// leaves 255 values for lead bytes and for each byte after one.
    /// </summary>
    public bool ReserveZero { get; init; }

    /// <summary>
    /// Gets the lowest byte value that a trail byte can have, or 0 for what <see cref="ReserveZero"/> gives (1 with it, 0 without). The byte values
    /// below it then only ever appear as the first byte of a code point.
    /// </summary>
    public int TrailFloor { get; init; }

    /// <summary>
    /// Gets a value indicating whether stretches are cut so that one lead byte's worth of 3-byte codes ends with plane 1 (at U+1FFFF), and not
    /// right after the BMP. The stretch below that part then runs on from the BMP into the start of plane 1.
    /// </summary>
    public bool PlaneOneCut { get; init; }

    /// <summary>
    /// Gets the number of values that a trail byte can have.
    /// </summary>
    public int Radix => 256 - (TrailFloor > 0 ? TrailFloor : ReserveZero ? 1 : 0);

    /// <summary>
    /// Gets a value indicating whether the controls and ASCII are encoded as their own byte values. That leaves 128 lead bytes for everything else,
    /// so the 1-byte window shrinks to 104 chars.
    /// </summary>
    public bool AsciiIdentity { get; init; }

    /// <summary>
    /// Gets a value indicating whether Japanese text has that slot at all. Without it kana take 2 bytes, like kanji.
    /// </summary>
    public bool KanaSlot { get; init; } = true;

    /// <summary>
    /// Gets the number of single chars that get a byte of their own: the last ones that took more than 1 byte.
    /// </summary>
    public int CacheSize { get; init; }
}

/// <summary>
/// A range of code points that takes a fixed number of bytes per code point (0 for "whatever the layout gives what is left over").
/// </summary>
internal readonly record struct OrderedPiece(int Start, int End, int Bytes);

/// <summary>
/// A split of all code points into ranges of 1 to 4 bytes each, with the lead bytes handed out in code point order. Bytes then sort like code points.
/// </summary>
internal sealed class OrderedLayout
{
    private readonly OrderedPiece[] _segments;
    private readonly int[] _firstLeads;
    private readonly short[] _segmentOfLead = new short[256];
    private readonly int _radix;
    private readonly int _leadStart;

    /// <summary>
    /// Gets the segments of the layout in code point order, each with its first lead byte. For the test vectors and the specification.
    /// </summary>
    internal IEnumerable<(OrderedPiece Segment, int FirstLead)> Segments => _segments.Select((s, i) => (s, _firstLeads[i]));

    private OrderedLayout(OrderedPiece[] segments, int[] firstLeads, int radix, int leadStart)
    {
        _segments = segments;
        _firstLeads = firstLeads;
        _radix = radix;
        _leadStart = leadStart;
        Array.Fill(_segmentOfLead, (short)-1);

        for (int s = 0; s < segments.Length; s++)
        {
            int leads = LeadCount(segments[s], radix);

            for (int i = 0; i < leads; i++)
                _segmentOfLead[firstLeads[s] + i] = (short)s;
        }
    }

    /// <summary>
    /// Builds the layout that the pieces give when each one is laid over the ones before it. Code points that no piece covers take 2 bytes where the
    /// stretch between two pieces has few enough of them for one lead byte, 3 bytes in the rest of planes 0 and 1 and 4 bytes beyond. Returns null if
    /// that needs more lead bytes than there are: 256, or 255 if byte 0x00 is kept out of the encoded form.
    /// </summary>
    public static OrderedLayout? TryBuild(List<OrderedPiece> pieces, bool reserveZero) => TryBuild(pieces, reserveZero, out _);

    /// <summary>
    /// Does the same and also returns the number of byte values that the layout takes up, counting the unused 0x00 if that is kept out.
    /// </summary>
    public static OrderedLayout? TryBuild(List<OrderedPiece> pieces, bool reserveZero, out int byteValues)
        => TryBuild(pieces, reserveZero, reserveZero ? 255 : 256, out byteValues);

    /// <summary>
    /// Does the same with a given number of values for a trail byte: the highest ones, so that the byte values below them are never trail bytes.
    /// </summary>
    public static OrderedLayout? TryBuild(List<OrderedPiece> pieces, bool reserveZero, int radix, out int byteValues, bool planeOneCut = false)
    {
        var segments = new List<OrderedPiece> { new(0, 0x10FFFF, 0) };

        foreach (var piece in pieces)
        {
            var next = new List<OrderedPiece>(segments.Count + 2);
            bool added = false;

            foreach (var segment in segments)
            {
                if (segment.End < piece.Start || segment.Start > piece.End)
                {
                    next.Add(segment);
                    continue;
                }

                if (segment.Start < piece.Start)
                    next.Add(segment with { End = piece.Start - 1 });

                if (!added)
                {
                    next.Add(piece);
                    added = true;
                }

                if (segment.End > piece.End)
                    next.Add(segment with { Start = piece.End + 1 });
            }

            segments = next;
        }

        var resolved = new List<OrderedPiece>(segments.Count + 4);

        foreach (var segment in segments)
        {
            if (segment.Bytes is not 0)
            {
                Append(resolved, segment);
                continue;
            }

            int start = segment.Start;

            // The second cut ends what one lead byte holds at 3 bytes after the BMP, so that every stretch takes exactly one lead byte.
            foreach (int cut in planeOneCut ? (ReadOnlySpan<int>)[0x20000 - (radix * radix), 0x20000, 0x110000] : [0x10000, 0x10000 + (radix * radix), 0x110000])
            {
                int end = Math.Min(segment.End, cut - 1);

                if (end < start)
                    continue;

                int size = end - start + 1;
                Append(resolved, new(start, end, size <= radix ? 2 : size <= radix * radix ? 3 : 4));
                start = end + 1;
            }
        }

        int[] firstLeads = new int[resolved.Count];
        int leadStart = reserveZero ? 1 : 0;
        int lead = leadStart;

        for (int s = 0; s < resolved.Count; s++)
        {
            firstLeads[s] = lead;
            lead += LeadCount(resolved[s], radix);
        }

        byteValues = lead;
        return lead <= 256 ? new OrderedLayout([.. resolved], firstLeads, radix, leadStart) : null;

        static void Append(List<OrderedPiece> list, OrderedPiece segment)
        {
            // Neighbours of the same size share lead bytes.
            if (list.Count > 0 && list[^1].Bytes == segment.Bytes && list[^1].End + 1 == segment.Start)
                list[^1] = list[^1] with { End = segment.End };
            else
                list.Add(segment);
        }
    }

    public int Encode(int codePoint, Span<byte> destination)
    {
        int low = 0;
        int high = _segments.Length - 1;

        while (low < high)
        {
            int middle = (low + high + 1) >> 1;

            if (_segments[middle].Start <= codePoint)
                low = middle;
            else
                high = middle - 1;
        }

        var segment = _segments[low];
        int offset = codePoint - segment.Start;
        int low256 = 256 - _radix;

        for (int i = segment.Bytes - 1; i > 0; i--)
        {
            destination[i] = (byte)((offset % _radix) + low256);
            offset /= _radix;
        }

        destination[0] = (byte)(_firstLeads[low] + offset);
        return segment.Bytes;
    }

    public int Decode(ReadOnlySpan<byte> source, out int codePoint)
    {
        int s = _segmentOfLead[source[0]];

        if (s < 0)
            throw new InvalidDataException("A lead byte that the layout does not use.");

        var segment = _segments[s];
        int offset = source[0] - _firstLeads[s];
        int low256 = 256 - _radix;

        for (int i = 1; i < segment.Bytes; i++)
        {
            // 00 is never in the encoded form; the bytes from 01 up to the lowest trail byte are only ever lead bytes.
            if (source[i] is 0)
                throw new InvalidDataException("A byte that the encoded form never has.");

            if (source[i] < low256)
                throw new InvalidDataException("A trail byte below the lowest trail byte.");

            offset = (offset * _radix) + source[i] - low256;
        }

        codePoint = segment.Start + offset;

        if (codePoint > segment.End)
            throw new InvalidDataException("A sequence beyond the end of its range.");

        return segment.Bytes;
    }

    private static int LeadCount(OrderedPiece segment, int radix)
    {
        int perLead = 1;

        for (int i = 1; i < segment.Bytes; i++)
            perLead *= radix;

        return (segment.End - segment.Start + perLead) / perLead;
    }
}

/// <summary>
/// Encodes and decodes one byte-ordered candidate. Built for finding out sizes, not for speed.
/// </summary>
internal sealed class OrderedLabCoder(OrderedOptions options)
{
    private const int NoWindow = -1;

    // The pseudo windows that the slot of Japanese text can wait for, next to real window starts.
    private const int AsciiLetters = -2;
    private const int HiraganaStart = 0x3040;
    private const int KatakanaStart = 0x30A0;

    private static readonly int[] _initialRecents = [0x0140, 0x0400, 0x0380, 0x0600, 0x0900, 0x0590, 0x0E00, 0x0530];

    // What the recently used windows start as when the scripts below U+0800 take 2 bytes anyway.
    private static readonly int[] _initialFarRecents = [0x0900, 0x0E00, 0x0980, 0x0B80, 0x0C00, 0x0A80, 0x1080, 0x0D00];

    private readonly Dictionary<string, OrderedLayout> _layouts = [];
    private readonly List<int> _recents = [];
    private readonly List<int> _cache = [];
    private readonly StringBuilder _key = new();

    private Context _context;
    private int _hot;
    private int _slot;
    private int _pending;
    private int _pendingCount;
    private int _slotPending;
    private int _slotPendingCount;
    private int _confidence;
    private int _slotConfidence;
    private int _previousHot;
    private OrderedLayout? _layout;

    private enum Context
    {
        General,
        Cjk,
        Hangul,
    }

    /// <summary>
    /// Gets the number of layouts that came up so far.
    /// </summary>
    public int LayoutCount => _layouts.Count;

    /// <summary>
    /// Gets how many of them could not fit everything the state asked for.
    /// </summary>
    public int CrowdedLayoutCount { get; private set; }

    /// <summary>
    /// Gets the layout of the state that the last call to <see cref="Encode"/> or <see cref="Decode"/> left. For the test vectors and the
    /// specification.
    /// </summary>
    internal OrderedLayout CurrentLayout => GetLayout();

    /// <summary>
    /// Describes the state that the last call to <see cref="Encode"/> or <see cref="Decode"/> left, for candidates with one second window at most.
    /// For the test vectors and the specification.
    /// </summary>
    internal string DescribeState()
    {
        static string Window(int start) => start is NoWindow ? "none" : $"{start:X4}-{WindowOf(start).End:X4}";

        string context = _context switch { Context.Cjk => "CJK", Context.Hangul => "Hangul", _ => "general" };
        string slot = _slot switch { HiraganaStart => "hiragana", KatakanaStart => "katakana", _ => "letters" };
        string second = _recents.Count > 0 ? Window(_recents[0]) : "none";
        string pending = _context is Context.General || _pending is NoWindow ? "none" : Window(_pending);

        return $"{context}; 1-byte {Window(_hot)}; second {second}; slot {slot}; confidence {_confidence}; pending {pending}";
    }

    public static int ReadCodePoint(string text, ref int index)
    {
        char c = text[index++];

        if (char.IsHighSurrogate(c) && index < text.Length && char.IsLowSurrogate(text[index]))
            return char.ConvertToUtf32(c, text[index++]);

        return c;
    }

    public byte[] Encode(string text)
    {
        Reset();

        using var output = new MemoryStream(text.Length + 8);
        Span<byte> buffer = stackalloc byte[4];
        int index = 0;

        while (index < text.Length)
        {
            int codePoint = ReadCodePoint(text, ref index);
            int length = GetLayout().Encode(codePoint, buffer);

            output.Write(buffer[..length]);
            Advance(codePoint, length);
        }

        return output.ToArray();
    }

    public string Decode(ReadOnlySpan<byte> bytes)
    {
        Reset();

        var text = new StringBuilder(bytes.Length);
        int previous = -1;

        while (bytes.Length > 0)
        {
            int length = GetLayout().Decode(bytes, out int codePoint);

            // A pair of surrogates is one code point and is encoded as one. Encoded as two it would give the same text a second form.
            if ((uint)(codePoint - 0xDC00) < 0x400 && (uint)(previous - 0xD800) < 0x400)
                throw new InvalidDataException("A low surrogate right after a high surrogate.");

            previous = codePoint;

            if (codePoint >= 0x10000)
                text.Append(char.ConvertFromUtf32(codePoint));
            else
                text.Append((char)codePoint);

            bytes = bytes[length..];
            Advance(codePoint, length);
        }

        return text.ToString();
    }

    /// <summary>
    /// Gets the window of a char: the range that becomes the 1-byte range when text of its script goes on. Chars without one do not change the state.
    /// </summary>
    private static (int Start, int End) WindowOf(int c)
    {
        if (c < 0xC0)
            return (NoWindow, 0);

        if (c < 0x300)
        {
            // Latin windows start at the first accented letter, U+00C0.
            int start = 0xC0 + ((c - 0xC0) & ~0x7F);
            return (start, Math.Min(start + 0x7F, 0x2FF));
        }

        if (c < 0x370)
            return (0x300, 0x36F);

        // The 16 code points in front of the Greek letters have no window: they would make a 1-byte window of 16 chars.
        if (c < 0x380)
            return (NoWindow, 0);

        if (c is >= 0x500 and < 0x600)
            return c < 0x530 ? (0x500, 0x52F) : c < 0x590 ? (0x530, 0x58F) : (0x590, 0x5FF);

        if (c is (>= 0x2000 and < 0x2100) or (>= 0x3000 and < 0x3100) or (>= 0x3400 and < 0xA000) or (>= 0xAC00 and < 0xE000) or (>= 0x20000 and < 0x40000))
            return (NoWindow, 0);

        return (c & ~0x7F, c | 0x7F);
    }

    private static (int Start, int End) WindowAt(int start) => WindowOf(start);

    private void Reset()
    {
        _context = Context.General;
        _hot = 0xC0;
        _slot = AsciiLetters;
        _pending = NoWindow;
        _pendingCount = 0;
        _slotPending = NoWindow;
        _slotPendingCount = 0;
        _confidence = 0;
        _slotConfidence = 0;
        _previousHot = NoWindow;
        _layout = null;

        _recents.Clear();
        if (options.Recents > 1)
            _recents.AddRange((options.NearZoneEnd is 0 ? _initialRecents : _initialFarRecents).AsSpan(0, Math.Min(options.Recents, _initialRecents.Length)));
        _cache.Clear();
    }

    /// <summary>
    /// Updates the state for a char that was just encoded or decoded. Only what both sides know goes into it: the text so far.
    /// </summary>
    private void Advance(int c, int encodedLength)
    {
        if (c >= 0x80 && options.CacheSize > 0)
        {
            int cached = _cache.IndexOf(c);

            if (cached >= 0)
            {
                // The order of the chars only decides which one goes first, so the layout stays as it is.
                _cache.RemoveAt(cached);
                _cache.Insert(0, c);
            }
            else if (encodedLength > 1)
            {
                _cache.Insert(0, c);

                if (_cache.Count > options.CacheSize)
                    _cache.RemoveAt(_cache.Count - 1);

                _layout = null;
            }
        }

        if (c < 0x80)
        {
            if (_context is Context.Cjk && char.IsAsciiLetter((char)c))
                AdvanceSlot(AsciiLetters);

            return;
        }

        if (c is >= 0xAC00 and <= 0xD7A3)
        {
            Enter(Context.Hangul);
            return;
        }

        if (c is (>= 0x3040 and < 0x3100) or (>= 0x3400 and < 0xA000) or (>= 0x20000 and < 0x40000) || (c is >= 0x3000 and < 0x3040 && _context is not Context.Hangul))
        {
            if (_context is not Context.Cjk)
            {
                Enter(Context.Cjk);
                _slot = AsciiLetters;
                _slotPending = NoWindow;
                _slotConfidence = 0;
            }

            _pending = NoWindow;

            // Only the letters of a kana move the slot to it, not the marks and signs around them.
            if (c is >= 0x3041 and <= 0x3096)
                AdvanceSlot(HiraganaStart);
            else if (c is >= 0x30A1 and <= 0x30FA)
                AdvanceSlot(KatakanaStart);

            return;
        }

        // Fullwidth forms belong to the text around them in the wide contexts.
        if (_context is not Context.General && c is >= 0xFF00 and <= 0xFFFF)
            return;

        int window = WindowOf(c).Start;

        if (window is NoWindow)
            return;

        if (_context is Context.General && window == _hot)
        {
            _pending = NoWindow;
            _confidence = Math.Min(_confidence + 1, options.Confidence);
            return;
        }

        if (!options.LastOnlyIfLong || encodedLength > 2)
        {
            _recents.Remove(window);
            _recents.Insert(0, window);
            _layout = null;

            if (_recents.Count > options.Recents)
                _recents.RemoveAt(_recents.Count - 1);
        }

        if (_context is Context.General && _confidence > 0)
        {
            _confidence--;
            return;
        }

        if (_pending != window)
        {
            _pending = window;
            _pendingCount = 0;
        }

        // Leaving a wide context for one char would make the next ideograph or syllable take 3 bytes, so that always needs two.
        if (++_pendingCount >= (_context is Context.General ? options.SwitchAfter : Math.Max(2, options.SwitchAfter)))
        {
            _recents.Remove(window);

            if (_hot != window)
            {
                _recents.Remove(_hot);
                _recents.Insert(0, _hot);
                _previousHot = _hot;

                if (_recents.Count > options.Recents)
                    _recents.RemoveAt(_recents.Count - 1);
            }

            _hot = window;
            _layout = null;
            _context = Context.General;
            _pending = NoWindow;
            _confidence = 0;
        }
    }

    private void Enter(Context context)
    {
        _pending = NoWindow;

        if (_context != context)
        {
            _context = context;
            _layout = null;
        }
    }

    private void AdvanceSlot(int target)
    {
        if (!options.KanaSlot)
            return;

        if (target == _slot)
        {
            _slotPending = NoWindow;
            _slotConfidence = Math.Min(_slotConfidence + 1, options.SlotConfidence);
            return;
        }

        if (_slotConfidence > 0)
        {
            _slotConfidence--;
            return;
        }

        if (_slotPending != target)
        {
            _slotPending = target;
            _slotPendingCount = 0;
        }

        if (++_slotPendingCount >= options.SlotSwitchAfter)
        {
            _slot = target;
            _slotPending = NoWindow;
            _slotConfidence = 0;
            _layout = null;
        }
    }

    /// <summary>
    /// Gets the window of a code point: its first and last code point, or (-1, 0) for none. For checking the layouts from outside.
    /// </summary>
    internal static (int Start, int End) WindowFor(int c) => WindowOf(c);

    /// <summary>
    /// Gets the pieces that the layout of a state is built from, for candidates with one second window at most: the context (0 general, 1 CJK,
    /// 2 Hangul), the kana slot (-2 for the ASCII letters, 0x3040 or 0x30A0), the first code point of the 1-byte window and of the second window
    /// (-1 for none). For checking the layouts from outside; the coder is left in its initial state.
    /// </summary>
    internal List<OrderedPiece> PiecesFor(int context, int slot, int hot, int second)
    {
        _context = (Context)context;
        _slot = slot;
        _hot = hot;
        _previousHot = NoWindow;
        _recents.Clear();
        _cache.Clear();

        if (second is not NoWindow)
            _recents.Add(second);

        var pieces = Compose(0, _recents.Count);
        Reset();
        return pieces;
    }

    /// <summary>
    /// Builds the layout of every state that the settings allow (or, where the previous window counts, of a spread of them) and returns the most byte
    /// values that one takes up. Only for settings with one recent window at most and without single chars, whose states can be listed.
    /// </summary>
    public int GetMostByteValues(out int stateCount, out string worst) => GetMostByteValues(out stateCount, out worst, out _);

    /// <summary>
    /// Does the same and also returns the most byte values that a layout without a second 2-byte window takes up. Such a window adds 2 at most:
    /// one lead byte for itself and one for the stretch that it cuts in two.
    /// </summary>
    public int GetMostByteValues(out int stateCount, out string worst, out int mostWithoutSecond)
    {
        mostWithoutSecond = 0;

        Reset();
        _recents.Clear();

        var hots = new List<int>();

        for (int c = 0xC0; c <= 0x10FFFF; c++)
        {
            int start = WindowOf(c).Start;

            if (start is not NoWindow && (hots.Count is 0 || hots[^1] != start))
                hots.Add(start);
        }

        int[] spread = [0xC0, 0x400, 0x900, 0xE80, 0xF00, 0x1200, 0x1F80, 0x2100, 0x2F80, 0x3100, 0xA000, 0xD780, 0xE000, 0xFB00, 0xFF80, 0x10000, 0x1F600, 0x1FF80, 0x20000, 0x10FF80];
        int most = 0;
        stateCount = 0;
        worst = string.Empty;

        foreach (int hot in hots)
        {
            var previous = new List<int> { NoWindow };

            if (options.PreviousHot || options.Recents > 0)
            {
                previous.AddRange(spread);

                if (Array.IndexOf(spread, hot) >= 0)
                    previous.AddRange(hots);

                for (int step = -4; step <= 4; step++)
                {
                    int near = hot + (step * 0x80);

                    if (near >= 0xC0 && near <= 0x10FFFF && WindowOf(near).Start is not NoWindow)
                        previous.Add(WindowOf(near).Start);
                }
            }

            foreach (int prev in previous)
            {
                if (prev == hot)
                    continue;

                foreach ((Context context, int slot) in (ReadOnlySpan<(Context, int)>)[
                    (Context.General, AsciiLetters), (Context.Hangul, AsciiLetters), (Context.Cjk, AsciiLetters), (Context.Cjk, HiraganaStart), (Context.Cjk, KatakanaStart),
                ])
                {
                    _hot = hot;
                    _previousHot = prev;
                    _context = context;
                    _slot = slot;
                    _recents.Clear();

                    if (options.Recents > 0 && prev is not NoWindow)
                        _recents.Add(prev);

                    OrderedLayout.TryBuild(Compose(0, _recents.Count), options.ReserveZero, options.Radix, out int byteValues, options.PlaneOneCut);
                    stateCount++;

                    if (prev is NoWindow)
                        mostWithoutSecond = Math.Max(mostWithoutSecond, byteValues);

                    if (byteValues > most)
                    {
                        most = byteValues;
                        worst = $"{context}, slot {slot:X}, 1-byte window {hot:X}, previous {prev:X}";
                    }
                }
            }
        }

        Reset();
        return most;
    }

    private OrderedLayout GetLayout()
    {
        if (_layout is not null)
            return _layout;

        _key.Clear();
        _key.Append((int)_context).Append('|').Append(_slot).Append('|').Append(_hot).Append('|').Append(options.PreviousHot ? _previousHot : 0).Append('|').AppendJoin(',', _recents).Append('|').AppendJoin(',', _cache);
        string key = _key.ToString();

        if (_layouts.TryGetValue(key, out _layout))
            return _layout;

        // What does not fit goes in a fixed order: the oldest single chars first, then the oldest windows.
        int cacheCount = _cache.Count;
        int recentCount = _recents.Count;

        while ((_layout = OrderedLayout.TryBuild(Compose(cacheCount, recentCount), options.ReserveZero, options.Radix, out _, options.PlaneOneCut)) is null)
        {
            if (cacheCount == _cache.Count && recentCount == _recents.Count)
                CrowdedLayoutCount++;

            if (cacheCount > 0)
                cacheCount--;
            else if (recentCount > 0)
                recentCount--;
            else
                throw new InvalidOperationException($"Candidate {options.Name}: the fixed part of the layout for {key} needs more than 256 lead bytes.");
        }

        _layouts.Add(key, _layout);
        return _layout;
    }

    private List<OrderedPiece> Compose(int cacheCount, int recentCount)
    {
        bool kana = _context is Context.Cjk && _slot is not AsciiLetters;

        var pieces = new List<OrderedPiece>();
        var hot = WindowAt(_hot);

        // These come first, so that everything with a place of its own goes over them.
        if (options.HotNeighbors && _context is Context.General)
        {
            int reach = options.Radix;

            if (hot.Start > 0x80)
                pieces.Add(new(Math.Max(0x80, hot.Start - reach), hot.Start - 1, 2));

            if (hot.End < 0x10FFFF)
                pieces.Add(new(hot.End + 1, Math.Min(0x10FFFF, hot.End + reach), 2));
        }

        if (options.PreviousHot && _previousHot is not NoWindow)
        {
            var previous = WindowAt(_previousHot);
            pieces.Add(new(previous.Start, previous.End, 2));
        }

        if (options.AsciiIdentity)
        {
            pieces.Add(new(0x00, 0x7F, 1));
            pieces.Add(new(0x80, 0xBF, 2));
        }
        else
        {
            // Tab, line feed and carriage return keep a byte of their own, with the two controls between them. The other controls take 2 bytes.
            pieces.Add(new(0x00, 0x1F, 2));

            if (options.CompactControls)
            {
                pieces.Add(new(0x09, 0x0A, 1));
                pieces.Add(new(0x0D, 0x0D, 1));
            }
            else
            {
                pieces.Add(new(0x09, 0x0D, 1));
            }

            if (kana)
            {
                // With kana in the 1-byte slot there are not enough lead bytes for the ASCII letters too.
                pieces.Add(new(0x20, 0x40, 1));
                pieces.Add(new(0x41, 0x7E, 2));
            }
            else
            {
                pieces.Add(new(0x20, 0x7E, 1));
            }

            pieces.Add(new(0x7F, 0xBF, 2)); // Latin-1 punctuation and symbols
        }

        if (options.NearZoneEnd is not 0 && _context is not Context.Cjk)
            pieces.Add(new(0x80, options.NearZoneEnd, 2));

        pieces.Add(new(0x2000, options.ShortPunctuation ? 0x20CF : 0x20FF, 2)); // General punctuation and currency symbols

        for (int i = recentCount - 1; i >= 0; i--)
        {
            var window = WindowAt(_recents[i]);
            pieces.Add(new(window.Start, window.End, 2));
        }

        switch (_context)
        {
            case Context.General:
                pieces.Add(new(hot.Start, hot.End, 2));
                pieces.Add(new(hot.Start, options.AsciiIdentity ? Math.Min(hot.End, hot.Start + 103) : hot.End, 1));
                break;

            case Context.Hangul:
                pieces.Add(new(hot.Start, hot.End, 2));
                pieces.Add(new(0x3000, 0x303F, 2));
                pieces.Add(new(0xAC00, 0xD7A3, 2));
                pieces.Add(new(0xFF00, 0xFFFF, 2));
                break;

            case Context.Cjk:
                pieces.Add(new(hot.Start, hot.End, 2));
                pieces.Add(new(0x3000, 0x300F, 1));
                pieces.Add(new(0x3010, 0x30FF, 2));

                // The letters of one kana, without the rarely used ends of its block.
                if (_slot is HiraganaStart)
                    pieces.Add(new(0x3041, 0x3096, 1));
                else if (_slot is KatakanaStart)
                    pieces.Add(new(0x30A1, 0x30FC, 1));

                pieces.Add(new(0x4E00, 0x9FFF, 2));
                pieces.Add(new(0xFF00, 0xFFFF, 2));

                // Chinese text takes its comma, colon, brackets and marks from the fullwidth forms.
                if (!kana && !options.AsciiIdentity)
                    pieces.Add(new(0xFF00, 0xFF1F, 1));

                break;
        }

        for (int i = 0; i < cacheCount; i++)
            pieces.Add(new(_cache[i], _cache[i], 1));

        return pieces;
    }
}
