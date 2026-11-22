using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;

namespace Singulink.Steno;

/// <content>
/// Contains the checks and conversions that work on the bytes directly: ASCII text, and conversion between Steno and UTF-8.
/// </content>
public sealed partial class StenoEncoding
{
    // The slices that a conversion between Steno and UTF-8 goes through UTF-16 in, on the stack.
    private const int Utf8Slice = 512;

    // The largest counts whose results fit in an int: Steno bytes to UTF-8 (4 per byte), and UTF-8 to Steno bytes (2 per byte).
    private const int MaxByteCountToUtf8 = int.MaxValue / 4;
    private const int MaxUtf8ByteCount = int.MaxValue / 2;

    // UTF-8 that throws rather than replaces: on invalid UTF-8 (DecoderFallbackException) and on a lone surrogate (EncoderFallbackException).
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Determines whether the specified Steno bytes encode a value that has only ASCII text in it: tab, line feed, carriage return and the printable
    /// ASCII characters U+0020..U+007E.
    /// </summary>
    /// <param name="bytes">The Steno bytes of a whole value.</param>
    /// <returns><see langword="true"/> if the value has only those characters (the empty value included); <see langword="false"/> otherwise, also for
    /// bytes that are not valid Steno data.</returns>
    /// <remarks>
    /// The answer comes from the bytes alone, without decoding. The bytes are not ASCII bytes: U+0020..U+007E are <c>07</c>..<c>65</c> (the code
    /// point minus 0x19), and tab, line feed and carriage return are <c>02</c>, <c>03</c> and <c>05</c>. A value that has only those characters is
    /// encoded in the initial layout throughout, so its bytes are exactly these values, and bytes that are all of these values are valid. When the
    /// result is <see langword="true"/>, the UTF-8 form has as many bytes as the Steno form.
    /// </remarks>
    public static bool IsAsciiText(ReadOnlySpan<byte> bytes)
    {
        ref byte start = ref MemoryMarshal.GetReference(bytes);
        int length = bytes.Length;
        int index = 0;

        if (Vector128.IsHardwareAccelerated && length >= Vector128<byte>.Count)
        {
            // Whole blocks, then the block that ends where the bytes end (which may overlap the one before).
            for (; index <= length - Vector128<byte>.Count; index += Vector128<byte>.Count)
            {
                if (!AreStenoAscii(Vector128.LoadUnsafe(ref start, (nuint)index)))
                    return false;
            }

            return index == length || AreStenoAscii(Vector128.LoadUnsafe(ref start, (nuint)(length - Vector128<byte>.Count)));
        }

        for (; index < length; index++)
        {
            if (!IsStenoAscii(Unsafe.Add(ref start, index)))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Gets the maximum number of UTF-8 bytes that a value of the specified number of Steno bytes can take.
    /// </summary>
    /// <param name="byteCount">The number of Steno bytes.</param>
    /// <returns><paramref name="byteCount"/> × 4.</returns>
    /// <remarks>
    /// A code point takes at least 1 Steno byte and at most 4 UTF-8 bytes. A supplementary code point of the main window takes 1 Steno byte, so the
    /// bound is reached up to the few bytes that the first code point of the window takes.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="byteCount"/> is negative, or the result would not fit in an <see cref="int"/>.
    /// </exception>
    public static int GetMaxUtf8ByteCount(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(byteCount, MaxByteCountToUtf8);

        return byteCount * 4;
    }

    /// <summary>
    /// Gets the number of UTF-8 bytes that the value in the specified Steno bytes takes.
    /// </summary>
    /// <param name="bytes">The Steno bytes of a whole value.</param>
    /// <returns>The number of UTF-8 bytes of the value.</returns>
    /// <exception cref="DecoderFallbackException">The bytes are not valid Steno data.</exception>
    /// <exception cref="EncoderFallbackException">The value has an unpaired surrogate (U+D800..U+DFFF), which has no UTF-8 form.</exception>
    public static int GetUtf8ByteCount(ReadOnlySpan<byte> bytes)
    {
        if (IsAsciiText(bytes))
            return bytes.Length;

        ToUtf8(bytes, [], countOnly: true, out int count);
        return count;
    }

    /// <summary>
    /// Converts the value in the specified Steno bytes to UTF-8.
    /// </summary>
    /// <param name="bytes">The Steno bytes of a whole value.</param>
    /// <param name="utf8">The destination for the UTF-8 bytes.</param>
    /// <returns>The number of bytes written to <paramref name="utf8"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="utf8"/> is too short for the UTF-8 bytes; the bytes written up to that point are
    /// undefined.</exception>
    /// <exception cref="DecoderFallbackException">The bytes are not valid Steno data.</exception>
    /// <exception cref="EncoderFallbackException">The value has an unpaired surrogate (U+D800..U+DFFF), which has no UTF-8 form.</exception>
    public static int GetUtf8Bytes(ReadOnlySpan<byte> bytes, Span<byte> utf8)
    {
        if (!TryGetUtf8Bytes(bytes, utf8, out int written))
            ThrowTooShort(nameof(utf8), "UTF-8 bytes");

        return written;
    }

    /// <summary>
    /// Converts the value in the specified Steno bytes to UTF-8, into a new array.
    /// </summary>
    /// <param name="bytes">The Steno bytes of a whole value.</param>
    /// <returns>The UTF-8 bytes.</returns>
    /// <exception cref="DecoderFallbackException">The bytes are not valid Steno data.</exception>
    /// <exception cref="EncoderFallbackException">The value has an unpaired surrogate (U+D800..U+DFFF), which has no UTF-8 form.</exception>
    public static byte[] GetUtf8Bytes(ReadOnlySpan<byte> bytes)
    {
        byte[] utf8 = new byte[GetUtf8ByteCount(bytes)];
        GetUtf8Bytes(bytes, utf8);
        return utf8;
    }

    /// <summary>
    /// Converts the value in the specified Steno bytes to UTF-8, if the destination is long enough.
    /// </summary>
    /// <param name="bytes">The Steno bytes of a whole value.</param>
    /// <param name="utf8">The destination for the UTF-8 bytes.</param>
    /// <param name="bytesWritten">The number of bytes written to <paramref name="utf8"/>, or 0 if it is too short.</param>
    /// <returns><see langword="true"/> if the UTF-8 bytes fit in <paramref name="utf8"/>; otherwise <see langword="false"/>, and the bytes written to
    /// it are undefined. <see cref="GetMaxUtf8ByteCount(int)"/> gives a length that is always enough.</returns>
    /// <exception cref="DecoderFallbackException">The bytes are not valid Steno data.</exception>
    /// <exception cref="EncoderFallbackException">The value has an unpaired surrogate (U+D800..U+DFFF), which has no UTF-8 form.</exception>
    public static bool TryGetUtf8Bytes(ReadOnlySpan<byte> bytes, Span<byte> utf8, out int bytesWritten)
    {
        if (IsAsciiText(bytes))
        {
            if (utf8.Length < bytes.Length)
            {
                bytesWritten = 0;
                return false;
            }

            StenoAsciiToUtf8(bytes, utf8);
            bytesWritten = bytes.Length;
            return true;
        }

        if (ToUtf8(bytes, utf8, countOnly: false, out bytesWritten))
            return true;

        bytesWritten = 0;
        return false;
    }

    /// <summary>
    /// Gets the maximum number of Steno bytes that a value of the specified number of UTF-8 bytes can take.
    /// </summary>
    /// <param name="utf8ByteCount">The number of UTF-8 bytes.</param>
    /// <returns><paramref name="utf8ByteCount"/> × 2.</returns>
    /// <remarks>
    /// An ASCII character (1 UTF-8 byte) takes at most 2 Steno bytes, a character of 2 UTF-8 bytes at most 3, one of 3 UTF-8 bytes at most 3, and
    /// one of 4 UTF-8 bytes at most 4. So no character takes more than 2 Steno bytes per UTF-8 byte; the ASCII control characters other than tab,
    /// line feed and carriage return take exactly that.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="utf8ByteCount"/> is negative, or the result would not fit in an
    /// <see cref="int"/>.</exception>
    public static int GetMaxByteCountFromUtf8(int utf8ByteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(utf8ByteCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(utf8ByteCount, MaxUtf8ByteCount);

        return utf8ByteCount * 2;
    }

    /// <summary>
    /// Gets the number of Steno bytes that the value in the specified UTF-8 bytes takes.
    /// </summary>
    /// <param name="utf8">The UTF-8 bytes of a whole value.</param>
    /// <returns>The number of Steno bytes of the value.</returns>
    /// <exception cref="DecoderFallbackException"><paramref name="utf8"/> is not valid UTF-8.</exception>
    public static int GetByteCountFromUtf8(ReadOnlySpan<byte> utf8)
    {
        if (IsUtf8Ascii(utf8))
            return utf8.Length;

        FromUtf8(utf8, [], countOnly: true, out int count);
        return count;
    }

    /// <summary>
    /// Converts the value in the specified UTF-8 bytes to Steno.
    /// </summary>
    /// <param name="utf8">The UTF-8 bytes of a whole value.</param>
    /// <param name="bytes">The destination for the Steno bytes.</param>
    /// <returns>The number of bytes written to <paramref name="bytes"/>.</returns>
    /// <remarks>
    /// A UTF-8 byte order mark at the start is the character U+FEFF of the value, and is encoded as such.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> is too short for the Steno bytes; the bytes written up to that point are
    /// undefined.</exception>
    /// <exception cref="DecoderFallbackException"><paramref name="utf8"/> is not valid UTF-8.</exception>
    public static int GetBytesFromUtf8(ReadOnlySpan<byte> utf8, Span<byte> bytes)
    {
        if (!TryGetBytesFromUtf8(utf8, bytes, out int written))
            ThrowTooShort(nameof(bytes), "encoded bytes");

        return written;
    }

    /// <summary>
    /// Converts the value in the specified UTF-8 bytes to Steno, into a new array.
    /// </summary>
    /// <param name="utf8">The UTF-8 bytes of a whole value.</param>
    /// <returns>The Steno bytes.</returns>
    /// <remarks>
    /// A UTF-8 byte order mark at the start is the character U+FEFF of the value, and is encoded as such.
    /// </remarks>
    /// <exception cref="DecoderFallbackException"><paramref name="utf8"/> is not valid UTF-8.</exception>
    public static byte[] GetBytesFromUtf8(ReadOnlySpan<byte> utf8)
    {
        byte[] bytes = new byte[GetByteCountFromUtf8(utf8)];
        GetBytesFromUtf8(utf8, bytes);
        return bytes;
    }

    /// <summary>
    /// Converts the value in the specified UTF-8 bytes to Steno, if the destination is long enough.
    /// </summary>
    /// <param name="utf8">The UTF-8 bytes of a whole value.</param>
    /// <param name="bytes">The destination for the Steno bytes.</param>
    /// <param name="bytesWritten">The number of bytes written to <paramref name="bytes"/>, or 0 if it is too short.</param>
    /// <returns><see langword="true"/> if the Steno bytes fit in <paramref name="bytes"/>; otherwise <see langword="false"/>, and the bytes written to
    /// it are undefined. <see cref="GetMaxByteCountFromUtf8(int)"/> gives a length that is always enough.</returns>
    /// <remarks>
    /// A UTF-8 byte order mark at the start is the character U+FEFF of the value, and is encoded as such.
    /// </remarks>
    /// <exception cref="DecoderFallbackException"><paramref name="utf8"/> is not valid UTF-8.</exception>
    public static bool TryGetBytesFromUtf8(ReadOnlySpan<byte> utf8, Span<byte> bytes, out int bytesWritten)
    {
        if (IsUtf8Ascii(utf8))
        {
            if (bytes.Length < utf8.Length)
            {
                bytesWritten = 0;
                return false;
            }

            Utf8AsciiToSteno(utf8, bytes);
            bytesWritten = utf8.Length;
            return true;
        }

        if (FromUtf8(utf8, bytes, countOnly: false, out bytesWritten))
            return true;

        bytesWritten = 0;
        return false;
    }

    /// <summary>
    /// Converts Steno bytes to UTF-8 through UTF-16 in slices on the stack, or counts the UTF-8 bytes. Returns <see langword="false"/> if the
    /// destination is too short.
    /// </summary>
    private static bool ToUtf8(ReadOnlySpan<byte> bytes, Span<byte> utf8, bool countOnly, out int written)
    {
        Span<char> chars = stackalloc char[Utf8Slice * 2];
        var state = default(StenoCodec.DecoderState);
        written = 0;
        int offset = 0;

        do
        {
            var slice = bytes[..Math.Min(bytes.Length, Utf8Slice)];
            bytes = bytes[slice.Length..];

            // A slice never ends between the two code units of a supplementary code point (the decoder writes both or neither), and a high surrogate
            // code point that ends a slice cannot be followed by a low one: so each slice is converted to UTF-8 on its own.
            var text = chars[..StenoCodec.DecodeChunk(ref state, slice, chars, flush: bytes.IsEmpty, offset)];
            offset += slice.Length;

            try
            {
                if (countOnly)
                {
                    written += StrictUtf8.GetByteCount(text);
                }
                else
                {
                    if (!StrictUtf8.TryGetBytes(text, utf8[written..], out int count))
                        return false;

                    written += count;
                }
            }
            catch (EncoderFallbackException ex)
            {
                int surrogate = ex.CharUnknown is '\0' ? ex.CharUnknownHigh : ex.CharUnknown;
                throw new EncoderFallbackException($"The value has an unpaired surrogate (U+{surrogate:X4}), which has no UTF-8 form.", ex);
            }
        }
        while (!bytes.IsEmpty);

        return true;
    }

    /// <summary>
    /// Converts UTF-8 to Steno bytes through UTF-16 in slices on the stack, or counts the Steno bytes. Returns <see langword="false"/> if the
    /// destination is too short.
    /// </summary>
    private static bool FromUtf8(ReadOnlySpan<byte> utf8, Span<byte> bytes, bool countOnly, out int written)
    {
        // A UTF-8 decoder gives at most one char per byte, plus one for a code point that it completes from the slice before; the encoder writes at
        // most 3 bytes per char, with one more char that it may hold from the slice before.
        Span<char> chars = stackalloc char[Utf8Slice + 1];
        Span<byte> scratch = stackalloc byte[(Utf8Slice + 2) * 3];
        var utf8Decoder = StrictUtf8.GetDecoder();
        var state = default(StenoCodec.EncoderState);
        written = 0;
        int offset = 0;

        do
        {
            var slice = utf8[..Math.Min(utf8.Length, Utf8Slice)];
            utf8 = utf8[slice.Length..];
            bool last = utf8.IsEmpty;
            int charCount;

            try
            {
                charCount = utf8Decoder.GetChars(slice, chars, flush: last);
            }
            catch (DecoderFallbackException ex)
            {
                ThrowInvalidUtf8(ex, offset);
                throw;
            }

            offset += slice.Length;
            var text = chars[..charCount];

            if (countOnly)
            {
                written += StenoCodec.EncodeChunk(ref state, text, scratch, last);
                continue;
            }

            // Straight into the destination if the worst case fits, otherwise through the scratch buffer.
            var room = bytes[written..];

            if (room.Length >= (charCount + 1) * 3)
            {
                written += StenoCodec.EncodeChunk(ref state, text, room, last);
            }
            else
            {
                int count = StenoCodec.EncodeChunk(ref state, text, scratch, last);

                if (count > room.Length)
                    return false;

                scratch[..count].CopyTo(room);
                written += count;
            }
        }
        while (!utf8.IsEmpty);

        return true;
    }

    [DoesNotReturn]
    private static void ThrowInvalidUtf8(DecoderFallbackException ex, int offset)
    {
        byte[] unknown = ex.BytesUnknown ?? [];
        int index = ex.Index + offset;

        string hex = System.Convert.ToHexString(unknown);
        throw new DecoderFallbackException($"The value is not valid UTF-8: the bytes {hex} at byte {index} are not the UTF-8 form of a code point.", unknown, index);
    }

    [DoesNotReturn]
    private static void ThrowTooShort(string paramName, string what) => throw new ArgumentException($"The value is too short for the {what}.", paramName);

    // The byte values of ASCII text, tab, line feed and carriage return in Steno (02, 03, 05 and 07..65) and in UTF-8 (09, 0A, 0D, 20..7E).

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsStenoAscii(uint b) => b - 0x07 < 0x5F || b - 0x02 < 2 || b == 0x05;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsUtf8Ascii(uint b) => b - 0x20 < 0x5F || b - 0x09 < 2 || b == 0x0D;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AreStenoAscii(Vector128<byte> block)
    {
        var ok = Vector128.LessThan(block - Vector128.Create((byte)0x07), Vector128.Create((byte)0x5F)) |
            Vector128.LessThan(block - Vector128.Create((byte)0x02), Vector128.Create((byte)2)) |
            Vector128.Equals(block, Vector128.Create((byte)0x05));

        return ok == Vector128<byte>.AllBitsSet;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AreUtf8Ascii(Vector128<byte> block)
    {
        var ok = Vector128.LessThan(block - Vector128.Create((byte)0x20), Vector128.Create((byte)0x5F)) |
            Vector128.LessThan(block - Vector128.Create((byte)0x09), Vector128.Create((byte)2)) |
            Vector128.Equals(block, Vector128.Create((byte)0x0D));

        return ok == Vector128<byte>.AllBitsSet;
    }

    private static bool IsUtf8Ascii(ReadOnlySpan<byte> utf8)
    {
        ref byte start = ref MemoryMarshal.GetReference(utf8);
        int length = utf8.Length;
        int index = 0;

        if (Vector128.IsHardwareAccelerated && length >= Vector128<byte>.Count)
        {
            for (; index <= length - Vector128<byte>.Count; index += Vector128<byte>.Count)
            {
                if (!AreUtf8Ascii(Vector128.LoadUnsafe(ref start, (nuint)index)))
                    return false;
            }

            return index == length || AreUtf8Ascii(Vector128.LoadUnsafe(ref start, (nuint)(length - Vector128<byte>.Count)));
        }

        for (; index < length; index++)
        {
            if (!IsUtf8Ascii(Unsafe.Add(ref start, index)))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Converts bytes that <see cref="IsAsciiText"/> accepts to UTF-8: <c>b + 0x19</c>, and <c>02</c>, <c>03</c>, <c>05</c> to tab, line feed and
    /// carriage return. The destination must be at least as long.
    /// </summary>
    private static void StenoAsciiToUtf8(ReadOnlySpan<byte> bytes, Span<byte> utf8)
    {
        ref byte source = ref MemoryMarshal.GetReference(bytes);
        ref byte target = ref MemoryMarshal.GetReference(utf8);
        int length = bytes.Length;
        int index = 0;

        if (Vector128.IsHardwareAccelerated && length >= Vector128<byte>.Count)
        {
            // The last block ends where the bytes end and may overlap the one before. It is read first, so that the result is right even if the
            // destination is the source.
            int last = length - Vector128<byte>.Count;
            var tail = StenoAsciiBlockToUtf8(Vector128.LoadUnsafe(ref source, (nuint)last));

            for (; index <= last; index += Vector128<byte>.Count)
                StenoAsciiBlockToUtf8(Vector128.LoadUnsafe(ref source, (nuint)index)).StoreUnsafe(ref target, (nuint)index);

            tail.StoreUnsafe(ref target, (nuint)last);
            return;
        }

        for (; index < length; index++)
        {
            uint b = Unsafe.Add(ref source, index);
            Unsafe.Add(ref target, index) = (byte)(b + (b >= 0x07 ? 0x19u : b == 0x05 ? 0x08u : 0x07u));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> StenoAsciiBlockToUtf8(Vector128<byte> block)
    {
        var deltas = Vector128.ConditionalSelect(
            Vector128.LessThan(block, Vector128.Create((byte)0x07)),
            Vector128.ConditionalSelect(Vector128.Equals(block, Vector128.Create((byte)0x05)), Vector128.Create((byte)0x08), Vector128.Create((byte)0x07)),
            Vector128.Create((byte)0x19));

        return block + deltas;
    }

    /// <summary>
    /// Converts UTF-8 bytes that are all ASCII text, tab, line feed or carriage return to Steno: the inverse of <see cref="StenoAsciiToUtf8"/>.
    /// </summary>
    private static void Utf8AsciiToSteno(ReadOnlySpan<byte> utf8, Span<byte> bytes)
    {
        ref byte source = ref MemoryMarshal.GetReference(utf8);
        ref byte target = ref MemoryMarshal.GetReference(bytes);
        int length = utf8.Length;
        int index = 0;

        if (Vector128.IsHardwareAccelerated && length >= Vector128<byte>.Count)
        {
            int last = length - Vector128<byte>.Count;
            var tail = Utf8AsciiBlockToSteno(Vector128.LoadUnsafe(ref source, (nuint)last));

            for (; index <= last; index += Vector128<byte>.Count)
                Utf8AsciiBlockToSteno(Vector128.LoadUnsafe(ref source, (nuint)index)).StoreUnsafe(ref target, (nuint)index);

            tail.StoreUnsafe(ref target, (nuint)last);
            return;
        }

        for (; index < length; index++)
        {
            uint c = Unsafe.Add(ref source, index);
            Unsafe.Add(ref target, index) = (byte)(c - (c >= 0x20 ? 0x19u : c == 0x0D ? 0x08u : 0x07u));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Utf8AsciiBlockToSteno(Vector128<byte> block)
    {
        var deltas = Vector128.ConditionalSelect(
            Vector128.LessThan(block, Vector128.Create((byte)0x20)),
            Vector128.ConditionalSelect(Vector128.Equals(block, Vector128.Create((byte)0x0D)), Vector128.Create((byte)0x08), Vector128.Create((byte)0x07)),
            Vector128.Create((byte)0x19));

        return block - deltas;
    }
}
