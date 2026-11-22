using System.IO.Compression;
using System.Text;
using K4os.Compression.LZ4;
using ZstdSharp;

namespace Singulink.Steno.Benchmarks;

/// <summary>
/// The codecs that Steno is compared with: the Unicode encodings, and UTF-8 followed by a general compressor applied to each value on its own.
/// </summary>
internal static class Codecs
{
    public static readonly Codec Utf8 = new EncodingCodec("UTF-8", new UTF8Encoding(false, true));
    public static readonly Codec Utf16 = new EncodingCodec("UTF-16", new UnicodeEncoding(false, false, true));
    public static readonly Codec Steno = new EncodingCodec("Steno", StenoEncoding.Instance);

    public static Codec? Scsu { get; } = IcuConverter.IsAvailable ? new IcuCodec("SCSU", "SCSU") : null;

    public static Codec? Bocu1 { get; } = IcuConverter.IsAvailable ? new IcuCodec("BOCU-1", "BOCU-1") : null;

    /// <summary>
    /// Gets the compressors on UTF-8, at a fast and at a strong setting each.
    /// </summary>
    public static IReadOnlyList<Codec> Compressors { get; } = [
        new CompressedCodec("Deflate fast", new DeflateCompressor(CompressionLevel.Fastest)),
        new CompressedCodec("Deflate max", new DeflateCompressor(CompressionLevel.SmallestSize)),
        new CompressedCodec("Brotli 1", new BrotliCompressor(1)),
        new CompressedCodec("Brotli 11", new BrotliCompressor(11)),
        new CompressedCodec("LZ4 fast", new Lz4Compressor(LZ4Level.L00_FAST)),
        new CompressedCodec("LZ4 max", new Lz4Compressor(LZ4Level.L12_MAX)),
        new CompressedCodec("Zstd 1", new ZstdCompressor(1)),
        new CompressedCodec("Zstd 19", new ZstdCompressor(19)),
    ];

    /// <summary>
    /// Gets the Unicode encodings (those that ICU does not give are left out).
    /// </summary>
    public static IReadOnlyList<Codec> Encodings { get; } = [Utf8, Utf16, .. Scsu is null ? [] : (Codec[])[Scsu, Bocu1!], Steno];

    /// <summary>
    /// Gets every codec, Steno last.
    /// </summary>
    public static IReadOnlyList<Codec> All { get; } = [Utf8, Utf16, .. Scsu is null ? [] : (Codec[])[Scsu, Bocu1!], .. Compressors, Steno];

    /// <summary>
    /// Gets the fast and the strong compressor setting that the speed measurements use.
    /// </summary>
    public static Codec FastCompressor => Compressors.First(c => c.Name is "Zstd 1");

    public static Codec StrongCompressor => Compressors.First(c => c.Name is "Brotli 11");

    public static Codec ByName(string name) => All.First(c => c.Name == name);
}

/// <summary>
/// A codec that turns text into bytes and back. An instance is not thread safe (some keep native state or scratch buffers): use <see cref="Clone"/>
/// for each thread.
/// </summary>
internal abstract class Codec
{
    public abstract string Name { get; }

    /// <summary>
    /// Gets a value indicating whether this is a general compressor on UTF-8 rather than an encoding of Unicode text.
    /// </summary>
    public virtual bool IsCompressor => false;

    public abstract int GetMaxByteCount(int charCount);

    public abstract int Encode(ReadOnlySpan<char> text, Span<byte> bytes);

    /// <summary>
    /// Decodes into <paramref name="chars"/>, which must be long enough for the text (the length of the original text is enough for every codec).
    /// </summary>
    public abstract int Decode(ReadOnlySpan<byte> bytes, Span<char> chars);

    public abstract Codec Clone();

    public byte[] Encode(string text)
    {
        byte[] buffer = new byte[GetMaxByteCount(text.Length)];
        return buffer.AsSpan(0, Encode(text, buffer)).ToArray();
    }

    public string Decode(byte[] bytes, int charCount)
    {
        char[] buffer = new char[charCount + 16];
        return new string(buffer, 0, Decode(bytes, buffer));
    }

    public override string ToString() => Name;
}

internal sealed class EncodingCodec(string name, Encoding encoding) : Codec
{
    public override string Name => name;

    public override int GetMaxByteCount(int charCount) => encoding.GetMaxByteCount(charCount);

    public override int Encode(ReadOnlySpan<char> text, Span<byte> bytes) => encoding.GetBytes(text, bytes);

    public override int Decode(ReadOnlySpan<byte> bytes, Span<char> chars) => encoding.GetChars(bytes, chars);

    public override Codec Clone() => this;
}

/// <summary>
/// An encoding of ICU4C, through the icu.dll of Windows. Each call is a native call.
/// </summary>
internal sealed class IcuCodec(string name, string icuName) : Codec
{
    private readonly IcuConverter _converter = new(icuName);

    public override string Name => name;

    public override int GetMaxByteCount(int charCount) => _converter.GetMaxByteCount(charCount);

