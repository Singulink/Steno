using System.Globalization;
using System.Text;
using PrefixClassName.MsTest;
using Shouldly;

namespace Singulink.Steno.Benchmarks.Tests;

/// <summary>
/// The C implementation in the rendered speed pages: read from the native comparison of the Rust implementation (codecs <c>steno-c</c> and
/// <c>steno-c-scalar</c>), shown as the seventh implementation and on a page of its own, and never as an alternative on the other pages. Each test
/// renders fabricated results from a temporary folder (through STENO_RESULTS) into another one.
/// </summary>
[PrefixTestClass]
[DoNotParallelize]
public sealed class CImplementationRenderTests
{
    private const string Pending = "The figures appear here after the benchmark run.";

    private static readonly string[] Texts = [
        "english", "french", "polish", "vietnamese", "russian", "greek", "arabic", "urdu", "hindi", "thai", "amharic", "chinese", "japanese", "korean",
    ];

    private static readonly string[] Wide = ["amharic", "chinese", "japanese", "korean"];

    [TestMethod]
    public void WithoutResults_TheCFragmentIsPending()
    {
        using var run = new Run(native: null);

        run.Page("impl-c.md").Trim().ShouldBe(Pending);
        run.Page("implementations.md").ShouldContain(Pending);
        run.Page("machine.md").ShouldContain("| C | Criterion, in the native benchmark of the Rust implementation |");
    }

    [TestMethod]
    public void C_IsTheSeventhImplementation_WithItsRatioAgainstTheUtf8OfTheSameRun()
    {
        using var run = new Run(Native(withC: true));
        string page = run.Page("implementations.md");

        page.ShouldContain("**The seven reference implementations on the same workloads");
        page.ShouldContain("measured in the Criterion harness of the Rust implementation's native benchmark, through its C interface");
        page.ShouldContain("compiled with clang at -O2");

        // Steno (C) is 0.8 of Steno (Rust) on half the texts and 1.2 on the others: 32 ns on the median text against Rust's 40.
        string c = Row(page, "| [C](../articles/implementations/c.md#performance)");
        c.ShouldStartWith("| [C](../articles/implementations/c.md#performance) | UTF-8 bytes, and UTF-16 code units | 32 | 26 |");

        // Against the UTF-8 of the same run (from UTF-16): C's UTF-16 form is 40 or 60 ns against 60.
        c.ShouldContain("about the same (faster on 7, about the same on 7 of 14)");
        Row(page, "| [Rust](../articles/implementations/rust.md#performance)").ShouldContain("| 40 | 32 |");
    }

    [TestMethod]
    public void C_StaysOutOfTheHeadlineAndTheOtherPages()
    {
        using var with = new Run(Native(withC: true));
        using var without = new Run(Native(withC: false));

        foreach (string file in Directory.EnumerateFiles(with.Output, "*.md"))
        {
            string name = Path.GetFileName(file);

            if (name is "impl-c.md" or "implementations.md" or "machine.md")
                continue;

            with.Page(name).ShouldBe(without.Page(name), $"{name} changes with the C rows");
            with.Page(name).ShouldNotContain("steno-c", Case.Insensitive, name);
            with.Page(name).ShouldNotContain("scalar", Case.Insensitive, name);
        }

        // The side-by-side page mentions the scalar build nowhere.
        with.Page("implementations.md").ShouldNotContain("scalar", Case.Insensitive);
        with.Page("implementations.md").ShouldNotContain("STENO_NO_SIMD");
    }

