using System.Globalization;
using System.Text;

namespace Singulink.Steno.Benchmarks;

/// <content>
/// Writes the pages that compare Steno with one other codec at a time, the page on stored values, and the trade-offs page with its
/// scoreboards, the causes of the exceptions and the speaker-weighted view.
/// </content>
internal static partial class ReportRender
{
    // A difference in size under this many percent (after rounding to a tenth) counts as tied.
    private const double TieBand = 0.5;

    private const int PageBytes = 8192;

    private const string Spec = "../articles/reference/specification.md";

    private const string SetDefinition = "A set of values is one language or text at one length of value, such as Russian in 16-character UDHR " +
        "values; a group of values is all the sets of one length in one corpus.";

    private const string WhyAboveU0EFF =
        "Every value starts ready for ASCII and the Latin letters, which take 1 byte, and for the scripts up to U+0EFF, whose first letter takes " +
        "2: Greek, Cyrillic, Armenian, Hebrew, Arabic, the Indic scripts, Thai and Lao. There is no room left in that starting state to give a " +
        "2-byte first letter to the scripts above U+0EFF as well, such as Georgian, Khmer, Burmese, Tibetan and Ethiopic. The cost is one byte, " +
        "once per value; from the second letter on, these scripts take 1 byte per letter too. ([§6.6](" + Spec + "#66-the-initial-layout), " +
        "[§13.6](" + Spec + "#136-every-layout-fits))";

    private const string WhySpansWindows =
        "The Steno Encoding moves its window by itself as the text moves, with no tag bytes; that is what keeps its bytes in the order of the " +
        "text, with one encoding per text. A script that spans more than one window, such as Ethiopic or the Arabic letters of Urdu, makes the " +
        "window move often, and each move costs a byte or two. SCSU places tag bytes where its encoder chooses, which is how it wins here and " +
        "why its bytes keep neither the order nor one encoding per text. ([§4](" + Spec + "#4-windows), [§7](" + Spec + "#7-state-update))";

    private const string WhyCjkFirst =
        "Every value starts ready for Latin text. The first ideograph, kana or Hangul syllable takes 3 bytes and switches the value to Chinese, " +
        "Japanese or Korean text; after that, ideographs and syllables take 2 bytes and kana 1. That switch is what gives Japanese {japanese}. " +
        "([§6.2](" + Spec + "#62-pieces), [§7](" + Spec + "#7-state-update))";

    private const string WhySymbols =
        "Dashes, quotation marks and currency signs take 2 bytes without moving the window away from the text's letters, so the text after them " +
        "stays at 1 byte per letter; other symbols, such as arrows and emoji, take 3. ([§6.2](" + Spec + "#62-pieces))";

    // The rivals that are encodings, with the file name of their comparison page in Docs/articles/comparisons.
    private static readonly (string Codec, string Slug)[] Rivals = [("UTF-8", "utf-8"), ("UTF-16", "utf-16"), ("SCSU", "scsu"), ("BOCU-1", "bocu-1")];

    private static readonly (string Script, string[] Languages)[] Scripts = [
        ("Latin", ["English", "French", "German", "Spanish", "Portuguese", "Italian", "Dutch", "Swedish", "Danish", "Finnish", "Icelandic", "Polish",
            "Czech", "Slovak", "Hungarian", "Romanian", "Lithuanian", "Latvian", "Croatian", "Slovenian", "Serbian (Latin)", "Turkish", "Indonesian"]),
        ("Vietnamese", ["Vietnamese"]),
        ("Cyrillic", ["Russian", "Ukrainian", "Bulgarian", "Serbian (Cyrillic)", "Serbian", "Kazakh", "Mongolian"]),
        ("Greek", ["Greek"]),
        ("Greek (polytonic)", ["Greek (polytonic)"]),
        ("Armenian", ["Armenian"]),
        ("Georgian", ["Georgian"]),
        ("Hebrew", ["Hebrew"]),
        ("Arabic script", ["Arabic", "Persian", "Urdu"]),
        ("Indic scripts", ["Hindi", "Marathi", "Nepali", "Bengali", "Punjabi", "Gujarati", "Tamil", "Telugu", "Kannada", "Malayalam", "Sinhala"]),
        ("Thai and Lao", ["Thai", "Lao"]),
        ("Khmer", ["Khmer"]),
        ("Burmese", ["Burmese"]),
        ("Tibetan", ["Tibetan"]),
        ("Ethiopic (Amharic)", ["Amharic"]),
        ("Chinese", ["Chinese (Simplified)", "Chinese (Traditional)", "Chinese"]),
        ("Japanese", ["Japanese"]),
        ("Korean", ["Korean"]),
    ];

    private static readonly string[] AboveU0EFF = ["Georgian", "Khmer", "Burmese", "Tibetan", "Amharic", "Greek (polytonic)"];

    private static readonly string[] CjkLanguages = ["Chinese (Simplified)", "Chinese (Traditional)", "Chinese", "Japanese", "Korean"];

    private static readonly string[] SymbolTexts = ["en_typographic", "chat_es_emoji", "math_science"];

