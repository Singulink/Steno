using System.Text;
using Singulink.Steno.Tests;

namespace Singulink.Steno.Benchmarks;

/// <summary>
/// The texts that the sizes and speeds are measured on: the built-in corpus (in the Corpus folder next to the tool), and an external folder of
/// Wikipedia articles fetched by fetch_wikipedia.py.
/// </summary>
internal static class Corpus
{
    public const string UdhrDocs = "UDHR, whole document";
    public const string UdhrParagraphs = "UDHR, each paragraph on its own";
    public const string UdhrSnippets = "UDHR, 16-char snippets";
    public const string UdhrSnippets8 = "UDHR, 8-char snippets";
    public const string UdhrSnippets4 = "UDHR, 4-char snippets";
    public const string MixedDocs = "Mixed content, whole document";
    public const string MixedLines = "Mixed content, each line on its own";
    public const string ShortStrings = "Short strings (names, addresses, titles), each on its own";
    public const string Samples = "Unit test samples";

    public const string WikiDocs = "Wikipedia, whole article";
    public const string WikiParagraphs = "Wikipedia, each paragraph on its own";
    public const string WikiSnippets = "Wikipedia, 16-char snippets";
    public const string WikiSnippets8 = "Wikipedia, 8-char snippets";
    public const string WikiSnippets4 = "Wikipedia, 4-char snippets";

    /// <summary>
    /// Gets or sets the folder of the built-in corpus: by default the Corpus folder next to the tool. The speed benchmarks, which run from a folder of
    /// their own, set it to the one in the repository.
    /// </summary>
    public static string Root { get; set; } = Path.Combine(AppContext.BaseDirectory, "Corpus");

    // Display order and names for the UDHR translations (ISO 639-3 file names).
    private static readonly (string File, string Name)[] _udhr = [
        ("eng", "English"), ("fra", "French"), ("deu", "German"), ("spa", "Spanish"), ("por_PT", "Portuguese"), ("swe", "Swedish"), ("fin", "Finnish"),
        ("isl", "Icelandic"), ("pol", "Polish"), ("ces", "Czech"), ("hun", "Hungarian"), ("ron", "Romanian"), ("lit", "Lithuanian"),
        ("srp_latn", "Serbian (Latin)"),
        ("tur", "Turkish"), ("vie", "Vietnamese"),
        ("rus", "Russian"), ("ukr", "Ukrainian"), ("bul", "Bulgarian"), ("srp_cyrl", "Serbian (Cyrillic)"), ("kaz", "Kazakh"),
        ("ell_monotonic", "Greek"), ("ell_polytonic", "Greek (polytonic)"), ("hye", "Armenian"), ("kat", "Georgian"),
        ("heb", "Hebrew"), ("arb", "Arabic"), ("pes_1", "Persian"), ("urd", "Urdu"),
        ("hin", "Hindi"), ("nep", "Nepali"), ("ben", "Bengali"), ("pan", "Punjabi"), ("guj", "Gujarati"), ("tam", "Tamil"), ("kan", "Kannada"),
        ("mal", "Malayalam"),
        ("tha", "Thai"), ("lao", "Lao"), ("khm", "Khmer"), ("mya", "Burmese"), ("bod", "Tibetan"), ("amh", "Amharic"),
        ("cmn_hans", "Chinese (Simplified)"), ("cmn_hant", "Chinese (Traditional)"), ("jpn", "Japanese"), ("kor", "Korean"),
    ];

    // The languages of fetch_wikipedia.py (Wikipedia codes), in the order of the UDHR list where they are in it.
    private static readonly (string Code, string Name)[] _wiki = [
        ("en", "English"), ("fr", "French"), ("de", "German"), ("es", "Spanish"), ("pt", "Portuguese"), ("it", "Italian"), ("nl", "Dutch"),
        ("sv", "Swedish"), ("da", "Danish"), ("fi", "Finnish"), ("is", "Icelandic"), ("pl", "Polish"), ("cs", "Czech"), ("sk", "Slovak"),
        ("hu", "Hungarian"), ("ro", "Romanian"), ("lt", "Lithuanian"), ("lv", "Latvian"), ("hr", "Croatian"), ("sl", "Slovenian"), ("tr", "Turkish"),
        ("vi", "Vietnamese"), ("id", "Indonesian"),
        ("ru", "Russian"), ("uk", "Ukrainian"), ("bg", "Bulgarian"), ("sr", "Serbian"), ("kk", "Kazakh"), ("mn", "Mongolian"),
        ("el", "Greek"), ("hy", "Armenian"), ("ka", "Georgian"),
        ("he", "Hebrew"), ("ar", "Arabic"), ("fa", "Persian"), ("ur", "Urdu"),
        ("hi", "Hindi"), ("mr", "Marathi"), ("ne", "Nepali"), ("bn", "Bengali"), ("pa", "Punjabi"), ("gu", "Gujarati"), ("ta", "Tamil"),
        ("te", "Telugu"), ("kn", "Kannada"), ("ml", "Malayalam"), ("si", "Sinhala"),
        ("th", "Thai"), ("lo", "Lao"), ("km", "Khmer"), ("my", "Burmese"), ("bo", "Tibetan"), ("am", "Amharic"),
        ("zh", "Chinese"), ("ja", "Japanese"), ("ko", "Korean"),
    ];

