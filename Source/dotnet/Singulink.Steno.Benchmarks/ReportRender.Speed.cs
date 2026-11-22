using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Singulink.Steno.Benchmarks;

/// <content>
/// Writes the speed pages from the results of the speed benchmarks of every implementation, in the common CSV format: the headline figures from the
/// native comparison of the Rust implementation, a page of each implementation against what a developer on its platform would use, the
/// implementations side by side, and the method page.
/// </content>
/// <remarks>
/// <para>Figures are shown as ratios against a stated baseline, rounded to what the error margins support: two significant figures, or one where the
/// combined margin is above 10%. A difference within the sum of the two error margins is "about the same". Rows can be left out with a reason in
/// <c>data/speed-exclusions.csv</c>; the method page lists every exclusion.</para>
/// <para>The results are read from the repository, or from the folder that the environment variable STENO_RESULTS names (with the same layout), so
/// that the rendering can be tried on sample files.</para>
/// <para>The C implementation has no benchmark tool of its own: it is measured in the native comparison of the Rust implementation, as the codecs
/// <c>steno-c</c> (with its vector code) and <c>steno-c-scalar</c> (the same file built with <c>-DSTENO_NO_SIMD</c>). Its rows are the native rows
/// with <c>steno-c</c> as its Steno, against the same alternatives, measured in the same process on the same inputs. The scalar build is shown
/// only on the page of the C implementation, against the vector build; neither is ever an alternative on the other pages.</para>
/// </remarks>
internal static partial class ReportRender
{
    private const string Pending = "The figures appear here after the benchmark run.";

    private const string SpeedMethodLink = "../articles/benchmarks/machine.md#speed";

    // The combined relative margin above which a ratio is shown with one significant figure instead of two.
    private const double OneDigitMargin = 0.10;

    // The codecs of the native comparison that are the C implementation: its Steno, and its scalar build.
    private const string CVector = "steno-c";
    private const string CScalar = "steno-c-scalar";

    // How the C implementation was measured, for the pages that show it.
    private const string CMethod = "C has no benchmark tool of its own: the C implementation was measured in the Criterion harness of the Rust " +
        "implementation's native benchmark, through its C interface, once per value, compiled with clang at -O2, in the same process and on the same " +
        "inputs as Steno (Rust), so the ratio between the two is direct.";

    private static readonly Implementation[] Implementations = [
        new("dotnet", ".NET", null, "UTF-16 strings", "BenchmarkDotNet", "the half-width of the 99.9% confidence interval of the mean"),
        new("rust", "Rust", "rust", "UTF-8 strings (`&str`), and UTF-16 code units", "Criterion", "the half-width of the 95% confidence interval of the mean"),
        new("c", "C", "c", "UTF-8 bytes, and UTF-16 code units", "Criterion, in the native benchmark of the Rust implementation",
            "the half-width of the 95% confidence interval of the mean"),
        new("go", "Go", "go", "UTF-8 strings, and UTF-16 code units", "the benchmarks of Go's `testing` package", "the sample standard deviation of the runs, around their median"),
        new("java", "Java", "java", "UTF-16 strings", "JMH", "the half-width of the 99.9% confidence interval of the mean"),
        new("typescript", "TypeScript", "typescript", "UTF-16 strings", "mitata", "the half-width of the 99.9% confidence interval of the mean"),
        new("python", "Python", "python", "strings of code points", "pyperf", "the standard deviation of the runs, around their mean"),
    ];

    // What makes the native comparison unfair in either direction, from the report of the Rust implementation.
    private static readonly string[] NativeCaveats = [
        "Steno's UTF-8 form takes a Rust `&str`, which is valid UTF-8 already, so it does no validation of its own; the validated UTF-8 copy does.",
        "ICU's converters are called through ICU's C API once per value. Each call resets the converter and checks its arguments, which costs most on " +
            "short values, and ICU works on UTF-16 only.",
        "The compressors work on each value by itself, as a database stores a field. Their frame headers and set-up cost most on short values: " +
            "one-shot Brotli sets up an encoder on every call, while Zstd and Deflate reuse a context and LZ4 needs none.",
        "The compressors are the C reference libraries, built with the default settings of the Rust crates that bundle them.",
    ];

    private static readonly string[] AlternativeOrder = ["utf8-copy", "utf8", "utf16", "scsu", "bocu1", "deflate", "brotli", "zstd", "lz4"];

    // ---------- Loading ----------

    private static SpeedData LoadSpeed(string output, List<SpeedRecord>? dotnetSpeed, MachineInfo? machine)
    {
        string root = Environment.GetEnvironmentVariable("STENO_RESULTS") is { Length: > 0 } r ? r : Report.FindRepoRoot();
        var data = new SpeedData { Exclusions = LoadExclusions(Path.Combine(output, "data", "speed-exclusions.csv")) };

        foreach (var implementation in Implementations)
        {
            var rows = new List<BenchRow>();
            var sizes = new Dictionary<(string Text, string Kind, string Codec), long>();
            var libraries = new Dictionary<string, string>();

            if (implementation.Folder is null)
            {
                LoadDotnet(root, dotnetSpeed, machine, rows, sizes, libraries);
                data.Tools[implementation.Id] = DotnetTool(root);
            }
            else
            {
                string bench = Path.Combine(root, "Source", implementation.Folder, "bench");

                if (implementation.Id is "rust")
                {
                    // The page of the Rust implementation is its native comparison, which has every alternative; its own results file has Steno
                    // and UTF-8 only. The C implementation's rows there are not alternatives.
                    rows.AddRange(ReadRows(Path.Combine(bench, "native-results.csv")).Where(x => x.Operation is "encode" or "decode" && !IsC(x.Codec)));

                    foreach (string[] fields in ReadCsv(Path.Combine(bench, "native-libraries.csv")))
                        libraries[fields[0]] = $"{fields[1]} ({fields[2]}), {fields[3]}";
                }
                else if (implementation.Id is "c")
                {
                    // Measured in the native comparison of the Rust implementation (see the remarks of the class): the native rows with steno-c as
                    // Steno, against the same alternatives, and the sizes and libraries of that run.
                    bench = Path.Combine(root, "Source", "rust", "bench");
                    var all = ReadRows(Path.Combine(bench, "native-results.csv")).Where(x => x.Operation is "encode" or "decode").ToList();
                    rows.AddRange(AsC(Exclude([.. all.Where(x => x.Codec is not "steno" and not CScalar)], data), CVector));
                    data.CScalar = Exclude(AsC(Exclude([.. all.Where(x => x.Codec is CScalar)], data), null), data);

                    foreach (string[] fields in ReadCsv(Path.Combine(bench, "native-libraries.csv")))
                        libraries[fields[0]] = $"{fields[1]} ({fields[2]}), {fields[3]}";

                    foreach (string[] fields in ReadCsv(Path.Combine(bench, "sizes.csv")))
                        sizes[(fields[1], fields[2], fields[3])] = long.Parse(fields[4], CultureInfo.InvariantCulture);

                    data.Tools[implementation.Id] = libraries.TryGetValue(CVector, out string? c)
                        ? $"Criterion, in the native benchmark of the Rust implementation (`Source/rust/bench`, whose BENCHMARKS.md has its settings), " +
                            $"through the C interface, once per value; `Source/c/steno.c` built by the cc crate at -O2: {c}."
                        : null;
                    data.Rows[implementation.Id] = Exclude(rows, data);
                    data.Sizes[implementation.Id] = sizes;
                    data.Libraries[implementation.Id] = libraries;
                    continue;
                }
                else
                {
                    rows.AddRange(ReadRows(Path.Combine(bench, "results.csv")));

                    foreach (string[] fields in ReadCsv(Path.Combine(bench, "libraries.csv")))
                        libraries[fields[0]] = $"{fields[1]}, {fields[2]}";
                }

                foreach (string[] fields in ReadCsv(Path.Combine(bench, "sizes.csv")))
                    sizes[(fields[1], fields[2], fields[3])] = long.Parse(fields[4], CultureInfo.InvariantCulture);

                data.Tools[implementation.Id] = ToolLine(Path.Combine(root, "Source", implementation.Folder, "BENCHMARKS.md"));
            }

            data.Rows[implementation.Id] = Exclude(rows, data);
            data.Sizes[implementation.Id] = sizes;
            data.Libraries[implementation.Id] = libraries;
        }

        // The headline: Steno (Rust) against the alternatives. The C implementation is not one of them.
        string native = Path.Combine(root, "Source", "rust", "bench");
        data.Native = Exclude([.. ReadRows(Path.Combine(native, "native-results.csv")).Where(x => !IsC(x.Codec))], data);

        foreach (string[] fields in ReadCsv(Path.Combine(native, "native-libraries.csv")).Where(f => !IsC(f[0])))
            data.NativeLibraries[fields[0]] = (fields[1], fields[3]);

        return data;
    }

