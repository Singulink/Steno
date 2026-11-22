using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Singulink.Steno;

/// <content>
/// Contains the entry points for the streaming encoder and decoder, which take a value in chunks.
/// </content>
internal static partial class StenoCodec
{
    /// <summary>
    /// The longest encoding of a code point, which is how many bytes the decoder can have to hold between two chunks, plus one.
    /// </summary>
    private const int MaxSequenceLength = 4;

    /// <summary>
    /// Encodes a chunk of a value. Returns the number of bytes written.
    /// </summary>
    /// <param name="state">The state of the encoder, which is updated (also by a flush, which does not end the value).</param>
    /// <param name="chars">The chunk.</param>
    /// <param name="bytes">The output.</param>
    /// <param name="flush">Whether to write a high surrogate at the end of the chunk (or held from the chunk before) as an unpaired surrogate,
    /// rather than hold it for the next chunk.</param>
    /// <remarks>
    /// A high surrogate at the end of a chunk is held back unless the call flushes, since the next chunk may start with its low surrogate. A low
    /// surrogate cannot follow a high surrogate that a flush wrote (the two code points would not be a value).
    /// </remarks>
    /// <exception cref="ArgumentException">The output is too small.</exception>
    /// <exception cref="EncoderFallbackException">The chunk starts with a low surrogate, and the call before flushed a high surrogate.</exception>
    public static int EncodeChunk(ref EncoderState state, ReadOnlySpan<char> chars, Span<byte> bytes, bool flush)
    {
        if (!state.Started)
        {
            state.State = _initialState;
            state.Started = true;
        }

        if (state.FlushedHigh && chars.Length > 0 && char.IsLowSurrogate(chars[0]))
            ThrowLowAfterFlushedHigh();

        ref var codec = ref state.State;
        ref ushort source = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(chars));
        ref byte target = ref MemoryMarshal.GetReference(bytes);
        int length = chars.Length;
        int room = bytes.Length;
        int written = 0;
        int index = 0;

        if (state.HeldHigh is not '\0')
        {
            if (length is 0 && !flush)
                return 0;

            // The held high surrogate, with the low surrogate that starts this chunk if there is one, from a buffer of their own.

            Span<ushort> pair = [state.HeldHigh, 0];
            int units = 1;

            if (length > 0 && char.IsLowSurrogate(chars[0]))
            {
                pair[1] = source;
                units = 2;
            }

            codec.Mark = 0;
            EncodeSlow(ref codec, ref MemoryMarshal.GetReference(pair), units, 0, ref target, room, ref written);

            state.HeldHigh = '\0';
            state.FlushedHigh = units is 1 && length is 0;
            index = units - 1;
            codec.Mark = index;
        }

        // The layout before the last char of the slow path, and whether the last run loop took nothing (see TablesAfter).
        Layout? before = null;
        bool idle = false;

        while (index < length)
        {
            var layout = codec.Layout;

            // A layout without its tables yet goes on in the slow path, and so does a char after a run loop that took nothing that the next one
            // would not take either.
            if (TablesAfter(layout, before) is { } tables && (!idle || tables.StartsRun(Unsafe.Add(ref source, index))))
            {
                int start = index;
                index = layout.HasEvents
                    ? tables.WideVectors
                        ? EncodeRun<WithEvents, Wide>(tables, ref codec, ref source, length, index, ref target, room, ref written)
                        : EncodeRun<WithEvents, NoWide>(tables, ref codec, ref source, length, index, ref target, room, ref written)
                    : tables.WideVectors
                        ? EncodeRun<NoEvents, Wide>(tables, ref codec, ref source, length, index, ref target, room, ref written)
                        : EncodeRun<NoEvents, NoWide>(tables, ref codec, ref source, length, index, ref target, room, ref written);
                idle = index == start;
            }

            if (index == length)
                break;

            if (index == length - 1 && !flush && char.IsHighSurrogate(chars[index]))
            {
                state.HeldHigh = chars[index];
                break;
            }

            before = codec.Layout;
            index = EncodeSlow(ref codec, ref source, length, index, ref target, room, ref written);
        }

        // Bring what the run loops left out of the state (the confidence and the pending window) up to date, so the next chunk can start afresh.
        CatchUp(ref codec, ref source, index);
        codec.Mark = 0;

        // Whether the last code point written is a high surrogate that this call flushed, rather than held.
        if (length > 0)
            state.FlushedHigh = char.IsHighSurrogate(chars[length - 1]) && state.HeldHigh is '\0';

