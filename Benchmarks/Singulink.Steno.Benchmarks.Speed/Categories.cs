using BenchmarkDotNet.Jobs;
using Perfolizer.Horology;

namespace Singulink.Steno.Benchmarks.Speed;

/// <summary>
/// The categories that split the benchmarks into parts that run, and can be run again, on their own, and the job that measures them.
/// </summary>
public static class Categories
{
    public const string Documents = "documents";
    public const string Values = "values";
    public const string Ascii = "ascii";
    public const string Compare = "compare";

    public const string StenoUtf8 = "steno-utf8";
    public const string Icu = "icu";
    public const string Compressors = "compressors";

    /// <summary>
    /// Gets the parts of a full run, as the categories that select each one (all of them must match).
    /// </summary>
    public static IReadOnlyList<string[]> Parts { get; } = [
        [Documents, StenoUtf8], [Documents, Icu], [Documents, Compressors],
        [Values, StenoUtf8], [Values, Icu], [Values, Compressors],
        [Ascii], [Compare],
    ];

    /// <summary>
    /// Gets the job of the measurements: one process per case, 6 warm-up and 15 measured iterations of about 250 ms each, without the overhead
    /// iterations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cases are steady (no I/O, nothing that warms up slowly after the JIT's tiers), so 6 warm-up iterations are plenty and 15 measured ones give
    /// a tight confidence interval; a launch count of 1 keeps the run to about 7 s per case, the 406 cases to about 50 minutes. The spread across
    /// iterations is reported with each figure, since the machine may be in use.</para>
    /// <para>
    /// Measuring BenchmarkDotNet's own overhead (an empty call, about 1 ns) would double the time of every case for nothing: every operation is a
    /// whole document or a loop over hundreds of values, so it takes from about 50 ns (copying an ASCII document) to milliseconds, and the
    /// overhead is under 2% of the shortest.</para>
    /// </remarks>
    public static Job MeasureJob { get; } = Job.Default
        .WithLaunchCount(1)
        .WithWarmupCount(6)
        .WithIterationCount(15)
        .WithIterationTime(TimeInterval.FromMilliseconds(250))
        .WithEvaluateOverhead(false);
}