    // The causes of the cases where an encoding is smaller than Steno. Every such case of 0.5% or more must match one; the renderer fails otherwise.
    private static readonly SizeCause[] SizeCauses = [
        new(
            "The first letter of a script above U+0EFF takes 3 bytes",
            "most of your values are under about 16 characters and in Georgian, Khmer, Burmese, Tibetan, Amharic or polytonic Greek",
            WhyAboveU0EFF,
            (codec, _, name) => codec is "BOCU-1" or "UTF-16" && AboveU0EFF.Contains(name)),
        new(
            "The script spans several windows (runs of up to 128 code points whose characters take 1 byte)",
            "you store Amharic or Urdu paragraphs or documents",
            WhySpansWindows,
            (codec, _, name) => codec is "SCSU" && name is "Amharic" or "Urdu"),
        new(
            "The first ideograph, kana or syllable of a value takes 3 bytes",
            "most of your values are very short Chinese, Japanese or Korean text, of about 4 characters",
            WhyCjkFirst,
            (codec, _, name) => codec is "UTF-16" or "SCSU" && CjkLanguages.Contains(name)),
        new(
            "Punctuation and symbols outside the letters' window",
            "your text is dense with typographic punctuation, mathematical symbols or emoji",
            WhySymbols,
            (codec, group, name) => codec is "SCSU" && group.Group is Corpus.MixedLines or Corpus.MixedDocs && SymbolTexts.Contains(name)),
    ];

    private static void WriteComparisons(
        string output,
        Action<string, string> write,
        SizeFile builtin,
        SizeFile? wiki,
        SpeedData speed)
    {
        string charts = Path.Combine(output, "charts");
        var speakers = LoadSpeakers();

        foreach (var (codec, slug) in Rivals)
        {
            write(Path.Combine(output, $"vs-{slug}.md"), RivalSizeMarkdown(builtin, wiki, speakers, codec));
            write(Path.Combine(charts, $"vs-{slug}-16char.svg"), RivalChart(builtin, codec));

            if (codec is not "UTF-16")
                write(Path.Combine(output, $"vs-{slug}-speed.md"), NativeRivalSpeedMarkdown(speed, codec));
        }

        write(Path.Combine(output, "vs-compressors.md"), CompressorsMarkdown(builtin, wiki));

        write(Path.Combine(output, "vs-compressors-speed.md"), NativeCompressorsSpeedMarkdown(speed));

        write(Path.Combine(output, "stored.md"), StoredMarkdown(builtin, wiki));
        write(Path.Combine(output, "trade-offs.md"), TradeOffsMarkdown(builtin, wiki, speed, speakers));
    }

    // ---------- Cells, scores and causes ----------

    private static List<(SizeFile File, SizeGroup Group, SizeRow Row)> Cells(SizeFile builtin, SizeFile? wiki) =>
        [.. Groups(builtin, wiki).SelectMany(g => g.File.Rows.Where(r => r.Group == g.Group.Group).Select(r => (g.File, g.Group, r)))];

    private static List<(SizeFile File, SizeGroup Group)> Groups(SizeFile builtin, SizeFile? wiki) =>
        [.. BuiltinGroups.Select(g => (builtin, g)), .. wiki is null ? [] : WikiGroups.Select(g => (wiki, g))];

    // The size of a codec against Steno, in percent: + is larger than Steno.
    private static double Against(Dictionary<string, long> bytes, string codec) => (bytes[codec] - bytes["Steno"]) * 100.0 / bytes["Steno"];

    private static int Score(double against)
    {
        double rounded = Math.Round(against, 1);
        return rounded >= TieBand ? 1 : rounded <= -TieBand ? -1 : 0;
    }

    private static (int StenoSmaller, int Tied, int RivalSmaller, int All) Scoreboard(IEnumerable<Dictionary<string, long>> cells, string codec)
    {
        int[] scores = [.. cells.Select(b => Score(Against(b, codec)))];
        return (scores.Count(s => s > 0), scores.Count(s => s == 0), scores.Count(s => s < 0), scores.Length);
    }

    private static SizeCause CauseOf(string codec, SizeGroup group, string name) =>
        SizeCauses.FirstOrDefault(c => c.Matches(codec, group, name)) ??
        throw new InvalidOperationException($"No cause covers {codec} smaller than Steno in {name}, {group.Label}.");

    private static string CauseWhy(SizeCause cause, SizeFile builtin)
    {
        var japanese = builtin.Rows.First(r => r.Group == Corpus.UdhrSnippets && r.Name == "Japanese");
        return cause.Why.Replace("{japanese}", Invariant(
            $"{(double)japanese.Bytes["Steno"] / japanese.Chars:F2} bytes per character on 16-character UDHR values, against {(double)japanese.Bytes["UTF-16"] / japanese.Chars:F2} in UTF-16 and {(double)japanese.Bytes["UTF-8"] / japanese.Chars:F2} in UTF-8"),
            StringComparison.Ordinal);
    }

    private static string ScriptOf(string language) =>
        Scripts.FirstOrDefault(s => s.Languages.Contains(language)).Script ?? throw new InvalidOperationException($"No script group for {language}.");

