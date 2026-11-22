using System.Diagnostics;

namespace Singulink.Steno.Benchmarks;

/// <summary>
/// Checks that every layout of the byte-ordered format (Steno, version 1: the reference candidate of <see cref="OrderedLab"/>, or another with
/// --candidate) fits in the byte values, for every state: every window as the 1-byte window, in every context and kana slot, with every window or
/// none as the second window. It also finds the most bytes that a code point takes in any layout, below U+10000, below U+20000 and above.
/// </summary>
/// <remarks>
/// The pieces of each layout come from the reference codec. Building all the layouts with the reference would take an hour, so they are laid and
/// counted here with a lean copy of <see cref="OrderedLayout.TryBuild(List{OrderedPiece}, bool, int, out int, bool)"/>, with the radix and the cuts
/// of the candidate, which is checked against it on every layout without a second window and on a regular sample of the others.
/// </remarks>
internal static class OrderedFit
{
    private const int NoWindow = -1;
    private const int SampleEvery = 1009;

    public static int Run(string[] args)
    {
        var stopwatch = Stopwatch.StartNew();
        string? name = Report.GetOption(args, "--candidate");
        var options = name is null ? OrderedLab.Reference : OrderedLab.Candidate(name);
        var coder = new OrderedLabCoder(options);
        int square = options.Radix * options.Radix;
        int[] cuts = options.PlaneOneCut ? [0x20000 - square, 0x20000, 0x110000] : [0x10000, 0x10000 + square, 0x110000];
        var format = new Format(options.Radix, cuts);

        Console.WriteLine($"Candidate {options.Name}: radix {format.Radix}, stretches cut at {string.Join(", ", format.Cuts[..^1].Select(c => $"U+{c:X4}"))}.");

        var windows = new List<(int Start, int End)>();

        for (int c = 0; c <= 0x10FFFF; c++)
        {
            var window = OrderedLabCoder.WindowFor(c);

            if (window.Start != NoWindow && (windows.Count == 0 || windows[^1].Start != window.Start))
                windows.Add(window);
        }

        (int Context, int Slot, string Name)[] kinds = [
            (0, -2, "general"), (2, -2, "Hangul"), (1, -2, "CJK, letters"), (1, 0x3040, "CJK, hiragana"),
            (1, 0x30A0, "CJK, katakana"),
        ];

        // The pieces of every state without a second window, and where the second window goes among them (the reference is not thread-safe).
        var bases = new List<(int Kind, int Hot, List<OrderedPiece> Pieces, int Insert)>();

        for (int k = 0; k < kinds.Length; k++)
        {
            foreach (var hot in windows)
            {
                var pieces = coder.PiecesFor(kinds[k].Context, kinds[k].Slot, hot.Start, NoWindow);
                int probe = hot.Start == 0x10FF80 ? 0x40000 : 0x10FF80;
                var withSecond = coder.PiecesFor(kinds[k].Context, kinds[k].Slot, hot.Start, probe);
                int insert = 0;

                while (insert < pieces.Count && pieces[insert] == withSecond[insert])
                    insert++;

                bases.Add((k, hot.Start, pieces, insert));
            }
        }

        Console.WriteLine($"{windows.Count:N0} windows; {bases.Count:N0} states without a second window ({kinds.Length} contexts and kana slots).");

        object sync = new();
        int mostWithout = 0;
        string mostWithoutAt = string.Empty;
        int most = 0;
        string mostAt = string.Empty;
        int mostAdded = int.MinValue;
        string mostAddedAt = string.Empty;
        long pairs = 0;
        long over = 0;
        long checkedAgainstReference = 0;
        long disagreements = 0;
        var overAt = new List<string>();
        int[] maxBytes = new int[3];

        // Parts of a stretch of exactly the lengths where the size changes (radix, radix + 1, radix^2, radix^2 + 1): an example of each, if any.
        int[] bounds = [format.Radix, format.Radix + 1, format.Radix * format.Radix, (format.Radix * format.Radix) + 1];
        string?[] boundExamples = new string?[bounds.Length];

        Parallel.ForEach(bases, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) }, item => {
            (int k, int hot, var pieces, int insert) = item;
            string name = kinds[k].Name;
            int[] localMax = new int[3];
            int[] partStarts = new int[bounds.Length];
            string?[] localExamples = new string?[bounds.Length];
            int baseValues = Values(pieces, format, localMax, bounds, partStarts);
            Note(partStarts, $"{name}, main window {hot:X4}, no second window");
            long localChecks = 1;
            var referenceLayout = OrderedLayout.TryBuild(pieces, true, format.Radix, out int referenceValues, options.PlaneOneCut);
            long localDisagreements = referenceLayout is null || referenceValues != baseValues ? 1 : 0;

            int localMost = 0;
            string localMostAt = string.Empty;
            int localAdded = int.MinValue;
            string localAddedAt = string.Empty;
            long localPairs = 0;
            long localOver = 0;
            var localOverAt = new List<string>();
            var spliced = new List<OrderedPiece>(pieces.Count + 1);

            void Note(int[] starts, string state)
            {
                for (int i = 0; i < starts.Length; i++)
                {
                    if (starts[i] >= 0)
                        localExamples[i] ??= $"{state}: the part from U+{starts[i]:X4}";
                }
            }

            foreach (var second in windows)
            {
                // In the general context the second window is never the 1-byte window.
                if (kinds[k].Context is 0 && second.Start == hot)
                    continue;

                spliced.Clear();
                spliced.AddRange(pieces);
                spliced.Insert(insert, new(second.Start, second.End, 2));

                int values = Values(spliced, format, localMax, bounds, partStarts);
                Note(partStarts, $"{name}, main window {hot:X4}, second window {second.Start:X4}");
                localPairs++;

                if (localPairs % SampleEvery == 0)
                {
                    localChecks++;

                    bool built = OrderedLayout.TryBuild(spliced, true, format.Radix, out int reference, options.PlaneOneCut) is not null;

                    if (built ? reference != values : values <= 256)
                        localDisagreements++;
                }

                if (values > localMost)
                {
                    localMost = values;
                    localMostAt = $"{name}, 1-byte window {hot:X4}, second window {second.Start:X4}";
                }

                if (values - baseValues > localAdded)
                {
                    localAdded = values - baseValues;
                    localAddedAt = $"{name}, 1-byte window {hot:X4}, second window {second.Start:X4}";
                }

                if (values > 256)
                {
                    localOver++;

                    if (localOverAt.Count < 10)
                        localOverAt.Add($"{name}, 1-byte window {hot:X4}, second window {second.Start:X4}: {values}");
                }
            }

            lock (sync)
            {
                for (int i = 0; i < maxBytes.Length; i++)
                    maxBytes[i] = Math.Max(maxBytes[i], localMax[i]);

                for (int i = 0; i < bounds.Length; i++)
                    boundExamples[i] ??= localExamples[i];

                pairs += localPairs;
                over += localOver;
                checkedAgainstReference += localChecks;
                disagreements += localDisagreements;

                if (overAt.Count < 10)
                    overAt.AddRange(localOverAt.Take(10 - overAt.Count));

                if (baseValues > mostWithout)
                {
                    mostWithout = baseValues;
                    mostWithoutAt = $"{name}, 1-byte window {hot:X4}";
                }

                if (localMost > most)
                {
                    most = localMost;
                    mostAt = localMostAt;
                }

                if (localAdded > mostAdded)
                {
                    mostAdded = localAdded;
                    mostAddedAt = localAddedAt;
                }
            }
        });

        Console.WriteLine($"Without a second window: at most {mostWithout} byte values, counting the unused 0x00 ({mostWithoutAt}).");
        Console.WriteLine($"With one ({pairs:N0} combinations): at most {most} byte values ({mostAt}).");
        Console.WriteLine($"A second window adds at most {mostAdded} byte values ({mostAddedAt}).");
        Console.WriteLine($"{over:N0} combinations need more than 256 byte values.");
        Console.WriteLine($"The most bytes of a code point in any layout: {maxBytes[0]} below U+10000, {maxBytes[1]} in U+10000..U+1FFFF, " +
            $"{maxBytes[2]} from U+20000.");

        for (int i = 0; i < bounds.Length; i++)
            Console.WriteLine($"A part of {bounds[i]:N0} code points: {boundExamples[i] ?? "in no layout"}.");

        foreach (string line in overAt)
            Console.WriteLine($"  {line}");

        Console.WriteLine($"Checked against the reference's own layout building on {checkedAgainstReference:N0} layouts: {disagreements:N0} disagreements.");
        Console.WriteLine($"Done in {stopwatch.Elapsed.TotalSeconds:F0} s.");

        return over == 0 && disagreements == 0 ? 0 : 1;
    }

    /// <summary>
    /// Lays the pieces over each other, gives the stretches their size and returns the byte values that the layout takes, counting the unused 0x00: the
    /// same as the reference, with the radix and cuts of the format. Raises the most bytes per code point seen below U+10000, in U+10000..U+1FFFF and
    /// from U+20000 (<paramref name="maxBytes"/>), and gives the first code point of a part of each length of <paramref name="bounds"/>, or -1.
    /// </summary>
    private static int Values(List<OrderedPiece> pieces, Format format, int[] maxBytes, int[] bounds, int[] partStarts)
    {
        int radix = format.Radix;
        Array.Fill(partStarts, -1);

        var segments = new List<OrderedPiece>(48) { new(0, 0x10FFFF, 0) };
        var next = new List<OrderedPiece>(48);

        foreach (var piece in pieces)
        {
            next.Clear();
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

            (segments, next) = (next, segments);
        }

        int leads = 0;
        int runStart = -1;
        int runEnd = -1;
        int runBytes = 0;

        void Add(int start, int end, int bytes)
        {
            if (start < 0x10000)
                maxBytes[0] = Math.Max(maxBytes[0], bytes);

            if (start < 0x20000 && end >= 0x10000)
                maxBytes[1] = Math.Max(maxBytes[1], bytes);

            if (end >= 0x20000)
                maxBytes[2] = Math.Max(maxBytes[2], bytes);

            if (runBytes == bytes && runEnd + 1 == start)
            {
                runEnd = end;
                return;
            }

            Flush();
            (runStart, runEnd, runBytes) = (start, end, bytes);
        }

        void Flush()
        {
            if (runBytes is 0)
                return;

            long perLead = runBytes switch { 1 => 1, 2 => radix, 3 => radix * radix, _ => (long)radix * radix * radix };
            leads += (int)((runEnd - runStart + perLead) / perLead);
        }

        foreach (var segment in segments)
        {
            if (segment.Bytes is not 0)
            {
                Add(segment.Start, segment.End, segment.Bytes);
                continue;
            }

            int start = segment.Start;

            foreach (int cut in format.Cuts)
            {
                int end = Math.Min(segment.End, cut - 1);

                if (end < start)
                    continue;

                int size = end - start + 1;
                int bound = Array.IndexOf(bounds, size);

                if (bound >= 0)
                    partStarts[bound] = start;

                Add(start, end, size <= radix ? 2 : size <= radix * radix ? 3 : 4);
                start = end + 1;
            }
        }

        Flush();
        return leads + 1;
    }

    /// <summary>
    /// The parameters of a format that the layouts depend on: the number of values of a trail byte, and where stretches are cut (the last is the end
    /// of the code space).
    /// </summary>
    private sealed record Format(int Radix, int[] Cuts);
}
