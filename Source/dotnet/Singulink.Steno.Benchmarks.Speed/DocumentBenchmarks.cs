using System.IO.Compression;
using System.Text;
using BenchmarkDotNet.Attributes;
using ZstdSharp;

namespace Singulink.Steno.Benchmarks.Speed;

/// <summary>
/// Encode and decode of a whole document (one value), for the 14 shared texts and the four mixed-content documents: Steno, UTF-8, ICU's SCSU and
/// BOCU-1 (a native call per value), and UTF-8 with Zstd level 1 and Brotli quality 11 (compressed as one value).
/// </summary>
/// <remarks>
/// Each method calls the API under test directly, into buffers allocated in the setup. The method names are what the import step of the report
/// tool reads: codec and direction.
/// </remarks>
[BenchmarkCategory(Categories.Documents)]
public class DocumentBenchmarks
{
    private static readonly StenoEncoding Steno = StenoEncoding.Instance;

    private readonly UTF8Encoding _utf8 = new(false);

    private string _text = null!;
    private byte[] _bytes = null!;
    private byte[] _scratch = null!;
    private char[] _chars = null!;
    private byte[] _steno = null!;
    private byte[] _utf8Bytes = null!;
    private byte[] _scsuBytes = null!;
    private byte[] _bocuBytes = null!;
    private byte[] _zstdBytes = null!;
    private byte[] _brotliBytes = null!;
    private IcuConverter _scsu = null!;
    private IcuConverter _bocu = null!;
    private Compressor _zstd = null!;
    private Decompressor _unzstd = null!;

    /// <summary>
    /// Gets or sets the name of the text (a file of Benchmarks/Workloads, or a mixed-content file of the corpus).
    /// </summary>
    [ParamsSource(nameof(Texts))]
    public string Text { get; set; } = null!;

    public static IEnumerable<string> Texts => Workloads.Selected([.. Workloads.Texts.Select(t => t.File), .. Workloads.MixedDocuments]);

    [GlobalSetup]
    public void Setup()
    {
        _text = Workloads.Document(Text);
        _bytes = new byte[(_text.Length * 4) + 1024];
        _scratch = new byte[(_text.Length * 4) + 1024];
        _chars = new char[_text.Length + 16];
        _scsu = new IcuConverter("SCSU");
        _bocu = new IcuConverter("BOCU-1");
        _zstd = new Compressor(1);
        _unzstd = new Decompressor();

        _steno = Steno.GetBytes(_text);
        _utf8Bytes = _utf8.GetBytes(_text);
        _scsuBytes = _scsu.Encode(_text);
        _bocuBytes = _bocu.Encode(_text);
        _zstdBytes = _bytes.AsSpan(0, _zstd.Wrap(_utf8Bytes, _bytes)).ToArray();

        if (!BrotliEncoder.TryCompress(_utf8Bytes, _bytes, out int brotli, 11, 22))
            throw new InvalidOperationException("Brotli could not compress.");

        _brotliBytes = _bytes.AsSpan(0, brotli).ToArray();
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
    public int StenoEncode() => Steno.GetBytes(_text, _bytes);

    [Benchmark, BenchmarkCategory(Categories.StenoUtf8)]
    public int StenoDecode() => Steno.GetChars(_steno, _chars);

    [Benchmark, BenchmarkCategory(Categories.StenoUtf8)]
    public int Utf8Encode() => _utf8.GetBytes(_text, _bytes);

    [Benchmark, BenchmarkCategory(Categories.StenoUtf8)]
    public int Utf8Decode() => _utf8.GetChars(_utf8Bytes, _chars);

    [Benchmark, BenchmarkCategory(Categories.Icu)]
    public int ScsuEncode() => _scsu.Encode(_text, _bytes);

    [Benchmark, BenchmarkCategory(Categories.Icu)]
    public int ScsuDecode() => _scsu.Decode(_scsuBytes, _chars);

    [Benchmark, BenchmarkCategory(Categories.Icu)]
    public int Bocu1Encode() => _bocu.Encode(_text, _bytes);

    [Benchmark, BenchmarkCategory(Categories.Icu)]
    public int Bocu1Decode() => _bocu.Decode(_bocuBytes, _chars);

    [Benchmark, BenchmarkCategory(Categories.Compressors)]
    public int Zstd1Encode()
    {
        int length = _utf8.GetBytes(_text, _scratch);
        return _zstd.Wrap(_scratch.AsSpan(0, length), _bytes);
    }

    [Benchmark, BenchmarkCategory(Categories.Compressors)]
    public int Zstd1Decode()
    {
        int length = _unzstd.Unwrap(_zstdBytes, _scratch);
        return _utf8.GetChars(_scratch.AsSpan(0, length), _chars);
    }

    [Benchmark, BenchmarkCategory(Categories.Compressors)]
    public int Brotli11Encode()
    {
        int length = _utf8.GetBytes(_text, _scratch);
        BrotliEncoder.TryCompress(_scratch.AsSpan(0, length), _bytes, out int written, 11, 22);
        return written;
    }

    [Benchmark, BenchmarkCategory(Categories.Compressors)]
    public int Brotli11Decode()
    {
        BrotliDecoder.TryDecompress(_brotliBytes, _scratch, out int length);
        return _utf8.GetChars(_scratch.AsSpan(0, length), _chars);
    }
}
