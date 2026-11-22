using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Singulink.Steno.Benchmarks;

/// <summary>
/// Turns the results of the BenchmarkDotNet runs (Singulink.Steno.Benchmarks.Speed) into the data files of the report (speed.jsonl, ascii.json and
/// compare.json, in the schema that the renderer reads) and into the common CSV of the speed benchmarks of every implementation.
/// </summary>
internal static class BenchmarkImport
{
    private static readonly Dictionary<string, string> CodecOfMethod = new() {
        ["Steno"] = "Steno", ["Utf8"] = "UTF-8", ["Scsu"] = "SCSU", ["Bocu1"] = "BOCU-1", ["Zstd1"] = "Zstd 1", ["Brotli11"] = "Brotli 11",
    };

    private static readonly (string Method, string Operation)[] AsciiOperations = [
        ("Copy", "Copy of the bytes (Span.CopyTo)"),
        ("AsciiIsValid", "Ascii.IsValid (.NET, on the UTF-8 bytes)"),
        ("IsAsciiText", "StenoEncoding.IsAsciiText"),
        ("StenoToUtf8", "Steno to UTF-8 (GetUtf8Bytes)"),
        ("Utf8ToSteno", "UTF-8 to Steno (GetBytesFromUtf8)"),
        ("StenoToUtf16", "Steno to UTF-16 (GetChars), for comparison"),
    ];

    private static readonly (string Method, string Operation)[] CompareOperations = [
        ("StenoBytes", "Steno bytes, compared as they are (SequenceCompareTo)"),
        ("StenoDecodedBoth", "Steno bytes, both decoded, then CompareCodePoints"),
        ("StringsCompareCodePoints", "Strings in memory, CompareCodePoints"),
        ("StringsCompareOrdinal", "Strings in memory, string.CompareOrdinal (another order)"),
        ("Utf8Bytes", "UTF-8 bytes, compared as they are (SequenceCompareTo)"),
    ];

    private static readonly (string Param, string Kind)[] ComparePairs = [
        ("neighbours", "neighbours in sorted order (long common prefixes)"),
        ("random", "random pairs"),
    ];

    /// <summary>
    /// Reads every result of BenchmarkDotNet under <paramref name="artifacts"/> and writes the data files that it has results for. Returns the number
    /// of results read.
    /// </summary>
    public static int Run(string artifacts, string data, string csv, bool speed, bool ascii, bool compare)
    {
        var results = Read(artifacts);
        Console.WriteLine($"   {results.Count} results of BenchmarkDotNet under {artifacts}");
        Directory.CreateDirectory(data);

        if (speed)
            WriteSpeed(results, data, csv);

        if (ascii)
            WriteAscii(results, data);

        if (compare)
            WriteCompare(results, data);

        return results.Count;
    }

