using System.Collections.Concurrent;

namespace Singulink.Steno.Benchmarks;

/// <summary>
/// Measures the encoded sizes of every codec over corpus entries, and checks that every codec gives every value back.
/// </summary>
internal static class SizeData
{
    /// <summary>
    /// Encodes every value of every entry with every codec, on 4 threads (each with codecs of its own), and decodes it back to check it. Returns the
    /// rows in the order of the entries, and the number of values that a codec did not give back.
    /// </summary>
    public static (List<SizeRow> Rows, long Failures) Measure(IReadOnlyList<CorpusEntry> entries, IReadOnlyList<Codec> codecs, Action<string>? progress)
    {
        var rows = new SizeRow?[entries.Count];
        var codecSets = new ConcurrentBag<Codec[]>();
        long failures = 0;
        int done = 0;

        Parallel.For(0, entries.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) }, i => {
            if (!codecSets.TryTake(out var local))
                local = [.. codecs.Select(c => c.Clone())];

            var entry = entries[i];
            int maxChars = entry.Units.Max(u => u.Length);
            byte[] bytes = new byte[local.Max(c => c.GetMaxByteCount(maxChars))];
            char[] chars = new char[maxChars + 16];
            var sizes = new Dictionary<string, long>();

            foreach (var codec in local)
            {
                long total = 0;

                foreach (string unit in entry.Units)
                {
                    int length = codec.Encode(unit, bytes);
                    total += length;

                    bool same;

                    try
                    {
                        same = chars.AsSpan(0, codec.Decode(bytes.AsSpan(0, length), chars)).SequenceEqual(unit);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        same = false;
                    }

                    if (!same && Interlocked.Increment(ref failures) <= 10)
                        Console.WriteLine($"!! {codec.Name} did not give back a value of {entry.Group} / {entry.Name}");
                }

                sizes[codec.Name] = total;
            }

            rows[i] = new SizeRow(entry.Group, entry.Name, entry.Units.Count, entry.CharCount, sizes);
            codecSets.Add(local);

            int count = Interlocked.Increment(ref done);

            if (count % 50 == 0 || count == entries.Count)
                progress?.Invoke($"  sizes: {count} of {entries.Count} sets of values");
        });

        return ([.. rows.Select(r => r!)], failures);
    }
}

/// <summary>
/// The encoded size of one set of values (a group of a corpus for one language or file) in every codec.
/// </summary>
internal sealed record SizeRow(string Group, string Name, int Values, long Chars, Dictionary<string, long> Bytes);
