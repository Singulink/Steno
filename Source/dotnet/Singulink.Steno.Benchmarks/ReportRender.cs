using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Singulink.Steno.Benchmarks;

/// <summary>
/// Writes the Markdown fragments and the SVG charts of the report from its data files. It measures nothing, so it can be run again at any time.
/// </summary>
/// <remarks>
/// The fragments are included as they are in the pages of the documentation site, so they are written for its readers: titles as in the site's table
/// of contents, display names instead of file names, and the note on how to read each table above the table.
/// </remarks>
internal static partial class ReportRender
{
    // A size loss against SCSU, BOCU-1 or UTF-16 that is smaller than this (in percent) is counted, but not listed row by row.
    private const double SmallLoss = 0.5;

    // How sizes are counted, said once at the top of every size fragment.
    private const string SizeUnits = "Bytes per character, counting characters as UTF-16 code units (an emoji counts as 2); smaller is better. Each value " +
        "is encoded on its own, as a database stores a field.";

    // Which sizes are the same in every implementation, said wherever sizes are introduced.
    private const string SizeSource = "Steno, UTF-8, UTF-16 and BOCU-1 give one encoding per text, so their sizes are the same in every correct " +
        "implementation; SCSU leaves choices to the encoder, and its sizes are those of ICU's encoder.";

    // Where the derived figures are explained, from a fragment in Docs/benchmarks.
    private const string MethodLink = "../articles/benchmarks/machine.md#derived-figures";

    private static readonly string[] CodecOrder = [
        "Steno", "UTF-8", "UTF-16", "SCSU", "BOCU-1", "Deflate fast", "Deflate max", "Brotli 1", "Brotli 11", "LZ4 fast", "LZ4 max", "Zstd 1", "Zstd 19",
    ];

    // The encodings, as against the compressors.
    private static readonly string[] Encodings = ["Steno", "UTF-8", "UTF-16", "SCSU", "BOCU-1"];

    // Title: the H1 of the group's page, as in the site's table of contents. Label: the group in a table row. Short: a column header. Slug: the file
    // name of the group's page, which must not change (the site and other pages link to it).
    private static readonly SizeGroup[] BuiltinGroups = [
        new(Corpus.UdhrSnippets4, "UDHR, 4-Char Values", "UDHR, 4-char values", "4 chars", "udhr-4-char-values"),
        new(Corpus.UdhrSnippets8, "UDHR, 8-Char Values", "UDHR, 8-char values", "8 chars", "udhr-8-char-values"),
        new(Corpus.UdhrSnippets, "UDHR, 16-Char Values", "UDHR, 16-char values", "16 chars", "udhr-16-char-values"),
        new(Corpus.UdhrParagraphs, "UDHR, Paragraphs", "UDHR, paragraphs", "Paragraphs", "udhr-each-paragraph-a-value"),
        new(Corpus.UdhrDocs, "UDHR, Documents", "UDHR, documents", "Documents", "udhr-each-document-a-value"),
        new(Corpus.MixedLines, "Mixed Content, Lines", "Mixed content, lines", "Mixed, lines", "mixed-content-each-line-a-value"),
        new(Corpus.MixedDocs, "Mixed Content, Documents", "Mixed content, documents", "Mixed, documents", "mixed-content-each-document-a-value"),
        new(Corpus.ShortStrings, "Names, Places, Titles, UI Strings", "Names and UI strings", "Names and UI strings", "names-places-titles-ui-strings"),
    ];

    private static readonly SizeGroup[] WikiGroups = [
        new(Corpus.WikiSnippets4, "Wikipedia, 4-Char Values", "Wikipedia, 4-char values", "4 chars", "wikipedia-4-char-values"),
        new(Corpus.WikiSnippets8, "Wikipedia, 8-Char Values", "Wikipedia, 8-char values", "8 chars", "wikipedia-8-char-values"),
        new(Corpus.WikiSnippets, "Wikipedia, 16-Char Values", "Wikipedia, 16-char values", "16 chars", "wikipedia-16-char-values"),
        new(Corpus.WikiParagraphs, "Wikipedia, Paragraphs", "Wikipedia, paragraphs", "Paragraphs", "wikipedia-each-paragraph-a-value"),
        new(Corpus.WikiDocs, "Wikipedia, Articles", "Wikipedia, articles", "Articles", "wikipedia-each-article-a-value"),
    ];