    private static Dictionary<string, long> ScriptBytes(SizeFile file, string group, string script, out long chars, out int values)
    {
        var rows = file.Rows.Where(r => r.Group == group && ScriptOf(r.Name) == script).ToList();
        chars = rows.Sum(r => r.Chars);
        values = rows.Sum(r => r.Values);
        return file.Codecs.ToDictionary(c => c, c => rows.Sum(r => r.Bytes[c]));
    }

    private static string[] ScriptsIn(SizeFile file, string group) =>
        [.. Scripts.Select(s => s.Script).Where(s => file.Rows.Any(r => r.Group == group && ScriptOf(r.Name) == s))];

    private static string PerCharText(long bytes, long chars) => ((double)bytes / chars).ToString("F2", CultureInfo.InvariantCulture);

    // A range of sizes against Steno in words: "38% to 81% larger".
    private static string LargerRange(IEnumerable<double> values)
    {
        double[] all = [.. values];
        string min = Magnitude(all.Min()), max = Magnitude(all.Max());
        return all.All(v => v > 0) ? (min == max ? $"{min} larger" : $"{min} to {max} larger") : $"{Percent(all.Min())} to {Percent(all.Max())}";
    }

    private static string Count(int n) => n switch {
        1 => "one", 2 => "two", 3 => "three", 4 => "four", 5 => "five", 6 => "six", 7 => "seven", 8 => "eight", 9 => "nine",
        _ => n.ToString("N0", CultureInfo.InvariantCulture),
    };

    // ---------- Text of the other fragments ----------

    private static string SmallestGroupsText(SizeFile builtin, SizeFile? wiki)
    {
        var groups = Groups(builtin, wiki);
        var smallest = groups.Where(g => {
            var bytes = Total(g.File, g.Group.Group).Bytes;
            return g.File.Codecs.All(c => c is "Steno" || bytes[c] > bytes["Steno"]);
        }).ToList();
        var others = groups.Except(smallest).Select(g => g.Group.Label).ToList();

        return Invariant($"Steno is the smallest of the 13 codecs over {smallest.Count} of the {groups.Count} groups of values: every group of values of up to 16 characters, the mixed lines and the names and UI strings.") +
            (others.Count is 0 ? string.Empty : " Over the others (" + string.Join("; ", others) + "), the stronger compressors are smaller.");
    }

    // ---------- Speakers ----------

