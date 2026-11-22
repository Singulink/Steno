using System.IO.Compression;
using System.Text;
using BenchmarkDotNet.Attributes;
using ZstdSharp;

namespace Singulink.Steno.Benchmarks.Speed;

/// <summary>
/// Encode and decode of the 16-char values of the 14 shared texts: one operation is every value of the text once, in a plain loop. The same codecs
/// as <see cref="DocumentBenchmarks"/>; ICU's SCSU and BOCU-1 pay a native call per value, and each compressor compresses each value by itself.
/// </summary>
[BenchmarkCategory(Categories.Values)]
public class ValueBenchmarks
{
    private static readonly StenoEncoding Steno = StenoEncoding.Instance;

    private readonly UTF8Encoding _utf8 = new(false);

    private string[] _values = null!;
    private byte[] _bytes = null!;
    private byte[] _scratch = null!;
    private char[] _chars = null!;
    private byte[][] _steno = null!;
    private byte[][] _utf8Bytes = null!;
    private byte[][] _scsuBytes = null!;
    private byte[][] _bocuBytes = null!;
    private byte[][] _zstdBytes = null!;
    private byte[][] _brotliBytes = null!;
    private IcuConverter _scsu = null!;
    private IcuConverter _bocu = null!;
    private Compressor _zstd = null!;
    private Decompressor _unzstd = null!;

    /// <summary>
    /// Gets or sets the name of the text (a file of Benchmarks/Workloads).
    /// </summary>
    [ParamsSource(nameof(Texts))]
    public string Text { get; set; } = null!;

    public static IEnumerable<string> Texts => Workloads.Selected(Workloads.Texts.Select(t => t.File));

    [GlobalSetup]
    public void Setup()
    {
        _values = Workloads.Values(Text);
        int longest = _values.Max(v => v.Length);
        _bytes = new byte[(longest * 4) + 1024];
        _scratch = new byte[(longest * 4) + 1024];
        _chars = new char[longest + 16];
        _scsu = new IcuConverter("SCSU");
        _bocu = new IcuConverter("BOCU-1");
        _zstd = new Compressor(1);
        _unzstd = new Decompressor();

        _steno = [.. _values.Select(v => Steno.GetBytes(v))];
        _utf8Bytes = [.. _values.Select(v => _utf8.GetBytes(v))];
        _scsuBytes = [.. _values.Select(v => _scsu.Encode(v))];
        _bocuBytes = [.. _values.Select(v => _bocu.Encode(v))];
        _zstdBytes = [.. _utf8Bytes.Select(b => _bytes.AsSpan(0, _zstd.Wrap(b, _bytes)).ToArray())];
        _brotliBytes = [.. _utf8Bytes.Select(b => BrotliEncoder.TryCompress(b, _bytes, out int n, 11, 22) ? _bytes.AsSpan(0, n).ToArray() : [])];
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _scsu.Dispose();
        _bocu.Dispose();
        _zstd.Dispose();
        _unzstd.Dispose();
    }

    [Benchmark, BenchmarkCategory(Categories.StenoUtf8)]
    public int StenoEncode()
    {
        int total = 0;

        foreach (string value in _values)
            total += Steno.GetBytes(value, _bytes);

        return total;
    }

    [Benchmark, BenchmarkCategory(Categories.StenoUtf8)]
    public int StenoDecode()
    {
        int total = 0;

        foreach (byte[] bytes in _steno)
            total += Steno.GetChars(bytes, _chars);

        return total;
    }

    [Benchmark, BenchmarkCategory(Categories.StenoUtf8)]
    public int Utf8Encode()
    {
        int total = 0;

        foreach (string value in _values)
            total += _utf8.GetBytes(value, _bytes);

        return total;
    }

    [Benchmark, BenchmarkCategory(Categories.StenoUtf8)]
    public int Utf8Decode()
    {
        int total = 0;

        foreach (byte[] bytes in _utf8Bytes)
            total += _utf8.GetChars(bytes, _chars);

        return total;
    }

    [Benchmark, BenchmarkCategory(Categories.Icu)]
    public int ScsuEncode()
    {
        int total = 0;

        foreach (string value in _values)
            total += _scsu.Encode(value, _bytes);

        return total;
    }

    [Benchmark, BenchmarkCategory(Categories.Icu)]
    public int ScsuDecode()
    {
        int total = 0;

        foreach (byte[] bytes in _scsuBytes)
            total += _scsu.Decode(bytes, _chars);

        return total;
    }

    [Benchmark, BenchmarkCategory(Categories.Icu)]
    public int Bocu1Encode()
    {
        int total = 0;

        foreach (string value in _values)
            total += _bocu.Encode(value, _bytes);

        return total;
    }

    [Benchmark, BenchmarkCategory(Categories.Icu)]
    public int Bocu1Decode()
    {
        int total = 0;

        foreach (byte[] bytes in _bocuBytes)
            total += _bocu.Decode(bytes, _chars);

        return total;
    }

    [Benchmark, BenchmarkCategory(Categories.Compressors)]
    public int Zstd1Encode()
    {
        int total = 0;

        foreach (string value in _values)
        {
            int length = _utf8.GetBytes(value, _scratch);
            total += _zstd.Wrap(_scratch.AsSpan(0, length), _bytes);
        }

        return total;
    }

    [Benchmark, BenchmarkCategory(Categories.Compressors)]
    public int Zstd1Decode()
    {
        int total = 0;

        foreach (byte[] bytes in _zstdBytes)
        {
            int length = _unzstd.Unwrap(bytes, _scratch);
            total += _utf8.GetChars(_scratch.AsSpan(0, length), _chars);
        }

        return total;
    }

    [Benchmark, BenchmarkCategory(Categories.Compressors)]
    public int Brotli11Encode()
    {
        int total = 0;

        foreach (string value in _values)
        {
            int length = _utf8.GetBytes(value, _scratch);
            BrotliEncoder.TryCompress(_scratch.AsSpan(0, length), _bytes, out int written, 11, 22);
            total += written;
        }

        return total;
    }

    [Benchmark, BenchmarkCategory(Categories.Compressors)]
    public int Brotli11Decode()
    {
        int total = 0;

        foreach (byte[] bytes in _brotliBytes)
        {
            BrotliDecoder.TryDecompress(bytes, _scratch, out int length);
            total += _utf8.GetChars(_scratch.AsSpan(0, length), _chars);
        }

        return total;
    }
}
