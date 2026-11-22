using System.Text;
using BenchmarkDotNet.Attributes;

namespace Singulink.Steno.Benchmarks.Speed;

/// <summary>
/// The paths for ASCII text: <see cref="StenoEncoding.IsAsciiText"/> and the conversions between Steno and UTF-8, against a copy of the bytes, .NET's
/// own ASCII check, and Steno to UTF-16. The text is the English document with everything that is not printable ASCII, tab or line feed taken out, as
/// one value or as its 16-char values (one operation is every value once).
/// </summary>
[BenchmarkCategory(Categories.Ascii)]
public class AsciiBenchmarks
{
    private static readonly StenoEncoding Steno = StenoEncoding.Instance;

    private byte[][] _steno = null!;
    private byte[][] _utf8 = null!;
    private byte[] _buffer = null!;
    private char[] _chars = null!;

    /// <summary>
    /// Gets or sets the kind of value: "document" (the whole text) or "16-char strings".
    /// </summary>
    [Params("document", "16-char strings")]
    public string Kind { get; set; } = null!;

    [GlobalSetup]
    public void Setup()
    {
        string text = new([.. Workloads.Document("english").Where(c => c is '\n' or '\t' or (>= ' ' and <= '~'))]);
        string[] values = Kind is "document" ? [text] : [.. text.Split('\n').SelectMany(l => Corpus.Chunk(l, 16))];

        _steno = [.. values.Select(v => Steno.GetBytes(v))];
        _utf8 = [.. values.Select(Encoding.UTF8.GetBytes)];
        _buffer = new byte[values.Max(v => v.Length) + 16];
        _chars = new char[_buffer.Length];

        if (_steno.Any(b => !StenoEncoding.IsAsciiText(b)))
            throw new InvalidOperationException("The text is not ASCII text to Steno.");
    }

    /// <summary>
    /// The baseline: a copy of the Steno bytes into a buffer, what a conversion of ASCII text cannot beat.
    /// </summary>
    [Benchmark]
    public int Copy()
    {
        int total = 0;

        foreach (byte[] bytes in _steno)
        {
            bytes.CopyTo(_buffer, 0);
            total += bytes.Length;
        }

        return total;
    }

    /// <summary>
    /// .NET's own check of the UTF-8 bytes (only whether every byte is below 0x80: a simpler test than the one of IsAsciiText).
    /// </summary>
    [Benchmark]
    public int AsciiIsValid()
    {
        int total = 0;

        foreach (byte[] bytes in _utf8)
        {
            if (Ascii.IsValid(bytes))
                total++;
        }

        return total;
    }

    [Benchmark]
    public int IsAsciiText()
    {
        int total = 0;

        foreach (byte[] bytes in _steno)
        {
            if (StenoEncoding.IsAsciiText(bytes))
                total++;
        }

        return total;
    }

    [Benchmark]
    public int StenoToUtf8()
    {
        int total = 0;

        foreach (byte[] bytes in _steno)
            total += StenoEncoding.GetUtf8Bytes(bytes, _buffer);

        return total;
    }

    [Benchmark]
    public int Utf8ToSteno()
    {
        int total = 0;

        foreach (byte[] bytes in _utf8)
            total += StenoEncoding.GetBytesFromUtf8(bytes, _buffer);

        return total;
    }

    [Benchmark]
    public int StenoToUtf16()
    {
        int total = 0;

        foreach (byte[] bytes in _steno)
            total += Steno.GetChars(bytes, _chars);

        return total;
    }
}
