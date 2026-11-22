using System.Text;
using PrefixClassName.MsTest;
using Shouldly;

namespace Singulink.Steno.Tests;

/// <summary>
/// The streaming encoder and decoder and the UTF-8 conversions against the fast oracle, for a time budget each (see <see cref="Fuzz"/>): random
/// calls with empty calls, failed calls, counts and Convert between them, flushes in the middle, one unit per call, Convert at every size, Reset, the
/// stream classes, and UTF-8.
/// </summary>
[PrefixTestClass]
public sealed class StreamingFuzzTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Converts text to UTF-8 and back against the oracle and the BCL, with exact and one-too-short destinations, the Try methods and the bounds.
    /// </summary>
    public static void CheckUtf8(string text, string what)
    {
        byte[] steno = FastReference.Encode(text);
        int maxUtf8 = StenoEncoding.GetMaxUtf8ByteCount(steno.Length);

        if (KindText.HasLoneSurrogate(text))
        {
            Should.Throw<EncoderFallbackException>(() => StenoEncoding.GetUtf8Bytes(steno), what);
            Should.Throw<EncoderFallbackException>(() => StenoEncoding.GetUtf8ByteCount(steno), what);
            Should.Throw<EncoderFallbackException>(() => StenoEncoding.TryGetUtf8Bytes(steno, new byte[maxUtf8], out _), what);
            return;
        }

        byte[] utf8 = Encoding.UTF8.GetBytes(text);

        StenoEncoding.GetUtf8ByteCount(steno).ShouldBe(utf8.Length, what);
        StenoEncoding.GetByteCountFromUtf8(utf8).ShouldBe(steno.Length, what);
        utf8.Length.ShouldBeLessThanOrEqualTo(maxUtf8, what);
        steno.Length.ShouldBeLessThanOrEqualTo(StenoEncoding.GetMaxByteCountFromUtf8(utf8.Length), what);

        CheckInto(utf8.Length, d => StenoEncoding.GetUtf8Bytes(steno, d), utf8, $"{what}: GetUtf8Bytes");
        CheckInto(steno.Length, d => StenoEncoding.GetBytesFromUtf8(utf8, d), steno, $"{what}: GetBytesFromUtf8");

        byte[] buffer = new byte[Math.Max(maxUtf8, StenoEncoding.GetMaxByteCountFromUtf8(utf8.Length))];
        StenoEncoding.TryGetUtf8Bytes(steno, buffer.AsSpan(0, maxUtf8), out int written).ShouldBeTrue(what);
        written.ShouldBe(utf8.Length, what);
        StenoEncoding.TryGetBytesFromUtf8(utf8, buffer, out written).ShouldBeTrue(what);
        written.ShouldBe(steno.Length, what);

        if (utf8.Length > 0)
        {
            StenoEncoding.TryGetUtf8Bytes(steno, buffer.AsSpan(0, utf8.Length - 1), out written).ShouldBeFalse(what);
            written.ShouldBe(0);
            StenoEncoding.TryGetBytesFromUtf8(utf8, buffer.AsSpan(0, steno.Length - 1), out written).ShouldBeFalse(what);
            written.ShouldBe(0);
        }
    }

    [TestMethod]
    [DataRow(601)]
    [DataRow(602)]
    public void RandomCalls_GiveOneShotResult(int seed)
    {
        var random = new Random(seed);
        var encoder = StenoEncoding.Instance.GetEncoder();
        var decoder = StenoEncoding.Instance.GetDecoder();

        long count = Fuzz.Run(i => {
            string text = KindText.Runs(random, random.Next(2) == 0 ? 60 : 600);
            byte[] expected = FastReference.Encode(text);

            // A new value: a flush does not end one, Reset does.
            encoder.Reset();
            decoder.Reset();

            byte[] streamed = StreamEncode(encoder, text, random);

            if (!streamed.AsSpan().SequenceEqual(expected))
            {
                throw new ShouldAssertException($"seed {seed}, text {i}: the bytes differ at {streamed.AsSpan().CommonPrefixLength(expected)}: " +
                    $"{Fuzz.Describe(text)}\nlibrary   {Fuzz.Hex(streamed)}\nreference {Fuzz.Hex(expected)}");
            }

            StreamDecode(decoder, expected, random).ShouldBe(text, $"seed {seed}, text {i}: {Fuzz.Describe(text)}");
        });

        TestContext.WriteLine($"{count} texts");
    }

    /// <summary>
    /// One char per call and one byte per call, with an empty call between every two, through the array, span and pointer overloads.
    /// </summary>
    [TestMethod]
    public unsafe void OneUnitPerCall()
    {
        var random = new Random(701);

        long count = Fuzz.Run(_ => {
            string text = KindText.Runs(random, 80);
            byte[] expected = FastReference.Encode(text);
            var encoder = StenoEncoding.Instance.GetEncoder();
            var output = new List<byte>();
            byte[] buffer = new byte[16];

            for (int i = 0; i < text.Length; i++)
            {
                output.AddRange(buffer.AsSpan(0, encoder.GetBytes([], buffer, false)));
                char[] one = [text[i]];

                int written = (i % 3) switch {
                    0 => encoder.GetBytes(one, 0, 1, buffer, 0, false),
                    1 => encoder.GetBytes(one.AsSpan(), buffer, false),
                    _ => PointerEncode(encoder, one, buffer),
                };

                output.AddRange(buffer.AsSpan(0, written));
            }

            output.AddRange(buffer.AsSpan(0, encoder.GetBytes([], buffer, true)));
            Fuzz.Hex([.. output]).ShouldBe(Fuzz.Hex(expected), $"one char per call: {Fuzz.Describe(text)}");

            var decoder = StenoEncoding.Instance.GetDecoder();
            var chars = new StringBuilder();
            char[] charBuffer = new char[4];

            for (int i = 0; i < expected.Length; i++)
            {
                chars.Append(charBuffer.AsSpan(0, decoder.GetChars([], charBuffer, false)));
                chars.Append(charBuffer.AsSpan(0, decoder.GetChars(expected, i, 1, charBuffer, 0, false)));
            }

            chars.Append(charBuffer.AsSpan(0, decoder.GetChars([], charBuffer, true)));
            chars.ToString().ShouldBe(text, $"one byte per call: {Fuzz.Describe(text)}");
        });

        TestContext.WriteLine($"{count} texts");

        static int PointerEncode(System.Text.Encoder encoder, char[] one, byte[] buffer)
        {
            fixed (char* c = one)
            fixed (byte* b = buffer)
                return encoder.GetBytes(c, 1, b, buffer.Length, false);
        }
    }

    /// <summary>
    /// Convert with every destination size from 0, in the loop that a caller would write, gives the one-shot result. It throws only when there is
    /// no room for the bytes of one char: never with 4 bytes of room (a surrogate pair takes 4), or 2 chars.
    /// </summary>
    [TestMethod]
    public void Convert_EverySize()
    {
        var random = new Random(801);

        long count = Fuzz.Run(_ => {
            string text = KindText.Runs(random, 40);
            byte[] expected = FastReference.Encode(text);

            for (int size = 0; size <= 14; size++)
            {
                var encoder = StenoEncoding.Instance.GetEncoder();
                var output = new List<byte>();
                int position = 0;
                byte[] destination = new byte[size];

                for (int guard = 0; ; guard++)
                {
                    guard.ShouldBeLessThan(10000, $"Convert does not end, size {size}");

                    try
                    {
                        encoder.Convert(text.AsSpan(position), destination, true, out int charsUsed, out int bytesUsed, out bool completed);
                        output.AddRange(destination.AsSpan(0, bytesUsed));
                        position += charsUsed;

                        if (completed)
                            break;

                        (charsUsed + bytesUsed).ShouldBeGreaterThan(0, $"Convert made no progress and did not throw, size {size}");
                    }
                    catch (ArgumentException)
                    {
                        destination.Length.ShouldBeLessThan(4, $"Convert threw with {destination.Length} bytes of room: {Fuzz.Describe(text)} at {position}");
                        destination = new byte[destination.Length + 1];
                    }
                }

                position.ShouldBe(text.Length);
                Fuzz.Hex([.. output]).ShouldBe(Fuzz.Hex(expected), $"Convert with size {size}: {Fuzz.Describe(text)}");

                var decoder = StenoEncoding.Instance.GetDecoder();
                var chars = new StringBuilder();
                int bytePosition = 0;
                char[] charDestination = new char[size];

                for (int guard = 0; ; guard++)
                {
                    guard.ShouldBeLessThan(10000, $"decoder Convert does not end, size {size}");

                    try
                    {
                        decoder.Convert(expected.AsSpan(bytePosition), charDestination, true, out int bytesUsed, out int charsUsed, out bool completed);
                        chars.Append(charDestination.AsSpan(0, charsUsed));
                        bytePosition += bytesUsed;

                        if (completed)
                            break;

                        (charsUsed + bytesUsed).ShouldBeGreaterThan(0, $"decoder Convert made no progress, size {size}");
                    }
                    catch (ArgumentException)
                    {
                        charDestination.Length.ShouldBeLessThan(2, $"decoder Convert threw with {charDestination.Length} chars of room");
                        charDestination = new char[charDestination.Length + 1];
                    }
                }

                chars.ToString().ShouldBe(text, $"decoder Convert with size {size}: {Fuzz.Describe(text)}");
            }
        });

        TestContext.WriteLine($"{count} texts x 15 sizes");
    }

    /// <summary>
    /// Reset at any point, also inside a pair or the bytes of a code point, starts a new value.
    /// </summary>
    [TestMethod]
    public void Reset_AtAnyPoint_StartsANewValue()
    {
        var random = new Random(901);

        long count = Fuzz.Run(_ => {
            string a = KindText.Runs(random, 50);
            string b = KindText.Runs(random, 50);
            byte[] encodedA = FastReference.Encode(a);
            byte[] encodedB = FastReference.Encode(b);

            var encoder = StenoEncoding.Instance.GetEncoder();
            int cut = random.Next(a.Length + 1);
            encoder.GetBytes(a.AsSpan(0, cut), new byte[StenoEncoding.Instance.GetMaxByteCount(cut)], false);
            encoder.Reset();
            Fuzz.Hex(StreamEncode(encoder, b, random)).ShouldBe(Fuzz.Hex(encodedB), $"encoder Reset after {cut} chars of {Fuzz.Describe(a)}");

            var decoder = StenoEncoding.Instance.GetDecoder();
            int byteCut = random.Next(encodedA.Length + 1);
            decoder.GetChars(encodedA.AsSpan(0, byteCut), new char[(byteCut * 2) + 2], false);
            decoder.Reset();
            StreamDecode(decoder, encodedB, random).ShouldBe(b, $"decoder Reset after {byteCut} bytes of {Fuzz.Describe(a)}");
        });

        TestContext.WriteLine($"{count} pairs");
    }

    /// <summary>
    /// Large values through StreamWriter (flushed at random points) and StreamReader, and the base-class paths (Encoding.GetString through an
    /// Encoding reference, and Encoding.Convert).
    /// </summary>
    [TestMethod]
    public void StreamsAndBaseClassPaths()
    {
        var random = new Random(1701);

        long count = Fuzz.Run(i => {
            string text = KindText.Runs(random, 50_000);
            byte[] expected = FastReference.Encode(text);
            var stream = new MemoryStream();

            using (var writer = new StreamWriter(stream, StenoEncoding.Instance, bufferSize: random.Next(1, 5000), leaveOpen: true))
            {
                for (int position = 0; position < text.Length;)
                {
                    int length = Math.Min(text.Length - position, random.Next(1, 3000));
                    writer.Write(text.AsSpan(position, length));
                    position += length;

                    // A flush anywhere but between the two halves of a pair keeps the stream one value.
                    if (random.Next(5) == 0 && !(position < text.Length && char.IsLowSurrogate(text[position]) && char.IsHighSurrogate(text[position - 1])))
                        writer.Flush();
                }
            }

            Fuzz.Hex(stream.ToArray()).ShouldBe(Fuzz.Hex(expected), $"StreamWriter, text {i}: {Fuzz.Describe(text)}");
            stream.Position = 0;

            using (var reader = new StreamReader(stream, StenoEncoding.Instance, detectEncodingFromByteOrderMarks: false, bufferSize: random.Next(1, 5000)))
                reader.ReadToEnd().ShouldBe(text, $"StreamReader, text {i}");

            ((Encoding)StenoEncoding.Instance).GetString(expected.AsSpan()).ShouldBe(text);
            ((Encoding)StenoEncoding.Instance).GetCharCount(expected.AsSpan()).ShouldBe(text.Length);

            if (!KindText.HasLoneSurrogate(text))
            {
                byte[] utf8 = Encoding.Convert(StenoEncoding.Instance, Encoding.UTF8, expected);
                Fuzz.Hex(utf8).ShouldBe(Fuzz.Hex(Encoding.UTF8.GetBytes(text)));
                Fuzz.Hex(Encoding.Convert(Encoding.UTF8, StenoEncoding.Instance, utf8)).ShouldBe(Fuzz.Hex(expected));
            }
        });

        TestContext.WriteLine($"{count} texts");
    }

    [TestMethod]
    public void Utf8Conversions()
    {
        var random = new Random(1001);

        long count = Fuzz.Run(i => {
            string text = KindText.Runs(random, random.Next(2) == 0 ? 100 : 3000);

            if (random.Next(2) == 0)
                text = KindText.RemoveLoneSurrogates(text);

            CheckUtf8(text, $"text {i}");
        });

        TestContext.WriteLine($"{count} texts");
    }

    /// <summary>
    /// Encodes the text through one encoder in random pieces, with empty calls, counts (which must predict what GetBytes writes and must not change
    /// the state), failed calls into outputs that are too short (which must leave the state as it was), Convert calls of random sizes, and flushes in
    /// the middle (anywhere but between the two halves of a pair).
    /// </summary>
    private static byte[] StreamEncode(System.Text.Encoder encoder, string text, Random random)
    {
        var output = new List<byte>();
        int position = 0;

        while (true)
        {
            int n = ChunkLength(random, text.Length - position);
            bool end = position + n == text.Length;
            int boundary = position + n;
            bool splitsPair = !end && boundary > 0 && char.IsHighSurrogate(text[boundary - 1]) && char.IsLowSurrogate(text[boundary]);
            bool flush = end ? random.Next(3) != 0 : !splitsPair && random.Next(6) == 0;
            var chunk = text.AsSpan(position, n);

            int predicted = encoder.GetByteCount(chunk, flush);

            if (random.Next(3) == 0)
                encoder.GetByteCount(text.AsSpan(position), true);

            predicted.ShouldBeLessThanOrEqualTo(StenoEncoding.Instance.GetMaxByteCount(n));

            if (predicted > 0 && random.Next(4) == 0)
            {
                byte[] small = new byte[random.Next(predicted)];
                Should.Throw<ArgumentException>(() => encoder.GetBytes(text.AsSpan(position, n), small, flush));
            }

            if (random.Next(4) == 0)
            {
                byte[] destination = new byte[random.Next(0, predicted + 8)];
                int charsUsed, bytesUsed;
                bool completed;

                try
                {
                    encoder.Convert(chunk, destination, flush, out charsUsed, out bytesUsed, out completed);
                }
                catch (ArgumentException)
                {
                    // Nothing was used; try again with another size.
                    continue;
                }

                output.AddRange(destination.AsSpan(0, bytesUsed));
                position += charsUsed;

                if (charsUsed == n && destination.Length >= predicted)
                    completed.ShouldBeTrue($"Convert of all {n} chars into {destination.Length} >= {predicted} bytes");

                if (position == text.Length && end && flush && completed)
                    return [.. output];

                continue;
            }

            byte[] bytes = new byte[predicted + random.Next(0, 4)];
            int written = encoder.GetBytes(chunk, bytes, flush);
            written.ShouldBe(predicted, $"GetBytes against GetByteCount (piece of {n} at {position}, flush {flush})");

            output.AddRange(bytes.AsSpan(0, written));
            position += n;

            if (end && flush)
                return [.. output];
        }
    }

    private static string StreamDecode(System.Text.Decoder decoder, byte[] bytes, Random random)
    {
        var output = new StringBuilder();
        int position = 0;

        while (true)
        {
            int n = ChunkLength(random, bytes.Length - position);
            bool end = position + n == bytes.Length;
            bool flush = end && random.Next(3) != 0;
            var chunk = bytes.AsSpan(position, n);

            int predicted = decoder.GetCharCount(chunk, flush);

            if (random.Next(3) == 0)
                decoder.GetCharCount(bytes.AsSpan(position), true);

            predicted.ShouldBeLessThanOrEqualTo(StenoEncoding.Instance.GetMaxCharCount(n));

            if (predicted > 0 && random.Next(4) == 0)
            {
                char[] small = new char[random.Next(predicted)];
                Should.Throw<ArgumentException>(() => decoder.GetChars(bytes.AsSpan(position, n), small, flush));
            }

            if (random.Next(4) == 0)
            {
                char[] destination = new char[random.Next(0, predicted + 8)];
                int bytesUsed, charsUsed;
                bool completed;

                try
                {
                    decoder.Convert(chunk, destination, flush, out bytesUsed, out charsUsed, out completed);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                output.Append(destination.AsSpan(0, charsUsed));
                position += bytesUsed;

                if (position == bytes.Length && flush && completed)
                    return output.ToString();

                continue;
            }

            char[] chars = new char[predicted + random.Next(0, 4)];
            int written = decoder.GetChars(chunk, chars, flush);
            written.ShouldBe(predicted);

            output.Append(chars.AsSpan(0, written));
            position += n;

            if (end && flush)
                return output.ToString();
        }
    }

    private static int ChunkLength(Random random, int rest) => rest is 0
        ? 0
        : random.Next(6) switch {
            0 => 0,
            1 => 1,
            2 => Math.Min(rest, random.Next(1, 4)),
            3 => Math.Min(rest, random.Next(1, 40)),
            4 => Math.Min(rest, random.Next(1, 300)),
            _ => rest,
        };

    private static void CheckInto(int size, Func<Span<byte>, int> write, byte[] expected, string what)
    {
        byte[] buffer = new byte[size + 64];
        buffer.AsSpan().Fill(0xCC);

        write(buffer.AsSpan(32, size)).ShouldBe(expected.Length, what);
        Fuzz.Hex(buffer.AsSpan(32, size)).ShouldBe(Fuzz.Hex(expected), what);
        (buffer.AsSpan(0, 32).ContainsAnyExcept((byte)0xCC) || buffer.AsSpan(32 + size).ContainsAnyExcept((byte)0xCC)).ShouldBeFalse(what);

        if (size > 0)
        {
            buffer.AsSpan().Fill(0xCC);
            Should.Throw<ArgumentException>(() => write(buffer.AsSpan(32, size - 1)), what);
            buffer.AsSpan(32 + size - 1).ContainsAnyExcept((byte)0xCC).ShouldBeFalse(what);
        }
    }
}
