using System.Text;
using BenchmarkDotNet.Attributes;

namespace Singulink.Steno.Benchmarks.Speed;

/// <summary>
/// Comparing two encoded values: the Steno bytes as they are, against decoding both and comparing the text, comparing strings that are in memory,
/// and comparing UTF-8 bytes. The keys are the 16-char values of every UDHR language and the short strings of the built-in corpus, each once, in
/// pairs of neighbours in sorted order (long common prefixes) or random pairs. One operation is every pair once.
/// </summary>
[BenchmarkCategory(Categories.Compare)]
public class CompareBenchmarks
{
    private static readonly StenoEncoding Steno = StenoEncoding.Instance;

    private string[] _values = null!;
    private byte[][] _steno = null!;
    private byte[][] _utf8 = null!;
    private int[] _x = null!;
    private int[] _y = null!;
    private char[] _a = null!;
    private char[] _b = null!;

    /// <summary>
    /// Gets or sets the kind of pairs: "neighbours" or "random".
    /// </summary>
    [Params("neighbours", "random")]
    public string Pairs { get; set; } = null!;

    [GlobalSetup]
    public void Setup()
    {
        Corpus.Root = Workloads.CorpusFolder;

        _values = [
            .. Corpus.Load()
                .Where(e => e.Group is Corpus.UdhrSnippets or Corpus.ShortStrings)
                .SelectMany(e => e.Units)
                .Distinct(StringComparer.Ordinal),
        ];

        Array.Sort(_values, StenoEncoding.CodePointComparer);

        _steno = [.. _values.Select(v => Steno.GetBytes(v))];
        _utf8 = [.. _values.Select(Encoding.UTF8.GetBytes)];
        _a = new char[_values.Max(v => v.Length) + 16];
        _b = new char[_a.Length];

        if (Pairs is "neighbours")
        {
            _x = [.. Enumerable.Range(0, _values.Length - 1)];
            _y = [.. _x.Select(i => i + 1)];
        }
        else
        {
            var random = new Random(1);
            _x = [.. Enumerable.Range(0, _values.Length).Select(_ => random.Next(_values.Length))];
            _y = [.. Enumerable.Range(0, _values.Length).Select(_ => random.Next(_values.Length))];
        }
    }

    [Benchmark]
    public int StenoBytes()
    {
        int total = 0;

        for (int i = 0; i < _x.Length; i++)
            total += _steno[_x[i]].AsSpan().SequenceCompareTo(_steno[_y[i]]);

        return total;
    }

    [Benchmark]
    public int StenoDecodedBoth()
    {
        int total = 0;

        for (int i = 0; i < _x.Length; i++)
        {
            int a = Steno.GetChars(_steno[_x[i]], _a);
            int b = Steno.GetChars(_steno[_y[i]], _b);
            total += StenoEncoding.CompareCodePoints(_a.AsSpan(0, a), _b.AsSpan(0, b));
        }

        return total;
    }

    [Benchmark]
    public int StringsCompareCodePoints()
    {
        int total = 0;

        for (int i = 0; i < _x.Length; i++)
            total += StenoEncoding.CompareCodePoints(_values[_x[i]], _values[_y[i]]);

        return total;
    }

    [Benchmark]
    public int StringsCompareOrdinal()
    {
        int total = 0;

        for (int i = 0; i < _x.Length; i++)
            total += string.CompareOrdinal(_values[_x[i]], _values[_y[i]]);

        return total;
    }

    [Benchmark]
    public int Utf8Bytes()
    {
        int total = 0;

        for (int i = 0; i < _x.Length; i++)
            total += _utf8[_x[i]].AsSpan().SequenceCompareTo(_utf8[_y[i]]);

        return total;
    }
}