    public override int Encode(ReadOnlySpan<char> text, Span<byte> bytes) => _converter.Encode(text, bytes);

    public override int Decode(ReadOnlySpan<byte> bytes, Span<char> chars) => _converter.Decode(bytes, chars);

    public override Codec Clone() => new IcuCodec(name, icuName);
}

/// <summary>
/// UTF-8 followed by a general compressor, on each value on its own. Decoding decompresses into a scratch buffer, then decodes the UTF-8.
/// </summary>
internal sealed class CompressedCodec(string name, ValueCompressor compressor) : Codec
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private byte[] _scratch = new byte[1024];

    public override string Name => name;

    public override bool IsCompressor => true;

    public override int GetMaxByteCount(int charCount) => compressor.GetMaxCompressedLength(charCount * 3);

    public override int Encode(ReadOnlySpan<char> text, Span<byte> bytes)
    {
        var scratch = Scratch(text.Length * 3);
        int length = Utf8.GetBytes(text, scratch);
        return compressor.Compress(scratch[..length], bytes);
    }

    public override int Decode(ReadOnlySpan<byte> bytes, Span<char> chars)
    {
        var scratch = Scratch(chars.Length * 3);
        int length = compressor.Decompress(bytes, scratch);
        return Utf8.GetChars(scratch[..length], chars);
    }

    public override Codec Clone() => new CompressedCodec(name, compressor.Clone());

    private Span<byte> Scratch(int length)
    {
        if (_scratch.Length < length)
            _scratch = new byte[Math.Max(length, _scratch.Length * 2)];

        return _scratch.AsSpan(0, length);
    }
}

internal abstract class ValueCompressor
{
    public abstract int GetMaxCompressedLength(int length);

    public abstract int Compress(ReadOnlySpan<byte> source, Span<byte> destination);

    public abstract int Decompress(ReadOnlySpan<byte> source, Span<byte> destination);

    public abstract ValueCompressor Clone();
}

/// <summary>
/// Raw Deflate (no zlib or gzip header) through System.IO.Compression. Each call makes a stream, as code that compresses values one by one does.
/// </summary>
internal sealed class DeflateCompressor(CompressionLevel level) : ValueCompressor
{
    private readonly MemoryStream _output = new();

    public override int GetMaxCompressedLength(int length) => length + (length / 8) + 64;

    public override int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        _output.SetLength(0);

        using (var deflate = new DeflateStream(_output, level, leaveOpen: true))
            deflate.Write(source);

        _output.GetBuffer().AsSpan(0, (int)_output.Length).CopyTo(destination);
        return (int)_output.Length;
    }

    public override unsafe int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        fixed (byte* pointer = source)
        {
            using var input = new UnmanagedMemoryStream(pointer, source.Length);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            int total = 0;

            while (true)
            {
                int read = deflate.Read(destination[total..]);

                if (read is 0)
                    return total;

                total += read;
            }
        }
    }

    public override ValueCompressor Clone() => new DeflateCompressor(level);
}

internal sealed class BrotliCompressor(int quality) : ValueCompressor
{
    public override int GetMaxCompressedLength(int length) => BrotliEncoder.GetMaxCompressedLength(length);

    public override int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (!BrotliEncoder.TryCompress(source, destination, out int written, quality, 22))
            throw new InvalidOperationException("Brotli could not compress.");

        return written;
    }

    public override int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (!BrotliDecoder.TryDecompress(source, destination, out int written))
            throw new InvalidOperationException("Brotli could not decompress.");

        return written;
    }

    public override ValueCompressor Clone() => this;
}

/// <summary>
/// LZ4 blocks (K4os.Compression.LZ4), which do not record their decompressed length.
/// </summary>
internal sealed class Lz4Compressor(LZ4Level level) : ValueCompressor
{
    public override int GetMaxCompressedLength(int length) => LZ4Codec.MaximumOutputSize(length);

    public override int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int written = LZ4Codec.Encode(source, destination, level);
        return written >= 0 ? written : throw new InvalidOperationException("LZ4 could not compress.");
    }

    public override int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int written = LZ4Codec.Decode(source, destination);
        return written >= 0 ? written : throw new InvalidOperationException("LZ4 could not decompress.");
    }

    public override ValueCompressor Clone() => this;
}

/// <summary>
/// Zstandard frames (ZstdSharp.Port), with a compressor and a decompressor kept for each instance.
/// </summary>
internal sealed class ZstdCompressor(int level) : ValueCompressor
{
    private readonly ZstdSharp.Compressor _compressor = new(level);
    private readonly Decompressor _decompressor = new();

    public override int GetMaxCompressedLength(int length) => ZstdSharp.Compressor.GetCompressBound(length);

    public override int Compress(ReadOnlySpan<byte> source, Span<byte> destination) => _compressor.Wrap(source, destination);

    public override int Decompress(ReadOnlySpan<byte> source, Span<byte> destination) => _decompressor.Unwrap(source, destination);

    public override ValueCompressor Clone() => new ZstdCompressor(level);
}
