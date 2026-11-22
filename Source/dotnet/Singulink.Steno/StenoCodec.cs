using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace Singulink.Steno;

/// <summary>
/// The codec of Steno, version 1 (see FORMAT.md): encodes and decodes whole values, and chunks of a value for the streaming encoder and decoder.
/// </summary>
/// <remarks>
/// <para>
/// The state (context, main window, kana slot, second window, confidence, pending window) picks a layout. Layouts are immutable objects that are kept
/// in a small cache keyed by the context, the kana slot, the main window and the second window, where a second window that changes nothing (all of its
/// chars have a place in the layout without it) counts as none.</para>
/// <para>
/// A layout comes in two forms. The light one is the list of its ranges, cheap to build (a few hundred nanoseconds) and enough for the slow path to
/// encode or decode any char. The tables of the run loops (several kilobytes) are built only for a layout that chars keep to; text that reaches a new
/// state at almost every char, more states than the cache holds, then costs a light build per char at worst rather than a build of the tables. The
/// cache is set-associative, and a state that missed it keeps its own last layouts, so that a few states never evict each other.</para>
/// <para>
/// Most chars leave the layout alone: the "pure" chars of a layout. The run loops handle the pure chars that take 1 or 2 bytes without touching the
/// state. The only effects of pure chars are that a char of the main window adds to the confidence (in the general context) and that a CJK char or a
/// Hangul syllable clears the pending window (in the wide contexts). Where that matters, the code that needs it counts those chars since the last
/// change of the state (its mark), and the streaming entry points do so at the end of each chunk.</para>
/// <para>
/// The run loops also handle the 2-byte chars that change the state in a way that only depends on the layout and the char: in the general context a
/// char of another window (which lowers the confidence or switches the main window to its own), in the CJK context a char that moves the kana slot.
/// The layout that each one leads to is remembered on the layout the first time the slow path makes that change. Everything else goes through the
/// slow path, which handles one code point and updates the state by the rules of the specification.</para>
/// <para>
/// Vector code is used where <see cref="Vector128.IsHardwareAccelerated"/> is true, and the wide blocks also need <see cref="CanShuffle"/>. Every vector
/// path has a scalar path that gives the same bytes, so the output does not depend on the hardware.</para>
/// </remarks>
internal static partial class StenoCodec
{
    private const int General = 0;
    private const int Cjk = 1;
    private const int Hangul = 2;

    private const int NoWindow = -1;
    private const int AsciiLetters = -2;
    private const int HiraganaStart = 0x3040;
    private const int KatakanaStart = 0x30A0;

    // A trail byte is 08..FF: the digit of base 248 plus 8. So the bytes 01..07 are only ever the first byte of a code point.
    private const int TrailFloor = 8;
    private const int Radix = 256 - TrailFloor;
    private const int MaxConfidence = 2;
    private const int NearZoneEnd = 0xEFF;
    private const int PunctuationEnd = 0x20CF;

    // In every layout printable ASCII takes the bytes from 7 (as far as the layout has ASCII singles), and tab and line feed the bytes 2 and 3.
    private const int AsciiFirstByte = 7;
    private const int AsciiDelta = 0x20 - AsciiFirstByte;
    private const int ControlDelta = 0x09 - 2;
    private const int CarriageReturnByte = 5;

    private const int MinVectorRun = 4;
    private const int ScalarStretch = 16;

    // The least input for which the wide blocks pay for getting into them: less is a short value, where one char at a time is faster.
    private const int MinWideChars = 24;
    private const int MinWideBytes = 48;

    // DivideBy248 with a multiply-high: value / 248 is (value >> 3) / 31, and for x = value >> 3 (below 8,192), x / 31 is x * 33,826 >> 20.
    // 33,826 * 31 is 2^20 + 30, so the product is 2^20 * x / 31 plus 30 * x / 31, and that excess stays below 2^20 / 31 (the gap from x mod 31 = 30
    // to the next multiple) because 30 * 8,191 is below 2^20. The tests check every 16-bit value on every form the machine has.
    private const ushort DivideBy248Multiplier = 33826;

    // The reasons of the specification for rejecting bytes (section 10.1).
    public const string ZeroByte = "zero-byte";
    public const string InvalidTrail = "invalid-trail";
    public const string UnusedLead = "unused-lead";
    public const string Truncated = "truncated";
    public const string PastRange = "past-range";
    public const string SurrogatePair = "surrogate-pair";

    // The layouts in use are bounded by the cache: a layout is referenced by the cache, by the states that use it (the initial state, and the state
    // of each encoder or decoder object, which also keeps the two layouts before its current one once it has missed the cache), and by its twin
    // with tables (see Layout.Tables), which the twin does not refer back to. What a layout remembers of the changes that lead away from it names
    // the layout that they lead to by its cache slot and id, and does not keep it alive. So beyond the layouts of live states there are at most
    // CacheSize layouts. A layout is its ranges (at most 40 of 16 bytes) and at most GeneralTransitionCount transitions (of 32 bytes, with an array
    // of 8 bytes each): about 2 KB. The tables that the run loops need (about 4 KB more) are built only for a layout that TablesAfterUses chars kept
    // to in the slow path, so that text that reaches a new layout at almost every char never pays for them. With the tables in every slot that is
    // about 6 KB a slot, 25 MB at the most; text that only hops leaves about 2 KB a slot.
    private const int CacheSize = 4096;

    // The cache is set-associative: a layout can go in any of the ways of its set, so that a few states that share a set do not evict each other
    // at every change between them.
    private const int CacheWays = 4;
    private const int TransitionSlots = 16;
    private const int StrayTransitions = 0;
    private const int SwitchTransitions = TransitionSlots;
    private const int SlotTransitions = 0;

    // In the general context, after the strays and switches: entering the Hangul context, then the CJK context with each value of the kana slot.
    private const int WideTransitions = 2 * TransitionSlots;
    private const int GeneralTransitionCount = WideTransitions + 4;
    private const int TablesAfterUses = 16;
    private const byte MixedPage = 0xFF;

    private static readonly Layout?[] _cache = new Layout?[CacheSize];

    // Per set of the cache, the way that a new layout takes next when the set is full.
    private static readonly byte[] _cacheTurns = new byte[CacheSize / CacheWays];

    // The number of layouts built so far, which gives each one an id of its own.
    private static long _layoutCount;
    private static readonly State _initialState = CreateInitialState();
    private static readonly Layout _initialLayout = _initialState.Layout;
    private static readonly Tables _initialTables = (Tables)_initialLayout;

    /// <summary>
    /// Encodes a whole value from the initial state. Returns the number of bytes written.
    /// </summary>
    /// <exception cref="ArgumentException">The destination is too small.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int EncodeValue(ReadOnlySpan<char> chars, Span<byte> bytes)
    {
        ref ushort source = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(chars));
        ref byte target = ref MemoryMarshal.GetReference(bytes);
        int written = 0;

