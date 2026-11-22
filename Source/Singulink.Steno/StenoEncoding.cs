using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Singulink.Steno;

/// <summary>
/// Represents the Steno Encoding, version 1, in the Steno reference implementation: a compact encoding of Unicode text whose bytes sort in the
/// code point order of the text.
/// </summary>
/// <remarks>
/// <para>
/// This class is the reference implementation's <see cref="Encoding"/> for the Steno Encoding (Steno for short), which is specified in <see
/// href="https://github.com/Singulink/Singulink.Steno/blob/main/FORMAT.md">Steno Encoding, version 1</see>. Steno bytes compared as unsigned
/// bytes (<see cref="MemoryExtensions.SequenceCompareTo{T}(ReadOnlySpan{T}, ReadOnlySpan{T})"/>) are in the order that <see
/// cref="CompareCodePoints(ReadOnlySpan{char}, ReadOnlySpan{char})"/> gives their text. The bytes of a prefix of a value are a prefix of the bytes of
/// the value. A value has exactly one encoding, so two values are equal exactly when their bytes are. The byte 00 never occurs, so it can end a value
/// inside a larger key.</para>
/// <para>
/// The bytes carry no header, version number or byte order mark, and <see cref="Encoding.GetPreamble"/> is empty: whoever stores them must record
/// that they are in the Steno Encoding, version 1, for example by <see cref="WebName"/>. <see cref="Encoding.CodePage"/> is 0, which <see
/// cref="Encoding.GetEncoding(int)"/> does not map back to this encoding.</para>
/// <para>
/// The encoding is stateful: the bytes of a code point depend on the code points before it in the same value. Each method of this class encodes or
/// decodes its input as one whole value. The bytes of two values one after the other are not the bytes of the two texts together; to keep several
/// values in one buffer, end each with a 00 byte or record its length. To encode or decode one value in pieces, use <see cref="GetEncoder"/> and <see
/// cref="GetDecoder"/>, whose remarks also say what this means for <see cref="StreamWriter"/>, <see cref="StreamReader"/> and <see
/// cref="BinaryReader"/>.</para>
/// <para>
/// Every sequence of UTF-16 code units can be encoded, unpaired surrogates included, so encoding never uses <see cref="Encoding.EncoderFallback"/>.
/// Bytes that are not valid Steno data throw a <see cref="DecoderFallbackException"/> whatever <see cref="Encoding.DecoderFallback"/> is, since the
/// state after invalid data is unknown and nothing can replace it. The message names the rule that the bytes break by its reason in the
/// specification (<c>zero-byte</c>, <c>unused-lead</c>, <c>truncated</c>, <c>past-range</c>, <c>surrogate-pair</c> or <c>invalid-trail</c>); it is
/// meant for diagnosis, and its wording is not part of the contract. The exception has the bytes of the code point read up to the defect as <see
/// cref="DecoderFallbackException.BytesUnknown"/>, and where the code point starts in the input as <see cref="DecoderFallbackException.Index"/>.</para>
/// <para>
/// Text is ordered by code point, which differs from ordinal order where a supplementary character meets U+E000..U+FFFF; see <see
/// cref="CompareCodePoints(ReadOnlySpan{char}, ReadOnlySpan{char})"/>.</para>
/// <para>
/// This class is thread safe. The encoder and decoder objects that it creates are not.</para>
/// </remarks>
public sealed partial class StenoEncoding : Encoding
{
    // The most chars or bytes that GetByteCount and GetCharCount convert in one piece on the stack. Longer input is counted in slices of this size.
    private const int CountSlice = 1024;

    // The largest char count whose worst-case byte count, (n + 1) * 3, fits in an int, and the largest byte count whose char count, n * 2, does.
    private const int MaxCharCount = (int.MaxValue / 3) - 1;
    private const int MaxByteCount = int.MaxValue / 2;