    /// <summary>
    /// Loads the built-in corpus: the UDHR translations whole, by paragraph and in 16-char pieces, the mixed content, the short strings and the
    /// samples of the unit tests.
    /// </summary>
    public static IReadOnlyList<CorpusEntry> Load()
    {
        var entries = new List<CorpusEntry>();

        foreach ((string file, string name) in _udhr)
        {
            // Real-world text is overwhelmingly NFC (the Vietnamese UDHR file is not).
            string[] lines = ReadLines(Path.Combine(Root, "udhr", file + ".txt"));
            string doc = string.Join('\n', lines);

            entries.Add(new(UdhrDocs, name, [doc]));
            entries.Add(new(UdhrParagraphs, name, lines));
            entries.Add(new(UdhrSnippets, name, [.. lines.SelectMany(l => Chunk(l, 16))]));
        }

        foreach (string path in Directory.EnumerateFiles(Path.Combine(Root, "mixed"), "*.txt").Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            string[] lines = ReadLines(path);

            entries.Add(new(MixedDocs, name, [string.Join('\n', lines)]));
            entries.Add(new(MixedLines, name, lines));
        }

        foreach (string path in Directory.EnumerateFiles(Path.Combine(Root, "short"), "*.txt").Order(StringComparer.Ordinal))
            entries.Add(new(ShortStrings, Path.GetFileNameWithoutExtension(path), ReadLines(path)));

        foreach ((string name, string text) in SampleTexts.All)
            entries.Add(new(Samples, name, [text]));

        return entries;
    }

    /// <summary>
    /// Loads the built-in corpus for the report: as <see cref="Load"/>, with the UDHR paragraphs also cut into pieces of 8 and 4 chars, and without the
    /// samples of the unit tests.
    /// </summary>
    public static IReadOnlyList<CorpusEntry> LoadForReport()
    {
        var entries = Load().Where(e => e.Group is not Samples).ToList();
        var paragraphs = entries.Where(e => e.Group is UdhrParagraphs).ToList();

        entries.AddRange(paragraphs.Select(e => new CorpusEntry(UdhrSnippets8, e.Name, [.. e.Units.SelectMany(u => Chunk(u, 8))])));
        entries.AddRange(paragraphs.Select(e => new CorpusEntry(UdhrSnippets4, e.Name, [.. e.Units.SelectMany(u => Chunk(u, 4))])));

        return entries;
    }

    /// <summary>
    /// Loads a folder of Wikipedia articles (files named <c>lang_nn.txt</c>, as fetch_wikipedia.py writes them): per language, the articles whole, by
    /// paragraph and in pieces of 16, 8 and 4 chars.
    /// </summary>
    public static IReadOnlyList<CorpusEntry> LoadWikipedia(string folder)
    {
        var entries = new List<CorpusEntry>();
        var files = Directory.EnumerateFiles(folder, "*.txt").ToLookup(f => Path.GetFileName(f).Split('_')[0]);

        foreach ((string code, string name) in _wiki)
        {
            if (!files[code].Any())
                continue;

            string[][] articles = [.. files[code].Order(StringComparer.Ordinal).Select(ReadLines)];
            string[] lines = [.. articles.SelectMany(a => a)];

            entries.Add(new(WikiDocs, name, [.. articles.Select(a => string.Join('\n', a))]));
            entries.Add(new(WikiParagraphs, name, lines));
            entries.Add(new(WikiSnippets, name, [.. lines.SelectMany(l => Chunk(l, 16))]));
            entries.Add(new(WikiSnippets8, name, [.. lines.SelectMany(l => Chunk(l, 8))]));
            entries.Add(new(WikiSnippets4, name, [.. lines.SelectMany(l => Chunk(l, 4))]));
        }

        return entries;
    }

    /// <summary>
    /// Splits a line into pieces of at most <paramref name="maxChars"/> UTF-16 code units without splitting surrogate pairs. Simulates short field values.
    /// </summary>
    internal static IEnumerable<string> Chunk(string line, int maxChars)
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

    /// <summary>
    /// Gets the lines of the UDHR translation of a language of the corpus, as every group of the UDHR is made from them.
    /// </summary>
    internal static string[] UdhrLines(string language) => ReadLines(Path.Combine(Root, "udhr", _udhr.First(u => u.Name == language).File + ".txt"));

    /// <summary>
    /// Gets a file of the corpus prepared as the corpus prepares it: normalized to NFC, each line trimmed, empty lines dropped.
    /// </summary>
    internal static string[] ReadLines(string path)
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

/// <summary>
/// A named set of strings that are each encoded independently (a fresh encoder per string); sizes are summed over the set.
/// </summary>
internal sealed record CorpusEntry(string Group, string Name, IReadOnlyList<string> Units)
{
    public long CharCount { get; } = Units.Sum(u => (long)u.Length);
}