        // Text that only has pure chars of the initial layout needs no state at all.
        int index = EncodeRun<NoEvents, NoWide>(
            _initialTables, ref Unsafe.NullRef<State>(), ref source, chars.Length, 0, ref target, bytes.Length, ref written);
        return index == chars.Length ? written : EncodeRest(ref source, chars.Length, index, ref target, bytes.Length, written);
    }

    /// <summary>
    /// Decodes a whole value from the initial state. Returns the number of chars written.
    /// </summary>
    /// <exception cref="ArgumentException">The destination is too small.</exception>
    /// <exception cref="DecoderFallbackException">The bytes are not a valid encoding.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DecodeValue(ReadOnlySpan<byte> bytes, Span<char> chars)
    {
        ref byte source = ref MemoryMarshal.GetReference(bytes);
        ref ushort target = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(chars));
        int written = 0;

        int index = DecodeRun<NoEvents, NoWide>(
            _initialTables, ref Unsafe.NullRef<State>(), ref source, bytes.Length, 0, ref target, chars.Length, ref written);
        return index == bytes.Length ? written : DecodeRest(ref source, bytes.Length, index, ref target, chars.Length, written);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int EncodeRest(ref ushort source, int length, int index, ref byte target, int room, int written)
    {
        var state = _initialState;
        bool idle = false;

        while (true)
        {
            var before = state.Layout;
            index = EncodeSlow(ref state, ref source, length, index, ref target, room, ref written);

            if (index == length)
                return written;

            // The lean loops where the layout has no events to handle, and where it has no wide blocks: a call to them would make the loop for
            // the narrow runs heavier. A layout without its tables yet goes on in the slow path (see TablesAfter).
            var layout = state.Layout;

            if (TablesAfter(layout, before) is not { } tables || (idle && !tables.StartsRun(Unsafe.Add(ref source, index))))
                continue;

            int start = index;

            index = layout.HasEvents
                ? tables.WideVectors
                    ? EncodeRun<WithEvents, Wide>(tables, ref state, ref source, length, index, ref target, room, ref written)
                    : EncodeRun<WithEvents, NoWide>(tables, ref state, ref source, length, index, ref target, room, ref written)
                : tables.WideVectors
                    ? EncodeRun<NoEvents, Wide>(tables, ref state, ref source, length, index, ref target, room, ref written)
                    : EncodeRun<NoEvents, NoWide>(tables, ref state, ref source, length, index, ref target, room, ref written);

            if (index == length)
                return written;

            idle = index == start;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int DecodeRest(ref byte source, int length, int index, ref ushort target, int room, int written)
    {
        var state = _initialState;
        bool idle = false;

        while (true)
        {
            var before = state.Layout;
            index = DecodeSlow(ref state, ref source, length, index, ref target, room, ref written);

            if (index == length)
                return written;

            var layout = state.Layout;

            if (TablesAfter(layout, before) is not { } tables || (idle && !tables.StartsRun(Unsafe.Add(ref source, index))))
                continue;

            int start = index;

            index = layout.HasEvents
                ? tables.WideVectors
                    ? DecodeRun<WithEvents, Wide>(tables, ref state, ref source, length, index, ref target, room, ref written)
                    : DecodeRun<WithEvents, NoWide>(tables, ref state, ref source, length, index, ref target, room, ref written)
                : tables.WideVectors
                    ? DecodeRun<NoEvents, Wide>(tables, ref state, ref source, length, index, ref target, room, ref written)
                    : DecodeRun<NoEvents, NoWide>(tables, ref state, ref source, length, index, ref target, room, ref written);

            if (index == length)
                return written;

            idle = index == start;
        }
    }

    /// <summary>
    /// Gets the tables of the layout that a char in the slow path left the state in, for the run loops, or null to go on in the slow path.
    /// </summary>
    /// <param name="layout">The layout of the state after the char.</param>
    /// <param name="before">The layout of the state before the char.</param>
    /// <remarks>
    /// The tables are built for a layout once <see cref="TablesAfterUses"/> chars in the slow path left the state in it as it was: chars that
    /// keep to a layout are what the run loops are for, and text that reaches a new layout at almost every char never pays for them. Even with
    /// the tables, after a run loop that took nothing the callers go into the next one only where it can take the next char
    /// (<see cref="Tables.StartsRun(ushort)"/>): getting into a run loop that leaves it at once costs more than the slow path.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Tables? TablesAfter(Layout layout, Layout? before) => layout.Tables ?? (layout == before ? layout.CountUse() : null);

    /// <summary>
    /// Encodes pure chars of the layout that take 1 or 2 bytes (and the 1-byte chars of a supplementary main window), for as long as they come, and
    /// with <see cref="WithEvents"/> also the 2-byte chars of events (see <see cref="Event"/>). Returns the index of the first char that it left alone.
    /// </summary>
    /// <remarks>
    /// The state is only used with <see cref="WithEvents"/>; with <see cref="NoEvents"/> it may be a null reference (for the initial state before
    /// anything changed it).
    /// </remarks>
    private static int EncodeRun<TEvents, TWide>(
        Tables tables, ref State state, ref ushort source, int length, int index, ref byte target, int room, ref int writtenRef)
        where TEvents : struct, IEvents
        where TWide : struct, IWide
    {
        int written = writtenRef;

        // Whether to look for a run to store as a vector first: not right after an event, which is a sign of short runs.
        bool look = true;

    Reload:
        uint asciiCount = (uint)tables.AsciiCount;
        uint aStart = (uint)tables.AStart;
        uint aCount = (uint)tables.ACount;
        uint aDelta = (uint)(tables.AStart - tables.AFirstByte);
        uint wideStart = (uint)tables.WideStart;
        uint wideCount = (uint)tables.WideCount;
        uint wideSegment = (uint)tables.WideSegment;
        uint wideLead = (uint)tables.WideLead;

        bool runs = false;

        while (true)
        {
            if (Vector128.IsHardwareAccelerated && look)
            {
                if (TWide.Enabled && tables.WideVectors && CanShuffle && length - index >= MinWideChars)
                {
                    // In the wide contexts: blocks of eight chars of the wide range (the ideographs, the Hangul syllables) mixed with singles.
                    runs = EncodeWideBlocks(tables, ref source, length, ref index, ref target, room, ref written);
                }
                else
                {
                    // Runs of ASCII and of the other range of singles (the main window, the kana of the slot or the fullwidth punctuation), 16 chars at a
                    // time, stored with no byte past the run. Whole blocks move on by a fixed 16 chars, so that the next block does not wait for the work
                    // on this one; the length of a run is only worked out where it ends.

                    // Set up here rather than with the rest, which a switch to another layout in the middle of short runs reloads.
                    var asciiCounts = Vector128.Create((ushort)asciiCount);
                    var aStarts = Vector128.Create((ushort)aStart);
                    var aCounts = Vector128.Create((ushort)aCount);
                    var aDeltas = Vector128.Create((ushort)aDelta);
                    var asciiDeltas = Vector128.Create((ushort)AsciiDelta);
                    var asciiStarts = Vector128.Create((ushort)0x20);
                    int available = Math.Min(length - index, room - written);
                    runs = false;

                    while (true)
                    {
                        Vector128<ushort> first;
                        Vector128<ushort> second;

                        if (available >= 16)
                        {
                            first = Vector128.LoadUnsafe(ref source, (nuint)index);
                            second = Vector128.LoadUnsafe(ref source, (nuint)(index + 8));
                        }
                        else if (available >= 4)
                        {
                            // The rest (of a short string, typically) in two overlapping pieces of 8 or of 4, if it is all singles.

                            int piece = available >= 8 ? 8 : 4;
                            Vector128<ushort> front;
                            Vector128<ushort> back;

                            if (piece == 8)
                            {
                                front = Vector128.LoadUnsafe(ref source, (nuint)index);
                                back = Vector128.LoadUnsafe(ref source, (nuint)(index + available - 8));
                            }
                            else
                            {
                                ref byte raw = ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref source, index));
                                front = Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref raw)).AsUInt16();
                                back = Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref raw, (available - 4) * 2))).AsUInt16();
                            }

                            var frontSingles = Vector128.LessThan(front - asciiStarts, asciiCounts) | Vector128.LessThan(front - aStarts, aCounts);
                            var backSingles = Vector128.LessThan(back - asciiStarts, asciiCounts) | Vector128.LessThan(back - aStarts, aCounts);
                            uint bits = Vector128.Narrow(frontSingles & backSingles, Vector128<ushort>.Zero).ExtractMostSignificantBits();

                            if (bits == (1u << piece) - 1)
                            {
                                ref byte output = ref Unsafe.Add(ref target, written);
                                ulong frontBytes = EncodeSingles(front, asciiCounts, aDeltas).AsUInt64().ToScalar();
                                ulong backBytes = EncodeSingles(back, asciiCounts, aDeltas).AsUInt64().ToScalar();

                                if (piece == 8)
                                {
                                    Unsafe.WriteUnaligned(ref output, frontBytes);
                                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, available - 8), backBytes);
                                }
                                else
                                {
                                    Unsafe.WriteUnaligned(ref output, (uint)frontBytes);
                                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, available - 4), (uint)backBytes);
                                }

                                index += available;
                                written += available;
                                break;
                            }

                            if (piece != 8)
                                break;

                            first = front;
                            second = Vector128<ushort>.Zero;
                        }
                        else
                        {
                            break;
                        }

                        var firstAscii = Vector128.LessThan(first - asciiStarts, asciiCounts);
                        var secondAscii = Vector128.LessThan(second - asciiStarts, asciiCounts);
                        var firstSingles = firstAscii | Vector128.LessThan(first - aStarts, aCounts);
                        var secondSingles = secondAscii | Vector128.LessThan(second - aStarts, aCounts);

                        var encoded = Vector128.Narrow(
                            first - Vector128.ConditionalSelect(firstAscii, asciiDeltas, aDeltas),
                            second - Vector128.ConditionalSelect(secondAscii, asciiDeltas, aDeltas));

                        ref byte destination = ref Unsafe.Add(ref target, written);

                        if ((firstSingles & secondSingles) == Vector128<ushort>.AllBitsSet)
                        {
                            // Only a block of 16 gets here: the second half of a block of 8 is zeros.
                            encoded.StoreUnsafe(ref destination);
                            index += 16;
                            written += 16;
                            available -= 16;
                            continue;
                        }

                        int count = BitOperations.TrailingZeroCount(~Vector128.Narrow(firstSingles, secondSingles).ExtractMostSignificantBits());

                        if (count >= 8)
                        {
                            // The first eight, then the eight that end where the run ends.
                            var last = EncodeSingles(Vector128.LoadUnsafe(ref source, (nuint)(index + count - 8)), asciiCounts, aDeltas);

                            Unsafe.WriteUnaligned(ref destination, encoded.AsUInt64().ToScalar());
                            Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, count - 8), last.AsUInt64().ToScalar());
                        }
                        else if (count >= MinVectorRun)
                        {
                            // The same with four at a time.
                            ulong lastChars = Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref source, index + count - 4)));
                            var last = EncodeSingles(Vector128.CreateScalarUnsafe(lastChars).AsUInt16(), asciiCounts, aDeltas);

                            Unsafe.WriteUnaligned(ref destination, encoded.AsUInt32().ToScalar());
                            Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, count - 4), last.AsUInt32().ToScalar());
                        }
                        else
                        {
                            break;
                        }

                        index += count;
                        written += count;

                        // What stopped the run is likely to be a single char between two runs.
                        runs = true;
                        break;
                    }
                }
            }

            // One char at a time for a stretch, or for one char if that ended a run that was worth storing as a vector. Nothing here takes more than
            // 2 bytes per char, which the end of the stretch allows for.

            look = true;
            int end = index + Math.Min(Math.Min(length - index, runs ? 1 : ScalarStretch), (room - written) >> 1);

            if (end <= index)
                break;

            do
            {
                uint c = Unsafe.Add(ref source, index);

                if (c - 0x20 < asciiCount)
                {
                    Unsafe.Add(ref target, written++) = (byte)(c - AsciiDelta);
                }
                else if (c - aStart < aCount)
                {
                    Unsafe.Add(ref target, written++) = (byte)(c - aDelta);
                }
                else if (c - wideStart < wideCount)
                {
                    uint offset = c - wideSegment;
                    uint lead = offset / Radix;

                    Unsafe.Add(ref target, written) = (byte)(wideLead + lead);
                    Unsafe.Add(ref target, written + 1) = (byte)(offset - (lead * Radix) + TrailFloor);
                    written += 2;
                }
                else if (c - 0x09 < 2)
                {
                    Unsafe.Add(ref target, written++) = (byte)(c - ControlDelta);
                }
                else
                {
                    // The other pure ranges of the layout.

                    var ranges = tables.Ranges;
                    int r = 0;

                    for (; r < ranges.Length; r++)
                    {
                        ref var range = ref ranges[r];

                        if (c - range.Low < range.Count)
                        {
                            uint offset = c - range.Segment;

                            if (range.Bytes == 1)
                            {
                                Unsafe.Add(ref target, written++) = (byte)(range.Lead + offset);
                            }
                            else
                            {
                                uint lead = offset / Radix;

                                Unsafe.Add(ref target, written) = (byte)(range.Lead + lead);
                                Unsafe.Add(ref target, written + 1) = (byte)(offset - (lead * Radix) + TrailFloor);
                                written += 2;
                            }

                            break;
                        }
                    }

                    if (r < ranges.Length)
                    {
                        index++;
                        continue;
                    }

                    // Something that is not pure, or a surrogate pair.

                    int code = (int)c;
                    int units = 1;

                    if (c - 0xD800 < 0x400)
                    {
                        if (length - index < 2)
                            goto Done;

                        uint low = Unsafe.Add(ref source, index + 1);

                        if (low - 0xDC00 >= 0x400)
                            goto Done;

                        // A 1-byte char of a supplementary main window.
                        if (c == tables.SuppHigh && low - tables.SuppLow < tables.SuppCount)
                        {
                            Unsafe.Add(ref target, written++) = (byte)(tables.SuppFirstByte + low - tables.SuppLow);
                            index += 2;
                            continue;
                        }

                        code = 0x10000 + (int)((c - 0xD800) << 10) + (int)(low - 0xDC00);
                        units = 2;
                    }

                    if (!TEvents.Enabled)
                        goto Done;

                    int s = tables.FindSegmentIndex(code);

                    if (tables.Segments[s].Bytes != 2)
                        goto Done;

                    var next = Event(tables, ref state, code, ref source, index);

                    if (next is null)
                        goto Done;

                    uint eventOffset = (uint)(code - tables.Segments[s].Start);
                    uint eventLead = eventOffset / Radix;

                    Unsafe.Add(ref target, written) = (byte)(tables.Segments[s].Lead + eventLead);
                    Unsafe.Add(ref target, written + 1) = (byte)(eventOffset - (eventLead * Radix) + TrailFloor);
                    written += 2;
                    index += units;
                    state.Mark = index;

                    if (next != tables)
                    {
                        tables = next;
                        look = false;
                        goto Reload;
                    }

                    continue;
                }

                index++;
            }
            while (index < end);
        }

    Done:
        writtenRef = written;
        return index;
    }

    /// <summary>
    /// Encodes blocks of eight chars that are all pure singles of the single ranges (ASCII, A, B, tab, line feed and carriage return) or chars of
    /// the wide range, as long as they come. Returns whether it encoded any.
    /// </summary>
    /// <remarks>
    /// The chars come out as 16-bit words, lead byte first, of which only the first byte counts for a single; a table that is indexed with which of
    /// the eight are singles has the shuffle that closes the gaps.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool EncodeWideBlocks(Tables tables, ref ushort source, int length, ref int indexRef, ref byte target, int room, ref int writtenRef)
    {
        int index = indexRef;
        int written = writtenRef;

        var asciiStarts = Vector128.Create((ushort)0x20);
        var asciiCounts = Vector128.Create((ushort)tables.AsciiCount);
        var asciiDeltas = Vector128.Create((ushort)AsciiDelta);
        var aStarts = Vector128.Create((ushort)tables.AStart);
        var aCounts = Vector128.Create((ushort)tables.ACount);
        var aDeltas = Vector128.Create((ushort)(tables.AStart - tables.AFirstByte));
        var bStarts = Vector128.Create((ushort)tables.BStart);
        var bCounts = Vector128.Create((ushort)tables.BCount);
        var bDeltas = Vector128.Create((ushort)(tables.BStart - tables.BFirstByte));
        var wideStarts = Vector128.Create((ushort)tables.WideStart);
        var wideCounts = Vector128.Create((ushort)tables.WideCount);
        var wideSegments = Vector128.Create((ushort)tables.WideSegment);
        var wideLeads = Vector128.Create((ushort)tables.WideLead);
        var trailFloors = Vector128.Create((ushort)TrailFloor);
        var controlStarts = Vector128.Create((ushort)0x09);
        var controlCounts = Vector128.Create((ushort)2);
        var controlDeltas = Vector128.Create((ushort)ControlDelta);
        var carriageReturns = Vector128.Create((ushort)0x0D);

        ref byte controls = ref MemoryMarshal.GetArrayDataReference(WideTables.EncodeControls);
        ref byte tailControls = ref MemoryMarshal.GetArrayDataReference(WideTables.EncodeTailControls);

        while (length - index >= 8 && room - written >= 16)
        {
            var block = Vector128.LoadUnsafe(ref source, (nuint)index);
            var ascii = Vector128.LessThan(block - asciiStarts, asciiCounts);
            var inA = Vector128.LessThan(block - aStarts, aCounts);
            var inB = Vector128.LessThan(block - bStarts, bCounts);
            var isReturn = Vector128.Equals(block, carriageReturns);
            var singles = ascii | inA | inB | Vector128.LessThan(block - controlStarts, controlCounts) | isReturn;
            var wide = Vector128.LessThan(block - wideStarts, wideCounts);

            if ((singles | wide) != Vector128<ushort>.AllBitsSet)
                break;

            var offsets = block - wideSegments;
            var leads = DivideBy248(offsets);
            var trails = offsets - (leads << 8) + (leads << 3) + trailFloors;
            var pairs = (wideLeads + leads) | (trails << 8);
            var bytes = block - Vector128.ConditionalSelect(
                ascii, asciiDeltas, Vector128.ConditionalSelect(inA, aDeltas, Vector128.ConditionalSelect(inB, bDeltas, controlDeltas - isReturn)));
            var words = Vector128.ConditionalSelect(wide, pairs, bytes);

            uint layoutBits = Vector128.Narrow(singles, singles).ExtractMostSignificantBits() & 0xFF;
            int count = 16 - BitOperations.PopCount(layoutBits);
            ref byte destination = ref Unsafe.Add(ref target, written);

            if (layoutBits == 0)
            {
                words.AsByte().StoreUnsafe(ref destination);
            }
            else
            {
                // Between 8 and 15 bytes: the first eight, then the last eight (which overlap the first eight).
                var packed = Shuffle(words.AsByte(), Vector128.LoadUnsafe(ref controls, layoutBits * (nuint)16));
                var tail = Shuffle(packed, Vector128.LoadUnsafe(ref tailControls, (nuint)(count - 8) * 16));

                Unsafe.WriteUnaligned(ref destination, packed.AsUInt64().ToScalar());
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, count - 8), tail.AsUInt64().ToScalar());
            }

            index += 8;
            written += count;
        }

        bool any = index != indexRef;
        indexRef = index;
        writtenRef = written;
        return any;
    }

    /// <summary>
    /// Decodes sequences that are pure singles of the single ranges or 2-byte chars under the full lead bytes of the wide range, eight bytes at a
    /// time, as long as they come. Returns whether it decoded any.
    /// </summary>
    /// <remarks>
    /// Which bytes are lead bytes cannot be told from the bytes one by one, since a trail byte can have any value from 08. It does follow from the bytes
    /// that may be lead bytes as a whole: a byte that cannot be one ends its sequence either way, so the byte after it starts one. That makes the first
    /// byte of each run of possible lead bytes a lead byte, and so the third, the fifth...
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool DecodeWideBlocks(Tables tables, ref byte source, int length, ref int indexRef, ref ushort target, int room, ref int writtenRef)
    {
        int index = indexRef;
        int written = writtenRef;

        var asciiFirsts = Vector128.Create((byte)AsciiFirstByte);
        var asciiCounts = Vector128.Create((byte)tables.AsciiCount);
        var aFirsts = Vector128.Create((byte)tables.AFirstByte);
        var aCounts = Vector128.Create((byte)tables.ACount);
        var bFirsts = Vector128.Create((byte)tables.BFirstByte);
        var bCounts = Vector128.Create((byte)tables.BCount);
        var controlFirsts = Vector128.Create((byte)(0x09 - ControlDelta));
        var controlCounts = Vector128.Create((byte)2);
        var returnBytes = Vector128.Create((byte)CarriageReturnByte);
        var leadFirsts = Vector128.Create((byte)tables.WideFullLead);
        var trailFloors = Vector128.Create((byte)TrailFloor);
        var leadCounts = Vector128.Create((byte)tables.WideFullLeadCount);

        var asciiFirsts16 = Vector128.Create((ushort)AsciiFirstByte);
        var asciiCounts16 = Vector128.Create((ushort)tables.AsciiCount);
        var asciiDeltas = Vector128.Create((ushort)AsciiDelta);
        var aFirsts16 = Vector128.Create((ushort)tables.AFirstByte);
        var aCounts16 = Vector128.Create((ushort)tables.ACount);
        var aDeltas = Vector128.Create((ushort)(tables.AStart - tables.AFirstByte));
        var bFirsts16 = Vector128.Create((ushort)tables.BFirstByte);
        var bCounts16 = Vector128.Create((ushort)tables.BCount);
        var bDeltas = Vector128.Create((ushort)(tables.BStart - tables.BFirstByte));
        var controlDeltas = Vector128.Create((ushort)ControlDelta);
        var pairDeltas = Vector128.Create((ushort)tables.WidePairDelta);
        var returnUnits = Vector128.Create((ushort)CarriageReturnByte);

        ref byte counts = ref MemoryMarshal.GetArrayDataReference(WideTables.DecodeCounts);
        ref byte controls = ref MemoryMarshal.GetArrayDataReference(WideTables.DecodeControls);
        ref byte tailControls = ref MemoryMarshal.GetArrayDataReference(WideTables.DecodeTailControls);

        while (length - index >= 16)
        {
            // Look at 32 bytes if there are that many.

            var first = Vector128.LoadUnsafe(ref source, (nuint)index);
            uint leadBits = Vector128.LessThan(first - leadFirsts, leadCounts).ExtractMostSignificantBits();
            uint singleBits = (Vector128.LessThan(first - asciiFirsts, asciiCounts) | Vector128.LessThan(first - aFirsts, aCounts) |
                Vector128.LessThan(first - bFirsts, bCounts) | Vector128.LessThan(first - controlFirsts, controlCounts) |
                Vector128.Equals(first, returnBytes)).ExtractMostSignificantBits();
            uint zeroBits = Vector128.LessThan(first, trailFloors).ExtractMostSignificantBits();
            int valid = 16;

            if (length - index >= 32)
            {
                var second = Vector128.LoadUnsafe(ref source, (nuint)(index + 16));
                leadBits |= Vector128.LessThan(second - leadFirsts, leadCounts).ExtractMostSignificantBits() << 16;
                var secondSingles = Vector128.LessThan(second - asciiFirsts, asciiCounts) | Vector128.LessThan(second - aFirsts, aCounts) |
                    Vector128.LessThan(second - bFirsts, bCounts) | Vector128.LessThan(second - controlFirsts, controlCounts) |
                    Vector128.Equals(second, returnBytes);

                singleBits |= secondSingles.ExtractMostSignificantBits() << 16;
                zeroBits |= Vector128.LessThan(second, trailFloors).ExtractMostSignificantBits() << 16;
                valid = 32;
            }

            uint runStarts = leadBits & ~(leadBits << 1);
            uint evenRuns = leadBits & ~(leadBits + (runStarts & 0x55555555));
            uint leads = (evenRuns & 0x55555555) | (leadBits & ~evenRuns & 0xAAAAAAAA);
            uint trails = leads << 1;

            if ((leads & 0xFFFF) == 0x5555 && (zeroBits & 0xAAAA) == 0)
            {
                // Nothing but pairs in the first 16 bytes, and maybe in the next 16 as well.

                bool both = valid == 32 && (leads >> 16) == 0x5555 && (zeroBits & 0xAAAA0000) == 0;
                int pairs = both ? 16 : 8;

                if (room - written < pairs)
                    break;

                PairsToChars(first, pairDeltas).StoreUnsafe(ref target, (nuint)written);

                if (both)
                    PairsToChars(Vector128.LoadUnsafe(ref source, (nuint)(index + 16)), pairDeltas).StoreUnsafe(ref target, (nuint)(written + 8));

                index += pairs * 2;
                written += pairs;
                continue;
            }

            // Stop before a sequence that is not a single or a pair, and before a lead byte whose trail byte is below 08 (zeroBits marks the bytes
            // below 08, which are never trail bytes). The last byte that the masks cover
            // is left out, since a lead byte there takes a byte that they do not cover.
            uint stops = (~(leadBits | singleBits) & ~trails) | ((zeroBits & trails) >> 1);

            if (valid == 16)
                stops &= 0xFFFF;

            int limit = Math.Min(Math.Min(stops == 0 ? valid : BitOperations.TrailingZeroCount(stops), valid - 1), length - index - 8);
            int position = 0;

            while (position + 8 <= limit)
            {
                uint layoutBits = (leads >> position) & 0xFF;
                int count = Unsafe.Add(ref counts, layoutBits);

                if (room - written < count)
                    break;

                var data = Vector128.LoadUnsafe(ref source, (nuint)(index + position));
                var units = Shuffle(data, Vector128.LoadUnsafe(ref controls, layoutBits * (nuint)16)).AsUInt16();

                // A pair is its lead byte and trail byte, and the char is lead * 248 + trail plus a constant: the word minus 8 times its upper byte.
                var upper = Vector128.ShiftRightLogical(units, 8);
                var isPair = ~Vector128.Equals(upper, Vector128<ushort>.Zero);
                var isAscii = Vector128.LessThan(units - asciiFirsts16, asciiCounts16);
                var isA = Vector128.LessThan(units - aFirsts16, aCounts16);
                var isB = Vector128.LessThan(units - bFirsts16, bCounts16);
                var singleChars = units + Vector128.ConditionalSelect(
                    isAscii,
                    asciiDeltas,
                    Vector128.ConditionalSelect(isA, aDeltas, Vector128.ConditionalSelect(isB, bDeltas, controlDeltas - Vector128.Equals(units, returnUnits))));
                var chars = Vector128.ConditionalSelect(isPair, units - (upper << 3) + pairDeltas, singleChars);

                // Between four and eight chars: the first four, then the last four (which may overlap the first four).
                var tail = Shuffle(chars.AsByte(), Vector128.LoadUnsafe(ref tailControls, (nuint)(count - 4) * 16));
                ref byte destination = ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref target, written));

                Unsafe.WriteUnaligned(ref destination, chars.AsUInt64().ToScalar());
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, (count - 4) * sizeof(ushort)), tail.AsUInt64().ToScalar());

                written += count;

                // A lead byte in the last of the eight takes the byte after them with it.
                position += 8 + (int)(layoutBits >> 7);
            }

            index += position;

            if (position == 0 || limit < valid - 1)
                break;
        }

        bool any = index != indexRef;
        indexRef = index;
        writtenRef = written;
        return any;
    }

    /// <summary>
    /// Turns eight pairs of a lead and a trail byte of the wide range into their chars.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> PairsToChars(Vector128<byte> bytes, Vector128<ushort> pairDeltas)
    {
        var raw = bytes.AsUInt16();
        var words = (raw << 8) | Vector128.ShiftRightLogical(raw, 8);
        return words - (Vector128.ShiftRightLogical(words, 8) << 3) + pairDeltas;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> EncodeSingles(Vector128<ushort> chars, Vector128<ushort> asciiCounts, Vector128<ushort> aDeltas)
    {
        var ascii = Vector128.LessThan(chars - Vector128.Create((ushort)0x20), asciiCounts);
        var encoded = chars - Vector128.ConditionalSelect(ascii, Vector128.Create((ushort)AsciiDelta), aDeltas);
        return Vector128.Narrow(encoded, encoded);
    }

    /// <summary>
    /// Decodes pure chars of the layout that take 1 or 2 bytes (and the 1-byte chars of a supplementary main window), for as long as they come, and
    /// with <see cref="WithEvents"/> also the 2-byte chars of events. Returns the index of the first byte that it left alone.
    /// </summary>
    /// <remarks>
    /// The state is only used with <see cref="WithEvents"/>; with <see cref="NoEvents"/> it may be a null reference (for the initial state before
    /// anything changed it).
    /// </remarks>
    private static int DecodeRun<TEvents, TWide>(
        Tables tables, ref State state, ref byte source, int length, int index, ref ushort target, int room, ref int writtenRef)
        where TEvents : struct, IEvents
        where TWide : struct, IWide
    {
        int written = writtenRef;

        // Whether to look for a run to store as a vector first: not right after an event, which is a sign of short runs.
        bool look = true;

    Reload:
        ref ushort singles = ref MemoryMarshal.GetArrayDataReference(tables.Singles);
        ref uint pairs = ref MemoryMarshal.GetArrayDataReference(tables.Pairs);

        bool runs = false;

        while (true)
        {
            if (Vector128.IsHardwareAccelerated && look)
            {
                if (TWide.Enabled && tables.WideVectors && CanShuffle && length - index >= MinWideBytes)
                {
                    runs = DecodeWideBlocks(tables, ref source, length, ref index, ref target, room, ref written);
                }
                else
                {
                    // As in EncodeRun.

                    var asciiCounts = Vector128.Create((byte)tables.AsciiCount);
                    var aFirsts = Vector128.Create((byte)tables.AFirstByte);
                    var aCounts = Vector128.Create((byte)tables.ACount);
                    var wideAsciiCounts = Vector128.Create((ushort)tables.AsciiCount);
                    var aDeltas = Vector128.Create((ushort)(tables.AStart - tables.AFirstByte));
                    var asciiFirsts = Vector128.Create((byte)AsciiFirstByte);
                    int available = Math.Min(length - index, room - written);
                    runs = false;

                    while (true)
                    {
                        Vector128<byte> block;

                        if (available >= 16)
                        {
                            block = Vector128.LoadUnsafe(ref source, (nuint)index);
                        }
                        else if (available >= 4)
                        {
                            // The rest in two overlapping pieces of 8 or of 4, as in EncodeRun.

                            int piece = available >= 8 ? 8 : 4;
                            Vector128<byte> front;
                            Vector128<byte> back;

                            if (piece == 8)
                            {
                                front = Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, index))).AsByte();
                                back = Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, index + available - 8))).AsByte();
                            }
                            else
                            {
                                front = Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref source, index))).AsByte();
                                back = Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref source, index + available - 4))).AsByte();
                            }

                            var frontSingles = Vector128.LessThan(front - asciiFirsts, asciiCounts) | Vector128.LessThan(front - aFirsts, aCounts);
                            var backSingles = Vector128.LessThan(back - asciiFirsts, asciiCounts) | Vector128.LessThan(back - aFirsts, aCounts);
                            uint bits = (frontSingles & backSingles).ExtractMostSignificantBits();

                            if (bits == (1u << piece) - 1)
                            {
                                ref ushort output = ref Unsafe.Add(ref target, written);
                                var frontChars = DecodeSingles(Vector128.WidenLower(front), wideAsciiCounts, aDeltas);
                                var backChars = DecodeSingles(Vector128.WidenLower(back), wideAsciiCounts, aDeltas);

                                if (piece == 8)
                                {
                                    frontChars.StoreUnsafe(ref output);
                                    backChars.StoreUnsafe(ref output, (nuint)(available - 8));
                                }
                                else
                                {
                                    Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref output), frontChars.AsUInt64().ToScalar());
                                    ref byte backOutput = ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref output, available - 4));
                                    Unsafe.WriteUnaligned(ref backOutput, backChars.AsUInt64().ToScalar());
                                }

                                index += available;
                                written += available;
                                break;
                            }

                            if (piece != 8)
                                break;

                            block = front;
                        }
                        else
                        {
                            break;
                        }

                        // Zero bytes (the upper half of a block of 8) are in neither range.
                        var isSingle = Vector128.LessThan(block - asciiFirsts, asciiCounts) | Vector128.LessThan(block - aFirsts, aCounts);

                        ref ushort destination = ref Unsafe.Add(ref target, written);
                        var head = DecodeSingles(Vector128.WidenLower(block), wideAsciiCounts, aDeltas);

                        if (isSingle == Vector128<byte>.AllBitsSet)
                        {
                            head.StoreUnsafe(ref destination);
                            DecodeSingles(Vector128.WidenUpper(block), wideAsciiCounts, aDeltas).StoreUnsafe(ref destination, 8);
                            index += 16;
                            written += 16;
                            available -= 16;
                            continue;
                        }

                        int count = BitOperations.TrailingZeroCount(~isSingle.ExtractMostSignificantBits());

                        if (count >= 8)
                        {
                            ulong last = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, index + count - 8));

                            head.StoreUnsafe(ref destination);
                            DecodeSingles(Vector128.WidenLower(Vector128.CreateScalarUnsafe(last).AsByte()), wideAsciiCounts, aDeltas)
                                .StoreUnsafe(ref destination, (nuint)(count - 8));
                        }
                        else if (count >= MinVectorRun)
                        {
                            uint last = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref source, index + count - 4));
                            var tail = DecodeSingles(Vector128.WidenLower(Vector128.CreateScalarUnsafe(last).AsByte()), wideAsciiCounts, aDeltas);

                            Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref destination), head.AsUInt64().ToScalar());
                            Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref destination, count - 4)), tail.AsUInt64().ToScalar());
                        }
                        else
                        {
                            break;
                        }

                        index += count;
                        written += count;
                        runs = true;
                        break;
                    }
                }
            }

            // No sequence here but the 1-byte chars of a supplementary window gives more chars than it has bytes, so the end of the stretch takes care
            // of the room for the others; those check the room themselves and end the stretch.
            look = true;
            int end = index + Math.Min(Math.Min(length - index, runs ? 1 : ScalarStretch), room - written);

            if (end <= index)
                break;

            do
            {
                uint b = Unsafe.Add(ref source, index);
                uint c = Unsafe.Add(ref singles, b);

                if (c != 0)
                {
                    Unsafe.Add(ref target, written++) = (ushort)c;
                    index++;
                    continue;
                }

                if (b - tables.SuppFirstByte < tables.SuppCount)
                {
                    // A 1-byte char of a supplementary main window.

                    if (room - written < 2)
                        goto Done;

                    Unsafe.Add(ref target, written) = (ushort)tables.SuppHigh;
                    Unsafe.Add(ref target, written + 1) = (ushort)(tables.SuppLow + b - tables.SuppFirstByte);
                    written += 2;
                    index++;
                    break;
                }

                if (length - index < 2)
                    goto Done;

                // The trail bytes of the pure chars of a lead: the char of the first one, the first one and the number of them.
                uint pair = Unsafe.Add(ref pairs, b);
                uint trail = Unsafe.Add(ref source, index + 1) - ((pair >> 8) & 0xFF);

                if (trail < (pair & 0xFF))
                {
                    Unsafe.Add(ref target, written++) = (ushort)((pair >> 16) + trail);
                    index += 2;
                    continue;
                }

                if (!TEvents.Enabled)
                    goto Done;

                // A 2-byte char that may be an event.

                uint leadInfo = tables.Leads[b];
                uint offset = (uint)Unsafe.Add(ref source, index + 1) - TrailFloor;

                if (offset >= (leadInfo & 0xFF))
                    goto Done;

                int code = (int)((leadInfo >> 8) + offset);

                if (code > 0xFFFF && room - written < 2)
                    goto Done;

                var next = Event(tables, ref state, code, ref target, written);

                if (next is null)
                    goto Done;

                if (code > 0xFFFF)
                {
                    Unsafe.Add(ref target, written) = (ushort)(0xD800 + ((code - 0x10000) >> 10));
                    Unsafe.Add(ref target, written + 1) = (ushort)(0xDC00 + (code & 0x3FF));
                    written += 2;
                }
                else
                {
                    Unsafe.Add(ref target, written++) = (ushort)code;
                }

                index += 2;
                state.Mark = written;

                if (next != tables)
                {
                    tables = next;
                    look = false;
                    goto Reload;
                }

                // A supplementary char gave 2 chars for its 2 bytes, which the stretch allows for.
            }
            while (index < end);
        }

    Done:
        writtenRef = written;
        return index;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> DecodeSingles(Vector128<ushort> bytes, Vector128<ushort> asciiCounts, Vector128<ushort> aDeltas)
    {
        var ascii = Vector128.LessThan(bytes - Vector128.Create((ushort)AsciiFirstByte), asciiCounts);
        return bytes + Vector128.ConditionalSelect(ascii, Vector128.Create((ushort)AsciiDelta), aDeltas);
    }

    /// <summary>
    /// Applies a 2-byte char of the layout that is not pure to the state, if what it does to the state only depends on the layout and the char and was
    /// seen before, and the layout after it has its tables. Returns the tables of the layout after it (the same tables if the layout stays), or null
    /// to leave the char to the slow path.
    /// </summary>
    /// <param name="tables">The tables of the layout of the state.</param>
    /// <param name="state">The state.</param>
    /// <param name="c">The char (code point).</param>
    /// <param name="text">The text, where the chars since the state's mark are pure chars of the layout.</param>
    /// <param name="position">The index of the char in the text.</param>
    /// <remarks>
    /// In the general context, a char of another window uses up one of the confidence (it takes 2 bytes here, so it leaves the second window alone and
    /// the layout stays) or, at confidence 0, makes its window the main one (and the old main window the last one). In the CJK context, an ASCII letter
    /// or a kana char moves the slot to its kind. The layout after any of these only depends on the layout and the window or the kind. The state keeps
    /// no record of the layouts before here (see <see cref="SetLayout"/>), which only the slow path needs.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Tables? Event(Tables tables, ref State state, int c, ref ushort text, int position)
    {
        if (tables.Context is General)
        {
            int window = WindowOf(c, out _);

            if (window == NoWindow)
                return null;

            int confidence = state.Confidence;

            if (confidence < MaxConfidence)
                confidence += CountHot(tables.Hot, tables.HotCount, ref text, state.Mark, position, MaxConfidence - confidence);

            if (confidence > 0)
            {
                state.Confidence = confidence - 1;
                return tables;
            }

            // Only to a layout that has its tables: the run loops go on in it.
            var transition = tables.Transitions[SwitchTransitions + ((window >> 7) & (TransitionSlots - 1))];

            if (transition is null || transition.Window != window || transition.Target is not { IsTables: true } target)
                return null;

            var next = Unsafe.As<Tables>(target);

            state.Last = state.Hot;
            state.Hot = window;
            state.HotEnd = next.HotEnd;
            state.Confidence = 0;
            state.Layout = next;
            return next;
        }
        else
        {
            int slot = SlotOfChar(c);

            if (slot == NoWindow || slot == tables.Slot)
                return null;

            // The mark moves past the chars since it, so what they did to the pending window must be done here: a kana char clears it anyway, and
            // after an ASCII letter (which does not) the slow path looks.
            if (tables.Transitions[SlotTransitions + SlotIndex(slot)]?.Target is not { IsTables: true } target ||
                (slot is AsciiLetters && state.Pending != NoWindow))
            {
                return null;
            }

            var next = Unsafe.As<Tables>(target);

            state.Pending = NoWindow;
            state.Slot = slot;
            state.Layout = next;
            return next;
        }
    }

    /// <summary>
    /// Gets the kind of kana slot that a char moves the slot to in the CJK context, or NoWindow.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SlotOfChar(int c)
    {
        if ((uint)((c | 0x20) - 'a') < 26)
            return AsciiLetters;

        // Only the letters of a kana, not the marks and signs around them.
        if ((uint)(c - 0x3041) <= 0x3096 - 0x3041)
            return HiraganaStart;

        if ((uint)(c - 0x30A1) <= 0x30FA - 0x30A1)
            return KatakanaStart;

        return NoWindow;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SlotIndex(int slot) => slot is AsciiLetters ? 0 : slot is HiraganaStart ? 1 : 2;

    /// <summary>
    /// Counts the chars of the main window among the text from <paramref name="from"/> to <paramref name="to"/>, from the end and up to
    /// <paramref name="most"/>. The text there is pure chars of the layout, of which the main window chars are its 1-byte chars. Only for the general
    /// context.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountHot(int mainStart, int mainCount, ref ushort text, int from, int to, int most)
    {
        int found = 0;
        uint size = (uint)mainCount;

        if (mainStart <= 0xFFFF)
        {
            uint hot = (uint)mainStart;
            int i = to;

            // Eight at a time from the end: where the chars of the main window are rare, the text since the last change can be long.
            if (Vector128.IsHardwareAccelerated)
            {
                var hots = Vector128.Create((ushort)hot);
                var sizes = Vector128.Create((ushort)size);

                for (; i - from >= 8; i -= 8)
                {
                    var block = Vector128.LoadUnsafe(ref text, (nuint)(i - 8));
                    found += BitOperations.PopCount(Vector128.LessThan(block - hots, sizes).ExtractMostSignificantBits());

                    if (found >= most)
                        return most;
                }
            }

            for (i--; i >= from; i--)
            {
                if (Unsafe.Add(ref text, i) - hot < size && ++found == most)
                    break;
            }
        }
        else
        {
            // All chars of a supplementary window have the same high surrogate.
            uint high = (uint)(0xD800 + ((mainStart - 0x10000) >> 10));
            uint low = (uint)(0xDC00 + (mainStart & 0x3FF));

            for (int i = to - 1; i > from; i--)
            {
                if (Unsafe.Add(ref text, i) - low < size && Unsafe.Add(ref text, i - 1) == high && ++found == most)
                    break;
            }
        }

        return found;
    }

    /// <summary>
    /// Encodes one code point of any kind and updates the state. Returns the index of the char after it.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int EncodeSlow(ref State state, ref ushort source, int length, int index, ref byte target, int room, ref int writtenRef)
    {
        CatchUp(ref state, ref source, index);

        int c = Unsafe.Add(ref source, index);
        int units = 1;

        if ((uint)(c - 0xD800) < 0x400 && length - index >= 2)
        {
            int low = Unsafe.Add(ref source, index + 1);

            if ((uint)(low - 0xDC00) < 0x400)
            {
                c = 0x10000 + ((c - 0xD800) << 10) + (low - 0xDC00);
                units = 2;
            }
        }

        var segment = state.Layout.FindSegment(c);
        int bytes = segment.Bytes;
        int written = writtenRef;

        if (room - written < bytes)
            ThrowBytesTooSmall();

        int offset = c - segment.Start;

        for (int i = bytes - 1; i > 0; i--)
        {
            Unsafe.Add(ref target, written + i) = (byte)((offset % Radix) + TrailFloor);
            offset /= Radix;
        }

        Unsafe.Add(ref target, written) = (byte)(segment.Lead + offset);
        writtenRef = written + bytes;

        index += units;
        Advance(ref state, c, bytes);
        state.Mark = index;

        return index;
    }

    /// <summary>
    /// Decodes one sequence of any kind, checking it, and updates the state. Returns the index of the byte after it.
    /// </summary>
    /// <remarks>
    /// The origin is where the input of the call starts in the source, for the index that an error reports: 0, or the number of bytes in front that a
    /// streaming decoder held from the call before.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int DecodeSlow(ref State state, ref byte source, int length, int index, ref ushort target, int room, ref int writtenRef, int origin = 0)
    {
        int written = writtenRef;
        CatchUp(ref state, ref target, written);

        var layout = state.Layout;
        int lead = Unsafe.Add(ref source, index);
        int s = layout.SegmentOfLead(lead);

        // The checks in the order of the specification: the lead byte, then each trail byte as it is read, then the range.

        if (s < 0)
            ThrowInvalid(lead is 0 ? ZeroByte : UnusedLead, ref source, index, 1, origin);

        var segment = layout.Segments[s];
        int bytes = segment.Bytes;
        int offset = lead - segment.Lead;

        for (int i = 1; i < bytes; i++)
        {
            if (index + i >= length)
                ThrowInvalid(Truncated, ref source, index, i, origin);

            int trail = Unsafe.Add(ref source, index + i);

            if (trail is 0)
                ThrowInvalid(ZeroByte, ref source, index, i + 1, origin);

            if (trail < TrailFloor)
                ThrowInvalid(InvalidTrail, ref source, index, i + 1, origin);

            offset = (offset * Radix) + trail - TrailFloor;
        }

        int c = segment.Start + offset;

        if (c > segment.End)
            ThrowInvalid(PastRange, ref source, index, bytes, origin);

        if (c > 0xFFFF)
        {
            if (room - written < 2)
                ThrowCharsTooSmall();

            Unsafe.Add(ref target, written) = (ushort)(0xD800 + ((c - 0x10000) >> 10));
            Unsafe.Add(ref target, written + 1) = (ushort)(0xDC00 + (c & 0x3FF));
            written += 2;
        }
        else
        {
            if (room - written < 1)
                ThrowCharsTooSmall();

            // A pair of surrogates is one code point and is encoded as one. The chars before this one in the output end with a high surrogate only
            // if that was a code point of its own: the run loops never give a surrogate, and a supplementary char ends with a low one. Before the
            // first char of a chunk, the state says what the last chunk ended with.
            if ((uint)(c - 0xDC00) < 0x400 && (written > 0 ? (uint)(Unsafe.Add(ref target, written - 1) - 0xD800) < 0x400 : state.PreviousHigh))
                ThrowInvalid(SurrogatePair, ref source, index, bytes, origin);

            Unsafe.Add(ref target, written++) = (ushort)c;
        }

        writtenRef = written;
        Advance(ref state, c, bytes);
        state.Mark = written;

        return index + bytes;
    }

    /// <summary>
    /// Applies what the pure chars since the previous change of the state did to it: a char of the main window adds to the confidence in the general
    /// context, and a CJK char or a Hangul syllable clears the pending window in the wide ones.
    /// </summary>
    private static void CatchUp(ref State state, ref ushort text, int end)
    {
        int start = state.Mark;

        if (state.Context is General)
        {
            if (state.Confidence < MaxConfidence)
                state.Confidence += CountHot(state.Hot, state.HotEnd - state.Hot + 1, ref text, start, end, MaxConfidence - state.Confidence);
        }
        else if (state.Pending != NoWindow)
        {
            bool cjk = state.Context is Cjk;

            for (int i = end - 1; i >= start; i--)
            {
                uint c = Unsafe.Add(ref text, i);

                if (cjk ? c - 0x3000 < 0x100 || c - 0x3400 < 0xA000 - 0x3400 : c - 0xAC00 < 0xD7A4 - 0xAC00)
                {
                    state.Pending = NoWindow;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Updates the state for a char that was just encoded or decoded in the given number of bytes (section 7 of the specification).
    /// </summary>
    private static void Advance(ref State state, int c, int bytes)
    {
        if (c < 0x80)
        {
            if (state.Context is Cjk && (uint)((c | 0x20) - 'a') < 26 && state.Slot != AsciiLetters)
                MoveSlot(ref state, AsciiLetters);

            return;
        }

        if (c >= 0xAC00 && c <= 0xD7A3)
        {
            state.Pending = NoWindow;

            if (state.Context is not Hangul)
            {
                state.Context = Hangul;
                EnterWide(ref state, WideTransitions);
            }

            return;
        }

        if ((c >= 0x3040 && c < 0x3100) || (c >= 0x3400 && c < 0xA000) || (c >= 0x20000 && c < 0x40000) ||
            (c >= 0x3000 && c < 0x3040 && state.Context is not Hangul))
        {
            state.Pending = NoWindow;
            int slot = c >= 0x3041 && c <= 0x3096 ? HiraganaStart : c >= 0x30A1 && c <= 0x30FA ? KatakanaStart : NoWindow;

            if (state.Context is not Cjk)
            {
                state.Context = Cjk;
                state.Slot = slot != NoWindow ? slot : AsciiLetters;
                EnterWide(ref state, WideTransitions + 1 + SlotIndex(state.Slot));
            }
            else if (slot != NoWindow && slot != state.Slot)
            {
                MoveSlot(ref state, slot);
            }

            return;
        }

        // Fullwidth forms belong to the text around them in the wide contexts.
        if (state.Context is not General && c >= 0xFF00 && c <= 0xFFFF)
            return;

        int window = WindowOf(c, out int windowEnd);

        if (window == NoWindow)
            return;

        if (state.Context is General)
        {
            if (window == state.Hot)
            {
                state.Confidence = Math.Min(state.Confidence + 1, MaxConfidence);
                return;
            }

            // Another window: it uses up one of the confidence (and makes its window the last one if it took more than 2 bytes), or it becomes the
            // main window. The layout after it only depends on the layout and the window, so it is remembered on the layout.

            var from = state.Layout;
            bool stray = state.Confidence > 0;

            if (stray)
            {
                state.Confidence--;

                if (bytes <= 2)
                    return;

                state.Last = window;
            }
            else
            {
                state.Last = state.Hot;
                state.Hot = window;
                state.HotEnd = windowEnd;
            }

            int slot = (stray ? StrayTransitions : SwitchTransitions) + ((window >> 7) & (TransitionSlots - 1));

            if (from.GetTransition(slot) is { } known && known.Window == window && known.Target is { } target)
            {
                SetLayout(ref state, target);
            }
            else
            {
                var next = GetLayout(ref state);
                SetLayout(ref state, next);
                from.SetTransition(slot, new Transition(window, next));
            }

            return;
        }

        // A window in a wide context: leaving it for one char would make the next ideograph or syllable take 3 bytes, so that takes two in a row.

        if (bytes > 2)
            state.Last = window;

        if (state.Pending != window)
        {
            state.Pending = window;
            state.PendingCount = 0;
        }

        if (++state.PendingCount >= 2)
        {
            if (state.Hot != window)
                state.Last = state.Hot;
            else if (state.Last == window)
                state.Last = NoWindow;

            state.Hot = window;
            state.HotEnd = windowEnd;
            state.Context = General;
            state.Pending = NoWindow;
            state.Confidence = 0;
        }

        SetLayout(ref state, GetLayout(ref state));
    }

    /// <summary>
    /// Gets the layout after the state entered a wide context: from a general layout through what it remembers of that change, which also depends
    /// on the second window (a window that the general layout covers may not be covered in the wide one).
    /// </summary>
    private static void EnterWide(ref State state, int index)
    {
        var from = state.Layout;

        if (from.Context is not General)
        {
            SetLayout(ref state, GetLayout(ref state));
            return;
        }

        if (from.GetTransition(index) is { } known && known.Window == state.Last && known.Target is { } target)
        {
            SetLayout(ref state, target);
        }
        else
        {
            var next = GetLayout(ref state);
            SetLayout(ref state, next);
            from.SetTransition(index, new Transition(state.Last, next));
        }
    }

    /// <summary>
    /// Moves the kana slot in the CJK context, through what the layout remembers of that move.
    /// </summary>
    private static void MoveSlot(ref State state, int slot)
    {
        var from = state.Layout;
        int index = SlotTransitions + SlotIndex(slot);
        state.Slot = slot;

        if (from.GetTransition(index)?.Target is { } target)
        {
            SetLayout(ref state, target);
        }
        else
        {
            var next = GetLayout(ref state);
            SetLayout(ref state, next);
            from.SetTransition(index, new Transition(NoWindow, next));
        }
    }

    /// <summary>
    /// Gets the window of a char (section 4 of the specification), or <see cref="NoWindow"/> for a char that has none.
    /// </summary>
    private static int WindowOf(int c, out int end)
    {
        end = 0;

        if (c < 0xC0)
            return NoWindow;

        int start;

        if (c < 0x300)
        {
            start = 0xC0 + ((c - 0xC0) & ~0x7F);
            end = Math.Min(start + 0x7F, 0x2FF);
        }
        else if (c < 0x370)
        {
            (start, end) = (0x300, 0x36F);
        }
        else if (c < 0x380)
        {
            // The 16 code points in front of the Greek letters have no window.
            return NoWindow;
        }
        else if (c >= 0x500 && c < 0x600)
        {
            (start, end) = c < 0x530 ? (0x500, 0x52F) : c < 0x590 ? (0x530, 0x58F) : (0x590, 0x5FF);
        }
        else if ((c >= 0x2000 && c < 0x2100) || (c >= 0x3000 && c < 0x3100) || (c >= 0x3400 && c < 0xA000) || (c >= 0xAC00 && c < 0xE000) ||
            (c >= 0x20000 && c < 0x40000))
        {
            return NoWindow;
        }
        else
        {
            (start, end) = (c & ~0x7F, c | 0x7F);
        }

        return start;
    }

    /// <summary>
    /// Gets the layout of a state: from the cache, or else one of the last layouts of the state itself, or else a new one.
    /// </summary>
    private static Layout GetLayout(ref State state)
    {
        int context = state.Context;
        int slot = context is Cjk ? state.Slot : AsciiLetters;
        int hot = state.Hot;
        int last = state.Last;

        // A second window that only has chars with a place of their own in the layout without it changes nothing.
        if (last != NoWindow && Layout.Covers(context, slot, hot, last))
            last = NoWindow;

        int set = CacheSet(context, slot, hot, last);
        int first = set * CacheWays;

        for (int way = 0; way < CacheWays; way++)
        {
            var cached = _cache[first + way];

            if (cached is not null && cached.Is(context, slot, hot, last))
                return cached;
        }

        // A miss: the state's own last layouts, so that going back and forth between a few states never depends on the shared cache. A state keeps
        // them from its first miss on (ordinary text never misses once the cache is warm, and pays nothing for them).
        if (state.Layout is { } current && current.Is(context, slot, hot, last))
            return current;

        if (state.Recent is { } recent)
        {
            if (recent.Find(context, slot, hot, last) is { } found)
                return found;
        }
        else
        {
            state.Recent = new RecentLayouts();
        }

        // An empty way of the set, or else the ways in turn. Layouts are immutable (but for what they remember of the changes that lead away from
        // them and their tables, which are written a reference at a time), and a reference is written atomically, so threads can share the cache
        // without a lock. A race on the turn only picks another way.
        int index = first;

        while (index < first + CacheWays - 1 && _cache[index] is not null)
            index++;

        if (_cache[index] is not null)
            index = first + (_cacheTurns[set]++ & (CacheWays - 1));

        var layout = new Layout(context, slot, hot, last, index);
        _cache[index] = layout;
        return layout;
    }

    /// <summary>
    /// Makes a layout the layout of the state, and keeps the layout before it with the state if the state keeps its last layouts.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetLayout(ref State state, Layout layout)
    {
        state.Recent?.Add(state.Layout, layout);
        state.Layout = layout;
    }

    /// <summary>
    /// Gets the set of the cache that a layout goes in.
    /// </summary>
    private static int CacheSet(int context, int slot, int hot, int last)
    {
        uint hash = (uint)context;
        hash = (hash * 0x9E3779B1) + (uint)slot;
        hash = (hash * 0x9E3779B1) + (uint)hot;
        hash = (hash * 0x9E3779B1) + (uint)last;
        hash ^= hash >> 15;
        hash *= 0x85EBCA77;
        hash ^= hash >> 13;

        return (int)(hash & ((CacheSize / CacheWays) - 1));
    }

    private static State CreateInitialState()
    {
        var state = new State {
            Context = General,
            Slot = AsciiLetters,
            Hot = 0xC0,
            HotEnd = 0x13F,
            Last = NoWindow,
            Pending = NoWindow,
        };

        // The run loops of a whole value start in the initial layout before there is a state, so it has its tables from the start. Building it
        // was a miss of the cache, which made the state keep its last layouts: every copy of the initial state would share them.
        state.Layout = GetLayout(ref state).BuildTables();
        state.Recent = null;
        return state;
    }

    /// <summary>
    /// Gets a value indicating whether <see cref="Shuffle"/> can be used: SSSE3 on x86 and x64, AdvSimd on ARM64.
    /// </summary>
    private static bool CanShuffle
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Ssse3.IsSupported || AdvSimd.Arm64.IsSupported;
    }

    /// <summary>
    /// Picks bytes by index. An index with its high bit set gives zero (on both kinds of hardware: the tables only use indices below 16 and 0x80).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Shuffle(Vector128<byte> bytes, Vector128<byte> indices)
        => Ssse3.IsSupported ? Ssse3.Shuffle(bytes, indices) : AdvSimd.Arm64.VectorTableLookup(bytes, indices);

    /// <summary>
    /// Divides each 16-bit lane by 248, exactly for every 16-bit value: with a multiply-high where the hardware has one, else with shifts only.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> DivideBy248(Vector128<ushort> values)
    {
        if (Sse2.IsSupported)
            return DivideBy248Sse2(values);

        if (AdvSimd.Arm64.IsSupported)
            return DivideBy248AdvSimd(values);

        return DivideBy248Shifts(values);
    }

    /// <summary>
    /// <see cref="DivideBy248"/> on x86 and x64 (SSE2): a multiply-high of the 16-bit lanes, then a shift.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> DivideBy248Sse2(Vector128<ushort> values)
    {
        var products = Sse2.MultiplyHigh(Sse2.ShiftRightLogical(values, 3), Vector128.Create(DivideBy248Multiplier));
        return Sse2.ShiftRightLogical(products, 4);
    }

    /// <summary>
    /// <see cref="DivideBy248"/> on ARM64 (AdvSimd): a widening multiply of each half, the high halves of the products narrowed back, then a shift.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> DivideBy248AdvSimd(Vector128<ushort> values)
    {
        var eighths = AdvSimd.ShiftRightLogical(values, 3);
        var multipliers = Vector128.Create(DivideBy248Multiplier);
        var lower = AdvSimd.MultiplyWideningLower(eighths.GetLower(), multipliers.GetLower());
        var upper = AdvSimd.MultiplyWideningUpper(eighths, multipliers);
        var products = AdvSimd.ShiftRightLogicalNarrowingUpper(AdvSimd.ShiftRightLogicalNarrowingLower(lower, 16), upper, 16);
        return AdvSimd.ShiftRightLogical(products, 4);
    }

    /// <summary>
    /// <see cref="DivideBy248"/> with shifts and additions only, for hardware without a 16-bit multiply-high.
    /// </summary>
    /// <remarks>
    /// With h = value >> 8 and t = 8 * h + (value &amp; 0xFF), value / 248 is h + t / 248, and t / 248 is ((t >> 3) + 1) * 33 >> 10 for t below 2,304;
    /// t is at most 8 * 255 + 255 = 2,295.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> DivideBy248Shifts(Vector128<ushort> values)
    {
        var highs = Vector128.ShiftRightLogical(values, 8);
        var eighths = Vector128.ShiftRightLogical((highs << 3) + (values & Vector128.Create((ushort)0xFF)), 3) + Vector128<ushort>.One;
        return highs + Vector128.ShiftRightLogical((eighths << 5) + eighths, 10);
    }

    [DoesNotReturn]
    private static void ThrowBytesTooSmall() => throw new ArgumentException("The value is too short for the encoded bytes.", "bytes");

    [DoesNotReturn]
    private static void ThrowLowAfterFlushedHigh() => throw new EncoderFallbackException(
        "The value is a low surrogate after a high surrogate that a flush wrote as an unpaired surrogate; the two would not be a value.");

    [DoesNotReturn]
    private static void ThrowCharsTooSmall() => throw new ArgumentException("The value is too short for the decoded chars.", "chars");

    /// <summary>
    /// Throws for bytes that are not valid Steno data. The reason is one of the reasons of the specification (section 10.1), which the message names.
    /// </summary>
    /// <remarks>
    /// The exception has the bytes of the code point read up to the defect as <see cref="DecoderFallbackException.BytesUnknown"/>, and where the
    /// code point starts in the input of the call as <see cref="DecoderFallbackException.Index"/>: negative for a code point that starts with bytes
    /// that a streaming decoder held from the call before, as in UTF8Encoding.
    /// </remarks>
    [DoesNotReturn]
    private static void ThrowInvalid(string reason, ref byte source, int index, int count, int origin)
    {
        string problem = reason switch {
            ZeroByte => "a byte 00",
            InvalidTrail => "a byte below 08 where a trail byte must be",
            UnusedLead => "a lead byte that the layout of the state does not use",
            Truncated => "the bytes end in the middle of a code point",
            PastRange => "a code point beyond the last code point of its range",
            _ => "a low surrogate code point directly after a high surrogate code point",
        };

        byte[] unknown = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref source, index), count).ToArray();
        int position = index - origin;
        string where = position >= 0 ? $"at byte {position}" : $"{-position} bytes before the start of this input";

        throw new DecoderFallbackException(
            $"The bytes are not valid Steno data ({reason}): {problem}, in the code point that starts {where}.", unknown, position);
    }

    internal struct State
    {
        public Layout Layout;

        // The last layouts that the state had before this one, once it missed the cache (see GetLayout), or null.
        public RecentLayouts? Recent;

        public int Context;
        public int Slot;
        public int Hot;
        public int HotEnd;

        // The second window (FORMAT.md names the fields: Hot is the main window, Last the second window), as it is: what it does to the layout
        // depends on the context.
        public int Last;

        public int Confidence;
        public int Pending;
        public int PendingCount;

        // Where the state last changed: the index of the char after the one that changed it, in the text (the input when encoding, the output when
        // decoding). Between the calls of the streaming encoder and decoder it is 0, and the state is up to date.
        public int Mark;

        // Whether the last code point that the decoder wrote, in an earlier chunk, is a high surrogate code point (section 10.1, rule 5).
        public bool PreviousHigh;
    }

    /// <summary>
    /// The two layouts that a state had before its current one, which <see cref="GetLayout"/> looks at when the cache misses.
    /// </summary>
    /// <remarks>
    /// A copy of a state shares them (the byte count of a streaming encoder works on a copy of its state). That does no harm: they only ever give a
    /// layout that is the layout of the key asked for.
    /// </remarks>
    internal sealed class RecentLayouts
    {
        private Layout? _previous;
        private Layout? _beforePrevious;

        /// <summary>
        /// Records a change of the layout of the state from one layout to another.
        /// </summary>
        public void Add(Layout from, Layout to)
        {
            if (to == from)
                return;

            if (to != _previous)
                _beforePrevious = _previous;

            _previous = from;
        }

        /// <summary>
        /// Gets the one of the layouts that is the layout of the given key, or null.
        /// </summary>
        public Layout? Find(int context, int slot, int hot, int last) =>
            _previous is { } previous && previous.Is(context, slot, hot, last)
                ? previous
                : _beforePrevious is { } beforePrevious && beforePrevious.Is(context, slot, hot, last) ? beforePrevious : null;
    }

    /// <summary>
    /// Whether a run loop handles events. The first run of a call has no state for it, and its loop is cheaper to get into without that code.
    /// </summary>
    private interface IEvents
    {
        static abstract bool Enabled { get; }
    }

    private readonly struct WithEvents : IEvents
    {
        public static bool Enabled => true;
    }

    private readonly struct NoEvents : IEvents
    {
        public static bool Enabled => false;
    }

    /// <summary>
    /// Whether a run loop has the wide blocks of the wide contexts.
    /// </summary>
    private interface IWide
    {
        static abstract bool Enabled { get; }
    }

    private readonly struct Wide : IWide
    {
        public static bool Enabled => true;
    }

    private readonly struct NoWide : IWide
    {
        public static bool Enabled => false;
    }

    /// <summary>
    /// Shuffle tables for the wide blocks. Every index in them is below 16 or 0x80, where Ssse3.Shuffle and AdvSimd.Arm64.VectorTableLookup agree.
    /// </summary>
    private static class WideTables
    {
        // Decoding, per layout of eight bytes (a bit for each lead byte, which takes the byte after it): the number of chars (4 to 8), the shuffle
        // that turns the bytes into 16-bit words (lead byte in the upper byte, nothing there for a single), and per number of chars above four the
        // shuffle that moves the last four chars to the front.
        public static readonly byte[] DecodeCounts = new byte[256];
        public static readonly byte[] DecodeControls = new byte[256 * 16];
        public static readonly byte[] DecodeTailControls = new byte[5 * 16];

        // Encoding, per set of the eight chars that are singles: the shuffle that closes the gaps between their 16-bit words, and per number of bytes
        // above eight the shuffle that moves the last eight bytes to the front.
        public static readonly byte[] EncodeControls = new byte[256 * 16];
        public static readonly byte[] EncodeTailControls = new byte[9 * 16];

        static WideTables()
        {
            const byte Zero = 0x80;

            for (int layout = 0; layout < 256; layout++)
            {
                var control = DecodeControls.AsSpan(layout * 16, 16);
                control.Fill(Zero);

                int position = 0;
                int count = 0;

                while (position < 8)
                {
                    if ((layout & (1 << position)) is not 0)
                    {
                        control[count * 2] = (byte)(position + 1);
                        control[(count * 2) + 1] = (byte)position;
                        position += 2;
                    }
                    else
                    {
                        control[count * 2] = (byte)position;
                        position++;
                    }

                    count++;
                }

                DecodeCounts[layout] = (byte)count;

                var encode = EncodeControls.AsSpan(layout * 16, 16);
                encode.Fill(Zero);
                int bytes = 0;

                for (int i = 0; i < 8; i++)
                {
                    encode[bytes++] = (byte)(i * 2);

                    if ((layout & (1 << i)) is 0)
                        encode[bytes++] = (byte)((i * 2) + 1);
                }
            }

            for (int extra = 0; extra <= 4; extra++)
            {
                var control = DecodeTailControls.AsSpan(extra * 16, 16);
                control.Fill(Zero);

                for (int i = 0; i < 8; i++)
                    control[i] = (byte)((extra * 2) + i);
            }

            for (int extra = 0; extra <= 8; extra++)
            {
                var control = EncodeTailControls.AsSpan(extra * 16, 16);
                control.Fill(Zero);

                for (int i = 0; i < 8; i++)
                    control[i] = (byte)(extra + i);
            }
        }
    }

    /// <summary>
    /// Where a char of a window leads from a layout of the general context, or a move of the kana slot from a layout of the CJK context, or entering a
    /// wide context from a layout of the general context (where <see cref="Window"/> is the second window of the state).
    /// </summary>
    /// <remarks>
    /// It names the layout that it leads to by its slot in the cache and its id, and does not keep it alive (see <see cref="CacheSize"/>):
    /// <see cref="Target"/> finds it there while the cache still has it, and otherwise the slow path gets it again.
    /// </remarks>
    internal sealed class Transition(int window, Layout next)
    {
        public readonly int Window = window;

        private readonly int _index = next.CacheIndex;
        private readonly long _id = next.Id;

        /// <summary>
        /// Gets the layout that the change leads to, or <see langword="null"/> if the cache no longer has it.
        /// </summary>
        public Layout? Target
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                var layout = _cache[_index];
                return layout is not null && layout.Id == _id ? layout : null;
            }
        }
    }

    /// <summary>
    /// A range of pure chars of a layout that takes 1 or 2 bytes per char: the chars from <see cref="Low"/>, in a segment that starts at
    /// <see cref="Segment"/> and whose first lead byte is <see cref="Lead"/>.
    /// </summary>
    internal readonly record struct FastRange(uint Low, uint Count, uint Segment, uint Lead, int Bytes);

    /// <summary>
    /// A range of a layout (section 6.4 of the specification): its first and last char, its first lead byte and its size in bytes. While a layout is
    /// built, a piece (section 6.2), with no lead byte.
    /// </summary>
    /// <remarks>
    /// Not a record struct: its constructor must inline into the code that builds layouts, which writes many of them in a row.
    /// </remarks>
    [method: MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly struct Segment(int start, int end, int lead, int bytes)
    {
        public int Start { get; init; } = start;

        public int End { get; init; } = end;

        public int Lead { get; init; } = lead;

        public int Bytes { get; init; } = bytes;
    }

    /// <summary>
    /// The layout of one state: its ranges, which are enough to encode or decode any one char, and what it remembers of the changes that lead away
    /// from it. The tables that the run loops need are built on demand, as a <see cref="Tables"/>: a twin of the layout with the same key and id,
    /// which takes its place in the cache.
    /// </summary>
    internal class Layout
    {
        // The most pieces of any layout (15, in the CJK context), and the most ranges, with room to spare: pieces have at most 2 * 16 ends, and
        // with the cuts of section 6.3 and the ends of the code space that makes at most 35 runs between them.
        private const int MaxPieces = 16;
        private const int MaxSegments = 40;

        // What the layout is for.
        public readonly int Context;
        public readonly int Slot;
        public readonly int Hot;
        public readonly int HotEnd;
        public readonly int Last;

        // The number of chars of the main window.
        public readonly int HotCount;

        // Whether the run loops have events to handle here: in the general and the CJK context.
        public readonly bool HasEvents;

        // Whether this is a Tables (a flag in the object itself: the events of the run loops test it with no type check).
        public readonly bool IsTables;

        public readonly Segment[] Segments;

        // The slot of the cache that the layout was made for, and a number that no other layout has (so that a transition can tell that the slot
        // still has the layout it leads to with one comparison).
        public readonly int CacheIndex;
        public readonly long Id;

        // The tables of the run loops once built (the twin, or the tables themselves), and how many chars took the slow path in the layout until
        // then (a race only loses a count).
        public Tables? Tables;
        private int _uses;

        // The first byte after the lead bytes of the layout (all of 01 up to it are lead bytes).
        private readonly int _leadEnd;

        // Where the chars of a window lead from here in the general context (by a hash of the window, StrayTransitions and SwitchTransitions on,
        // and entering a wide context, WideTransitions on), or the moves of the kana slot in the CJK context (by SlotIndex, SlotTransitions on).
        // Filled in by the slow path, made when first needed.
        private Transition?[]? _transitions;

        public Layout(int context, int slot, int hot, int last, int cacheIndex)
        {
            Id = Interlocked.Increment(ref _layoutCount);
            CacheIndex = cacheIndex;
            Context = context;
            Slot = slot;
            Hot = hot;
            Last = last;
            HasEvents = context is not Hangul;

            WindowOf(hot, out int hotEnd);
            HotEnd = hotEnd;
            HotCount = hotEnd - hot + 1;

            Span<Segment> pieces = stackalloc Segment[MaxPieces];
            int pieceCount = Compose(context, slot, hot, hotEnd, last, pieces);

            Span<Segment> segments = stackalloc Segment[MaxSegments];
            int count = Resolve(pieces[..pieceCount], segments);

            int nextLead = 1;

            for (int s = 0; s < count; s++)
            {
                var segment = segments[s];
                int leads = LeadCount(segment.Start, segment.End, segment.Bytes);

                // Every layout needs at most 254 lead bytes, 01..FE, so FF is never a lead byte (FORMAT.md, section 13.6, which rests on building every
                // layout). A layout that needs more is a defect of this code, not of the input.
                if (nextLead + leads > 255)
                    throw new InvalidOperationException($"The layout for context {context}, slot {slot:X}, hot {hot:X}, last {last:X} does not fit.");

                segments[s] = segment with { Lead = nextLead };
                nextLead += leads;
            }

            Segments = segments[..count].ToArray();
            _leadEnd = nextLead;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Layout"/> class as the twin of a layout that gets its tables (the base of
        /// <see cref="Tables"/>): the same key, id, ranges and transitions.
        /// </summary>
        /// <param name="layout">The layout.</param>
        protected Layout(Layout layout)
        {
            Context = layout.Context;
            Slot = layout.Slot;
            Hot = layout.Hot;
            HotEnd = layout.HotEnd;
            HotCount = layout.HotCount;
            Last = layout.Last;
            HasEvents = layout.HasEvents;
            Segments = layout.Segments;
            CacheIndex = layout.CacheIndex;
            Id = layout.Id;
            _leadEnd = layout._leadEnd;
            _transitions = layout.EnsureTransitions();
            IsTables = true;
            Tables = (Tables)this;
        }

        /// <summary>
        /// Gets whether this is the layout of the given key.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Is(int context, int slot, int hot, int last) => Hot == hot && Last == last && Context == context && Slot == slot;

        /// <summary>
        /// Gets what a layout remembers of a change that leads away from it, or null.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Transition? GetTransition(int index) => _transitions is { } transitions ? transitions[index] : null;

        /// <summary>
        /// Remembers a change that leads away from the layout.
        /// </summary>
        public void SetTransition(int index, Transition transition) => EnsureTransitions()[index] = transition;

        /// <summary>
        /// Gets the transitions of the layout, made if needed (once: its twin shares the array).
        /// </summary>
        protected Transition?[] EnsureTransitions()
        {
            if (_transitions is { } transitions)
                return transitions;

            transitions = new Transition?[Context is General ? GeneralTransitionCount : 3];
            return Interlocked.CompareExchange(ref _transitions, transitions, null) ?? transitions;
        }

        /// <summary>
        /// Counts a char that took the slow path in the layout, and builds the tables once enough did. Returns the tables if they are built.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public Tables? CountUse() => ++_uses < TablesAfterUses ? null : BuildTables();

        /// <summary>
        /// Builds the tables of the run loops. Two threads may both build them; both are the same, and either one is kept.
        /// </summary>
        /// <remarks>
        /// The tables take the place of the layout in the cache, so that a transition to the layout finds them with no step between: the run loops
        /// go on from an event with no more loads than they would need with the tables in the layout itself.
        /// </remarks>
        public Tables BuildTables()
        {
            var tables = new Tables(this);
            Tables = tables;
            Interlocked.CompareExchange(ref _cache[CacheIndex], tables, this);
            return tables;
        }

        /// <summary>
        /// Gets the range that holds a char: from the tables if the layout has them.
        /// </summary>
        public Segment FindSegment(int c) => Segments[Tables is { } tables ? tables.FindSegmentIndex(c) : FindSegment(Segments, c)];

        /// <summary>
        /// Gets the index of the range that a lead byte belongs to, or -1 for 00 and for an unused lead byte: from the tables if the layout has them.
        /// </summary>
        public int SegmentOfLead(int lead)
        {
            if (Tables is { } tables)
                return tables.LeadSegments[lead];

            if ((uint)(lead - 1) >= (uint)(_leadEnd - 1))
                return -1;

            var segments = Segments;
            int low = 0;
            int high = segments.Length - 1;

            while (low < high)
            {
                int middle = (low + high + 1) >> 1;

                if (segments[middle].Lead <= lead)
                    low = middle;
                else
                    high = middle - 1;
            }

            return low;
        }

        /// <summary>
        /// Gets whether every char of a window has a place of its own in the layout of the key without a second window (from a piece other than that
        /// of the second window), so that the window as the second window changes nothing.
        /// </summary>
        public static bool Covers(int context, int slot, int hot, int window)
        {
            WindowOf(hot, out int hotEnd);
            WindowOf(window, out int end);

            Span<Segment> pieces = stackalloc Segment[MaxPieces];
            pieces = pieces[..Compose(context, slot, hot, hotEnd, NoWindow, pieces)];

            // Walk from the start of the window through the pieces that hold the next char, as far as they reach.
            int next = window;
            bool moved = true;

            while (moved)
            {
                moved = false;

                foreach (var piece in pieces)
                {
                    if (piece.Start <= next && next <= piece.End)
                    {
                        next = piece.End + 1;
                        moved = true;

                        if (next > end)
                            return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Gets the index of the range that holds a char, by a binary search.
        /// </summary>
        public static int FindSegment(Segment[] segments, int c)
        {
            int low = 0;
            int high = segments.Length - 1;

            while (low < high)
            {
                int middle = (low + high + 1) >> 1;

                if (segments[middle].Start <= c)
                    low = middle;
                else
                    high = middle - 1;
            }

            return low;
        }

        private static int LeadCount(int start, int end, int bytes)
        {
            int perLead = 1;

            for (int i = 1; i < bytes; i++)
                perLead *= Radix;

            return (end - start + perLead) / perLead;
        }

        /// <summary>
        /// Writes the pieces of the layout (section 6.2 of the specification), to be laid over each other in this order. Returns their number.
        /// </summary>
        private static int Compose(int context, int slot, int hot, int hotEnd, int last, Span<Segment> pieces)
        {
            bool kana = context is Cjk && slot != AsciiLetters;
            int count = 0;

            // The chars next to the main window come first, so that everything with a place of its own goes over them.
            if (context is General)
            {
                Add(pieces, ref count, Math.Max(0x80, hot - Radix), hot - 1, 2);

                if (hotEnd < 0x10FFFF)
                    Add(pieces, ref count, hotEnd + 1, Math.Min(0x10FFFF, hotEnd + Radix), 2);
            }

            Add(pieces, ref count, 0x00, 0x1F, 2);
            Add(pieces, ref count, 0x09, 0x0A, 1);
            Add(pieces, ref count, 0x0D, 0x0D, 1);

            if (kana)
            {
                Add(pieces, ref count, 0x20, 0x40, 1);
                Add(pieces, ref count, 0x41, 0x7E, 2);
            }
            else
            {
                Add(pieces, ref count, 0x20, 0x7E, 1);
            }

            Add(pieces, ref count, 0x7F, 0xBF, 2);

            if (context is not Cjk)
                Add(pieces, ref count, 0x80, NearZoneEnd, 2);

            Add(pieces, ref count, 0x2000, PunctuationEnd, 2);

            if (last != NoWindow)
            {
                WindowOf(last, out int lastEnd);
                Add(pieces, ref count, last, lastEnd, 2);
            }

            switch (context)
            {
                case General:
                    Add(pieces, ref count, hot, hotEnd, 2);
                    Add(pieces, ref count, hot, hotEnd, 1);
                    break;

                case Hangul:
                    Add(pieces, ref count, hot, hotEnd, 2);
                    Add(pieces, ref count, 0x3000, 0x303F, 2);
                    Add(pieces, ref count, 0xAC00, 0xD7A3, 2);
                    Add(pieces, ref count, 0xFF00, 0xFFFF, 2);
                    break;

                case Cjk:
                    Add(pieces, ref count, hot, hotEnd, 2);
                    Add(pieces, ref count, 0x3000, 0x300F, 1);
                    Add(pieces, ref count, 0x3010, 0x30FF, 2);

                    if (slot is HiraganaStart)
                        Add(pieces, ref count, 0x3041, 0x3096, 1);
                    else if (slot is KatakanaStart)
                        Add(pieces, ref count, 0x30A1, 0x30FC, 1);

                    Add(pieces, ref count, 0x4E00, 0x9FFF, 2);
                    Add(pieces, ref count, 0xFF00, 0xFFFF, 2);

                    if (!kana)
                        Add(pieces, ref count, 0xFF00, 0xFF1F, 1);

                    break;
            }

            return count;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void Add(Span<Segment> pieces, ref int count, int start, int end, int bytes) => pieces[count++] = new(start, end, 0, bytes);
        }

        /// <summary>
        /// Lays the pieces over each other, gives the stretches between them their size (section 6.3 of the specification) and joins runs of the same
        /// size (section 6.4). Returns the number of ranges.
        /// </summary>
        /// <remarks>
        /// One sweep over the ends of the pieces and the cuts of section 6.3, in order, with the set of the pieces that hold the chars from each end
        /// to the next: the last of them gives the size, or none does and the chars are part of a stretch. Runs of a stretch join up to a cut.
        /// </remarks>
        private static int Resolve(ReadOnlySpan<Segment> pieces, Span<Segment> output)
        {
            const int LowCut = 0x20000 - (Radix * Radix);
            const int HighCut = 0x20000;

            // An event is a char in the upper bits and what happens there in the low 6: a piece starts (32 + its index), a piece ends before it (its
            // index), or a cut (63, which changes no piece). The end of the code space is the last event.
            const int Shift = 6;
            const int Cut = 63;

            Span<int> events = stackalloc int[(2 * MaxPieces) + 3];
            int eventCount = 0;

            for (int p = 0; p < pieces.Length; p++)
            {
                events[eventCount++] = (pieces[p].Start << Shift) | 32 | p;
                events[eventCount++] = ((pieces[p].End + 1) << Shift) | p;
            }

            events[eventCount++] = (LowCut << Shift) | Cut;
            events[eventCount++] = (HighCut << Shift) | Cut;
            events[eventCount++] = (0x110000 << Shift) | Cut;
            events = events[..eventCount];

            // Insertion sort: the pieces and the cuts come mostly in order.
            for (int i = 1; i < eventCount; i++)
            {
                int value = events[i];
                int j = i - 1;

                while (j >= 0 && events[j] > value)
                {
                    events[j + 1] = events[j];
                    j--;
                }

                events[j + 1] = value;
            }

            int count = 0;
            uint held = 0;
            int e = 0;

            // The run from start to the next event, and the size of the stretch that it is part of, which starts at stretchStart (or -1 for none).
            int start = 0;
            int stretchStart = -1;

            while (true)
            {
                // Apply the events at the start of the run.
                while (e < eventCount && events[e] >> Shift == start)
                {
                    int what = events[e++] & ((1 << Shift) - 1);

                    if (what is not Cut)
                        held = what >= 32 ? held | (1u << (what - 32)) : held & ~(1u << what);
                }

                int next = e < eventCount ? events[e] >> Shift : 0x110000;
                int bytes = held is 0 ? 0 : pieces[31 - BitOperations.LeadingZeroCount(held)].Bytes;

                if (bytes is 0)
                {
                    if (stretchStart < 0)
                        stretchStart = start;

                    // The stretch goes on to the next run unless a piece or a cut starts there.
                    if (next < 0x110000 && next is not LowCut and not HighCut && IsStretchAt(events, e, next, held, Shift))
                    {
                        start = next;
                        continue;
                    }

                    int length = next - stretchStart;
                    Append(output, ref count, stretchStart, next - 1, length <= Radix ? 2 : length <= Radix * Radix ? 3 : 4);
                    stretchStart = -1;
                }
                else
                {
                    Append(output, ref count, start, next - 1, bytes);
                }

                if (next >= 0x110000)
                    return count;

                start = next;
            }

            // Whether no piece holds the chars from the given event on: the pieces held before it, less the ones that end there, plus the ones that
            // start there.
            static bool IsStretchAt(ReadOnlySpan<int> events, int e, int at, uint held, int shift)
            {
                for (; e < events.Length && events[e] >> shift == at; e++)
                {
                    int what = events[e] & ((1 << shift) - 1);

                    if (what >= 32 && what is not Cut)
                        return false;

                    if (what < 32)
                        held &= ~(1u << what);
                }

                return held is 0;
            }

            // Neighbours of the same size share lead bytes.
            static void Append(Span<Segment> output, ref int count, int start, int end, int bytes)
            {
                if (count > 0 && output[count - 1].Bytes == bytes)
                    output[count - 1] = output[count - 1] with { End = end };
                else
                    output[count++] = new(start, end, 0, bytes);
            }
        }
    }

    /// <summary>
    /// A layout with what the run loops need of it: tables by byte and by page of chars, and the ranges that they check first. It is the twin of the
    /// layout that it was built for, which refers to it; it holds no other layout.
    /// </summary>
    internal sealed class Tables : Layout
    {
        // The transitions of the layout (the same array as the twin's), for the events of the run loops.
        public readonly Transition?[] Transitions;

        // For each 128 chars of the BMP, the segment that has all of them, or MixedPage.
        public readonly byte[] PageSegments = new byte[0x10000 >> 7];

        // For each byte, the segment that it is a lead byte of, or -1.
        public readonly sbyte[] LeadSegments = new sbyte[256];

        // For each byte, the char that it stands for if it is a pure single of the BMP, otherwise 0.
        public readonly ushort[] Singles = new ushort[256];

        // For each lead byte of 2-byte chars, the pure chars that it covers: the char of the first trail byte of them in the upper 16 bits, then that
        // trail byte and the number of them. 0 where there are none.
        public readonly uint[] Pairs = new uint[256];

        // For each lead byte of 2-byte chars, the char of its first trail byte in the upper bits and the number of its trail bytes in the low byte.
        public readonly uint[] Leads = new uint[256];

        // The pure ranges other than the ones the run loops check first: printable ASCII from 0x20 (95 chars, or 33 with kana in the slot), range A
        // (the singles of the main window in the BMP, of the kana of the slot or of the fullwidth punctuation) and the largest range of 2-byte chars
        // ("wide": the ideographs, the Hangul syllables, or general punctuation).
        public readonly FastRange[] Ranges;

        public readonly int AsciiCount;
        public readonly int AStart;
        public readonly int ACount;
        public readonly int AFirstByte;
        public readonly int WideStart;
        public readonly int WideCount;
        public readonly int WideSegment;
        public readonly int WideLead;

        // The second largest range of non-ASCII singles ("B", the CJK punctuation in the CJK context), for the wide blocks.
        public readonly int BStart;
        public readonly int BCount;
        public readonly int BFirstByte;

        // For the wide blocks of the wide contexts: whether there are any, the first of the lead bytes of the wide range whose 248 chars are all pure
        // and their number, and what turns a lead and trail byte as a 16-bit word, less its upper byte, into the char (modulo 65536).
        public readonly bool WideVectors;
        public readonly int WideFullLead;
        public readonly int WideFullLeadCount;
        public readonly int WidePairDelta;

        // A supplementary main window in the general context: the high surrogate of its chars, the first low surrogate, their number and the byte of
        // the first one. The number is 0 otherwise.
        public readonly uint SuppHigh;
        public readonly uint SuppLow;
        public readonly uint SuppCount;
        public readonly uint SuppFirstByte;

        public Tables(Layout layout)
            : base(layout)
        {
            Transitions = EnsureTransitions();

            int context = Context;
            int slot = Slot;
            int hot = Hot;
            int hotEnd = HotEnd;
            var segments = Segments;
            int segmentCount = segments.Length;
            Array.Fill(LeadSegments, (sbyte)-1);

            Array.Fill(PageSegments, MixedPage);

            for (int s = 0; s < segmentCount; s++)
            {
                for (int page = (segments[s].Start + 0x7F) >> 7; page < PageSegments.Length && (page << 7) + 0x7F <= segments[s].End; page++)
                    PageSegments[page] = (byte)s;
            }

            for (int s = 0; s < segmentCount; s++)
            {
                var segment = segments[s];
                int perLead = segment.Bytes is 1 ? 1 : segment.Bytes is 2 ? Radix : segment.Bytes is 3 ? Radix * Radix : Radix * Radix * Radix;
                int leadEnd = segment.Lead + ((segment.End - segment.Start + perLead) / perLead);

                for (int lead = segment.Lead; lead < leadEnd; lead++)
                    LeadSegments[lead] = (sbyte)s;

                if (segment.Bytes is not 2)
                    continue;

                for (int lead = segment.Lead; lead < leadEnd; lead++)
                {
                    int first = segment.Start + ((lead - segment.Lead) * Radix);
                    Leads[lead] = ((uint)first << 8) | (uint)Math.Min(Radix, segment.End - first + 1);
                }
            }

            // The ranges of pure chars that take 1 or 2 bytes, in the BMP.

            var pure = PureRanges(context, slot, hot, hotEnd);
            var fast = new List<FastRange>();

            foreach (var segment in segments)
            {
                if (segment.Bytes > 2 || segment.Start > 0xFFFF)
                    continue;

                foreach ((int low, int high) in pure)
                {
                    int from = Math.Max(low, segment.Start);
                    int to = Math.Min(Math.Min(high, segment.End), 0xFFFF);

                    if (from <= to)
                        fast.Add(new((uint)from, (uint)(to - from + 1), (uint)segment.Start, (uint)segment.Lead, segment.Bytes));
                }
            }

            // The run loops never take a surrogate: the encoder must see a pair as one code point, and the decoder enforces the surrogate-pair rule
            // (section 10.1, rule 5) only in the slow path, by looking at the char before. A pure range with surrogates in it would break both.
            if (fast.Any(r => r.Low <= 0xDFFF && r.Low + r.Count - 1 >= 0xD800))
                throw new InvalidOperationException("A range of pure chars has surrogates in it.");

            foreach (var range in fast)
            {
                if (range.Bytes is 1)
                {
                    for (uint c = range.Low; c < range.Low + range.Count; c++)
                        Singles[range.Lead + c - range.Segment] = (ushort)c;
                }
                else
                {
                    // Each lead byte covers 248 chars of the segment; keep the longest stretch of pure chars under each one.

                    uint firstLead = range.Lead + ((range.Low - range.Segment) / Radix);
                    uint lastLead = range.Lead + ((range.Low + range.Count - 1 - range.Segment) / Radix);

                    for (uint lead = firstLead; lead <= lastLead; lead++)
                    {
                        uint leadStart = range.Segment + ((lead - range.Lead) * Radix);
                        uint from = Math.Max(leadStart, range.Low);
                        uint to = Math.Min(leadStart + Radix - 1, range.Low + range.Count - 1);
                        uint count = to - from + 1;

                        if (count > (Pairs[lead] & 0xFF))
                            Pairs[lead] = (from << 16) | ((from - leadStart + TrailFloor) << 8) | count;
                    }
                }
            }

            var ascii = fast.Single(r => r.Low is 0x20);

            if (ascii.Bytes is not 1 || ascii.Lead + 0x20 - ascii.Segment is not AsciiFirstByte)
                throw new InvalidOperationException("ASCII is not where the run loops expect it.");

            AsciiCount = (int)ascii.Count;

            var a = fast.Where(r => r.Bytes is 1 && r.Low >= 0x80).DefaultIfEmpty().MaxBy(r => r.Count);

            if (a.Count > 0)
            {
                AStart = (int)a.Low;
                ACount = (int)a.Count;
                AFirstByte = (int)(a.Lead + a.Low - a.Segment);
            }

            var wide = fast.Where(r => r.Bytes is 2).DefaultIfEmpty().MaxBy(r => r.Count);

            if (wide.Count > 0)
            {
                WideStart = (int)wide.Low;
                WideCount = (int)wide.Count;
                WideSegment = (int)wide.Segment;
                WideLead = (int)wide.Lead;
            }

            Ranges = [.. fast.Where(r => r != ascii && r != a && r != wide && r.Low is not 0x09).OrderByDescending(r => r.Count)];

            var b = fast.Where(r => r.Bytes is 1 && r.Low >= 0x80 && r != a).DefaultIfEmpty().MaxBy(r => r.Count);

            if (b.Count > 0)
            {
                BStart = (int)b.Low;
                BCount = (int)b.Count;
                BFirstByte = (int)(b.Lead + b.Low - b.Segment);
            }

            if (context is not General && wide.Count > 0)
            {
                // The lead bytes of the wide range whose trail bytes all give a char of the range.
                uint fullFirst = wide.Lead + ((wide.Low - wide.Segment + Radix - 1) / Radix);
                uint fullEnd = wide.Lead + ((wide.Low + wide.Count - wide.Segment) / Radix);

                if (fullEnd > fullFirst)
                {
                    WideVectors = true;
                    WideFullLead = (int)fullFirst;
                    WideFullLeadCount = (int)(fullEnd - fullFirst);
                    WidePairDelta = (int)((wide.Segment - (wide.Lead * Radix) - TrailFloor) & 0xFFFF);
                }
            }

            if (context is General && hot > 0xFFFF)
            {
                var segment = segments[Layout.FindSegment(segments, hot)];
                SuppHigh = (uint)(0xD800 + ((hot - 0x10000) >> 10));
                SuppLow = (uint)(0xDC00 + (hot & 0x3FF));
                SuppCount = (uint)(hotEnd - hot + 1);
                SuppFirstByte = (uint)(segment.Lead + hot - segment.Start);
            }
        }

        /// <summary>
        /// Gets whether the run loops take a char without leaving them, or may: a single, a char of the wide range, the high surrogate of a
        /// supplementary main window, or a char that takes 2 bytes (which may be an event).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool StartsRun(ushort c)
        {
            if ((uint)(c - 0x20) < (uint)AsciiCount || (uint)(c - AStart) < (uint)ACount || (uint)(c - WideStart) < (uint)WideCount || c == SuppHigh)
                return true;

            int page = PageSegments[c >> 7];
            return page != MixedPage && Segments[page].Bytes is 2;
        }

        /// <summary>
        /// Gets whether the run loops take a char that starts with a byte without leaving them, or may: a single, a lead byte of 2-byte chars (which
        /// may be an event), or a 1-byte char of a supplementary main window.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool StartsRun(byte b) => Singles[b] != 0 || Leads[b] != 0 || b - SuppFirstByte < SuppCount;

        /// <summary>
        /// Gets the index of the range that holds a char: from the table of pages in most of the BMP.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int FindSegmentIndex(int c)
        {
            if (c <= 0xFFFF)
            {
                int page = PageSegments[c >> 7];

                if (page != MixedPage)
                    return page;
            }

            return Layout.FindSegment(Segments, c);
        }

        /// <summary>
        /// Gets the ranges of chars that do not change the layout, sorted.
        /// </summary>
        private static List<(int Low, int High)> PureRanges(int context, int slot, int hot, int hotEnd)
        {
            var ranges = new List<(int Low, int High)>();

            // The ASCII letters move the slot to themselves.
            if (context is Cjk && slot != AsciiLetters)
                ranges.AddRange([(0x00, 0x40), (0x5B, 0x60), (0x7B, 0xBF)]);
            else
                ranges.Add((0x00, 0xBF));

            ranges.AddRange([(0x0370, 0x037F), (0x2000, 0x20FF)]);

            switch (context)
            {
                case General:
                    if (hot <= 0xFFFF)
                        ranges.Add((hot, hotEnd));

                    break;

                case Hangul:
                    ranges.AddRange([(0x3000, 0x303F), (0xAC00, 0xD7A3), (0xFF00, 0xFFFF)]);
                    break;

                case Cjk:
                    ranges.Add((0x3000, 0x303F));

                    // The letters of a kana move the slot to their kind (U+3041..U+3096 and U+30A1..U+30FA); the marks and signs around them do not.
                    if (slot is HiraganaStart)
                        ranges.AddRange([(0x3040, 0x30A0), (0x30FB, 0x30FF)]);
                    else if (slot is KatakanaStart)
                        ranges.AddRange([(0x3040, 0x3040), (0x3097, 0x30FF)]);
                    else
                        ranges.AddRange([(0x3040, 0x3040), (0x3097, 0x30A0), (0x30FB, 0x30FF)]);

                    ranges.AddRange([(0x3400, 0x9FFF), (0xFF00, 0xFFFF)]);
                    break;
            }

            ranges.Sort();
            return ranges;
        }
    }
}