    // Display names of the files of the mixed content and of the names and UI strings, which the data identifies by file name.
    private static readonly Dictionary<string, string> TextNames = new() {
        ["bilingual_ar_en"] = "Bilingual Arabic and English",
        ["chat_ar_emoji"] = "Arabic chat with emoji",
        ["chat_en_emoji"] = "English chat with emoji",
        ["chat_es_emoji"] = "Spanish chat with emoji",
        ["chat_hi_emoji"] = "Hindi chat with emoji",
        ["chat_ja_emoji"] = "Japanese chat with emoji",
        ["chat_ko_emoji"] = "Korean chat with emoji",
        ["chat_ru_emoji"] = "Russian chat with emoji",
        ["chat_zh_emoji"] = "Chinese chat with emoji",
        ["en_typographic"] = "English with typographic punctuation",
        ["hinglish"] = "Hindi and English mixed",
        ["html_el"] = "Greek HTML",
        ["json_catalog"] = "JSON catalog",
        ["math_science"] = "Math and science notation",
        ["multilingual_ui"] = "UI strings in many languages",
        ["news_ja"] = "Japanese news",
        ["news_ko"] = "Korean news",
        ["news_zh"] = "Chinese news",
        ["social_posts"] = "Social media posts",
        ["tech_ja_en"] = "Japanese technical text with English",
        ["tech_ko_en"] = "Korean technical text with English",
        ["tech_ru_en"] = "Russian technical text with English",
        ["tech_zh_en"] = "Chinese technical text with English",
        ["person_names"] = "Names of people",
        ["place_names"] = "Places and addresses",
        ["titles"] = "Titles",
        ["ui_strings"] = "UI strings",
    };

    public static int Run(string output)
    {
        string data = Path.Combine(output, "data");
        string charts = Path.Combine(output, "charts");
        string languages = Path.Combine(output, "sizes-by-language");
        Directory.CreateDirectory(charts);
        Directory.CreateDirectory(languages);

        var machine = Load<MachineInfo>(Path.Combine(data, "machine.json"));
        var builtin = Load<SizeFile>(Path.Combine(data, "sizes-builtin.json"));
        var wiki = Load<SizeFile>(Path.Combine(data, "sizes-wikipedia.json"));
        var ascii = Load<List<AsciiRecord>>(Path.Combine(data, "ascii.json"));
        string speedPath = Path.Combine(data, "speed.jsonl");
        var speed = File.Exists(speedPath)
            ? File.ReadLines(speedPath).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<SpeedRecord>(l)!).ToList()
            : null;

        var written = new List<string>();

        void Write(string path, string text)
        {
            File.WriteAllText(path, text.ReplaceLineEndings("\n"), new UTF8Encoding(false));
            written.Add(Path.GetRelativePath(output, path).Replace('\\', '/'));
        }

        var speedData = LoadSpeed(output, speed, machine);

        if (machine is not null)
            Write(Path.Combine(output, "machine.md"), MachineMarkdown(machine, builtin, wiki, speedData));

        if (builtin is not null)
        {
            Write(Path.Combine(output, "sizes.md"), SizesSummary(builtin, wiki));

            foreach (var file in (SizeFile?[])[builtin, wiki])
            {
                if (file is null)
                    continue;

                foreach (var group in file == builtin ? BuiltinGroups : WikiGroups)
                    Write(Path.Combine(languages, group.Slug + ".md"), SizesByLanguage(file, group));
            }

            var udhrDocs = BuiltinGroups.First(g => g.Group == Corpus.UdhrDocs);
            var udhr16 = BuiltinGroups.First(g => g.Group == Corpus.UdhrSnippets);
            Write(Path.Combine(charts, "size-vs-utf8-udhr.svg"), SizeVersusUtf8Chart(builtin, udhrDocs, "UDHR documents"));
            Write(Path.Combine(charts, "size-vs-utf8-udhr-16char.svg"), SizeVersusUtf8Chart(builtin, udhr16, "UDHR, 16-char values"));
            Write(Path.Combine(charts, "size-by-length-udhr.svg"), SizeByLengthChart(builtin, BuiltinGroups[..5], "UDHR, 47 languages"));

            if (wiki is not null)
            {
                var wikiDocs = WikiGroups.First(g => g.Group == Corpus.WikiDocs);
                var wiki16 = WikiGroups.First(g => g.Group == Corpus.WikiSnippets);
                Write(Path.Combine(charts, "size-vs-utf8-wikipedia.svg"), SizeVersusUtf8Chart(wiki, wikiDocs, "Wikipedia articles"));
                Write(Path.Combine(charts, "size-vs-utf8-wikipedia-16char.svg"), SizeVersusUtf8Chart(wiki, wiki16, "Wikipedia, 16-char values"));
                string what = $"Wikipedia, {wiki.Rows.Select(r => r.Name).Distinct().Count()} languages";
                Write(Path.Combine(charts, "size-by-length-wikipedia.svg"), SizeByLengthChart(wiki, WikiGroups, what));
            }
        }

        Write(Path.Combine(output, "speed.md"), NativeSpeedMarkdown(speedData));
        Write(Path.Combine(output, "compare.md"), NativeCompareMarkdown(speedData));
        Write(Path.Combine(output, "implementations.md"), SideBySideMarkdown(speedData));
        Write(Path.Combine(output, "headline.md"), HeadlineMarkdown(speedData));