        return written;
    }

    /// <summary>
    /// Decodes a chunk of an encoded value. Returns the number of chars written.
    /// </summary>
    /// <param name="state">The state of the decoder, which is updated (also by a flush, which does not end the value).</param>
    /// <param name="bytes">The chunk.</param>
    /// <param name="chars">The output.</param>
    /// <param name="flush">Whether the input ends with this chunk, so that it must not end in the middle of a code point.</param>
    /// <param name="offset">Where the chunk starts in the input of the caller, for the index that an error reports.</param>
    /// <remarks>
    /// The bytes of a code point that the chunk ends in the middle of are held until the next chunk completes them, unless this is the last chunk.
    /// </remarks>
    /// <exception cref="ArgumentException">The output is too small.</exception>
    /// <exception cref="DecoderFallbackException">The bytes are not valid Steno data. Its index is where the code point starts in the chunk, plus
    /// <paramref name="offset"/> (the position of the chunk in the input of the caller).</exception>
    public static int DecodeChunk(ref DecoderState state, ReadOnlySpan<byte> bytes, Span<char> chars, bool flush, int offset = 0)
    {
        if (!state.Started)
        {
            state.State = _initialState;
            state.Started = true;
        }

        ref var codec = ref state.State;
        ref byte source = ref MemoryMarshal.GetReference(bytes);
        ref ushort target = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(chars));
        int length = bytes.Length;
        int room = chars.Length;
        int written = 0;
        int index = 0;

        if (state.HeldCount > 0)
        {
            // The held bytes, completed from the start of this chunk, in a buffer of their own.

            Span<byte> sequence = stackalloc byte[MaxSequenceLength];
            int held = state.HeldCount;

            for (int i = 0; i < held; i++)
                sequence[i] = (byte)(state.HeldBytes >> (8 * i));

            // Only the bytes of a code point that is valid so far are held, so the lead byte is in use.
            int size = codec.Layout.Segments[codec.Layout.SegmentOfLead(sequence[0])].Bytes;
            int taken = Math.Min(size - held, length);
            bytes[..taken].CopyTo(sequence[held..]);

            if (held + taken < size && !flush && sequence[held..(held + taken)].IndexOfAnyInRange((byte)0, (byte)(TrailFloor - 1)) < 0)
            {
                state.HeldBytes = Pack(sequence[..(held + taken)]);
                state.HeldCount = held + taken;
                return 0;
            }

            // Complete, or it is the last chunk (then DecodeSlow rejects the sequence as truncated), or it has a byte 00 (rejected as well).
            codec.Mark = 0;
            DecodeSlow(ref codec, ref MemoryMarshal.GetReference(sequence), held + taken, 0, ref target, room, ref written, origin: held - offset);

            state.HeldCount = 0;
            state.HeldBytes = 0;
            index = taken;
            codec.Mark = written;
        }

        // The layout before the last char of the slow path, and whether the last run loop took nothing (see TablesAfter).
        Layout? before = null;
        bool idle = false;

        while (index < length)
        {
            var layout = codec.Layout;

            // A layout without its tables yet goes on in the slow path, and so does a char after a run loop that took nothing that the next one
            // would not take either.
            if (TablesAfter(layout, before) is { } tables && (!idle || tables.StartsRun(Unsafe.Add(ref source, index))))
            {
                int start = index;
                index = layout.HasEvents
                    ? tables.WideVectors
                        ? DecodeRun<WithEvents, Wide>(tables, ref codec, ref source, length, index, ref target, room, ref written)
                        : DecodeRun<WithEvents, NoWide>(tables, ref codec, ref source, length, index, ref target, room, ref written)
                    : tables.WideVectors
                        ? DecodeRun<NoEvents, Wide>(tables, ref codec, ref source, length, index, ref target, room, ref written)
                        : DecodeRun<NoEvents, NoWide>(tables, ref codec, ref source, length, index, ref target, room, ref written);
                idle = index == start;
            }

            if (index == length)
                break;

            // A code point that the chunk ends in the middle of: hold its bytes for the next chunk, if they are valid so far. Otherwise DecodeSlow
            // reports what is wrong with them, in the order of the specification.
            if (!flush)
            {
                int segment = codec.Layout.SegmentOfLead(Unsafe.Add(ref source, index));

                if (segment >= 0 && length - index < codec.Layout.Segments[segment].Bytes &&
                    bytes[(index + 1)..].IndexOfAnyInRange((byte)0, (byte)(TrailFloor - 1)) < 0)
                {
                    state.HeldBytes = Pack(bytes[index..]);
                    state.HeldCount = length - index;
                    index = length;
                    break;
                }
            }

            before = codec.Layout;
            index = DecodeSlow(ref codec, ref source, length, index, ref target, room, ref written, origin: -offset);
        }

        CatchUp(ref codec, ref target, written);
        codec.Mark = 0;

        // The output ends with a high surrogate only if that was a code point of its own (see DecodeSlow).
        if (written > 0)
            codec.PreviousHigh = char.IsHighSurrogate(chars[written - 1]);

        return written;

        static uint Pack(ReadOnlySpan<byte> sequence)
        {
            uint packed = 0;

            for (int i = 0; i < sequence.Length; i++)
                packed |= (uint)sequence[i] << (8 * i);

            return packed;
        }
    }

    /// <summary>
    /// The state of a streaming encoder. The default value is the initial state.
    /// </summary>
    internal struct EncoderState
    {
        // The state of the codec, up to date (its mark is 0). Only valid once started.
        public State State;
        public bool Started;

        // A high surrogate at the end of the last chunk, or '\0'.
        public char HeldHigh;

        // Whether the last code point written is a high surrogate that a flush wrote: a low surrogate cannot follow it.
        public bool FlushedHigh;
    }

    /// <summary>
    /// The state of a streaming decoder. The default value is the initial state.
    /// </summary>
    internal struct DecoderState
    {
        // The state of the codec, up to date (its mark is 0). Only valid once started.
        public State State;
        public bool Started;

        // The bytes of a code point that the last chunk ended in the middle of (1 to 3 of them, first byte in the low bits).
        public uint HeldBytes;
        public int HeldCount;
    }
}
