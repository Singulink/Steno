using System.Text;

namespace Singulink.Steno.Benchmarks;

/// <summary>
/// The shared workloads of the speed benchmarks of every implementation (Benchmarks/Workloads): 14 texts, each as a whole document and as values of
/// at most 16 UTF-16 code units, plus the four mixed-content documents of the built-in corpus that only the .NET benchmarks use.
/// </summary>
/// <remarks>
/// <c>&lt;name&gt;.document.txt</c> is the document with its lines joined by LF and a final LF; the value is the content without the final LF.
/// <c>&lt;name&gt;.values.txt</c> has one value per line, LF after each. Both are UTF-8 without a byte order mark.
/// </remarks>
internal static class Workloads
{
    /// <summary>
    /// Gets the 14 texts: the file name of each and the UDHR language it is made from.
    /// </summary>
    public static readonly (string File, string Language)[] Texts = [
        ("english", "English"), ("french", "French"), ("polish", "Polish"), ("vietnamese", "Vietnamese"), ("russian", "Russian"), ("greek", "Greek"),
        ("arabic", "Arabic"), ("urdu", "Urdu"), ("hindi", "Hindi"), ("thai", "Thai"), ("amharic", "Amharic"), ("chinese", "Chinese (Simplified)"),
        ("japanese", "Japanese"), ("korean", "Korean"),
    ];

    /// <summary>
    /// Gets the four mixed-content documents of the built-in corpus (file names in Corpus/mixed) that the .NET benchmarks also measure.
    /// </summary>
    public static readonly string[] MixedDocuments = ["chat_en_emoji", "chat_ru_emoji", "json_catalog", "tech_ja_en"];

    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>
    /// Gets the repository: from the environment variable STENO_REPO (which the speed benchmarks give the processes they start), or the folder with
    /// FORMAT.md above the program.
    /// </summary>
    public static string Repository
    {
        get {
            if (Environment.GetEnvironmentVariable("STENO_REPO") is { Length: > 0 } repository)
                return repository;

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "FORMAT.md")))
                    return dir.FullName;
            }

            throw new DirectoryNotFoundException("The repository (the folder with FORMAT.md) was not found; set STENO_REPO.");
        }
    }

    public static string Folder => Path.Combine(Repository, "Benchmarks", "Workloads");

    /// <summary>
    /// Gets the names that the environment variable STENO_TEXTS lists (comma-separated), or all of them if it is not set: a way to run the speed
    /// benchmarks on a few texts only, as the smoke run does.
    /// </summary>
    public static IEnumerable<string> Selected(IEnumerable<string> names) =>
        Environment.GetEnvironmentVariable("STENO_TEXTS") is { Length: > 0 } selected ? names.Where(n => selected.Split(',').Contains(n)) : names;

    public static string CorpusFolder => Path.Combine(Repository, "Benchmarks", "Singulink.Steno.Benchmarks", "Corpus");

    /// <summary>
    /// Gets the document of a text, or of a mixed-content file of the corpus.
    /// </summary>
    public static string Document(string name)
    {
        if (MixedDocuments.Contains(name))
            return string.Join('\n', Corpus.ReadLines(Path.Combine(CorpusFolder, "mixed", name + ".txt")));

        string content = File.ReadAllText(Path.Combine(Folder, name + ".document.txt"), Utf8);
        return content.EndsWith('\n') ? content[..^1] : content;
    }

    /// <summary>
    /// Gets the 16-char values of a text.
    /// </summary>
    public static string[] Values(string name)
    {
        string content = File.ReadAllText(Path.Combine(Folder, name + ".values.txt"), Utf8);
        return content.Split('\n')[..^1];
    }

    /// <summary>
    /// Gets the display name of a text as the report names its workloads (the UDHR language, or the mixed-content file).
    /// </summary>
    public static string DisplayName(string name) => Texts.FirstOrDefault(t => t.File == name).Language ?? name;

    /// <summary>
    /// Writes the files of the 14 texts from the built-in corpus, or with <paramref name="check"/> only compares them with the files that are there.
    /// Returns the number of files that differ.
    /// </summary>
    public static int Write(string folder, bool check)
    {
        Corpus.Root = CorpusFolder;
        Directory.CreateDirectory(folder);
        int different = 0;

        foreach ((string file, string language) in Texts)
        {
            string[] lines = Corpus.UdhrLines(language);
            string document = string.Join('\n', lines) + "\n";
            string values = string.Concat(lines.SelectMany(l => Corpus.Chunk(l, 16)).Select(v => v + "\n"));

            foreach ((string path, string content) in (ReadOnlySpan<(string, string)>)[
                (Path.Combine(folder, file + ".document.txt"), document),
                (Path.Combine(folder, file + ".values.txt"), values)])
            {
                byte[] bytes = Utf8.GetBytes(content);
                bool same = File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);

                if (!same)
                {
                    different++;
                    Console.WriteLine($"  {(check ? "differs" : "written")}: {Path.GetFileName(path)}");

                    if (!check)
                        File.WriteAllBytes(path, bytes);
                }
            }
        }

        Console.WriteLine($"{(Texts.Length * 2) - different} of {Texts.Length * 2} files {(check ? "are" : "were")} already byte for byte the same.");
        return different;
    }
}