        foreach (var implementation in Implementations)
            Write(Path.Combine(output, $"impl-{implementation.Id}.md"), ImplementationMarkdown(speedData, implementation));

        if (ascii is not null)
            Write(Path.Combine(output, "ascii.md"), AsciiMarkdown(ascii, speedData));

        if (builtin is not null)
            WriteComparisons(output, Write, builtin, wiki, speedData);

        Write(Path.Combine(output, "README.md"), IndexMarkdown(written, machine));

        Console.WriteLine($"   wrote {written.Count} files");
        return 0;
    }

    // ---------- Sizes ----------

    private static string SizesSummary(SizeFile builtin, SizeFile? wiki)
    {
        var md = new StringBuilder();
        md.AppendLine("# Encoded Size");
        md.AppendLine();
        md.AppendLine("**" + SmallestGroupsText(builtin, wiki) + "**");
        md.AppendLine();
        md.AppendLine(SizeUnits + " In parentheses is the size against Steno: + is larger than Steno, − is smaller. The compressors compress the " +
            "UTF-8 of each value by itself, with nothing shared between values. " + SizeSource);
        md.AppendLine();

        foreach ((var file, var groups, string title, string what) in (ReadOnlySpan<(SizeFile?, SizeGroup[], string, string)>)[
            (builtin, BuiltinGroups, "Built-in Corpus", "The UDHR in 47 languages, 23 files of mixed content, and names, places, titles and UI strings."),
            (wiki, WikiGroups, "Wikipedia", wiki is null ? string.Empty : WikiSource(wiki) + "."),
        ])
        {
            if (file is null)
                continue;

            md.AppendLine($"## {title}");
            md.AppendLine();
            md.AppendLine(what);
            md.AppendLine();

            var totals = groups.Select(g => (Group: g, Total: Total(file, g.Group))).ToList();
            string[] headers = ["Codec", .. totals.Select(t => t.Group.Short)];
            var rows = new List<string[]>();

            foreach (string codec in Ordered(file.Codecs))
            {
                rows.Add([
                    Bold(codec, codec is "Steno"), .. totals.Select(t => {
                        double perChar = (double)t.Total.Bytes[codec] / t.Total.Chars;
                        return codec is "Steno" ? $"{perChar:F2}" : $"{perChar:F2} ({Relative(t.Total.Bytes[codec], t.Total.Bytes["Steno"])})";
                    }),
                ]);
            }

            md.Append(Table(headers, rows));
            md.AppendLine();
            md.AppendLine("The groups of values:");
            md.AppendLine();
            md.AppendLine("| Group | Values | Characters |");
            md.AppendLine("|---|--:|--:|");

            foreach (var (group, total) in totals)
                md.AppendLine(Invariant($"| {group.Label} | {total.Values:N0} | {total.Chars:N0} |"));

            md.AppendLine();
        }

        return md.ToString();
    }

    private static string SizesByLanguage(SizeFile file, SizeGroup group)
    {
        var rows = file.Rows.Where(r => r.Group == group.Group).ToList();
        string[] codecs = Ordered(file.Codecs);
        bool byFile = group.Group is Corpus.MixedLines or Corpus.MixedDocs or Corpus.ShortStrings;
        string first = byFile ? "Text" : "Language";
        var md = new StringBuilder();

        md.AppendLine($"# {group.Title}");
        md.AppendLine();
        md.AppendLine($"Source: {GroupSource(file, group)}. " + SizeUnits + " The second table gives the size of each codec against Steno: + is " +
            "larger than Steno, − is smaller. " + SizeSource);
        md.AppendLine();

        var total = Total(file, group.Group);
        string[] headers = [first, "Values", "Chars", .. codecs.Select(c => Bold(c, c is "Steno"))];

        md.Append(Table(headers, [
            .. rows.Select(r => (string[])[
                DisplayName(r.Name), Invariant($"{r.Values:N0}"), Invariant($"{r.Chars:N0}"), .. codecs.Select(c => PerChar(r.Bytes[c], r.Chars, c)),
            ]),
            ["**Total**", Invariant($"{total.Values:N0}"), Invariant($"{total.Chars:N0}"), .. codecs.Select(c => PerChar(total.Bytes[c], total.Chars, c))],
        ]));

        md.AppendLine();
        md.AppendLine("Against Steno (+ is larger than Steno, − is smaller):");
        md.AppendLine();

        string[] others = [.. codecs.Where(c => c is not "Steno")];

        md.Append(Table([first, .. others], [
            .. rows.Select(r => (string[])[DisplayName(r.Name), .. others.Select(c => Relative(r.Bytes[c], r.Bytes["Steno"]))]),
            ["**Total**", .. others.Select(c => Relative(total.Bytes[c], total.Bytes["Steno"]))],
        ]));

        return md.ToString();

        static string PerChar(long bytes, long chars, string codec) => ((double)bytes / chars).ToString("F2", CultureInfo.InvariantCulture);
    }

    private static string SizeVersusUtf8Chart(SizeFile file, SizeGroup group, string what)
    {
        var rows = file.Rows.Where(r => r.Group == group.Group).ToList();
        string[] series = [.. ((string[])["Steno", "SCSU", "BOCU-1", "UTF-16"]).Where(file.Codecs.Contains)];
        string note = "SCSU sizes are those of ICU's encoder.";

        // The compressors are left out of the chart. Where they are smaller than Steno, say by how much, so that the chart does not flatter.
        string[] compressors = [.. file.Codecs.Where(c => !Encodings.Contains(c))];
        double[] best = [.. rows.Select(r => compressors.Min(c => (r.Bytes[c] - r.Bytes["Steno"]) * 100.0 / r.Bytes["Steno"]))];

        if (best.Any(b => b < 0))
        {
            note = Invariant($"Compressors are not shown: the smallest is {-best.Max():F0}% to {-best.Min():F0}% smaller than Steno in each language ") +
                $"(Size by Language, {group.Title}). SCSU sizes are those of ICU's encoder.";
        }

        return new SvgChart {
            Title = $"Encoded size against UTF-8, by language ({what})",
            Subtitle = "Size as a percentage of the UTF-8 size; smaller is better. 100% is UTF-8.",
            Categories = [.. rows.Select(r => DisplayName(r.Name))],
            Series = series,
            Values = [.. rows.Select(r => series.Select(s => 100.0 * r.Bytes[s] / r.Bytes["UTF-8"]).ToArray())],
            Format = v => v.ToString("F0", CultureInfo.InvariantCulture) + "%",
            Description = $"Bar chart of the encoded size of {what} in Steno, SCSU, BOCU-1 and UTF-16 as a percentage of UTF-8, per language. " +
                $"The same numbers are in the table {group.Title}, under Size by Language.",
            Note = note,
        }.Render();
    }

    private static string SizeByLengthChart(SizeFile file, SizeGroup[] groups, string what)
    {
        string[] series = Ordered(file.Codecs);

        return new SvgChart {
            Title = $"Encoded size by length of value ({what})",
            Subtitle = "Bytes per character (UTF-16 code units) over all values; smaller is better. Each value encoded or compressed on its own.",
            Categories = [.. groups.Select(g => g.Short)],
            Series = series,
            Values = [
                .. groups.Select(g => {
                    var total = Total(file, g.Group);
                    return series.Select(s => (double)total.Bytes[s] / total.Chars).ToArray();
                }),
            ],
            Format = v => v.ToString("F2", CultureInfo.InvariantCulture),
            Description = $"Bar chart of bytes per character for every codec, for values of 4, 8 and 16 characters, paragraphs and whole documents ({what}). " +
                "The same numbers are in the tables on the Encoded Size page.",
            Note = "The compressors are UTF-8 followed by the compressor, on each value by itself.",
        }.Render();
    }

    // ---------- ASCII and comparison ----------

    private static string AsciiMarkdown(List<AsciiRecord> records, SpeedData data)
    {
        records = [
            .. records.Where(r => {
                var exclusion = data.Exclusions.FirstOrDefault(e => e.Matches("dotnet", "english", r.Kind, "ascii", "native", e.Codec) &&
                    e.Codec is not ("" or "*") && r.Operation.StartsWith(e.Codec, StringComparison.Ordinal));

                if (exclusion is not null)
                    data.Excluded[exclusion] = data.Excluded.GetValueOrDefault(exclusion) + 1;

                return exclusion is null;
            }),
        ];

        var toUtf8 = records.First(r => r.Kind == "document" && r.Operation.StartsWith("Steno to UTF-8", StringComparison.Ordinal));
        var toUtf16 = records.First(r => r.Kind == "document" && r.Operation.StartsWith("Steno to UTF-16", StringComparison.Ordinal));

        var md = new StringBuilder();
        md.AppendLine("# ASCII Fast Paths");
        md.AppendLine();
        md.AppendLine($"**On ASCII text, the .NET reference implementation converts Steno bytes to UTF-8 {Ratio(toUtf8, toUtf16)} as fast as it decodes them to UTF-16, on a document.**");
        md.AppendLine();
        md.AppendLine("Text that has only printable ASCII, tab, line feed and carriage return takes 1 byte per character in Steno, the code point minus " +
            "0x19, so the .NET reference implementation checks it and converts it to and from UTF-8 in a single vectorized pass. The text is the " +
            "English UDHR with every character that is not printable ASCII, tab or line feed taken out. Each operation is given as a share of the " +
            "speed of a plain copy of the same bytes, rounded to what the error margins support ([how](" + SpeedMethodLink + ")); the copy's own " +
            "speed, in millions of bytes (which here are characters) per second, gives the scale.");
        md.AppendLine();

        string[] kinds = [.. records.Select(r => r.Kind).Distinct()];
        string[] operations = [.. records.Select(r => r.Operation).Distinct()];
        AsciiRecord Copy(string kind) => records.First(r => r.Kind == kind && r.Operation.StartsWith("Copy", StringComparison.Ordinal));

        md.Append(Table(["Operation", .. kinds.Select(k => $"{Capitalize(KindName(k))}, against the copy")], [
            .. operations.Select(o => (string[])[
                o,
                .. kinds.Select(k => {
                    var r = records.First(x => x.Kind == k && x.Operation == o);
                    var copy = Copy(k);
                    return r == copy ? Anchor(r.Throughput, r.Error) + " MB/s" : Share(r, copy);
                }),
            ]),
        ]));

        return md.ToString();

        static string Share(AsciiRecord r, AsciiRecord copy)
        {
            double margin = Math.Sqrt(Math.Pow(r.Error / r.Throughput, 2) + Math.Pow(copy.Error / copy.Throughput, 2));
            return Significant(100 * r.Throughput / copy.Throughput, margin > OneDigitMargin ? 1 : 2) + "%";
        }

        static string Ratio(AsciiRecord a, AsciiRecord b)
        {
            double margin = Math.Sqrt(Math.Pow(a.Error / a.Throughput, 2) + Math.Pow(b.Error / b.Throughput, 2));
            return Significant(a.Throughput / b.Throughput, margin > OneDigitMargin ? 1 : 2) + " times";
        }
    }

    // ---------- Losses ----------

    private static string LossesMarkdown(SizeFile builtin, SizeFile? wiki)
    {
        var files = ((SizeFile?, SizeGroup[])[])[(builtin, BuiltinGroups), (wiki, WikiGroups)];
        var sets = files.Where(f => f.Item1 is not null)
            .SelectMany(f => f.Item2.SelectMany(g => f.Item1!.Rows.Where(r => r.Group == g.Group).Select(r => (File: f.Item1!, Group: g, Row: r))))
            .ToList();

        // Every size loss: a set of values (a language or a text, in a group) and a codec that is smaller than Steno there.
        var losses = sets.SelectMany(s => s.File.Codecs
                .Where(c => c is not "Steno" && s.Row.Bytes[c] < s.Row.Bytes["Steno"])
                .Select(c => (s.Group, s.Row, Codec: c, Percent: (s.Row.Bytes[c] - s.Row.Bytes["Steno"]) * 100.0 / s.Row.Bytes["Steno"])))
            .ToList();

        var md = new StringBuilder();
        md.AppendLine("# Where Steno Loses");
        md.AppendLine();
        md.AppendLine("Every group of values, language and workload where another codec is smaller than the Steno Encoding (Steno for short) or " +
            "faster than the .NET reference implementation, the .NET library in this repository, and by how much. A size is a property of the " +
            "encoding and is given against Steno (−9% is 9% smaller than Steno). A speed is a property of an implementation and is given as a factor " +
            "of the reference implementation's throughput (×3.32 is 3.32 times as fast). Sizes are exact; speeds are indicative.");
        md.AppendLine();

        // Summary: one line per codec.

        md.AppendLine("## Summary");
        md.AppendLine();

        foreach (string codec in (string[])["UTF-8", "UTF-16", "SCSU", "BOCU-1"])
        {
            var mine = losses.Where(l => l.Codec == codec).ToList();
            var line = new StringBuilder($"- **{codec}**: ");

            if (mine.Count is 0)
            {
                line.Append("never smaller than Steno.");
            }
            else
            {
                var listed = mine.Where(l => Math.Round(-l.Percent, 1) >= SmallLoss).ToList();
                var worst = mine.MinBy(l => l.Percent);

                if (listed.Count > 0)
                {
                    line.Append(Invariant($"smaller than Steno by {SmallLoss:0.0}% or more in {Sets(listed.Count)}, in "));
                    line.Append(Join([.. listed.Select(l => DisplayName(l.Row.Name)).Distinct()]));
                    line.Append(Invariant($"; by up to {-worst.Percent:F0}%, in {DisplayName(worst.Row.Name)} in the group {worst.Group.Label}."));
                }

                int small = mine.Count - listed.Count;

                if (small > 0)
                    line.Append(Invariant($"{(listed.Count > 0 ? " " : string.Empty)}Smaller by less than {SmallLoss:0.0}% in {(listed.Count > 0 ? small + " more " + (small is 1 ? "set" : "sets") + " of values" : Sets(small))}."));
            }

            md.AppendLine(line.ToString());
        }

        {
            var compressorGroups = files.Where(f => f.Item1 is not null)
                .SelectMany(f => f.Item2.Select(g => (Group: g, Best: BestCompressor(f.Item1!, Total(f.Item1!, g.Group).Bytes))))
                .ToList();
            var smaller = compressorGroups.Where(g => g.Best.Percent < 0).ToList();
            var notSmaller = compressorGroups.Where(g => g.Best.Percent >= 0).Select(g => g.Group.Label).ToList();
            var worst = smaller.MinBy(g => g.Best.Percent);

            var line = new StringBuilder("- **The compressors**: ");

            if (smaller.Count is 0)
            {
                line.Append("never smaller than Steno over a group of values.");
            }
            else
            {
                line.Append(Invariant($"smaller than Steno over {smaller.Count} of the {compressorGroups.Count} groups of values: "));
                line.Append(string.Join("; ", smaller.Select(g => g.Group.Label)));
                line.Append(Invariant($". By up to {-worst.Best.Percent:F0}%, with {worst.Best.Codec} in the group {worst.Group.Label}. "));
                line.Append("Over each of the other groups, every compressor is larger than Steno: " + string.Join("; ", notSmaller) + ".");
            }

            md.AppendLine(line.ToString());
        }

        md.AppendLine();

        // Size against the encodings that, like Steno, are not compressors.

        md.AppendLine("## Size Against UTF-8, UTF-16, SCSU and BOCU-1");
        md.AppendLine();

        var encodingLosses = losses.Where(l => Encodings.Contains(l.Codec)).ToList();
        var encodingRows = encodingLosses.Where(l => Math.Round(-l.Percent, 1) >= SmallLoss)
            .GroupBy(l => (l.Group.Label, l.Row.Name), l => l)
            .Select(g => (string[])[g.Key.Label, DisplayName(g.Key.Name), string.Join(", ", g.OrderBy(l => l.Percent).Select(l => $"{l.Codec} {Percent(l.Percent)}"))])
            .ToList();
        int leftOut = encodingLosses.Count(l => Math.Round(-l.Percent, 1) < SmallLoss);

        md.AppendLine(Invariant($"Every set of values where one of these encodings is smaller than Steno by {SmallLoss:0.0}% or more ({encodingRows.Count} rows). ") +
            (leftOut > 0 ? Invariant($"{leftOut} more cases, where one of them is smaller by less than {SmallLoss:0.0}%, are on the Size by Language pages.") : string.Empty));
        md.AppendLine();
        md.Append(Table(["Group", "Language or text", "Smaller than Steno"], encodingRows));
        md.AppendLine();

        // Size against the compressors, last: they win on long text, and give up the order, the prefixes and one encoding per text.

        md.AppendLine("## Size Against the Compressors");
        md.AppendLine();
        md.AppendLine("The compressors are smaller than Steno on paragraphs and whole documents, and their bytes keep none of the order, the prefixes " +
            "or one encoding per text. Over each group of values:");
        md.AppendLine();

        var totalRows = new List<string[]>();

        foreach ((var file, var groups) in files)
        {
            if (file is null)
                continue;

            foreach (var group in groups)
            {
                var total = Total(file, group.Group);
                string smaller = Smaller(file, total.Bytes, compressorsOnly: true);
                totalRows.Add([group.Label, smaller.Length is 0 ? "none" : smaller]);
            }
        }

        md.Append(Table(["Group", "Smaller than Steno"], totalRows));
        md.AppendLine();

        var compressorRows = sets
            .Select(s => (s.Group, s.Row, Smaller: Smaller(s.File, s.Row.Bytes, compressorsOnly: true)))
            .Where(s => s.Smaller.Length > 0)
            .Select(s => (string[])[s.Group.Label, DisplayName(s.Row.Name), s.Smaller])
            .ToList();

        md.AppendLine("<details>");
        md.AppendLine(Invariant($"<summary>Every language and text where a compressor is smaller than Steno ({compressorRows.Count} rows)</summary>"));
        md.AppendLine();
        md.Append(Table(["Group", "Language or text", "Smaller than Steno"], compressorRows));
        md.AppendLine();
        md.AppendLine("</details>");

        return md.ToString();

        static string Smaller(SizeFile file, Dictionary<string, long> bytes, bool compressorsOnly) => string.Join(", ", Ordered(file.Codecs)
            .Where(c => c is not "Steno" && (!compressorsOnly || !Encodings.Contains(c)) && bytes[c] < bytes["Steno"])
            .OrderBy(c => bytes[c])
            .Select(c => $"{c} {Relative(bytes[c], bytes["Steno"])}"));

        static (string Codec, double Percent) BestCompressor(SizeFile file, Dictionary<string, long> bytes) => file.Codecs
            .Where(c => !Encodings.Contains(c))
            .Select(c => (Codec: c, Percent: (bytes[c] - bytes["Steno"]) * 100.0 / bytes["Steno"]))
            .MinBy(x => x.Percent);
    }

    // ---------- Index and machine ----------

    private static string MachineMarkdown(MachineInfo machine, SizeFile? builtin, SizeFile? wiki, SpeedData speed)
    {
        var md = new StringBuilder();
        md.AppendLine("# How the Numbers Were Measured");
        md.AppendLine();
        md.AppendLine($"- **Machine:** {machine.Cpu}, {machine.LogicalProcessors} logical processors, {Intrinsics(machine.Intrinsics)}; {machine.Os}");
        md.AppendLine($"- **Sizes:** measured on {machine.Date} by the benchmark tool of the repository on {machine.Runtime}, at the commit {machine.Commit}.");
        md.AppendLine($"- **SCSU and BOCU-1 sizes:** ICU's converters, from {IcuName(machine)}. SCSU leaves choices to the encoder that change the size, so " +
            "another SCSU encoder can give other sizes.");
        md.AppendLine($"- **Compressor sizes:** System.IO.Compression for Deflate (raw, without a zlib header) and Brotli, {machine.Lz4} for " +
            $"LZ4 blocks, and {machine.Zstd} for Zstandard (Zstd) frames, each on the UTF-8 of each value by itself. An LZ4 block does not " +
            "record its decompressed length, and the LZ4 sizes do not include one; a store would have to keep it. A Zstandard frame records it. " +
            "Deflate and Brotli streams mark their own end.");

        if (builtin is not null)
        {
            md.AppendLine("- **Built-in corpus:** the [Corpus](https://github.com/Singulink/Steno/tree/main/Source/dotnet/Singulink.Steno.Benchmarks/Corpus) " +
                "folder of the benchmark tool: the UDHR in 47 languages, mixed content, and names, places, titles and UI strings.");
        }

        if (wiki is not null)
        {
            md.AppendLine($"- **Wikipedia corpus:** {WikiSource(wiki)}. Wikipedia text is under CC BY-SA and is not in the repository. Articles change, so a " +
                "new fetch gives other numbers.");
        }

        long failures = (builtin?.RoundTripFailures ?? 0) + (wiki?.RoundTripFailures ?? 0);
        md.AppendLine(failures is 0
            ? "- **Round trip:** every codec decoded every value back to its text."
            : "- **Round trip:** SOME VALUES DID NOT DECODE BACK TO THEIR TEXT; see the data files.");
        md.AppendLine();
        md.AppendLine("A codec here is an encoding (Steno, UTF-8, UTF-16, SCSU, BOCU-1), or UTF-8 followed by a general compressor (Deflate, Brotli, LZ4 " +
            "and Zstandard, each at a fast and a strong level): 13 in all. Sizes are exact. " + SizeSource);
        md.AppendLine();
        md.Append(SpeedMethodMarkdown(speed));
        md.AppendLine();
        md.AppendLine("## Derived Figures");
        md.AppendLine();
        md.AppendLine("Two figures on these pages are calculated from the measurements:");
        md.AppendLine();
        md.AppendLine("- **Nanoseconds per value** = the tool's time for one operation, which encodes or decodes every value of a text once, ÷ the number " +
            "of values. The values are pieces of at most 16 characters, so most are 16 and the last of each paragraph is shorter.");
        md.AppendLine("- **Values per 8 KB page** = 8,192 ÷ the mean bytes per 16-character value, rounded down, with the mean taken over all the " +
            "values of the script's languages. It ignores what a store adds to each record, such as lengths, headers and slot arrays, so it is a " +
            "calculation from the measured sizes, not a measurement of any database.");
        return md.ToString();
    }

    private static string IndexMarkdown(List<string> written, MachineInfo? machine)
    {
        var md = new StringBuilder();
        md.AppendLine("# Benchmarks of Steno");
        md.AppendLine();
        md.AppendLine("Generated by the benchmark tool (Source/dotnet/Singulink.Steno.Benchmarks); do not edit by hand. To regenerate everything:");
        md.AppendLine();
        md.AppendLine("```");
        md.AppendLine("python Source/dotnet/Singulink.Steno.Benchmarks/fetch_wikipedia.py <folder>    # once; the text is not in the repository");
        md.AppendLine("dotnet run -c Release --project Source/dotnet/Singulink.Steno.Benchmarks -- report --wiki <folder> --wiki-date <date> --force");
        md.AppendLine("```");
        md.AppendLine();
        md.AppendLine("Without `--force`, the parts whose data files are in `data/` are kept, and the speed part goes on where it stopped; " +
            "`--parts render` writes the fragments and charts again from the data alone.");
        md.AppendLine();

        if (machine is not null)
            md.AppendLine($"Measured on {machine.Date}: {machine.Cpu}, {machine.Os}, {machine.Runtime}.").AppendLine();

        md.AppendLine("Files:");
        md.AppendLine();

        foreach (string file in written.Order(StringComparer.Ordinal))
            md.AppendLine($"- [{file}]({file})");

        md.AppendLine("- data/: the measurements (JSON and CSV)");
        return md.ToString();
    }

    // ---------- Helpers ----------

    private static (int Values, long Chars, Dictionary<string, long> Bytes) Total(SizeFile file, string group)
    {
        var rows = file.Rows.Where(r => r.Group == group).ToList();
        return (rows.Sum(r => r.Values), rows.Sum(r => r.Chars), file.Codecs.ToDictionary(c => c, c => rows.Sum(r => r.Bytes[c])));
    }

    private static string[] Ordered(IEnumerable<string> codecs) =>
        [.. codecs.OrderBy(c => Array.IndexOf(CodecOrder, c) is var i && i >= 0 ? i : int.MaxValue)];

    private static string Relative(long value, long reference) =>
        value == reference ? "0%" : Percent((value - reference) * 100.0 / reference);

    /// <summary>
    /// Formats a difference in percent: whole percent, a tenth under 1% (so that it does not read as a tie), and "0.0%" under 0.05%.
    /// </summary>
    private static string Percent(double percent)
    {
        double rounded = Math.Round(percent, 1);

        if (rounded == 0)
            return "0.0%";

        return percent.ToString(Math.Abs(rounded) < 1 ? "+0.0;−0.0" : "+0;−0", CultureInfo.InvariantCulture) + "%";
    }

    private static string DisplayName(string name) => TextNames.TryGetValue(name, out string? display) ? display : name;

    private static string KindName(string kind) => kind switch {
        "neighbours in sorted order (long common prefixes)" => "sorted neighbours",
        "16-char strings" => "16-char values",
        "document" => "document",
        _ => kind,
    };

    private static string Sets(int count) => count is 1 ? "1 set of values" : Invariant($"{count} sets of values");

    private static string KindLabel(string kind) => kind == SpeedWorkloads.Document ? "Documents" : "16-char values";

    private static string Capitalize(string text) => text.Length is 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Join(IReadOnlyList<string> items) => items.Count switch {
        0 => string.Empty,
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    private static string IcuName(MachineInfo? machine) =>
        machine is null ? "ICU4C" : machine.Icu.Replace(" (icu.dll of Windows)", ", the icu.dll of Windows", StringComparison.Ordinal);

    private static string Intrinsics(string intrinsics)
    {
        // "AVX2 True, AVX-512 True, SSSE3 True" -> "supports AVX2, AVX-512 and SSSE3"
        var parts = intrinsics.Split(',', StringSplitOptions.TrimEntries).Select(p => p.Split(' ')).ToList();
        string[] yes = [.. parts.Where(p => p.Length is 2 && p[1] is "True").Select(p => p[0])];
        string[] no = [.. parts.Where(p => p.Length is 2 && p[1] is not "True").Select(p => p[0])];
        return (yes.Length > 0 ? "supports " + Join(yes) : string.Empty) + (no.Length > 0 ? (yes.Length > 0 ? ", not " : "does not support ") + Join(no) : string.Empty);
    }

    // "388 articles fetched by fetch_wikipedia.py on 2026-11-20" -> "388 articles in 56 languages, fetched on 2026-11-20"
    private static string WikiSource(SizeFile wiki)
    {
        int languages = wiki.Rows.Select(r => r.Name).Distinct().Count();
        var match = FetchedRegex().Match(wiki.Source);
        return match.Success ? Invariant($"{match.Groups[1].Value} articles in {languages} languages, fetched on {match.Groups[2].Value}") : wiki.Source;
    }

    private static string GroupSource(SizeFile file, SizeGroup group) => group.Group switch {
        Corpus.MixedLines or Corpus.MixedDocs => "23 files of mixed content, in the repository",
        Corpus.ShortStrings => "names of people and places, titles and UI strings, in the repository",
        _ when file.Corpus is "built-in" => "the UDHR in 47 languages, in the repository",
        _ => "Wikipedia, " + WikiSource(file),
    };

    [GeneratedRegex(@"^(\d+) articles fetched by \S+ on (\S+)$")]
    private static partial Regex FetchedRegex();

    private static string Bold(string text, bool bold) => bold ? $"**{text}**" : text;

    private static string Table(string[] headers, IEnumerable<string[]> rows)
    {
        var list = rows.ToList();
        var md = new StringBuilder();
        md.AppendLine("| " + string.Join(" | ", headers) + " |");

        // Numbers to the right, text to the left.
        md.AppendLine("|" + string.Concat(headers.Select((_, i) => i is not 0 && list.All(r => IsNumber(r[i])) ? "--:|" : "---|")));

        foreach (string[] row in list)
            md.AppendLine("| " + string.Join(" | ", row.Select(c => c.Replace("|", "\\|", StringComparison.Ordinal))) + " |");

        return md.ToString();

        static bool IsNumber(string cell) => cell.TrimStart('*').FirstOrDefault() is (>= '0' and <= '9') or '+' or '−' or '–' or '-';
    }

    private static T? Load<T>(string path)
        where T : class
        => File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private sealed record SizeGroup(string Group, string Title, string Label, string Short, string Slug);
}