    [TestMethod]
    public void TheCPage_HasTheAlternativesOfACProgrammerAndTheVectorCode()
    {
        using var run = new Run(Native(withC: true));
        string page = run.Page("impl-c.md");

        page.ShouldStartWith("The figures of the C reference implementation, Steno (C), against what a developer on its platform would use");
        page.ShouldContain("Steno (C) takes about 32 ns to encode a 16-character value and about 26 ns to decode one");
        Row(page, "| SCSU |").ShouldContain("| 2.5× faster (faster on all 14) |");
        Row(page, "| BOCU-1 |").ShouldContain("| 2.7× faster (faster on all 14) |");
        Row(page, "| Zstd 1 |").ShouldContain("| 8.3× faster (faster on all 14) |");
        Row(page, "| UTF-8, validated copy |").ShouldContain("| 2.4× slower (slower on all 14) |");
        page.ShouldContain("Rust's standard library, where a C program would use its own");

        // The vector code buys a third on ten texts and nothing on the four wide ones (the scalar build is 1.5 times as slow, or the same).
        page.ShouldContain("### The Vector Code");
        const string Third = "50% faster (faster on 10, about the same on 4 of 14)";
        Row(page, "| UTF-8 |").ShouldBe($"| UTF-8 | {Third} | {Third} | {Third} | {Third} |");
        page.ShouldContain(
            "It buys nothing (about the same or slower) on Amharic, Chinese (Simplified), Japanese and Korean (in every form, length and operation).");

        // The scalar build is no alternative in the summary table.
        page[..page.IndexOf("### The Vector Code", StringComparison.Ordinal)].ShouldNotContain("scalar", Case.Insensitive);
    }

    [TestMethod]
    public void Exclusions_ApplyToTheCRows()
    {
        // The scalar build left out: the vector section is pending, and the method page counts the rows (14 texts, 2 kinds, 2 operations, 2 forms: 112).
        using (var run = new Run(Native(withC: true), "c,*,*,*,*,steno-c-scalar,\"For the test\""))
        {
            run.Page("impl-c.md").ShouldContain("### The Vector Code\n\n" + Pending, Case.Sensitive);
            Row(run.Page("machine.md"), "| c | any | any | any | any | steno-c-scalar |").ShouldContain("| 112 | For the test |");
        }

        // Steno (C) on one text left out, as Steno of the C implementation: that text is not measured for C, and Rust keeps it.
        using (var run = new Run(Native(withC: true), "c,english,values,*,*,steno,\"For the test\""))
        {
            Row(run.Page("implementations.md"), "| English |").ShouldBe("| English | 40 | not measured |");
        }

        // The same row left out by its name in the native results.
        using (var run = new Run(Native(withC: true), "rust,english,values,*,*,steno-c,\"For the test\""))
        {
            Row(run.Page("implementations.md"), "| English |").ShouldBe("| English | 40 | not measured |");
        }
    }