    // Code page 0 (there is no registered code page for Steno), with the fallbacks that tell the truth: decoding throws, and encoding never falls back.
    private StenoEncoding()
        : base(0, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
    {
    }

    /// <summary>
    /// Gets the shared instance of the reference implementation's encoding for the Steno Encoding.
    /// </summary>
    /// <remarks>
    /// The instance is read-only and thread safe.
    /// </remarks>
    public static StenoEncoding Instance { get; } = new StenoEncoding();

    /// <inheritdoc/>
    public override string EncodingName => "Steno";

    /// <inheritdoc/>
    public override string WebName => "x-steno";

    /// <inheritdoc/>
    /// <remarks>
    /// The same as <see cref="WebName"/>.
    /// </remarks>
    public override string BodyName => WebName;

    /// <inheritdoc/>
    /// <remarks>
    /// The same as <see cref="WebName"/>.
    /// </remarks>
    public override string HeaderName => WebName;

    /// <inheritdoc/>
    /// <remarks>
    /// Always 0: the Steno Encoding has no Windows code page.
    /// </remarks>
    public override int WindowsCodePage => 0;

    /// <inheritdoc/>
    /// <remarks>
    /// Always <see langword="false"/>.
    /// </remarks>
    public override bool IsBrowserDisplay => false;

    /// <inheritdoc/>
    /// <remarks>
    /// Always <see langword="false"/>.
    /// </remarks>
    public override bool IsBrowserSave => false;

    /// <inheritdoc/>
    /// <remarks>
    /// Always <see langword="false"/>.
    /// </remarks>
    public override bool IsMailNewsDisplay => false;

    /// <inheritdoc/>
    /// <remarks>
    /// Always <see langword="false"/>.
    /// </remarks>
    public override bool IsMailNewsSave => false;

    /// <inheritdoc/>
    public override bool IsSingleByte => false;

    /// <summary>
    /// Determines whether the specified object is a <see cref="StenoEncoding"/>.
    /// </summary>
    /// <param name="value">The object to compare with this one.</param>
    /// <returns><see langword="true"/> if <paramref name="value"/> is a <see cref="StenoEncoding"/> (the instance or a clone of it); otherwise <see
    /// langword="false"/>.</returns>
    /// <remarks>
    /// The fallbacks are not compared: every <see cref="StenoEncoding"/> ignores them.
    /// </remarks>
    public override bool Equals([NotNullWhen(true)] object? value) => value is StenoEncoding;

    /// <inheritdoc/>
    public override int GetHashCode() => WebName.GetHashCode(StringComparison.Ordinal);

    #region Encoding overrides

    /// <inheritdoc/>
    /// <remarks>
    /// Returns (<paramref name="charCount"/> + 1) × 3. A UTF-16 code unit takes at most 3 bytes (a surrogate pair takes at most 4), and the extra code
    /// unit allows for a high surrogate that an encoder holds from the call before.
    /// </remarks>
    public override int GetMaxByteCount(int charCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(charCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(charCount, MaxCharCount);

        return (charCount + 1) * 3;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Returns <paramref name="byteCount"/> × 2. A code point takes at least 1 byte and at most 2 UTF-16 code units, also when a decoder completes a
    /// code point that it holds from the call before (it takes at least one byte of the call).
    /// </remarks>
    public override int GetMaxCharCount(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(byteCount, MaxByteCount);

        return byteCount * 2;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// The encoder encodes one value given in pieces: over any split of the value, it writes the bytes that <see cref="GetBytes(ReadOnlySpan{char},
    /// Span{byte})"/> writes for the whole value. A high surrogate at the end of a piece is held until the next call, which may start with its low
    /// surrogate. <see cref="System.Text.Encoder.Reset"/> starts a new value.</para>
    /// <para>
    /// A call with <c>flush</c> set to <see langword="true"/> writes a held high surrogate as an unpaired surrogate, and does not end the value: the
    /// next call goes on with the same value. So <see cref="StreamWriter.Flush()"/> can be called at any point of a stream, except between the two
    /// halves of a surrogate pair: the high surrogate is then written as an unpaired surrogate, and a low surrogate after it (which together with it would
    /// not be a value) makes the call that gets it throw an <see cref="EncoderFallbackException"/>.</para>
    /// <para>
    /// A call that throws leaves the encoder as it was. <c>Convert</c> throws <see cref="ArgumentException"/> only when the output has no room for even
    /// one char; like UTF8Encoding's, it writes a held high surrogate on its own when that fits and the next char does not. The encoder is not thread
    /// safe.</para>
    /// </remarks>
    public override System.Text.Encoder GetEncoder() => new Encoder();

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// The decoder decodes one value given in pieces: over any split of the bytes, it writes what <see cref="GetChars(ReadOnlySpan{byte},
    /// Span{char})"/> writes for the whole value. The bytes of a code point that a piece ends in the middle of are held until the next call completes
    /// them. <see cref="System.Text.Decoder.Reset"/> starts a new value.</para>
    /// <para>
    /// A call with <c>flush</c> set to <see langword="true"/> says that the input ends there: it throws a <see cref="DecoderFallbackException"/> with
    /// the reason <c>truncated</c> if bytes of an unfinished code point are held. It does not end the value either, so a reader that flushes at the end
    /// of the data it has (as <see cref="StreamReader"/> does) and then gets more goes on with the same value.</para>
    /// <para>
    /// Create a <see cref="StreamReader"/> with <c>detectEncodingFromByteOrderMarks</c> set to <see langword="false"/>: a value can start with the
    /// bytes <c>EF BB BF</c> (U+09B3 U+09CF does), which the reader would take for a UTF-8 byte order mark. <see cref="BinaryReader.ReadString"/>
    /// decodes a string of up to 128 bytes as a whole value, but a longer one with its decoder in pieces, without resetting it between strings: so a
    /// string longer than 128 bytes is read wrongly unless it is the first such string that the reader reads (or the strings before it leave the
    /// decoder in its initial state). Store such strings as byte arrays with their length instead.</para>
    /// <para>
    /// A call that throws, for invalid data or an output that is too short, leaves the decoder as it was. The decoder is not thread safe.</para>
    /// </remarks>
    public override System.Text.Decoder GetDecoder() => new Decoder();

    /// <inheritdoc path="/*[not(self::exception[@cref='T:System.Text.EncoderFallbackException'])]"/>
    public override int GetByteCount(ReadOnlySpan<char> chars)
    {
        // The size is only known by encoding. Small input goes in one piece into a buffer on the stack, longer input in slices.

        if (chars.Length <= CountSlice)
        {
            Span<byte> buffer = stackalloc byte[CountSlice * 3];
            return StenoCodec.EncodeValue(chars, buffer);
        }

        return CountBytes(chars, default);
    }

    /// <inheritdoc path="/*[not(self::exception[@cref='T:System.Text.EncoderFallbackException'])]"/>
    public override int GetBytes(ReadOnlySpan<char> chars, Span<byte> bytes) => StenoCodec.EncodeValue(chars, bytes);

    /// <inheritdoc path="/*[not(self::exception[@cref='T:System.Text.EncoderFallbackException'])]"/>
    public override bool TryGetBytes(ReadOnlySpan<char> chars, Span<byte> bytes, out int bytesWritten)
    {
        // A destination for the worst case cannot be too small; for a smaller one the size is checked first.
        if (bytes.Length < (long)chars.Length * 3 && GetByteCount(chars) > bytes.Length)
        {
            bytesWritten = 0;
            return false;
        }

        bytesWritten = StenoCodec.EncodeValue(chars, bytes);
        return true;
    }

    /// <inheritdoc/>
    /// <exception cref="DecoderFallbackException">The bytes are not valid Steno data, whatever <see cref="Encoding.DecoderFallback"/> is.</exception>
    public override int GetCharCount(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length <= CountSlice)
        {
            Span<char> buffer = stackalloc char[CountSlice * 2];
            return StenoCodec.DecodeValue(bytes, buffer);
        }

        return CountChars(bytes, default);
    }

    /// <inheritdoc/>
    /// <exception cref="DecoderFallbackException">The bytes are not valid Steno data, whatever <see cref="Encoding.DecoderFallback"/> is.</exception>
    public override int GetChars(ReadOnlySpan<byte> bytes, Span<char> chars) => StenoCodec.DecodeValue(bytes, chars);

    /// <inheritdoc/>
    /// <remarks>
    /// As with UTF8Encoding, <see langword="false"/> only means that the destination is too short: bytes that are not valid Steno data throw.
    /// </remarks>
    /// <exception cref="DecoderFallbackException">The bytes are not valid Steno data, whatever <see cref="Encoding.DecoderFallback"/> is.</exception>
    public override bool TryGetChars(ReadOnlySpan<byte> bytes, Span<char> chars, out int charsWritten)
    {
        if (chars.Length < (long)bytes.Length * 2 && GetCharCount(bytes) > chars.Length)
        {
            charsWritten = 0;
            return false;
        }

        charsWritten = StenoCodec.DecodeValue(bytes, chars);
        return true;
    }

    /// <inheritdoc path="/*[not(self::exception[@cref='T:System.Text.EncoderFallbackException'])]"/>
    public override int GetByteCount(char[] chars)
    {
        ArgumentNullException.ThrowIfNull(chars);
        return GetByteCount(chars.AsSpan());
    }

    /// <inheritdoc path="/*[not(self::exception[@cref='T:System.Text.EncoderFallbackException'])]"/>
    public override int GetByteCount(char[] chars, int index, int count)
    {
        CheckRange(chars, index, count);
        return GetByteCount(chars.AsSpan(index, count));
    }

    /// <inheritdoc path="/*[not(self::exception[@cref='T:System.Text.EncoderFallbackException'])]"/>
    public override int GetByteCount(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return GetByteCount(s.AsSpan());
    }

    /// <inheritdoc path="/*[not(self::exception[@cref='T:System.Text.EncoderFallbackException'])]"/>
    public override unsafe int GetByteCount(char* chars, int count)
    {
        CheckPointer(chars, count, nameof(chars), nameof(count));
        return GetByteCount(new ReadOnlySpan<char>(chars, count));
    }

    /// <inheritdoc path="/*[not(self::exception[@cref='T:System.Text.EncoderFallbackException'])]"/>
    public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
    {
        CheckRange(chars, charIndex, charCount, nameof(chars), nameof(charIndex), nameof(charCount));
        CheckIndex(bytes, byteIndex, nameof(bytes), nameof(byteIndex));

        return GetBytes(chars.AsSpan(charIndex, charCount), bytes.AsSpan(byteIndex));
    }

    /// <inheritdoc path="/*[not(self::exception[@cref='T:System.Text.EncoderFallbackException'])]"/>
    public override int GetBytes(string s, int charIndex, int charCount, byte[] bytes, int byteIndex)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentOutOfRangeException.ThrowIfNegative(charIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(charCount);

        if (charCount > s.Length - charIndex)
            ThrowTooShortForRange(nameof(s));

        CheckIndex(bytes, byteIndex, nameof(bytes), nameof(byteIndex));

        return GetBytes(s.AsSpan(charIndex, charCount), bytes.AsSpan(byteIndex));
    }

    /// <inheritdoc path="/*[not(self::exception[@cref='T:System.Text.EncoderFallbackException'])]"/>
    public override unsafe int GetBytes(char* chars, int charCount, byte* bytes, int byteCount)
    {
        CheckPointer(chars, charCount, nameof(chars), nameof(charCount));
        CheckPointer(bytes, byteCount, nameof(bytes), nameof(byteCount));

        return GetBytes(new ReadOnlySpan<char>(chars, charCount), new Span<byte>(bytes, byteCount));
    }

    /// <inheritdoc path="/*[not(self::exception[@cref='T:System.Text.EncoderFallbackException'])]"/>
    public override byte[] GetBytes(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return GetBytes(s.AsSpan());
    }

    /// <inheritdoc path="/*[not(self::exception[@cref='T:System.Text.EncoderFallbackException'])]"/>
    public override byte[] GetBytes(char[] chars)
    {
        ArgumentNullException.ThrowIfNull(chars);
        return GetBytes(chars.AsSpan());
    }

    /// <inheritdoc path="/*[not(self::exception[@cref='T:System.Text.EncoderFallbackException'])]"/>
    public override byte[] GetBytes(char[] chars, int index, int count)
    {
        CheckRange(chars, index, count);
        return GetBytes(chars.AsSpan(index, count));
    }

    /// <summary>
    /// Encodes the specified characters as one value into a new byte array.
    /// </summary>
    /// <param name="chars">The characters to encode.</param>
    /// <returns>The encoded bytes.</returns>
    public byte[] GetBytes(ReadOnlySpan<char> chars)
    {
        // The encoded size is not known up front and finding it out costs as much as encoding, so encode into a worst-case sized scratch buffer, unless
        // the worst case does not fit in an array: then count first.

        const int MaxStackBytes = 768;

        if (chars.Length > MaxCharCount)
        {
            byte[] bytes = new byte[GetByteCount(chars)];
            StenoCodec.EncodeValue(chars, bytes);
            return bytes;
        }

        int maxByteCount = chars.Length * 3;

        if (maxByteCount <= MaxStackBytes)
        {
            Span<byte> buffer = stackalloc byte[MaxStackBytes];
            return buffer[..GetBytes(chars, buffer)].ToArray();
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(maxByteCount);

        try
        {
            return rented.AsSpan(0, GetBytes(chars, rented)).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <inheritdoc/>
    /// <exception cref="DecoderFallbackException">The bytes are not valid Steno data, whatever <see cref="Encoding.DecoderFallback"/> is.</exception>
    public override int GetCharCount(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return GetCharCount(bytes.AsSpan());
    }

    /// <inheritdoc/>
    /// <exception cref="DecoderFallbackException">The bytes are not valid Steno data, whatever <see cref="Encoding.DecoderFallback"/> is.</exception>
    public override int GetCharCount(byte[] bytes, int index, int count)
    {
        CheckRange(bytes, index, count, nameof(bytes));
        return GetCharCount(bytes.AsSpan(index, count));
    }

    /// <inheritdoc/>
    /// <exception cref="DecoderFallbackException">The bytes are not valid Steno data, whatever <see cref="Encoding.DecoderFallback"/> is.</exception>
    public override unsafe int GetCharCount(byte* bytes, int count)
    {
        CheckPointer(bytes, count, nameof(bytes), nameof(count));
        return GetCharCount(new ReadOnlySpan<byte>(bytes, count));
    }

    /// <inheritdoc/>
    /// <exception cref="DecoderFallbackException">The bytes are not valid Steno data, whatever <see cref="Encoding.DecoderFallback"/> is.</exception>
    public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
    {
        CheckRange(bytes, byteIndex, byteCount, nameof(bytes), nameof(byteIndex), nameof(byteCount));
        CheckIndex(chars, charIndex, nameof(chars), nameof(charIndex));

        return GetChars(bytes.AsSpan(byteIndex, byteCount), chars.AsSpan(charIndex));
    }

    /// <inheritdoc/>
    /// <exception cref="DecoderFallbackException">The bytes are not valid Steno data, whatever <see cref="Encoding.DecoderFallback"/> is.</exception>
    public override unsafe int GetChars(byte* bytes, int byteCount, char* chars, int charCount)
    {
        CheckPointer(bytes, byteCount, nameof(bytes), nameof(byteCount));
        CheckPointer(chars, charCount, nameof(chars), nameof(charCount));

        return GetChars(new ReadOnlySpan<byte>(bytes, byteCount), new Span<char>(chars, charCount));
    }

    /// <inheritdoc/>
    /// <exception cref="DecoderFallbackException">The bytes are not valid Steno data, whatever <see cref="Encoding.DecoderFallback"/> is.</exception>
    public override char[] GetChars(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return GetString(bytes.AsSpan()).ToCharArray();
    }

    /// <inheritdoc/>
    /// <exception cref="DecoderFallbackException">The bytes are not valid Steno data, whatever <see cref="Encoding.DecoderFallback"/> is.</exception>
    public override char[] GetChars(byte[] bytes, int index, int count)
    {
        CheckRange(bytes, index, count, nameof(bytes));
        return GetString(bytes.AsSpan(index, count)).ToCharArray();
    }

    /// <inheritdoc/>
    /// <exception cref="DecoderFallbackException">The bytes are not valid Steno data, whatever <see cref="Encoding.DecoderFallback"/> is.</exception>
    public override string GetString(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return GetString(bytes.AsSpan());
    }

    /// <inheritdoc/>
    /// <exception cref="DecoderFallbackException">The bytes are not valid Steno data, whatever <see cref="Encoding.DecoderFallback"/> is.</exception>
    public override string GetString(byte[] bytes, int index, int count)
    {
        CheckRange(bytes, index, count, nameof(bytes));
        return GetString(bytes.AsSpan(index, count));
    }

    /// <summary>
    /// Decodes the specified bytes as one value into a string.
    /// </summary>
    /// <param name="bytes">The bytes to decode.</param>
    /// <returns>The decoded string.</returns>
    /// <remarks>
    /// Hides <see cref="Encoding.GetString(ReadOnlySpan{byte})"/> to decode in one pass. A call through an <see cref="Encoding"/> reference returns the
    /// same string.
    /// </remarks>
    /// <exception cref="DecoderFallbackException">The bytes are not valid Steno data, whatever <see cref="Encoding.DecoderFallback"/> is.</exception>
    public new string GetString(ReadOnlySpan<byte> bytes)
    {
        const int MaxStackChars = 512;

        // The worst case does not fit in an array: count first.
        int maxCharCount = bytes.Length > MaxByteCount ? GetCharCount(bytes) : bytes.Length * 2;

        if (maxCharCount <= MaxStackChars)
        {
            Span<char> buffer = stackalloc char[MaxStackChars];
            return new string(buffer[..GetChars(bytes, buffer)]);
        }

        char[] rented = ArrayPool<char>.Shared.Rent(maxCharCount);

        try
        {
            return new string(rented.AsSpan(0, GetChars(bytes, rented)));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    #endregion

    /// <summary>
    /// Counts the bytes of chars that a streaming encoder in the given state writes, in slices into a buffer on the stack.
    /// </summary>
    private static int CountBytes(ReadOnlySpan<char> chars, StenoCodec.EncoderState state, bool flush = true)
    {
        // Each slice gives 3 bytes per char at most, with one more char that the encoder may have held from the slice before (see GetMaxByteCount).
        Span<byte> buffer = stackalloc byte[(CountSlice + 1) * 3];
        int count = 0;

        do
        {
            var slice = chars[..Math.Min(chars.Length, CountSlice)];
            chars = chars[slice.Length..];
            count += StenoCodec.EncodeChunk(ref state, slice, buffer, flush && chars.IsEmpty);
        }
        while (!chars.IsEmpty);

        return count;
    }

    /// <summary>
    /// Counts the chars of bytes that a streaming decoder in the given state writes, in slices into a buffer on the stack.
    /// </summary>
    private static int CountChars(ReadOnlySpan<byte> bytes, StenoCodec.DecoderState state, bool flush = true)
    {
        // Each slice gives 2 chars per byte at most, also when it completes a code point that the slice before held (see GetMaxCharCount).
        Span<char> buffer = stackalloc char[CountSlice * 2];
        int count = 0;
        int offset = 0;

        do
        {
            var slice = bytes[..Math.Min(bytes.Length, CountSlice)];
            bytes = bytes[slice.Length..];
            count += StenoCodec.DecodeChunk(ref state, slice, buffer, flush && bytes.IsEmpty, offset);
            offset += slice.Length;
        }
        while (!bytes.IsEmpty);

        return count;
    }

    private static void CheckRange<T>(T[] array, int index, int count, string arrayName = "chars", string indexName = "index", string countName = "count")
    {
        ArgumentNullException.ThrowIfNull(array, arrayName);
        ArgumentOutOfRangeException.ThrowIfNegative(index, indexName);
        ArgumentOutOfRangeException.ThrowIfNegative(count, countName);

        // As in UTF8Encoding, a range past the end is the fault of the array, whichever of index and count is too large.
        if (count > array.Length - index)
            ThrowTooShortForRange(arrayName);
    }

    [DoesNotReturn]
    private static void ThrowTooShortForRange(string paramName) =>
        throw new ArgumentOutOfRangeException(paramName, "The value is too short for the index and count.");

    private static void CheckIndex<T>(T[] array, int index, string arrayName, string indexName)
    {
        ArgumentNullException.ThrowIfNull(array, arrayName);
        ArgumentOutOfRangeException.ThrowIfNegative(index, indexName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, array.Length, indexName);
    }

    private static unsafe void CheckPointer(void* pointer, int count, string pointerName, string countName)
    {
        if (pointer is null)
            throw new ArgumentNullException(pointerName);

        ArgumentOutOfRangeException.ThrowIfNegative(count, countName);
    }
}
