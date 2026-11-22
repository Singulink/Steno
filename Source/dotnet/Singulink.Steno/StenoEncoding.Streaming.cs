using System.Text;

namespace Singulink.Steno;

/// <content>
/// Contains the streaming encoder and decoder. What they guarantee is documented on <see cref="GetEncoder"/> and <see cref="GetDecoder"/>.
/// </content>
public sealed partial class StenoEncoding
{
    /// <summary>
    /// Encodes one value given in pieces (see <see cref="GetEncoder"/>).
    /// </summary>
    /// <remarks>
    /// Every member works on a copy of the state and stores it back only when it succeeds, so a call that throws leaves the encoder as it was.
    /// </remarks>
    private sealed class Encoder : System.Text.Encoder
    {
        private StenoCodec.EncoderState _state;

        /// <inheritdoc/>
        public override void Reset() => _state = default;

        /// <inheritdoc/>
        public override int GetByteCount(char[] chars, int index, int count, bool flush)
        {
            CheckRange(chars, index, count);
            return GetByteCount(chars.AsSpan(index, count), flush);
        }

        /// <inheritdoc/>
        public override unsafe int GetByteCount(char* chars, int count, bool flush)
        {
            CheckPointer(chars, count, nameof(chars), nameof(count));
            return GetByteCount(new ReadOnlySpan<char>(chars, count), flush);
        }

        /// <inheritdoc/>
        public override int GetByteCount(ReadOnlySpan<char> chars, bool flush) => CountBytes(chars, _state, flush);

        /// <inheritdoc/>
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex, bool flush)
        {
            CheckRange(chars, charIndex, charCount, nameof(chars), nameof(charIndex), nameof(charCount));
            CheckIndex(bytes, byteIndex, nameof(bytes), nameof(byteIndex));

            return GetBytes(chars.AsSpan(charIndex, charCount), bytes.AsSpan(byteIndex), flush);
        }

        /// <inheritdoc/>
        public override unsafe int GetBytes(char* chars, int charCount, byte* bytes, int byteCount, bool flush)
        {
            CheckPointer(chars, charCount, nameof(chars), nameof(charCount));
            CheckPointer(bytes, byteCount, nameof(bytes), nameof(byteCount));

            return GetBytes(new ReadOnlySpan<char>(chars, charCount), new Span<byte>(bytes, byteCount), flush);
        }

        /// <inheritdoc/>
        public override int GetBytes(ReadOnlySpan<char> chars, Span<byte> bytes, bool flush)
        {
            var state = _state;
            int written = StenoCodec.EncodeChunk(ref state, chars, bytes, flush);
            _state = state;

            return written;
        }

        /// <inheritdoc/>
        public override void Convert(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex, int byteCount, bool flush,
            out int charsUsed, out int bytesUsed, out bool completed)
        {
            CheckRange(chars, charIndex, charCount, nameof(chars), nameof(charIndex), nameof(charCount));
            CheckRange(bytes, byteIndex, byteCount, nameof(bytes), nameof(byteIndex), nameof(byteCount));

            Convert(chars.AsSpan(charIndex, charCount), bytes.AsSpan(byteIndex, byteCount), flush, out charsUsed, out bytesUsed, out completed);
        }

        /// <inheritdoc/>
        public override unsafe void Convert(char* chars, int charCount, byte* bytes, int byteCount, bool flush,
            out int charsUsed, out int bytesUsed, out bool completed)
        {
            CheckPointer(chars, charCount, nameof(chars), nameof(charCount));
            CheckPointer(bytes, byteCount, nameof(bytes), nameof(byteCount));

            Convert(new ReadOnlySpan<char>(chars, charCount), new Span<byte>(bytes, byteCount), flush, out charsUsed, out bytesUsed, out completed);
        }

