namespace Singulink.Steno.Benchmarks;

/// <summary>
/// The workloads of the speed measurements, as the report names them: a text as one whole document, or as 16-char values.
/// </summary>
internal static class SpeedWorkloads
{
    public const string Document = "document";
    public const string Strings16 = "16-char strings";

    public static IReadOnlyList<SpeedWorkload> All { get; } = [
        .. Workloads.Texts.Select(t => new SpeedWorkload($"document/{t.Language}", Document, t.Language, Corpus.UdhrDocs, t.File)),
        .. Workloads.MixedDocuments.Select(f => new SpeedWorkload($"document/{f}", Document, f, Corpus.MixedDocs, f)),
        .. Workloads.Texts.Select(t => new SpeedWorkload($"16/{t.Language}", Strings16, t.Language, Corpus.UdhrSnippets, t.File)),
    ];

    /// <summary>
    /// Gets the codecs that the speed is measured for, by the names that the report uses.
    /// </summary>
    public static IReadOnlyList<string> Codecs { get; } = ["Steno", "UTF-8", "SCSU", "BOCU-1", "Zstd 1", "Brotli 11"];
}

/// <summary>
/// A workload of the speed measurements: <paramref name="File"/> is the name of the file in Benchmarks/Workloads (or of the mixed-content file).
/// </summary>
internal sealed record SpeedWorkload(string Id, string Kind, string Name, string Group, string File);

/// <summary>
/// The speed of a codec on a workload: encode and decode throughput in millions of UTF-16 chars per second (from BenchmarkDotNet's mean), the
/// half-width of their 99.9% confidence intervals, and the mean time of one operation in ns.
/// </summary>
internal sealed record SpeedRecord(string Workload, int Pass, string Codec, double Encode, double Decode, long Chars, long Bytes,
    double EncodeError = 0, double DecodeError = 0, double EncodeNanoseconds = 0, double DecodeNanoseconds = 0, int Values = 0);

/// <summary>
/// The throughput of an operation on ASCII text, in millions of bytes (= chars) per second, with the half-width of its 99.9% confidence interval.
/// </summary>
internal sealed record AsciiRecord(string Kind, string Operation, double Throughput, int Values, long Chars, double Error = 0, double Nanoseconds = 0);

/// <summary>
/// The time of one comparison of two values in ns, with the half-width of its 99.9% confidence interval.
/// </summary>
internal sealed record CompareRecord(string Kind, string Operation, double Nanoseconds, int Pairs, double Error = 0);