    private static Dictionary<string, (double Millions, string Entry)> LoadSpeakers()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "speakers.tsv");
        var speakers = new Dictionary<string, (double, string)>();

        foreach (string line in File.ReadLines(path))
        {
            if (line.Length is 0 || line.StartsWith('#'))
                continue;

            string[] fields = line.Split('\t');
            speakers[fields[0]] = (double.Parse(fields[1], CultureInfo.InvariantCulture), fields[2]);
        }

        return speakers;
    }

    // One language of the corpora, folded across their spellings: the entry of the source, or the language itself where the source does not list it.
    private static string LanguageKey(string name, Dictionary<string, (double Millions, string Entry)> speakers) =>
        speakers.TryGetValue(name, out var s) ? s.Entry : name switch {
            "Greek (polytonic)" => "Greek",
            "Serbian (Cyrillic)" or "Serbian (Latin)" => "Serbian",
            _ => name,
        };

    // The plain name of a language for a list: the three spellings of Chinese are one language.
    private static string LanguageName(string name) => name.StartsWith("Chinese", StringComparison.Ordinal) ? "Chinese" : name;

    private static double SpeakerShare(IEnumerable<string> languages, Dictionary<string, (double Millions, string Entry)> speakers)
    {
        var covered = speakers.Values.DistinctBy(s => s.Entry).ToList();
        string[] keys = [.. languages.Select(l => LanguageKey(l, speakers)).Distinct()];
        return 100 * covered.Where(s => keys.Contains(s.Entry)).Sum(s => s.Millions) / covered.Sum(s => s.Millions);
    }

    private static string[] CorpusLanguages(SizeFile builtin, SizeFile? wiki) =>
        [.. Cells(builtin, wiki).Where(c => !IsText(c.Group)).Select(c => c.Row.Name).Distinct()];

    private static bool IsText(SizeGroup group) => group.Group is Corpus.MixedLines or Corpus.MixedDocs or Corpus.ShortStrings;

    private static bool Is16(SizeGroup group) => group.Group is Corpus.UdhrSnippets or Corpus.WikiSnippets;

    // ---------- Derived figures ----------

    private static int ValuesPerPage(long bytes, int values) => (int)(PageBytes / ((double)bytes / values));

    // ---------- One rival: size ----------

    private static string RivalSizeMarkdown(SizeFile builtin, SizeFile? wiki, Dictionary<string, (double Millions, string Entry)> speakers, string x)
    {
        var cells = Cells(builtin, wiki);
        var (stenoSmaller, tied, rivalSmaller, all) = Scoreboard(cells.Select(c => c.Row.Bytes), x);
        var groups = Groups(builtin, wiki);
        double[] groupAgainst = [.. groups.Select(g => Against(Total(g.File, g.Group.Group).Bytes, x))];
        int groupsSmaller = groupAgainst.Count(a => a > 0);
        var md = new StringBuilder();

        md.AppendLine("## Size, Head to Head");
        md.AppendLine();
        md.AppendLine(Invariant($"**Steno is smaller than {x} over {(groupsSmaller == groups.Count ? $"every one of the {groups.Count}" : $"{groupsSmaller} of the {groups.Count}")} groups of values ({x} is {LargerRange(groupAgainst)} than Steno), and in {stenoSmaller} of the {all} sets of values; the two are tied (within 0.5%) in {tied}, and {x} is smaller in {(rivalSmaller is 0 ? "none" : rivalSmaller.ToString(CultureInfo.InvariantCulture))}.**"));
        md.AppendLine();
        md.AppendLine(SetDefinition + " " + SizeUnits + (x is "SCSU" ? " SCSU sizes are those of ICU's encoder; another SCSU encoder can give other sizes." : string.Empty));
        md.AppendLine();
        md.AppendLine("By group of values:");
        md.AppendLine();
        md.Append(Table(["Group", "**Steno**", x, $"{x} against Steno (+ is larger)"], [
            .. groups.Select(g => {
                var t = Total(g.File, g.Group.Group);
                return (string[])[g.Group.Label, PerCharText(t.Bytes["Steno"], t.Chars), PerCharText(t.Bytes[x], t.Chars), Relative(t.Bytes[x], t.Bytes["Steno"])];
            }),
        ]));
        md.AppendLine();
        md.AppendLine("By script, over the UDHR in 47 languages:");
        md.AppendLine();
        md.Append(Table(["Script", "**Steno**, 4 chars", $"{x}, 4 chars", $"{x} against Steno, 4 chars", "**Steno**, 16 chars", $"{x}, 16 chars", $"{x} against Steno, 16 chars"], [
            .. ScriptsIn(builtin, Corpus.UdhrSnippets).Select(s => {
                var b4 = ScriptBytes(builtin, Corpus.UdhrSnippets4, s, out long c4, out _);
                var b16 = ScriptBytes(builtin, Corpus.UdhrSnippets, s, out long c16, out _);
                return (string[])[s, PerCharText(b4["Steno"], c4), PerCharText(b4[x], c4), Relative(b4[x], b4["Steno"]),
                    PerCharText(b16["Steno"], c16), PerCharText(b16[x], c16), Relative(b16[x], b16["Steno"])];
            }),
        ]));
        md.AppendLine();
        md.AppendLine($"![Bytes per character by language, 16-character values: Steno and {x}](charts/vs-{RivalSlug(x)}-16char.svg)");
        md.AppendLine();

        var exceptions = cells.Where(c => Score(Against(c.Row.Bytes, x)) < 0).ToList();
        md.AppendLine("### Where " + x + " Is Smaller");
        md.AppendLine();

        if (exceptions.Count is 0)
        {
            md.AppendLine($"{x} is not smaller than Steno, by 0.5% or more, in any set of values.");
            md.AppendLine();
        }
        else
        {
            var byCause = exceptions.GroupBy(c => CauseOf(x, c.Group, c.Row.Name)).ToList();
            md.AppendLine(Invariant($"{x} is smaller than Steno by 0.5% or more in {exceptions.Count} of the {all} sets of values, for {(byCause.Count is 1 ? "one reason" : $"{Count(byCause.Count)} reasons")}:"));
            md.AppendLine();
            md.Append(Table(["Cause", "Sets of values", "By up to", "Why the design accepts this"], [
                .. byCause.Select(g => {
                    var worst = g.MinBy(c => Against(c.Row.Bytes, x));
                    return (string[])[g.Key.Title, Where([.. g]), Magnitude(Against(worst.Row.Bytes, x)), CauseWhy(g.Key, builtin)];
                }),
            ]));
            md.AppendLine();
            string[] languages = [.. exceptions.Where(c => !IsText(c.Group)).Select(c => c.Row.Name).Distinct()];

            if (languages.Length > 0)
            {
                md.AppendLine(Invariant($"The languages where {x} is smaller at some length of value account for about {SpeakerShare(languages.Where(speakers.ContainsKey), speakers):F0}% of the speakers of the large languages measured ([how this is counted](../articles/comparisons/trade-offs.md#weighted-by-speakers))."));
                md.AppendLine();
            }
        }

        md.AppendLine("## Values per 8 KB Page");
        md.AppendLine();
        md.AppendLine($"How many 16-character values fit in 8 KB, by script, over the UDHR in 47 languages; more is better. Calculated from the measured sizes ([how]({MethodLink})).");
        md.AppendLine();
        md.Append(Table(["Script", "**Steno**", x, "Steno fits more (+) or fewer (−)"], [
            .. ScriptsIn(builtin, Corpus.UdhrSnippets).Select(s => {
                var b = ScriptBytes(builtin, Corpus.UdhrSnippets, s, out _, out int values);
                int steno = ValuesPerPage(b["Steno"], values), other = ValuesPerPage(b[x], values);
                return (string[])[s, Invariant($"{steno:N0}"), Invariant($"{other:N0}"), Percent((steno - other) * 100.0 / other)];
            }),
        ]));
        return md.ToString();
    }

    private static string RivalSlug(string codec) => Rivals.First(r => r.Codec == codec).Slug;

    private static string RivalChart(SizeFile builtin, string codec)
    {
        var rows = builtin.Rows.Where(r => r.Group == Corpus.UdhrSnippets).ToList();
        string[] series = ["Steno", codec];

        return new SvgChart {
            Title = $"Steno and {codec}, by language (UDHR, 16-character values)",
            Subtitle = "Bytes per character (UTF-16 code units) over all values; smaller is better. Each value encoded on its own.",
            Categories = [.. rows.Select(r => r.Name)],
            Series = series,
            Values = [.. rows.Select(r => series.Select(s => (double)r.Bytes[s] / r.Chars).ToArray())],
            Format = v => v.ToString("F2", CultureInfo.InvariantCulture),
            Description = $"Bar chart of bytes per character of Steno and {codec} on the 16-character values of the UDHR, per language. The same " +
                "numbers are in the table UDHR, 16-Char Values, under Size by Language.",
            Note = codec is "SCSU" ? "SCSU sizes are those of ICU's encoder." : null,
        }.Render();
    }

    // ---------- Compressors ----------

    private static string CompressorsMarkdown(SizeFile builtin, SizeFile? wiki)
    {
        string[] compressors = [.. Ordered(builtin.Codecs).Where(c => !Encodings.Contains(c))];
        var cells = Cells(builtin, wiki);
        int anySmaller = cells.Count(c => compressors.Any(k => Score(Against(c.Row.Bytes, k)) < 0));
        var groups = Groups(builtin, wiki);
        var smallerGroups = groups.Where(g => compressors.Any(k => Against(Total(g.File, g.Group.Group).Bytes, k) < 0)).ToList();
        double best = groups.Min(g => compressors.Min(k => Against(Total(g.File, g.Group.Group).Bytes, k)));
        var md = new StringBuilder();

        md.AppendLine("## Size by Length of Value");
        md.AppendLine();
        md.AppendLine(Invariant($"**Every compressor is larger than Steno over each group of values of up to 16 characters, over the mixed lines and over the names and UI strings. Over paragraphs and whole documents the strongest are smaller, by up to {-best:F0}% over a group of values.** ") +
            Invariant($"At least one compressor is smaller than Steno in {anySmaller} of the {cells.Count} sets of values. ") + SetDefinition + " " + SizeUnits +
            " In parentheses is the size against Steno: + is larger than Steno, − is smaller. Each value is compressed on its own, from its UTF-8, with " +
            "nothing shared between values.");
        md.AppendLine();

        foreach ((string title, string[] subset) in (ReadOnlySpan<(string, string[])>)[
            ("At the strongest levels:", [.. compressors.Where(c => c.EndsWith("max", StringComparison.Ordinal) || c.EndsWith("11", StringComparison.Ordinal) || c.EndsWith("19", StringComparison.Ordinal))]),
            ("At the fastest levels:", [.. compressors.Where(c => c.EndsWith("fast", StringComparison.Ordinal) || c.EndsWith(" 1", StringComparison.Ordinal))])])
        {
            md.AppendLine(title);
            md.AppendLine();
            md.Append(Table(["Group", "**Steno**", "UTF-8", .. subset], [
                .. groups.Select(g => {
                    var t = Total(g.File, g.Group.Group);
                    return (string[])[g.Group.Label, PerCharText(t.Bytes["Steno"], t.Chars),
                        $"{PerCharText(t.Bytes["UTF-8"], t.Chars)} ({Relative(t.Bytes["UTF-8"], t.Bytes["Steno"])})",
                        .. subset.Select(k => $"{PerCharText(t.Bytes[k], t.Chars)} ({Relative(t.Bytes[k], t.Bytes["Steno"])})")];
                }),
            ]));
            md.AppendLine();
        }

        md.AppendLine("![Encoded size by length of value (UDHR, 47 languages)](charts/size-by-length-udhr.svg)");
        md.AppendLine();
        md.AppendLine(Invariant($"The compressors are smaller over {Count(smallerGroups.Count)} of the {groups.Count} groups: {string.Join("; ", smallerGroups.Select(g => g.Group.Label))}."));
        return md.ToString();
    }

    // ---------- Stored values ----------

    private static string StoredMarkdown(SizeFile builtin, SizeFile? wiki)
    {
        string[] codecs = ["Steno", .. Rivals.Select(r => r.Codec)];
        string[] scripts = ScriptsIn(builtin, Corpus.UdhrSnippets);
        int smallest16 = scripts.Count(s => ScoreText(ScriptBytes(builtin, Corpus.UdhrSnippets, s, out _, out _)) is "Smallest");
        int smallest4 = scripts.Count(s => ScoreText(ScriptBytes(builtin, Corpus.UdhrSnippets4, s, out _, out _)) is "Smallest");
        bool overall = ((string[])[Corpus.UdhrSnippets4, Corpus.UdhrSnippets8, Corpus.UdhrSnippets]).All(g => {
            var bytes = Total(builtin, g).Bytes;
            return Rivals.All(r => bytes[r.Codec] > bytes["Steno"]);
        });
        var md = new StringBuilder();

        md.AppendLine("# Stored Values");
        md.AppendLine();
        md.AppendLine(Invariant($"**{(overall ? "Over the whole UDHR in 47 languages, Steno is the smallest of the five per-value encodings at every length of value up to 16 characters. " : string.Empty)}By script, it is the smallest in {smallest16} of the {scripts.Length} script groups on 16-character values and {smallest4} on 4-character values; where another is smaller, it is BOCU-1 or UTF-16, by the margins in the scorecard below.**"));
        md.AppendLine();
        md.AppendLine("The size of short values, such as names, titles, keys and fields, in Steno and in the other per-value encodings. " + SizeUnits +
            " In parentheses is the size against Steno: + is larger than Steno, − is smaller. " + SizeSource);
        md.AppendLine();

        foreach ((var file, string corpus) in (ReadOnlySpan<(SizeFile?, string)>)[(builtin, "UDHR in 47 languages"), (wiki, "Wikipedia in 56 languages")])
        {
            if (file is null)
                continue;

            string g16 = file == builtin ? Corpus.UdhrSnippets : Corpus.WikiSnippets;
            string g4 = file == builtin ? Corpus.UdhrSnippets4 : Corpus.WikiSnippets4;

            foreach ((string group, string length) in (ReadOnlySpan<(string, string)>)[(g16, "16-Character Values"), (g4, "4-Character Values")])
            {
                md.AppendLine($"## Size by Script, {length} ({corpus})");
                md.AppendLine();
                md.Append(Table(["Script", .. codecs.Select(c => Bold(c, c is "Steno"))], [
                    .. ScriptsIn(file, group).Select(s => {
                        var b = ScriptBytes(file, group, s, out long chars, out _);
                        return (string[])[s, .. codecs.Select(c => c is "Steno"
                            ? PerCharText(b[c], chars)
                            : $"{PerCharText(b[c], chars)} ({Relative(b[c], b["Steno"])})")];
                    }),
                ]));
                md.AppendLine();
            }
        }

        md.AppendLine("## Scorecard by Script");
        md.AppendLine();
        md.AppendLine("Where Steno is the smallest of the five per-value encodings, tied with another (within 0.5%), or behind one, over the UDHR in 47 " +
            "languages:");
        md.AppendLine();
        md.Append(Table(["Script", "4-character values", "16-character values"], [
            .. scripts.Select(s => (string[])[s,
                ScoreText(ScriptBytes(builtin, Corpus.UdhrSnippets4, s, out _, out _)),
                ScoreText(ScriptBytes(builtin, Corpus.UdhrSnippets, s, out _, out _))]),
        ]));
        md.AppendLine();
        md.AppendLine("## Values per 8 KB Page");
        md.AppendLine();
        md.AppendLine($"How many 16-character values fit in 8 KB, by script, over the UDHR in 47 languages; more is better. Calculated from the measured sizes ([how]({MethodLink})).");
        md.AppendLine();
        md.Append(Table(["Script", .. codecs.Select(c => Bold(c, c is "Steno"))], [
            .. scripts.Select(s => {
                var b = ScriptBytes(builtin, Corpus.UdhrSnippets, s, out _, out int values);
                return (string[])[s, .. codecs.Select(c => Invariant($"{ValuesPerPage(b[c], values):N0}"))];
            }),
        ]));
        return md.ToString();

        static string ScoreText(Dictionary<string, long> bytes)
        {
            var others = Rivals.Select(r => (r.Codec, Against: Against(bytes, r.Codec))).ToList();
            var behind = others.Where(o => Score(o.Against) < 0).OrderBy(o => o.Against).ToList();
            var tied = others.Where(o => Score(o.Against) == 0).ToList();

            if (behind.Count > 0)
                return "Behind " + string.Join(", ", behind.Select(b => $"{b.Codec} ({Percent(b.Against)})"));

            return tied.Count > 0 ? "Tied with " + string.Join(", ", tied.Select(l => l.Codec)) : "Smallest";
        }
    }

    // ---------- Trade-offs ----------

    private static string TradeOffsMarkdown(SizeFile builtin, SizeFile? wiki, SpeedData speed,
        Dictionary<string, (double Millions, string Entry)> speakers)
    {
        var cells = Cells(builtin, wiki);
        var groups = Groups(builtin, wiki);
        string[] compressors = [.. Ordered(builtin.Codecs).Where(c => !Encodings.Contains(c))];
        bool everyGroup = Rivals.All(r => groups.All(g => Against(Total(g.File, g.Group.Group).Bytes, r.Codec) > 0));
        bool shortGroups = groups.Where(g => g.Group.Short.EndsWith("chars", StringComparison.Ordinal))
            .All(g => compressors.All(k => Against(Total(g.File, g.Group.Group).Bytes, k) > 0));
        var md = new StringBuilder();

        md.AppendLine("# Trade-offs at a Glance");
        md.AppendLine();
        md.AppendLine("**" + (everyGroup ? Invariant($"Steno is smaller than UTF-8, UTF-16, SCSU and BOCU-1 over every one of the {groups.Count} groups of values measured") : "Steno is smaller than UTF-8, UTF-16, SCSU and BOCU-1 over most groups of values measured") +
            (shortGroups ? ", and smaller than every general compressor over each group of values of up to 16 characters." : ".") +
            Invariant($"** The exceptions are single languages or texts at particular lengths of value. They have {Count(SizeCauses.Length)} causes, listed below with why the design accepts them. Each row links to the page that compares the two in full."));
        md.AppendLine();
        md.AppendLine(SetDefinition + " " + SizeSource + " The speeds are those of the Rust reference implementation, Steno (Rust), against native " +
            "implementations of the alternatives, and are indicative.");
        md.AppendLine();
        md.AppendLine("## Size Scoreboard");
        md.AppendLine();
        md.AppendLine(Invariant($"Over the {groups.Count} groups of values and the {cells.Count} sets of values measured; tied means within 0.5%:"));
        md.AppendLine();

        var rows = new List<string[]>();

        foreach (var (codec, slug) in Rivals)
        {
            var (s, t, r, _) = Scoreboard(cells.Select(c => c.Row.Bytes), codec);
            double[] perGroup = [.. groups.Select(g => Against(Total(g.File, g.Group.Group).Bytes, codec))];
            var worst = cells.MinBy(c => Against(c.Row.Bytes, codec));
            double w = Against(worst.Row.Bytes, codec);
            rows.Add([$"[{codec}](../articles/comparisons/{slug}.md)", Invariant($"{perGroup.Count(a => a > 0)} of {groups.Count}"), Range(perGroup),
                Invariant($"{s}"), Invariant($"{t}"), Invariant($"{r}"),
                w < 0 ? $"{Percent(w)}: {DisplayName(worst.Row.Name)}, {worst.Group.Label}" : "none"]);
        }

        {
            int s = 0, t = 0, r = 0;

            foreach (var c in cells)
            {
                int best = compressors.Select(k => Score(Against(c.Row.Bytes, k))).Min();
                if (best > 0) s++; else if (best == 0) t++; else r++;
            }

            var worst = cells.Select(c => (Cell: c, Codec: compressors.MinBy(k => Against(c.Row.Bytes, k))!))
                .MinBy(x => Against(x.Cell.Row.Bytes, x.Codec));
            double[] perGroup = [.. groups.Select(g => compressors.Min(k => Against(Total(g.File, g.Group.Group).Bytes, k)))];
            rows.Add(["[General compressors](../articles/comparisons/compressors.md) (the smallest of the eight)",
                Invariant($"{perGroup.Count(a => a > 0)} of {groups.Count}"), Range(perGroup), Invariant($"{s}"), Invariant($"{t}"), Invariant($"{r}"),
                $"{worst.Codec} {Percent(Against(worst.Cell.Row.Bytes, worst.Codec))}: {DisplayName(worst.Cell.Row.Name)}, {worst.Cell.Group.Label}"]);
        }

        md.Append(Table(["Alternative", "Groups where Steno is smaller", "Other's size against Steno, per group (+ is larger)", "Sets where Steno is smaller",
            "Tied", "Sets where the other is smaller", "Largest margin of the other"], rows));
        md.AppendLine();

        md.Append(NativeScoreboard(speed));
        md.AppendLine();

        md.AppendLine("## Why Another Encoding Is Sometimes Smaller");
        md.AppendLine();
        md.AppendLine("Every set of values where another encoding is smaller than Steno by 0.5% or more has one of these causes:");
        md.AppendLine();

        var causeRows = new List<string[]>();

        foreach (var cause in SizeCauses)
        {
            var hits = cells.SelectMany(c => Rivals.Select(r => (c, r.Codec)))
                .Where(h => Score(Against(h.c.Row.Bytes, h.Codec)) < 0 && CauseOf(h.Codec, h.c.Group, h.c.Row.Name) == cause)
                .ToList();

            if (hits.Count is 0)
                continue;

            var worst = hits.MinBy(h => Against(h.c.Row.Bytes, h.Codec));
            causeRows.Add([cause.Title, cause.AffectsIf, Join([.. hits.Select(h => h.Codec).Distinct()]),
                Invariant($"{hits.Count} sets of values; up to {Magnitude(Against(worst.c.Row.Bytes, worst.Codec))} ({worst.Codec}, {DisplayName(worst.c.Row.Name)}, {worst.c.Group.Label})"),
                CauseWhy(cause, builtin)]);
        }

        md.Append(Table(["Cause", "Affects you if", "Ahead", "How many and by how much", "Why the design accepts this"], causeRows));
        md.AppendLine();

        md.AppendLine("## Speed");
        md.AppendLine();
        md.AppendLine("Transcoding does more work in Steno than copying UTF-8: the encoder and the decoder keep a small state and look up a table " +
            "for it. The state is what makes the bytes small, and a store that compares, sorts, matches and hashes the Steno bytes pays the " +
            "transcoding only when text enters or leaves it." + (speed.Native.Count is 0 ? string.Empty :
            " Against a validated UTF-8 copy, the baseline for a system that already holds UTF-8, Steno (Rust) is " +
            Summary(Versuses(speed.Native, "utf8-copy", "native", "values", "encode")) + " to encode 16-character values and " +
            Summary(Versuses(speed.Native, "utf8-copy", "native", "values", "decode")) + " to decode them ([the method](" + SpeedMethodLink + ")).") +
            " Text that changes script often is the slowest to encode and decode.");
        md.AppendLine();

        md.AppendLine("## Long Documents");
        md.AppendLine();
        md.AppendLine(Invariant($"On paragraphs and whole documents a general compressor is the right tool: it finds repetition across a long text, which a per-value encoding cannot, and it is up to {-groups.Min(g => compressors.Min(k => Against(Total(g.File, g.Group.Group).Bytes, k))):F0}% smaller than Steno over a group of values. Its bytes keep none of the order, the prefixes or one encoding per text, which a document that is stored and read back whole does not need. See [Steno and General Compressors](../articles/comparisons/compressors.md)."));
        md.AppendLine();

        string[] languages = CorpusLanguages(builtin, wiki);
        var listed = speakers.Values.DistinctBy(s => s.Entry).ToList();
        int distinct = languages.Select(l => LanguageKey(l, speakers)).Distinct().Count();

        md.AppendLine("## Weighted by Speakers");
        md.AppendLine();
        md.AppendLine(Invariant($"How many people speak a language where another encoding is smaller than Steno, at any length of value (often only at 4 characters) and at 16 characters. Of the {distinct} languages measured, {listed.Count} have 50 million speakers or more and are in the source: Wikipedia's list of languages by total number of speakers (first and second language, figures from Ethnologue, 2026), read on 2026-11-21. The smaller languages are left out, and a speaker of several languages is counted in each, so the shares are approximate."));
        md.AppendLine();
        md.Append(Table(["Alternative", "Languages where it is smaller", "Share of the speakers of those {listed} languages, at any length".Replace("{listed}", Invariant($"{listed.Count}"), StringComparison.Ordinal), "At 16 characters"], [
            .. Rivals.Select(r => {
                var smaller = cells.Where(c => !IsText(c.Group) && Score(Against(c.Row.Bytes, r.Codec)) < 0).ToList();
                string[] any = [.. smaller.Select(c => c.Row.Name).Distinct()];
                string[] at16 = [.. smaller.Where(c => Is16(c.Group)).Select(c => c.Row.Name).Distinct()];
                string[] names = [.. any.Select(LanguageName).Distinct()];
                string[] missing = [.. names.Where(n => !any.Any(a => LanguageName(a) == n && speakers.ContainsKey(a)))];
                string list = names.Length is 0 ? "none" : Join(names) + (missing.Length is 0 ? string.Empty : $" ({Join(missing)} not in the source)");
                return (string[])[r.Codec, list, Invariant($"{SpeakerShare(any.Where(speakers.ContainsKey), speakers):F0}%"),
                    Invariant($"{SpeakerShare(at16.Where(speakers.ContainsKey), speakers):F0}%")];
            }),
        ]));
        md.AppendLine();
        md.AppendLine("## Every Case");
        md.AppendLine();
        md.AppendLine("Each comparison page lists its exceptions with their cause. Every set of values where an encoding is smaller than Steno by 0.5% " +
            "or more, every text where an encoding is faster than Steno (Rust) beyond the error margins, and every case where a compressor " +
            "is smaller follow.");
        md.AppendLine();
        md.AppendLine("<details>");
        md.AppendLine("<summary>Every case, row by row</summary>");
        md.AppendLine();
        string losses = LossesMarkdown(builtin, wiki).ReplaceLineEndings("\n");
        string detail = losses[losses.IndexOf("\n## Size Against UTF-8", StringComparison.Ordinal)..];
        int compressorsAt = detail.IndexOf("\n## Size Against the Compressors", StringComparison.Ordinal);
        detail = detail[..compressorsAt] + "\n## Speed\n\n" + (speed.Native.Count is 0 ? Pending + "\n" : NativeFasterCases(speed)) + detail[compressorsAt..];
        md.Append(detail.Replace("\n## ", "\n### ", StringComparison.Ordinal).TrimStart('\n'));
        md.AppendLine();
        md.AppendLine("</details>");
        return md.ToString();
    }

    // The sets of values of a list, by language or text: "Georgian (UDHR: 4, 8 and 16 chars; Wikipedia: 4, 8 and 16 chars)".
    private static string Where(IReadOnlyList<(SizeFile File, SizeGroup Group, SizeRow Row)> cells) => Join([
        .. cells.GroupBy(c => c.Row.Name).Select(byName => DisplayName(byName.Key) + " (" + string.Join("; ", byName
            .GroupBy(c => c.Group.Label.Split(',')[0])
            .Select(byCorpus => byCorpus.Key + ": " + Join([.. byCorpus.Select(c => c.Group.Short.Replace("Mixed, ", string.Empty, StringComparison.Ordinal).Replace(" chars", string.Empty, StringComparison.Ordinal).ToLowerInvariant())]) +
                (byCorpus.All(c => c.Group.Short.EndsWith("chars", StringComparison.Ordinal)) ? " chars" : string.Empty))) + ")"),
    ]);

    // The size of a difference in percent, without its sign: a tenth under 1%.
    private static string Magnitude(double percent) =>
        Math.Abs(percent).ToString(Math.Round(Math.Abs(percent), 1) < 1 ? "0.0" : "0", CultureInfo.InvariantCulture) + "%";

    private static string Range(IEnumerable<double> values)
    {
        double[] all = [.. values];
        return all.Min() == all.Max() ? Percent(all.Min()) : $"{Percent(all.Min())} to {Percent(all.Max())}";
    }

    private sealed record SizeCause(string Title, string AffectsIf, string Why, Func<string, SizeGroup, string, bool> Matches);
}