        /// <inheritdoc/>
        public override void Convert(ReadOnlySpan<char> chars, Span<byte> bytes, bool flush, out int charsUsed, out int bytesUsed, out bool completed)
        {
            var state = _state;
            charsUsed = 0;
            bytesUsed = 0;

            while (true)
            {
                int room = bytes.Length - bytesUsed;
                int rest = chars.Length - charsUsed;

                if (rest is 0)
                {
                    // All the chars are in; a flush writes a held high surrogate, if there is room for it.
                    completed = !flush || CountBytes([], state, flush: true) <= room;

                    if (completed && flush)
                        bytesUsed += StenoCodec.EncodeChunk(ref state, [], bytes[bytesUsed..], flush: true);

                    break;
                }

                // n chars, with a high surrogate held over, take at most (n + 1) * 3 bytes: pieces of that size are sure to fit.
                int fit = (room / 3) - 1;

                if (fit >= rest)
                {
                    bytesUsed += StenoCodec.EncodeChunk(ref state, chars[charsUsed..], bytes[bytesUsed..], flush);
                    charsUsed = chars.Length;
                    completed = true;
                    break;
                }

                if (fit > 0)
                {
                    bytesUsed += StenoCodec.EncodeChunk(ref state, chars.Slice(charsUsed, fit), bytes[bytesUsed..], flush: false);
                    charsUsed += fit;
                    continue;
                }

                // Near the end of the room, one char at a time while it fits.
                var next = chars.Slice(charsUsed, 1);

                if (CountBytes(next, state, flush: false) <= room)
                {
                    bytesUsed += StenoCodec.EncodeChunk(ref state, next, bytes[bytesUsed..], flush: false);
                    charsUsed++;
                    continue;
                }

                // A held high surrogate that the next char does not pair is a code point of its own: as UTF8Encoding does, write it on its own
                // (which is what a flush does) if there is room for that and not for the char too.
                if (state.HeldHigh is not '\0' && !char.IsLowSurrogate(next[0]) && CountBytes([], state, flush: true) <= room)
                {
                    bytesUsed += StenoCodec.EncodeChunk(ref state, [], bytes[bytesUsed..], flush: true);
                    continue;
                }

                completed = false;
                break;
            }

            if (!completed && charsUsed is 0 && bytesUsed is 0)
                throw new ArgumentException("The value is too short for the bytes of one char.", nameof(bytes));

            _state = state;
        }
    }

    /// <summary>
    /// Decodes one value given in pieces (see <see cref="GetDecoder"/>).
    /// </summary>
    /// <remarks>
    /// Every member works on a copy of the state and stores it back only when it succeeds, so a call that throws leaves the decoder as it was.
    /// </remarks>
    private sealed class Decoder : System.Text.Decoder
    {
        private StenoCodec.DecoderState _state;

        /// <inheritdoc/>
        public override void Reset() => _state = default;

        /// <inheritdoc/>
        public override int GetCharCount(byte[] bytes, int index, int count) => GetCharCount(bytes, index, count, flush: false);

        /// <inheritdoc/>
        public override int GetCharCount(byte[] bytes, int index, int count, bool flush)
        {
            CheckRange(bytes, index, count, nameof(bytes));
            return GetCharCount(bytes.AsSpan(index, count), flush);
        }

        /// <inheritdoc/>
        public override unsafe int GetCharCount(byte* bytes, int count, bool flush)
        {
            CheckPointer(bytes, count, nameof(bytes), nameof(count));
            return GetCharCount(new ReadOnlySpan<byte>(bytes, count), flush);
        }

        /// <inheritdoc/>
        public override int GetCharCount(ReadOnlySpan<byte> bytes, bool flush) => CountChars(bytes, _state, flush);

        /// <inheritdoc/>
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
            => GetChars(bytes, byteIndex, byteCount, chars, charIndex, flush: false);

        /// <inheritdoc/>
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex, bool flush)
        {
            CheckRange(bytes, byteIndex, byteCount, nameof(bytes), nameof(byteIndex), nameof(byteCount));
            CheckIndex(chars, charIndex, nameof(chars), nameof(charIndex));

            return GetChars(bytes.AsSpan(byteIndex, byteCount), chars.AsSpan(charIndex), flush);
        }

        /// <inheritdoc/>
        public override unsafe int GetChars(byte* bytes, int byteCount, char* chars, int charCount, bool flush)
        {
            CheckPointer(bytes, byteCount, nameof(bytes), nameof(byteCount));
            CheckPointer(chars, charCount, nameof(chars), nameof(charCount));

            return GetChars(new ReadOnlySpan<byte>(bytes, byteCount), new Span<char>(chars, charCount), flush);
        }

        /// <inheritdoc/>
        public override int GetChars(ReadOnlySpan<byte> bytes, Span<char> chars, bool flush)
        {
            var state = _state;
            int written = StenoCodec.DecodeChunk(ref state, bytes, chars, flush);
            _state = state;

            return written;
        }

        /// <inheritdoc/>
        public override void Convert(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex, int charCount, bool flush,
            out int bytesUsed, out int charsUsed, out bool completed)
        {
            CheckRange(bytes, byteIndex, byteCount, nameof(bytes), nameof(byteIndex), nameof(byteCount));
            CheckRange(chars, charIndex, charCount, nameof(chars), nameof(charIndex), nameof(charCount));

            Convert(bytes.AsSpan(byteIndex, byteCount), chars.AsSpan(charIndex, charCount), flush, out bytesUsed, out charsUsed, out completed);
        }

        /// <inheritdoc/>
        public override unsafe void Convert(byte* bytes, int byteCount, char* chars, int charCount, bool flush,
            out int bytesUsed, out int charsUsed, out bool completed)
        {
            CheckPointer(bytes, byteCount, nameof(bytes), nameof(byteCount));
            CheckPointer(chars, charCount, nameof(chars), nameof(charCount));

            Convert(new ReadOnlySpan<byte>(bytes, byteCount), new Span<char>(chars, charCount), flush, out bytesUsed, out charsUsed, out completed);
        }

        /// <inheritdoc/>
        public override void Convert(ReadOnlySpan<byte> bytes, Span<char> chars, bool flush, out int bytesUsed, out int charsUsed, out bool completed)
        {
            var state = _state;
            bytesUsed = 0;
            charsUsed = 0;

            while (true)
            {
                int room = chars.Length - charsUsed;
                int rest = bytes.Length - bytesUsed;

                if (rest is 0)
                {
                    // All the bytes are in; a flush fails on the bytes of an unfinished code point, and writes nothing otherwise.
                    if (flush)
                        StenoCodec.DecodeChunk(ref state, [], chars[charsUsed..], flush: true, bytesUsed);

                    completed = true;
                    break;
                }

                // n bytes give at most 2n chars, also with the bytes of an unfinished code point held over: pieces of that size are sure to fit.
                int fit = room / 2;

                if (fit >= rest)
                {
                    charsUsed += StenoCodec.DecodeChunk(ref state, bytes[bytesUsed..], chars[charsUsed..], flush, bytesUsed);
                    bytesUsed = bytes.Length;
                    completed = true;
                    break;
                }

                if (fit > 0)
                {
                    charsUsed += StenoCodec.DecodeChunk(ref state, bytes.Slice(bytesUsed, fit), chars[charsUsed..], flush: false, bytesUsed);
                    bytesUsed += fit;
                    continue;
                }

                // Near the end of the room, one byte at a time while it fits.
                var next = bytes.Slice(bytesUsed, 1);

                if (CountChars(next, state, flush: false) > room)
                {
                    completed = false;
                    break;
                }

                charsUsed += StenoCodec.DecodeChunk(ref state, next, chars[charsUsed..], flush: false, bytesUsed);
                bytesUsed++;
            }

            if (!completed && bytesUsed is 0 && charsUsed is 0)
                throw new ArgumentException("The value is too short for the chars of one code point.", nameof(chars));

            _state = state;
        }
    }
}
