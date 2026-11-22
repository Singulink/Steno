using System.Globalization;

namespace Singulink.Steno.Benchmarks;

/// <summary>
/// Prints the encoded sizes of every codec over the corpus, group by group, and checks that every codec gives every value back. The report command
/// writes the same measurements as data files, tables and charts.
/// </summary>
internal static class SizeReport
{
    public static int Run(string[] args)
    {
        string? filter = Report.GetOption(args, "--filter");
        string? wiki = Report.GetOption(args, "--wiki");
        bool details = args.Contains("--details");

        Console.WriteLine(IcuConverter.IsAvailable
            ? $"SCSU and BOCU-1: ICU4C {IcuConverter.Version} (icu.dll of Windows)."
            : "ICU (icu.dll) is not available: SCSU and BOCU-1 are left out.");

        var entries = (wiki is null ? Corpus.LoadForReport() : [.. Corpus.LoadForReport(), .. Corpus.LoadWikipedia(wiki)]).ToList();

        if (filter is not null)
        {
            entries = [.. entries.Where(e =>
                e.Group.Contains(filter, StringComparison.OrdinalIgnoreCase) || e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))];
        }

        var codecs = Codecs.All;
        (var rows, long failures) = SizeData.Measure(entries, codecs, null);

        Console.WriteLine("Bytes per char over each group (each value encoded on its own); in brackets, the size against Steno. Smaller is better.");

        foreach (var group in rows.GroupBy(r => r.Group))
        {
            Console.WriteLine();
            Console.WriteLine($"== {group.Key} ({group.Sum(r => r.Values):N0} values, {group.Sum(r => r.Chars):N0} chars) ==");

            if (details)
            {
                foreach (var row in group)
                    Print(row.Name, row.Chars, row.Bytes);
            }

            Print("TOTAL", group.Sum(r => r.Chars), codecs.ToDictionary(c => c.Name, c => group.Sum(r => r.Bytes[c.Name])));
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "Every codec gave every value back." : $"!! {failures} values did not come back.");
        return failures == 0 ? 0 : 1;

        void Print(string name, long chars, Dictionary<string, long> bytes)
        {
            Console.WriteLine($"  {(name.Length <= 24 ? name : name[..23] + "…"),-24}" + string.Concat(codecs.Select(c =>
                $"  {c.Name} {((double)bytes[c.Name] / chars).ToString("F2", CultureInfo.InvariantCulture)}" +
                (c.Name is "Steno" ? string.Empty : $" ({Relative(bytes[c.Name], bytes["Steno"])})"))));
        }

        static string Relative(long value, long steno)
        {
            return ((value - steno) * 100.0 / steno).ToString("+0;-0;0", CultureInfo.InvariantCulture) + "%";
        }
    }
}