    private static bool IsC(string codec) => codec is CVector or CScalar;

    // Native rows as rows of the C implementation, with the given codec as its Steno (or the codecs as they are, for null).
    private static List<BenchRow> AsC(List<BenchRow> rows, string? steno) =>
        [.. rows.Select(x => x with { Implementation = "c", Codec = steno is not null && x.Codec == steno ? "steno" : x.Codec })];

    // The .NET results: the common CSV for Steno and UTF-8, and the data file of the report for the other codecs (with BenchmarkDotNet's times).
    private static void LoadDotnet(string root, List<SpeedRecord>? speed, MachineInfo? machine, List<BenchRow> rows,
        Dictionary<(string Text, string Kind, string Codec), long> sizes, Dictionary<string, string> libraries)
    {
        rows.AddRange(ReadRows(Path.Combine(root, "Benchmarks", "results", "dotnet.csv")));

        if (speed is not null)
        {
            foreach (var record in speed.Where(s => s.EncodeNanoseconds > 0 || s.DecodeNanoseconds > 0))
            {
                string kind = record.Workload.StartsWith("document/", StringComparison.Ordinal) ? "document" : "values";
                string language = record.Workload[(record.Workload.IndexOf('/', StringComparison.Ordinal) + 1)..];
                string? text = Workloads.Texts.FirstOrDefault(t => t.Language == language || t.File == language).File;
                string codec = DotnetCodecId(record.Codec);

                if (text is null)
                    continue;

                sizes[(text, kind, codec)] = record.Bytes;

                if (rows.Any(x => x.Text == text && x.Kind == kind && x.Codec == codec))
                    continue;

                foreach ((string operation, double ns, double throughput, double error) in (ReadOnlySpan<(string, double, double, double)>)[
                    ("encode", record.EncodeNanoseconds, record.Encode, record.EncodeError), ("decode", record.DecodeNanoseconds, record.Decode, record.DecodeError),
                ])
                {
                    if (ns > 0)
                        rows.Add(new BenchRow("dotnet", text, kind, operation, "native", codec, ns, throughput > 0 ? ns * error / throughput : 0, record.Values, record.Chars));
                }
            }
        }

        string icu = machine is null ? "ICU4C" : machine.Icu;
        libraries["steno"] = "Singulink.Steno (this repository)";
        libraries["utf8"] = ".NET, `Encoding.UTF8` (GetBytes and GetChars into buffers allocated once)";
        libraries["scsu-icu"] = $"ICU4C through its C API, once per value: {icu}";
        libraries["bocu1-icu"] = $"ICU4C through its C API, once per value: {icu}";
        libraries["zstd-1"] = "ZstdSharp.Port, Zstandard at level 1";
        libraries["brotli-11"] = "System.IO.Compression, Brotli at level 11";
    }

    private static string DotnetCodecId(string codec) => codec switch {
        "Steno" => "steno",
        "UTF-8" => "utf8",
        "SCSU" => "scsu-icu",
        "BOCU-1" => "bocu1-icu",
        "Zstd 1" => "zstd-1",
        "Brotli 11" => "brotli-11",
        _ => codec.ToLowerInvariant().Replace(' ', '-'),
    };

    private static string? DotnetTool(string root)
    {
        string folder = Path.Combine(root, "Benchmarks", "results", "dotnet-bdn");

        if (!Directory.Exists(folder))
            return null;

        string? report = Directory.EnumerateFiles(folder, "*-report-full.json", SearchOption.AllDirectories).OrderBy(File.GetLastWriteTimeUtc).LastOrDefault();

        if (report is null)
            return null;

        using var document = JsonDocument.Parse(File.ReadAllText(report));
        var host = document.RootElement.GetProperty("HostEnvironmentInfo");
        return $"BenchmarkDotNet {host.GetProperty("BenchmarkDotNetVersion").GetString()}, one launch, 6 warm-up iterations and 15 measured iterations of " +
            $"250 ms per benchmark, on {host.GetProperty("RuntimeVersion").GetString()}.";
    }

    // The tool line of a BENCHMARKS.md ("- **Tool:** ..." or "- Tool: ..."), with the lines that continue it.
    private static string? ToolLine(string path)
    {
        if (!File.Exists(path))
            return null;

        var match = ToolRegex().Match(File.ReadAllText(path).ReplaceLineEndings("\n"));
        return match.Success ? Regex.Replace(match.Groups[1].Value, @"\s*\n\s+", " ").Trim() : null;
    }

    [GeneratedRegex(@"^- (?:\*\*)?Tool:(?:\*\*)? (.+(?:\n  .+)*)", RegexOptions.Multiline)]
    private static partial Regex ToolRegex();

    private static List<BenchRow> ReadRows(string path) => [
        .. ReadCsv(path).Select(f => new BenchRow(f[0], f[1], f[2], f[3], f[4], f[5], Number(f[6]), Number(f[7]),
            int.Parse(f[8], CultureInfo.InvariantCulture), long.Parse(f[9], CultureInfo.InvariantCulture))).Where(x => x.Ns > 0),
    ];

    private static double Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : 0;