    // The first line of a page that starts with the given text.
    private static string Row(string page, string start) =>
        page.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.StartsWith(start, StringComparison.Ordinal)) ??
        throw new ShouldAssertException($"No line starts with {start}.");

    /// <summary>
    /// A fabricated native comparison: per value on 16-character values, Steno (Rust) 40 ns from UTF-8 and 50 from UTF-16; the C implementation 0.8
    /// of that on the first seven texts and 1.2 on the others; its scalar build 1.5 times as slow, but the same on the four wide texts. Decode is 0.8
    /// of encode. Documents are one value of 8,000 code units at a quarter of the cost per code unit. Every error is 1%.
    /// </summary>
    private static (string Results, string Libraries) Native(bool withC)
    {
        (string Codec, string Form, double Ns)[] codecs = [
            ("steno", "native", 40), ("steno", "utf16", 50), ("utf8-copy", "native", 20), ("utf8", "utf16", 60), ("scsu-icu", "utf16", 150),
            ("bocu1-icu", "utf16", 160), ("zstd-1", "native", 400), ("brotli-11", "native", 20000),
        ];

        var csv = new StringBuilder("implementation,text,kind,operation,form,codec,ns_per_operation,error_ns,values,utf16_units\n");

        for (int i = 0; i < Texts.Length; i++)
        {
            string text = Texts[i];

            foreach ((string kind, int values) in (ReadOnlySpan<(string, int)>)[("values", 500), ("document", 1)])
            {
                foreach ((string operation, double scale) in (ReadOnlySpan<(string, double)>)[("encode", 1.0), ("decode", 0.8)])
                {
                    void Add(string codec, string form, double perValue)
                    {
                        double ns = perValue * scale * (kind is "document" ? 2000 : 1) * values;
                        csv.Append(CultureInfo.InvariantCulture, $"rust,{text},{kind},{operation},{form},{codec},{ns:0.0},{ns * 0.01:0.0},{values},8000\n");
                    }

                    foreach (var (codec, form, ns) in codecs)
                        Add(codec, form, ns);

                    if (!withC)
                        continue;

                    foreach ((string form, double ns) in (ReadOnlySpan<(string, double)>)[("native", 40), ("utf16", 50)])
                    {
                        double c = ns * (i < 7 ? 0.8 : 1.2);
                        Add("steno-c", form, c);
                        Add("steno-c-scalar", form, Wide.Contains(text) ? c : c * 1.5);
                    }
                }
            }
        }

        string libraries = """
            codec,library,implementation,version
            steno,"singulink-steno","Rust, this crate","0.1.0 (vector code on)"
            utf8-copy,"Rust standard library (str::from_utf8 and a copy)","Rust","rustc 1.91.0"
            utf8,"Rust standard library (char::decode_utf16, str::encode_utf16)","Rust","rustc 1.91.0"
            scsu-icu,"ICU4C, ucnv_fromUChars and ucnv_toUChars","C, the system icu.dll","76.1"
            bocu1-icu,"ICU4C, ucnv_fromUChars and ucnv_toUChars","C, the system icu.dll","76.1"
            zstd-1,"libzstd through the zstd crate (bulk API, contexts reused)","C","zstd-sys 2.0.15"
            brotli-11,"Google brotli through the brotlic crate","C","brotlic-sys 0.2.2"

            """;

        if (withC)
        {
            libraries += """
                steno-c,"the plain C implementation (Ports/c/steno.c), built with the cc crate at -O2, vector code on","C","0.1.0, clang version 19.1.0"
                steno-c-scalar,"the plain C implementation (Ports/c/steno.c), built with -DSTENO_NO_SIMD","C","0.1.0, clang version 19.1.0"

                """;
        }

        return (csv.ToString(), libraries);
    }

    /// <summary>
    /// A render of fabricated results: a temporary results folder with the native comparison (or none), and a temporary output folder with the
    /// exclusions, both deleted afterwards.
    /// </summary>
    private sealed class Run : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "steno-render-" + Guid.NewGuid().ToString("N"));

        public Run((string Results, string Libraries)? native, string? exclusion = null)
        {
            string bench = Path.Combine(_root, "results", "Ports", "rust", "bench");
            Directory.CreateDirectory(bench);
            Directory.CreateDirectory(Path.Combine(Output, "data"));

            if (native is { } n)
            {
                File.WriteAllText(Path.Combine(bench, "native-results.csv"), n.Results);
                File.WriteAllText(Path.Combine(bench, "native-libraries.csv"), n.Libraries);
            }

            // The machine of the repository's data, for the method page, which needs it.
            File.Copy(Path.Combine(Workloads.Repository, "Docs", "benchmarks", "data", "machine.json"), Path.Combine(Output, "data", "machine.json"));
            File.WriteAllText(Path.Combine(Output, "data", "speed-exclusions.csv"),
                "implementation,text,kind,operation,form,codec,reason\n" + (exclusion is null ? string.Empty : exclusion + "\n"));

            string? previous = Environment.GetEnvironmentVariable("STENO_RESULTS");

            try
            {
                Environment.SetEnvironmentVariable("STENO_RESULTS", Path.Combine(_root, "results"));
                ReportRender.Run(Output).ShouldBe(0);
            }
            finally
            {
                Environment.SetEnvironmentVariable("STENO_RESULTS", previous);
            }
        }

        public string Output => Path.Combine(_root, "output");

        public string Page(string name) => File.ReadAllText(Path.Combine(Output, name));

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