    /// <summary>
    /// Reads the full JSON reports of BenchmarkDotNet; the latest result of each case counts.
    /// </summary>
    private static List<Result> Read(string artifacts)
    {
        var results = new Dictionary<(string, string, string), Result>();

        if (!Directory.Exists(artifacts))
            return [];

        foreach (string path in Directory.EnumerateFiles(artifacts, "*-report-full.json", SearchOption.AllDirectories).OrderBy(File.GetLastWriteTimeUtc))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));

            foreach (var benchmark in document.RootElement.GetProperty("Benchmarks").EnumerateArray())
            {
                if (!benchmark.TryGetProperty("Statistics", out var statistics) || statistics.ValueKind is not JsonValueKind.Object)
                    continue;

                string type = benchmark.GetProperty("Type").GetString()!;
                string method = benchmark.GetProperty("Method").GetString()!;
                string parameters = benchmark.GetProperty("Parameters").GetString() ?? string.Empty;
                string parameter = parameters.Split('&').Select(p => p.Split('=', 2)).FirstOrDefault(p => p.Length is 2)?[1] ?? string.Empty;

                double mean = Number(statistics.GetProperty("Mean"));
                double margin = statistics.TryGetProperty("ConfidenceInterval", out var interval) && interval.TryGetProperty("Margin", out var m)
                    ? Number(m) : 0;

                if (double.IsFinite(mean) && mean > 0)
                    results[(type, method, parameter)] = new Result(type, method, parameter, mean, double.IsFinite(margin) ? margin : 0);
            }
        }

        return [.. results.Values];
    }

    private static void WriteSpeed(List<Result> results, string data, string csvPath)
    {
        var records = new List<SpeedRecord>();
        var csv = new StringBuilder("implementation,text,kind,operation,form,codec,ns_per_operation,error_ns,values,utf16_units\n");

        foreach (var workload in SpeedWorkloads.All)
        {
            string type = workload.Kind == SpeedWorkloads.Document ? "DocumentBenchmarks" : "ValueBenchmarks";
            string[] values = workload.Kind == SpeedWorkloads.Document ? [Workloads.Document(workload.File)] : Workloads.Values(workload.File);
            long chars = values.Sum(v => (long)v.Length);

            foreach (var (method, codecName) in CodecOfMethod)
            {
                var encode = results.FirstOrDefault(r => r.Type == type && r.Method == method + "Encode" && r.Parameter == workload.File);
                var decode = results.FirstOrDefault(r => r.Type == type && r.Method == method + "Decode" && r.Parameter == workload.File);

                if (encode is null && decode is null)
                    continue;

                var codec = Codecs.ByName(codecName);
                long bytes = values.Sum(v => (long)codec.Encode(v).Length);

                records.Add(new SpeedRecord(workload.Id, 1, codecName, Throughput(chars, encode), Throughput(chars, decode), chars, bytes,
                    ThroughputError(chars, encode), ThroughputError(chars, decode), Round(encode?.Mean), Round(decode?.Mean), values.Length));

                if (codecName is "Steno" or "UTF-8" && Workloads.Texts.Any(t => t.File == workload.File))
                {
                    string kind = workload.Kind == SpeedWorkloads.Document ? "document" : "values";
                    string codecId = codecName is "Steno" ? "steno" : "utf8";

                    foreach ((string operation, var result) in (ReadOnlySpan<(string, Result?)>)[("encode", encode), ("decode", decode)])
                    {
                        if (result is not null)
                        {
                            csv.Append(Invariant($"dotnet,{workload.File},{kind},{operation},native,{codecId},"));
                            csv.Append(Invariant($"{result.Mean:0.###},{result.Margin:0.###},{values.Length},{chars}\n"));
                        }
                    }
                }
            }
        }

        File.WriteAllText(Path.Combine(data, "speed.jsonl"), string.Concat(records.Select(r => JsonSerializer.Serialize(r) + "\n")), new UTF8Encoding(false));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csvPath))!);
        File.WriteAllText(csvPath, csv.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"   speed.jsonl: {records.Count} records; {Path.GetFileName(csvPath)}: {csv.ToString().Count(c => c is '\n') - 1} rows");
    }

    private static void WriteAscii(List<Result> results, string data)
    {
        // The text of AsciiBenchmarks: the English document without anything that is not printable ASCII, tab or line feed.
        string text = new([.. Workloads.Document("english").Where(c => c is '\n' or '\t' or (>= ' ' and <= '~'))]);
        var records = new List<AsciiRecord>();

        foreach (string kind in (string[])["document", "16-char strings"])
        {
            string[] values = kind is "document" ? [text] : [.. text.Split('\n').SelectMany(l => Corpus.Chunk(l, 16))];
            long chars = values.Sum(v => (long)v.Length);

            foreach ((string method, string operation) in AsciiOperations)
            {
                if (results.FirstOrDefault(r => r.Type == "AsciiBenchmarks" && r.Method == method && r.Parameter == kind) is { } result)
                {
                    records.Add(new AsciiRecord(kind, operation, Throughput(chars, result), values.Length, chars, ThroughputError(chars, result),
                        Round(result.Mean)));
                }
            }
        }

        if (records.Count > 0)
            File.WriteAllText(Path.Combine(data, "ascii.json"), JsonSerializer.Serialize(records, Report.Json));

        Console.WriteLine($"   ascii.json: {records.Count} records");
    }

    private static void WriteCompare(List<Result> results, string data)
    {
        // The keys of CompareBenchmarks: as many pairs as it makes of them.
        Corpus.Root = Workloads.CorpusFolder;
        int count = Corpus.Load()
            .Where(e => e.Group is Corpus.UdhrSnippets or Corpus.ShortStrings)
            .SelectMany(e => e.Units)
            .Distinct(StringComparer.Ordinal)
            .Count();

        var records = new List<CompareRecord>();

        foreach ((string param, string kind) in ComparePairs)
        {
            int pairs = param is "neighbours" ? count - 1 : count;

            foreach ((string method, string operation) in CompareOperations)
            {
                if (results.FirstOrDefault(r => r.Type == "CompareBenchmarks" && r.Method == method && r.Parameter == param) is { } result)
                    records.Add(new CompareRecord(kind, operation, Math.Round(result.Mean / pairs, 3), pairs, Math.Round(result.Margin / pairs, 3)));
            }
        }

        if (records.Count > 0)
            File.WriteAllText(Path.Combine(data, "compare.json"), JsonSerializer.Serialize(records, Report.Json));

        Console.WriteLine($"   compare.json: {records.Count} records");
    }

    // A number of the JSON of BenchmarkDotNet, which writes NaN (one iteration has no spread) as a string.
    private static double Number(JsonElement element) => element.ValueKind is JsonValueKind.Number
        ? element.GetDouble()
        : double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : double.NaN;

    // Millions of chars per second from the mean time of an operation in ns.
    private static double Throughput(long chars, Result? result) => result is null ? 0 : Math.Round(chars * 1000.0 / result.Mean, 1);

    private static double ThroughputError(long chars, Result? result) =>
        result is null ? 0 : Math.Round((chars * 1000.0 / result.Mean) * result.Margin / result.Mean, 1);

    private static double Round(double? value) => value is null ? 0 : Math.Round(value.Value, 3);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// One case of BenchmarkDotNet: the mean time of an operation in ns, and the half-width of its 99.9% confidence interval.
    /// </summary>
    private sealed record Result(string Type, string Method, string Parameter, double Mean, double Margin);
}