    // The rows of a CSV file without its header, with quoted fields; nothing if the file is not there. Lines that start with # are comments.
    private static IEnumerable<string[]> ReadCsv(string path)
    {
        if (!File.Exists(path))
            yield break;

        foreach (string line in File.ReadLines(path).Skip(1))
        {
            if (line.Length is 0 || line.StartsWith('#'))
                continue;

            var fields = new List<string>();
            var field = new StringBuilder();
            bool quoted = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (quoted)
                {
                    if (c is '"' && i + 1 < line.Length && line[i + 1] is '"')
                    {
                        field.Append(c);
                        i++;
                    }
                    else if (c is '"')
                    {
                        quoted = false;
                    }
                    else
                    {
                        field.Append(c);
                    }
                }
                else if (c is '"')
                {
                    quoted = true;
                }
                else if (c is ',')
                {
                    fields.Add(field.ToString());
                    field.Clear();
                }
                else
                {
                    field.Append(c);
                }
            }

            fields.Add(field.ToString());
            yield return [.. fields];
        }
    }

    private static List<Exclusion> LoadExclusions(string path) => [
        .. ReadCsv(path).Where(f => f.Length >= 7).Select(f => new Exclusion(f[0], f[1], f[2], f[3], f[4], f[5], string.Join(",", f[6..]).Trim())),
    ];

    private static List<BenchRow> Exclude(List<BenchRow> rows, SpeedData data)
    {
        var kept = new List<BenchRow>();

        foreach (var row in rows)
        {
            var exclusion = data.Exclusions.FirstOrDefault(e => e.Matches(row.Implementation, row.Text, row.Kind, row.Operation, row.Form, row.Codec));

            if (exclusion is null)
                kept.Add(row);
            else
                data.Excluded[exclusion] = data.Excluded.GetValueOrDefault(exclusion) + 1;
        }

        return kept;
    }

    // ---------- Comparing two figures ----------

    // Steno against another codec on one workload: how many times as long one takes as the other, which is faster, whether the difference lies
    // within the two error margins, and how many significant figures the margins support.
    private static Versus Compare(BenchRow steno, BenchRow other)
    {
        double ts = steno.Ns / steno.Values, to = other.Ns / other.Values;
        double es = steno.ErrorNs / steno.Values, eo = other.ErrorNs / other.Values;
        double margin = Math.Sqrt(Math.Pow(es / ts, 2) + Math.Pow(eo / to, 2));
        return new Versus(Math.Max(ts, to) / Math.Min(ts, to), ts <= to, Math.Abs(ts - to) <= es + eo, margin > OneDigitMargin ? 1 : 2, Math.Log(to / ts));
    }

    // "2.3× faster", "40% slower" or "about the same": Steno against the other. Below a factor of 2 the difference is given in percent of the time.
    private static string VersusText(Versus v)
    {
        if (v.Same)
            return "about the same";

        string word = v.StenoFaster ? "faster" : "slower";

        string percent = Significant((v.Factor - 1) * 100, v.Digits);

        return v.Factor >= 2 || double.Parse(percent, NumberStyles.Number, CultureInfo.InvariantCulture) >= 100
            ? $"{Significant(v.Factor, v.Digits)}× {word}"
            : $"{percent}% {word}";
    }

    private static string Significant(double value, int digits)
    {
        if (value is 0 || !double.IsFinite(value))
            return "0";

        double scale = Math.Pow(10, digits - 1 - Math.Floor(Math.Log10(Math.Abs(value))));
        double rounded = Math.Round(value * scale) / scale;
        return rounded.ToString(rounded >= 1000 ? "N0" : "0.##########", CultureInfo.InvariantCulture);
    }

    // An absolute figure rounded to what its own margin supports.
    private static string Anchor(double value, double error) => Significant(value, value > 0 && error / value > OneDigitMargin ? 1 : 2);

    // The Steno row that an alternative is compared with: the same text, kind and operation, in the alternative's form if Steno has it.
    private static BenchRow? Partner(List<BenchRow> rows, BenchRow other) =>
        rows.FirstOrDefault(x => x.Codec is "steno" && x.Text == other.Text && x.Kind == other.Kind && x.Operation == other.Operation && x.Form == other.Form) ??
        rows.FirstOrDefault(x => x.Codec is "steno" && x.Text == other.Text && x.Kind == other.Kind && x.Operation == other.Operation && x.Form is "native");

    private static List<(string Text, Versus Versus)> Versuses(List<BenchRow> rows, string codec, string form, string kind, string operation) => [
        .. rows.Where(x => x.Codec == codec && x.Form == form && x.Kind == kind && x.Operation == operation)
            .Select(x => (x.Text, Steno: Partner(rows, x), Other: x))
            .Where(p => p.Steno is not null)
            .Select(p => (p.Text, Compare(p.Steno!, p.Other)))
            .OrderBy(p => TextIndex(p.Text)),
    ];

    // The median text (of the two middle texts, the one less favourable to Steno), with how many texts went each way.
    private static string Summary(List<(string Text, Versus Versus)> versuses)
    {
        if (versuses.Count is 0)
            return "not measured";

        var ordered = versuses.OrderBy(v => v.Versus.Same ? 0 : v.Versus.Log).ToList();
        var median = ordered[(ordered.Count - 1) / 2].Versus;
        return $"{VersusText(median)} ({Tally(versuses)})";
    }

    // "faster on 10, about the same on 3, slower on 1 of 14"
    private static string Tally(List<(string Text, Versus Versus)> versuses)
    {
        int faster = versuses.Count(v => !v.Versus.Same && v.Versus.StenoFaster);
        int same = versuses.Count(v => v.Versus.Same);
        int slower = versuses.Count - faster - same;
        var parts = new List<string>();

        if (faster > 0)
            parts.Add(Invariant($"faster on {faster}"));

        if (same > 0)
            parts.Add(Invariant($"about the same on {same}"));

        if (slower > 0)
            parts.Add(Invariant($"slower on {slower}"));

        return parts.Count is 1 && versuses.Count > 1
            ? Invariant($"{parts[0].Split(' ')[0]}{(parts[0].StartsWith("about", StringComparison.Ordinal) ? " the same" : string.Empty)} on all {versuses.Count}")
            : string.Join(", ", parts) + Invariant($" of {versuses.Count}");
    }

    // Steno's own time per value (or its throughput on documents) on the median text, rounded to what its margin supports.
    private static string StenoAnchor(List<BenchRow> rows, string form, string kind, string operation)
    {
        var steno = rows.Where(x => x.Codec is "steno" && x.Form == form && x.Kind == kind && x.Operation == operation).OrderBy(x => x.Ns / x.Values).ToList();

        if (steno.Count is 0)
            return "?";

        var median = steno[(steno.Count - 1) / 2];
        return kind is "values"
            ? Anchor(median.Ns / median.Values, median.ErrorNs / median.Values) + " ns"
            : Anchor(median.Units * 1000.0 / median.Ns, median.Units * 1000.0 / median.Ns * median.ErrorNs / median.Ns) + " million";
    }

    private static int TextIndex(string text) => Array.FindIndex(Workloads.Texts, t => t.File == text) is var i and >= 0 ? i : int.MaxValue;

    private static string TextName(string text) => Workloads.Texts.FirstOrDefault(t => t.File == text).Language ?? text;

    private static string CodecName(string codec, string form = "native") => codec switch {
        "steno" => "Steno",
        "utf8" => form is "utf16" ? "UTF-8, from UTF-16" : "UTF-8",
        "utf8-copy" => "UTF-8, validated copy",
        "utf16" => "UTF-16",
        "deflate-fast" => "Deflate fast",
        "deflate-max" => "Deflate max",
        "brotli-1" => "Brotli 1",
        "brotli-11" => "Brotli 11",
        "zstd-1" => "Zstd 1",
        "zstd-19" => "Zstd 19",
        "lz4" => "LZ4",
        "lz4-hc" => "LZ4 HC",
        _ when codec.StartsWith("scsu", StringComparison.Ordinal) => "SCSU",
        _ when codec.StartsWith("bocu1", StringComparison.Ordinal) => "BOCU-1",
        _ when codec.StartsWith("deflate-", StringComparison.Ordinal) => "Deflate " + codec["deflate-".Length..],
        _ => codec,
    };

    private static int AlternativeRank(string codec) =>
        Array.FindIndex(AlternativeOrder, p => codec == p || codec.StartsWith(p + "-", StringComparison.Ordinal) || (p is "utf8" && codec is "utf8")) is var i and >= 0
            ? i
            : AlternativeOrder.Length;

    private static List<(string Codec, string Form)> Alternatives(List<BenchRow> rows) => [
        .. rows.Where(x => x.Codec is not "steno" && x.Operation is "encode" or "decode").Select(x => (x.Codec, x.Form)).Distinct()
            .OrderBy(a => AlternativeRank(a.Codec)).ThenBy(a => a.Codec, StringComparer.Ordinal),
    ];

    private static bool IsCompressor(string codec) => AlternativeRank(codec) >= Array.IndexOf(AlternativeOrder, "deflate");

    // ---------- Native headline pages ----------

    private static string NativeLibrariesText(SpeedData data)
    {
        var list = data.NativeLibraries.Where(l => l.Key is not "steno")
            .GroupBy(l => l.Value)
            .Select(g => $"{Join([.. g.Select(l => CodecName(l.Key, data.Native.FirstOrDefault(x => x.Codec == l.Key)?.Form ?? "native"))])}: {g.Key.Library}, {g.Key.Version}")
            .ToList();
        return list.Count is 0 ? string.Empty : "The alternatives: " + string.Join("; ", list) + ".";
    }

    private static string NativeSpeedMarkdown(SpeedData data)
    {
        var md = new StringBuilder();
        md.AppendLine("# Cost per Value and Speed");
        md.AppendLine();

        var rows = data.Native;

        if (rows.Count is 0)
        {
            md.AppendLine(Pending);
            return md.ToString();
        }

        md.AppendLine(Invariant($"**The Rust reference implementation, Steno (Rust), encodes a 16-character value in about {StenoAnchor(rows, "native", "values", "encode")} and decodes one in about {StenoAnchor(rows, "native", "values", "decode")}** (from UTF-8, the median text). Against the alternatives, as the median text and how many texts went each way:"));
        md.AppendLine();

        foreach (var (codec, form) in Alternatives(rows).Where(a => !IsCompressor(a.Codec)))
        {
            md.AppendLine($"- **{NativeLabel(codec, form)}:** encode {Summary(Versuses(rows, codec, form, "values", "encode"))}; decode " +
                $"{Summary(Versuses(rows, codec, form, "values", "decode"))}.");
        }

        md.AppendLine();
        md.AppendLine("These figures are from native code: the Rust reference implementation, which has a C interface, against libraries that are " +
            "native too, so no call crosses a language boundary per value. That is what a database engine would embed. Each implementation's " +
            "own figures, against what a developer on its platform would use, are on its page under Implementations, and " +
            "[Implementations Side by Side](../articles/benchmarks/implementations.md) puts them together. Encode is text to bytes, decode is bytes to text. " +
            "Each figure is Steno against the alternative on the same text; [how the figures are rounded](" + SpeedMethodLink + ").");
        md.AppendLine();
        md.AppendLine(NativeLibrariesText(data));
        md.AppendLine();
        md.AppendLine("## Two Baselines for UTF-8");
        md.AppendLine();
        md.AppendLine("- **A validated copy.** A system that already holds UTF-8 text stores UTF-8 by checking it and copying it, so against this " +
            "baseline any encoding step is extra work. Steno's UTF-8 form is compared with it.");
        md.AppendLine("- **A conversion from UTF-16.** A system that holds UTF-16 strings, as .NET, Java and JavaScript do, stores UTF-8 by converting " +
            "it. Steno's UTF-16 form is compared with it, and so are SCSU and BOCU-1, which ICU converts from UTF-16.");
        md.AppendLine();

        string[] encodings = [.. Alternatives(rows).Where(a => !IsCompressor(a.Codec)).Select(a => a.Codec + "|" + a.Form)];

        foreach (string kind in (string[])["values", "document"])
        {
            md.AppendLine(kind is "values" ? "## 16-Character Values" : "## Whole Documents");
            md.AppendLine();
            md.AppendLine(kind is "values"
                ? Invariant($"Steno (Rust) takes about {StenoAnchor(rows, "native", kind, "encode")} to encode a 16-character value from UTF-8 and about {StenoAnchor(rows, "native", kind, "decode")} to decode one, on the median text. Each value is encoded on its own, into a buffer allocated once.")
                : Invariant($"Steno (Rust) encodes about {StenoAnchor(rows, "native", kind, "encode")} and decodes about {StenoAnchor(rows, "native", kind, "decode")} characters (UTF-16 code units) per second from and to UTF-8, on the median text."));
            md.AppendLine();
            md.Append(VersusTable(rows, encodings, kind, NativeLabel));
            md.AppendLine();
        }

        md.AppendLine("## Caveats");
        md.AppendLine();

        foreach (string caveat in NativeCaveats)
            md.AppendLine("- " + caveat);

        md.AppendLine();
        md.AppendLine("The general compressors are on [Steno and General Compressors](../articles/comparisons/compressors.md#speed-head-to-head).");
        return md.ToString();
    }

    private static string NativeLabel(string codec, string form) => codec switch {
        "utf8-copy" => "UTF-8, validated copy",
        "utf8" when form is "utf16" => "UTF-8, from UTF-16",
        _ when codec.StartsWith("scsu", StringComparison.Ordinal) => "SCSU (ICU)",
        _ when codec.StartsWith("bocu1", StringComparison.Ordinal) => "BOCU-1 (ICU)",
        _ => CodecName(codec, form),
    };

    // One row per text, and per alternative an encode and a decode column: Steno against it.
    private static string VersusTable(List<BenchRow> rows, string[] alternatives, string kind, Func<string, string, string> label)
    {
        var md = new StringBuilder();

        foreach (string operation in (string[])["encode", "decode"])
        {
            md.AppendLine(Capitalize(operation) + ":");
            md.AppendLine();
            md.Append(VersusTable(rows, alternatives, kind, operation, label));
            md.AppendLine();
        }

        return md.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string VersusTable(List<BenchRow> rows, string[] alternatives, string kind, string operation, Func<string, string, string> label)
    {
        var columns = alternatives.Select(a => a.Split('|')).Select(a => (Codec: a[0], Form: a[1], Operation: operation)).ToList();
        var cells = columns.ToDictionary(c => c, c => Versuses(rows, c.Codec, c.Form, kind, c.Operation).ToDictionary(v => v.Text, v => v.Versus));
        string[] texts = [.. rows.Where(x => x.Kind == kind && x.Codec is "steno").Select(x => x.Text).Distinct().OrderBy(TextIndex)];

        return Table(["Text", .. columns.Select(c => $"Against {label(c.Codec, c.Form)}")], [
            .. texts.Select(t => (string[])[TextName(t), .. columns.Select(c => cells[c].TryGetValue(t, out var v) ? VersusText(v) : "not measured")]),
        ]);
    }

    private static string NativeCompareMarkdown(SpeedData data)
    {
        var md = new StringBuilder();
        md.AppendLine("# Comparing Without Decoding");
        md.AppendLine();

        var rows = data.Native.Where(x => x.Operation is "compare").ToList();

        if (rows.Count is 0)
        {
            md.AppendLine(Pending);
            return md.ToString();
        }

        var decoded = rows.Where(x => x.Codec is "steno-decode").Select(x => (x.Text, Versus: Compare(Find(x, "steno")!, x))).ToList();
        var utf8 = rows.Where(x => x.Codec is "utf8").Select(x => (x.Text, Versus: Compare(Find(x, "steno")!, x))).ToList();
        double[] gains = [.. decoded.Select(d => d.Versus.StenoFaster ? d.Versus.Factor : 1 / d.Versus.Factor)];
        int digits = decoded.Min(d => d.Versus.Digits);

        md.AppendLine(Invariant($"**In the native measurements, comparing the Steno bytes of two values is {Significant(gains.Min(), digits)} to {Significant(gains.Max(), digits)} times as fast as decoding both and comparing the text. Against comparing UTF-8 bytes, it is {Tally(utf8)} cases (each text with sorted neighbours and with random pairs).**"));
        md.AppendLine();
        md.AppendLine("Steno bytes compare as plain bytes in the code point order of their text, so a store can sort, compare and search encoded keys " +
            "without decoding them. The figures are those of the Rust reference implementation: Steno bytes and UTF-8 bytes compared as byte " +
            "slices, and \"decode both\" decoding the two values to UTF-8 and comparing the text. The values are the distinct 16-character values " +
            "of each text, sorted. Sorted neighbours are each value and the next one, which often share a long prefix; random pairs mostly differ early.");
        md.AppendLine();

        foreach (string pairs in (string[])["neighbours", "random"])
        {
            var steno = rows.Where(x => x.Codec is "steno" && x.Form == pairs).OrderBy(x => x.Ns / x.Values).ToList();

            if (steno.Count is 0)
                continue;

            var middle = steno[(steno.Count - 1) / 2];
            md.AppendLine(Invariant($"Comparing the Steno bytes of {(pairs is "neighbours" ? "sorted neighbours" : "random pairs")} takes about {Anchor(middle.Ns / middle.Values, middle.ErrorNs / middle.Values)} ns, on the median text."));
            md.AppendLine();
        }

        string[] texts = [.. rows.Select(x => x.Text).Distinct().OrderBy(TextIndex)];
        md.Append(Table(["Text", "Sorted neighbours: against decoding both", "Sorted neighbours: against UTF-8 bytes", "Random pairs: against decoding both", "Random pairs: against UTF-8 bytes"], [
            .. texts.Select(t => (string[])[
                TextName(t), .. ((string[])["neighbours", "random"]).SelectMany(p => ((string[])["steno-decode", "utf8"]).Select(c => {
                    var other = rows.FirstOrDefault(x => x.Text == t && x.Form == p && x.Codec == c);
                    var steno = other is null ? null : Find(other, "steno");
                    return other is null || steno is null ? "not measured" : VersusText(Compare(steno, other));
                })),
            ]),
        ]));
        return md.ToString();

        BenchRow? Find(BenchRow other, string codec) => rows.FirstOrDefault(x => x.Text == other.Text && x.Form == other.Form && x.Codec == codec);
    }

    // The speed section of a comparison page: Steno (Rust) against one encoding, by text.
    private static string NativeRivalSpeedMarkdown(SpeedData data, string rival)
    {
        var md = new StringBuilder();
        md.AppendLine("## Speed, Head to Head");
        md.AppendLine();
        var rows = data.Native;
        var alternatives = Alternatives(rows).Where(a => (a.Codec.StartsWith("utf8", StringComparison.Ordinal) ? "UTF-8" : CodecName(a.Codec)) == rival).ToList();

        if (alternatives.Count is 0)
        {
            md.AppendLine(Pending);
            return md.ToString();
        }

        md.AppendLine("The figures of the Rust reference implementation, Steno (Rust), against " +
            (rival is "UTF-8" ? "Rust's standard library" : $"ICU's {rival} converter, called through its C API once per value") +
            ", both native code; each cell is Steno against " + rival + " on one text ([how the figures are rounded](" + SpeedMethodLink + ")). " +
            "The figures of the other implementations are on their pages under Implementations.");
        md.AppendLine();

        if (rival is "UTF-8")
        {
            md.AppendLine("UTF-8 has two baselines. For a system that already holds UTF-8 text, storing UTF-8 is a validated copy, and any encoding step " +
                "is extra work against it; Steno's UTF-8 form is compared with it. For a system that holds UTF-16 strings, storing UTF-8 is a " +
                "conversion; Steno's UTF-16 form is compared with it. " + NativeCaveats[0]);
        }
        else
        {
            md.AppendLine(NativeCaveats[1] + " Steno's UTF-16 form is compared with it.");
        }

        md.AppendLine();
        string[] columns = [.. alternatives.Select(a => a.Codec + "|" + a.Form)];

        string anchorForm = alternatives.Any(a => a.Form is "native") ? "native" : "utf16";
        string from = anchorForm is "native" ? "UTF-8" : "UTF-16";

        foreach (string kind in (string[])["values", "document"])
        {
            md.AppendLine(kind is "values"
                ? Invariant($"16-character values; Steno (Rust) takes about {StenoAnchor(rows, anchorForm, kind, "encode")} to encode one from {from} and {StenoAnchor(rows, anchorForm, kind, "decode")} to decode one, on the median text:")
                : "Whole documents:");
            md.AppendLine();
            md.Append(VersusTable(rows, columns, kind, NativeLabel));
            md.AppendLine();
        }

        return md.ToString();
    }

    private static string NativeCompressorsSpeedMarkdown(SpeedData data)
    {
        var md = new StringBuilder();
        md.AppendLine("## Speed, Head to Head");
        md.AppendLine();
        var rows = data.Native;
        var compressors = Alternatives(rows).Where(a => IsCompressor(a.Codec)).ToList();

        if (compressors.Count is 0)
        {
            md.AppendLine(Pending);
            return md.ToString();
        }

        md.AppendLine("The figures of the Rust reference implementation, Steno (Rust), against the C reference library of each compressor, each on " +
            "the UTF-8 of each value by itself. Each cell is Steno against the compressor: the median text, and how many of the texts went each way " +
            "([how the figures are rounded](" + SpeedMethodLink + ")). " + NativeCaveats[2] + " " + NativeCaveats[3]);
        md.AppendLine();
        md.Append(SummaryTable(rows, compressors, c => data.NativeLibraries.TryGetValue(c, out var l) ? $"{l.Library}, {l.Version}" : string.Empty, sizes: null));
        return md.ToString();
    }

    // One row per alternative: Steno against it, for 16-character values and documents, encode and decode, with its size where it is known.
    private static string SummaryTable(List<BenchRow> rows, List<(string Codec, string Form)> alternatives, Func<string, string> library,
        Func<string, string, string, string>? sizes)
    {
        string[] headers = [
            "Alternative", "Library", "Encode, 16-char values", "Decode, 16-char values", .. sizes is null ? [] : (string[])["Size, 16-char values"],
            "Encode, documents", "Decode, documents", .. sizes is null ? [] : (string[])["Size, documents"],
        ];

        return Table(headers, [
            .. alternatives.Select(a => (string[])[
                CodecName(a.Codec, a.Form), library(a.Codec),
                Summary(Versuses(rows, a.Codec, a.Form, "values", "encode")), Summary(Versuses(rows, a.Codec, a.Form, "values", "decode")),
                .. sizes is null ? [] : (string[])[sizes(a.Codec, a.Form, "values")],
                Summary(Versuses(rows, a.Codec, a.Form, "document", "encode")), Summary(Versuses(rows, a.Codec, a.Form, "document", "decode")),
                .. sizes is null ? [] : (string[])[sizes(a.Codec, a.Form, "document")],
            ]),
        ]);
    }

    // The speed scoreboard of the trade-offs page: on how many texts Steno (Rust) is faster, about the same or slower.
    private static string NativeScoreboard(SpeedData data)
    {
        var rows = data.Native;
        var md = new StringBuilder();
        md.AppendLine("## Speed Scoreboard");
        md.AppendLine();

        if (rows.Count is 0)
        {
            md.AppendLine(Pending);
            return md.ToString();
        }

        md.AppendLine("The Rust reference implementation, Steno (Rust), against native implementations of the alternatives, over the texts of the " +
            "shared workloads: on how many Steno is faster, about the same (the difference lies within the error margins) or slower. UTF-16 is not " +
            "measured, since storing it is a copy where strings are UTF-16.");
        md.AppendLine();
        md.Append(Table(["Alternative", "Encode, 16-char values", "Decode, 16-char values", "Encode, documents", "Decode, documents"], [
            .. Alternatives(rows).Where(a => !IsCompressor(a.Codec)).Select(a => (string[])[
                $"[{NativeLabel(a.Codec, a.Form)}](../articles/comparisons/{RivalSlug(a.Codec.StartsWith("utf8", StringComparison.Ordinal) ? "UTF-8" : CodecName(a.Codec))}.md)",
                Tally(Versuses(rows, a.Codec, a.Form, "values", "encode")), Tally(Versuses(rows, a.Codec, a.Form, "values", "decode")),
                Tally(Versuses(rows, a.Codec, a.Form, "document", "encode")), Tally(Versuses(rows, a.Codec, a.Form, "document", "decode")),
            ]),
        ]));
        return md.ToString();
    }

    // Every text where an alternative is faster than Steno (Rust), beyond the error margins.
    private static string NativeFasterCases(SpeedData data)
    {
        var rows = data.Native;
        var list = new List<string[]>();

        foreach (string kind in (string[])["values", "document"])
        {
            foreach (string text in rows.Where(x => x.Kind == kind).Select(x => x.Text).Distinct().OrderBy(TextIndex))
            {
                string Faster(string operation) => string.Join("; ", Alternatives(rows).Where(a => !IsCompressor(a.Codec))
                    .Select(a => (a, Versuses(rows, a.Codec, a.Form, kind, operation).FirstOrDefault(v => v.Text == text).Versus))
                    .Where(p => p.Versus is not null && !p.Versus.Same && !p.Versus.StenoFaster)
                    .Select(p => $"{NativeLabel(p.a.Codec, p.a.Form)} ({VersusText(p.Versus!).Replace("slower", "faster than Steno", StringComparison.Ordinal)})"));

                string e = Faster("encode"), d = Faster("decode");

                if (e.Length > 0 || d.Length > 0)
                    list.Add([TextName(text), kind is "values" ? "16-char values" : "Document", e.Length is 0 ? "none" : e, d.Length is 0 ? "none" : d]);
            }
        }

        return list.Count is 0
            ? "No encoding is faster than Steno (Rust) on any text, beyond the error margins.\n"
            : Table(["Text", "Values", "Encode: faster than Steno (Rust)", "Decode: faster than Steno (Rust)"], list);
    }

    // ---------- One implementation ----------

    private static string ImplementationMarkdown(SpeedData data, Implementation implementation)
    {
        var rows = data.Rows[implementation.Id];
        var md = new StringBuilder();
        string steno = $"Steno ({implementation.Name})";

        if (!rows.Any(x => x.Codec is "steno"))
        {
            md.AppendLine(Pending);
            return md.ToString();
        }

        var alternatives = Alternatives(rows);
        var libraries = data.Libraries[implementation.Id];
        string stenoForm = rows.Any(x => x.Codec is "steno" && x.Form is "native") ? "native" : rows.First(x => x.Codec is "steno").Form;

        md.AppendLine(Invariant($"The figures of the {implementation.Name} reference implementation, {steno}, against what a developer on its platform would use, on the shared workloads: 14 texts, each as one whole document and as values of at most 16 characters. Measured with {implementation.Tool}; its error margin here is {implementation.Margin}. Each cell is Steno against the alternative: the median text, and how many of the texts went each way ([how the figures are rounded and compared]({SpeedMethodLink}))."));
        md.AppendLine();
        md.AppendLine(Invariant($"{steno} takes about {StenoAnchor(rows, stenoForm, "values", "encode")} to encode a 16-character value and about {StenoAnchor(rows, stenoForm, "values", "decode")} to decode one; on whole documents it encodes about {StenoAnchor(rows, stenoForm, "document", "encode")} and decodes about {StenoAnchor(rows, stenoForm, "document", "decode")} characters (UTF-16 code units) per second. Both are the median text, from and to {implementation.TextForm.Split(',')[0]}."));
        md.AppendLine();

        if (!alternatives.Any(a => a.Codec.StartsWith("scsu", StringComparison.Ordinal) || a.Codec.StartsWith("bocu1", StringComparison.Ordinal)))
        {
            md.AppendLine($"{implementation.Name} has no implementation of SCSU or BOCU-1 in common use, so the choices there are UTF-8, a general compressor or Steno.");
            md.AppendLine();
        }

        md.Append(SummaryTable(rows, alternatives, c => libraries.TryGetValue(c, out string? l) ? l : string.Empty, (codec, form, kind) => SizeText(data, implementation, codec, kind)));
        md.AppendLine();
        md.AppendLine("Size is the alternative's total size over the 14 texts against Steno's: + is larger than Steno, − is smaller. A compressor works on " +
            "each value by itself, as a database stores a field.");

        if (implementation.Id is "rust")
        {
            md.AppendLine();

            foreach (string caveat in NativeCaveats)
                md.AppendLine("- " + caveat);
        }
        else if (implementation.Id is "c")
        {
            md.AppendLine();
            md.AppendLine("- " + CMethod + " The UTF-8 baselines are those of that run, Rust's standard library, where a C program would use its own.");

            foreach (string caveat in NativeCaveats[1..])
                md.AppendLine("- " + caveat);

            md.AppendLine();
            md.Append(CVectorMarkdown(data));
        }

        return md.ToString();
    }

    // What the vector code of the C implementation buys: steno-c against steno-c-scalar, by form, on values and documents.
    private static string CVectorMarkdown(SpeedData data)
    {
        var md = new StringBuilder();
        md.AppendLine("### The Vector Code");
        md.AppendLine();

        // The vector build is Steno in the rows of C; the scalar build is compared with it as an alternative would be.
        var rows = data.Rows["c"].Where(x => x.Codec is "steno").Concat(data.CScalar).ToList();
        string[] forms = [.. ((string[])["native", "utf16"]).Where(f => rows.Any(x => x.Codec is CScalar && x.Form == f))];

        if (forms.Length is 0)
        {
            md.AppendLine(Pending);
            return md.ToString();
        }

        md.AppendLine("Steno (C) as built, with its vector code, against the same file built with `-DSTENO_NO_SIMD`, which has none: the median text, and " +
            "how many of the texts went each way.");
        md.AppendLine();
        md.Append(Table(["Form", "Encode, 16-char values", "Decode, 16-char values", "Encode, documents", "Decode, documents"], [
            .. forms.Select(f => (string[])[
                f is "native" ? "UTF-8" : "UTF-16",
                Summary(Versuses(rows, CScalar, f, "values", "encode")), Summary(Versuses(rows, CScalar, f, "values", "decode")),
                Summary(Versuses(rows, CScalar, f, "document", "encode")), Summary(Versuses(rows, CScalar, f, "document", "decode")),
            ]),
        ]));
        md.AppendLine();

        // Where it buys nothing: the texts where the vector build is not faster beyond the error margins, grouped by the cells that share them.
        var nothing = new List<(string Cell, string Texts)>();
        int cells = 0;

        foreach (string form in forms)
        {
            foreach (string kind in (string[])["values", "document"])
            {
                foreach (string operation in (string[])["encode", "decode"])
                {
                    var versuses = Versuses(rows, CScalar, form, kind, operation);

                    if (versuses.Count is 0)
                        continue;

                    cells++;
                    string[] texts = [.. versuses.Where(v => v.Versus.Same || !v.Versus.StenoFaster).Select(v => TextName(v.Text))];

                    if (texts.Length > 0)
                    {
                        string cell = $"{(form is "native" ? "UTF-8" : "UTF-16")} {(kind is "values" ? "16-char values" : "documents")} {operation}";
                        nothing.Add((cell, Join(texts)));
                    }
                }
            }
        }

        var groups = nothing.GroupBy(n => n.Texts).ToList();

        md.AppendLine(nothing.Count is 0
            ? "The vector code is faster on every text, beyond the error margins."
            : "It buys nothing (about the same or slower) on " + string.Join("; and on ", groups.Select(g => g.Key + " (" +
                (g.Count() == cells ? "in every form, length and operation" : Join([.. g.Select(n => n.Cell)])) + ")")) + ".");
        return md.ToString();
    }

    private static string SizeText(SpeedData data, Implementation implementation, string codec, string kind)
    {
        long steno = 0, other = 0;

        foreach (var (text, _) in Workloads.Texts)
        {
            long? s = SizeOf(data, implementation, text, kind, "steno");
            long? o = SizeOf(data, implementation, text, kind, codec);

            if (s is null || o is null)
                return "not recorded";

            steno += s.Value;
            other += o.Value;
        }

        return Relative(other, steno);
    }

    // The size of a codec on a workload: from the implementation's sizes, or computed for the encodings whose bytes every implementation shares.
    private static long? SizeOf(SpeedData data, Implementation implementation, string text, string kind, string codec)
    {
        if (data.Sizes[implementation.Id].TryGetValue((text, kind, codec), out long size))
            return size;

        if (codec is "utf8-copy")
            codec = "utf8";

        var shared = codec switch {
            "steno" => Codecs.Steno,
            "utf8" => Codecs.Utf8,
            "utf16" => Codecs.Utf16,
            _ => null,
        };

        if (shared is null)
            return null;

        string[] values = kind is "document" ? [Workloads.Document(text)] : Workloads.Values(text);
        return values.Sum(v => (long)shared.Encode(v).Length);
    }

    // ---------- Side by side ----------

    private static string SideBySideMarkdown(SpeedData data)
    {
        var md = new StringBuilder();
        md.AppendLine("# Implementations Side by Side");
        md.AppendLine();

        var present = Implementations.Where(i => data.Rows[i.Id].Any(x => x.Codec is "steno")).ToList();

        if (present.Count is 0)
        {
            md.AppendLine(Pending);
            return md.ToString();
        }

        md.AppendLine(Invariant($"**The {Count(Implementations.Length)} reference implementations on the same workloads, Steno only.") +
            " Two things to keep in mind:**");
        md.AppendLine();
        md.AppendLine("- **The text forms differ by language.** A Rust or Go string is UTF-8, as are the bytes that the C implementation takes, a .NET, " +
            "Java or JavaScript string is UTF-16, and a Python string is a sequence of code points. Each implementation is measured from and to its " +
            "own language's strings, so each figure includes what that form costs.");
        md.AppendLine("- **Each figure reflects that implementation's stage of tuning, not a limit of its language.** The implementations encode the " +
            "same bytes, and some have had more work on speed than others.");
        md.AppendLine();
        md.AppendLine("Each figure is the median text, rounded to what its error margin supports ([how](" + SpeedMethodLink + ")). The last two columns " +
            "compare each implementation with its own platform's UTF-8, in the same harness: a ratio that does not depend on how fast the language is.");
        md.AppendLine();

        if (present.Any(i => i.Id is "c"))
        {
            md.AppendLine(CMethod + " Its UTF-8 baseline is the one of the Rust rows, Rust's standard library in the same run.");
            md.AppendLine();
        }

        md.Append(Table([
            "Implementation", "Text form", "Encode, 16-char value (ns)", "Decode, 16-char value (ns)", "Encode, documents (million chars/s)",
            "Decode, documents (million chars/s)", "Encode, 16-char values, against its UTF-8", "Decode, 16-char values, against its UTF-8",
        ], [
            .. present.Select(i => {
                var rows = data.Rows[i.Id];
                string form = rows.Any(x => x.Codec is "steno" && x.Form is "native") ? "native" : "utf16";
                string Bare(string kind, string op) => StenoAnchor(rows, form, kind, op).Replace(" ns", string.Empty, StringComparison.Ordinal).Replace(" million", string.Empty, StringComparison.Ordinal);
                string Utf8(string op)
                {
                    var forms = rows.Where(x => x.Codec is "utf8").Select(x => x.Form).Distinct().ToList();
                    return forms.Count is 0 ? "not measured" : Summary(Versuses(rows, "utf8", forms.Contains("native") ? "native" : forms[0], "values", op));
                }

                return (string[])[
                    $"[{i.Name}](../articles/implementations/{i.Id}.md#performance)", i.TextForm, Bare("values", "encode"), Bare("values", "decode"),
                    Bare("document", "encode"), Bare("document", "decode"), Utf8("encode"), Utf8("decode"),
                ];
            }),
        ]));
        md.AppendLine();

        foreach (string op in (string[])["encode", "decode"])
        {
            md.AppendLine($"## {Capitalize(op)}, 16-Character Values, by Text");
            md.AppendLine();
            md.AppendLine("Nanoseconds per value, rounded to what each margin supports; smaller is better.");
            md.AppendLine();
            string[] texts = [.. Workloads.Texts.Select(t => t.File).Where(t => present.Any(i => data.Rows[i.Id].Any(x => x.Text == t)))];
            md.Append(Table(["Text", .. present.Select(i => i.Name)], [
                .. texts.Select(t => (string[])[
                    TextName(t), .. present.Select(i => {
                        var rows = data.Rows[i.Id];
                        string form = rows.Any(x => x.Codec is "steno" && x.Form is "native") ? "native" : "utf16";
                        var row = rows.FirstOrDefault(x => x.Codec is "steno" && x.Form == form && x.Kind is "values" && x.Operation == op && x.Text == t);
                        return row is null ? "not measured" : Anchor(row.Ns / row.Values, row.ErrorNs / row.Values);
                    }),
                ]),
            ]));
            md.AppendLine();
        }

        return md.ToString();
    }

    // ---------- Method ----------

    private static string SpeedMethodMarkdown(SpeedData data)
    {
        var md = new StringBuilder();
        md.AppendLine("## Speed");
        md.AppendLine();
        md.AppendLine("Every implementation is measured with the benchmarking tool of its own ecosystem, on the same workloads: the files in " +
            "[Benchmarks/Workloads](https://github.com/Singulink/Steno/tree/main/Benchmarks/Workloads), 14 texts, each as one whole document " +
            "and as values of at most 16 UTF-16 code units. One operation encodes or decodes every value of a text once, in a plain loop that calls " +
            "the library directly, and the time per value is the tool's time per operation divided by the number of values. All the benchmarks ran on " +
            "one machine that was doing other work at the same time, so the figures are indicative, and their error margins are part of the result. " +
            CMethod);
        md.AppendLine();
        md.Append(Table(["Implementation", "Tool", "Error margin", "Settings and versions"], [
            .. Implementations.Select(i => (string[])[i.Name, i.Tool, i.Margin, data.Tools.GetValueOrDefault(i.Id) ?? "After the benchmark run."]),
        ]));
        md.AppendLine();
        md.AppendLine("The headline figures, on the comparison pages and on [Cost per Value and Speed](../articles/benchmarks/speed.md), are those of the Rust reference " +
            "implementation, which has a C interface, against native libraries: no call crosses a language boundary per value, and native code is " +
            "what a database engine would embed. What makes that comparison unfair in either direction:");
        md.AppendLine();

        foreach (string caveat in NativeCaveats)
            md.AppendLine("- " + caveat);

        md.AppendLine();
        md.AppendLine("#### How the Figures Are Shown");
        md.AppendLine();
        md.AppendLine("The pages show ratios against a stated baseline, not raw timings: \"2.3× faster\" means that the other codec takes 2.3 times " +
            "as long as Steno; \"40% slower\" means that Steno takes 1.4 times as long as the other. Below a factor of 2 the difference is given in " +
            "percent. Each ratio is rounded to two significant figures, or to one where the combined relative error margin of the two figures is above " +
            "10%. Where the difference between the two times lies within the sum of their error margins, the cell says \"about the same\", not a " +
            "win for either. A summary over the texts shows the median text (of the two middle texts, the one less favourable to Steno) and how " +
            "many texts went each way. Each table has one absolute figure for scale, Steno's own time or throughput on the median text, rounded the " +
            "same way. The error margins are those the tools report, which differ in kind (see the table above).");
        md.AppendLine();
        md.AppendLine("#### Left Out");
        md.AppendLine();

        if (data.Exclusions.Count is 0)
        {
            md.AppendLine("No figure is left out.");
        }
        else
        {
            md.AppendLine("These figures are left out of the pages, each for the reason given; the data files still have them.");
            md.AppendLine();
            md.Append(Table(["Implementation", "Text", "Kind", "Operation", "Form", "Codec", "Figures left out", "Reason"], [
                .. data.Exclusions.Select(e => (string[])[
                    Any(e.Implementation), Any(e.Text), Any(e.Kind), Any(e.Operation), Any(e.Form), Any(e.Codec),
                    Invariant($"{data.Excluded.GetValueOrDefault(e)}"), e.Reason,
                ]),
            ]));
        }

        return md.ToString();

        static string Any(string field) => field is "" or "*" ? "any" : field;
    }

    // The sentences for the README and the home page, from the native results.
    private static string HeadlineMarkdown(SpeedData data)
    {
        var md = new StringBuilder();
        md.AppendLine("# Speed Statement for the README and the Home Page");
        md.AppendLine();
        md.AppendLine("Generated from the native results; copy the paragraph below into the README and the home page after each benchmark run.");
        md.AppendLine();

        var rows = data.Native;

        if (rows.Count is 0)
        {
            md.AppendLine(Pending);
            return md.ToString();
        }

        string Median(string codec, string form, string op) => Summary(Versuses(rows, codec, form, "values", op)) is var s && s.IndexOf(" (", StringComparison.Ordinal) is var i and > 0 ? s[..i] : "not measured";

        md.AppendLine(Invariant($"> In native code, the Rust reference implementation encodes a 16-character value in about {StenoAnchor(rows, "native", "values", "encode")} and decodes one in about {StenoAnchor(rows, "native", "values", "decode")}. ") +
            $"Against ICU's SCSU it is {Median("scsu-icu", "utf16", "encode")} to encode and {Median("scsu-icu", "utf16", "decode")} to decode; against " +
            $"ICU's BOCU-1, {Median("bocu1-icu", "utf16", "encode")} to encode and {Median("bocu1-icu", "utf16", "decode")} to decode. Against a validated copy of UTF-8, the " +
            $"baseline for a system that already holds UTF-8, it is {Median("utf8-copy", "native", "encode")} to encode and " +
            $"{Median("utf8-copy", "native", "decode")} to decode; a store that works on the bytes pays that only when text enters or leaves it " +
            "([Cost per Value and Speed](https://www.singulink.com/Docs/Steno/articles/benchmarks/speed.html)).");
        return md.ToString();
    }

    private sealed record Implementation(string Id, string Name, string? Folder, string TextForm, string Tool, string Margin);

    private sealed record BenchRow(string Implementation, string Text, string Kind, string Operation, string Form, string Codec, double Ns, double ErrorNs,
        int Values, long Units);

    private sealed record Versus(double Factor, bool StenoFaster, bool Same, int Digits, double Log);

    private sealed record Exclusion(string Implementation, string Text, string Kind, string Operation, string Form, string Codec, string Reason)
    {
        public bool Matches(string implementation, string text, string kind, string operation, string form, string codec) =>
            Is(Implementation, implementation) && Is(Text, text) && Is(Kind, kind) && Is(Operation, operation) && Is(Form, form) && Is(Codec, codec);

        private static bool Is(string pattern, string value) => pattern is "" or "*" || string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SpeedData
    {
        public Dictionary<string, List<BenchRow>> Rows { get; } = [];

        public Dictionary<string, Dictionary<(string Text, string Kind, string Codec), long>> Sizes { get; } = [];

        public Dictionary<string, Dictionary<string, string>> Libraries { get; } = [];

        public Dictionary<string, string?> Tools { get; } = [];

        public List<BenchRow> Native { get; set; } = [];

        // The rows of the scalar build of the C implementation, as rows of the C implementation.
        public List<BenchRow> CScalar { get; set; } = [];

        public Dictionary<string, (string Library, string Version)> NativeLibraries { get; } = [];

        public List<Exclusion> Exclusions { get; init; } = [];

        public Dictionary<Exclusion, int> Excluded { get; } = [];
    }
}
