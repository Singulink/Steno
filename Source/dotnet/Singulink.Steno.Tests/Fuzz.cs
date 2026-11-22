using System.Diagnostics;
using System.Globalization;
using System.Text;
using Shouldly;

namespace Singulink.Steno.Tests;

/// <summary>
/// The time budget of the fuzz tests, and the checks that they share.
/// </summary>
/// <remarks>
/// Each timed fuzz test runs for <see cref="Budget"/>: 1 second by default, so that the suite stays short, or the number of seconds in the environment
/// variable <c>STENO_FUZZ_SECONDS</c> for long runs. With a budget of 10 seconds or more, the tests that are too slow or need too much memory for the
/// suite (megabyte values, every window, every 3-byte string after more lead bytes) run in full as well.
/// </remarks>
internal static class Fuzz
{
    private const byte Canary = 0xCC;
    private const char CharCanary = '쳌';

    public static TimeSpan Budget { get; } = TimeSpan.FromSeconds(
        double.TryParse(Environment.GetEnvironmentVariable("STENO_FUZZ_SECONDS"), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            ? seconds : 1);

    public static bool IsLong => Budget >= TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs an action until the budget is spent (at least once). Returns the number of runs.
    /// </summary>
    public static long Run(Action<long> action)
    {
        var watch = Stopwatch.StartNew();
        long count = 0;

        do
        {
            action(count);
            count++;
        }
        while (watch.Elapsed < Budget);

        return count;
    }

    public static string Describe(string text, int max = 60)
    {
        var units = text.Take(max).Select(c => ((int)c).ToString("X4", CultureInfo.InvariantCulture));
        return $"{text.Length} chars: {string.Join(' ', units)}{(text.Length > max ? " ..." : string.Empty)}";
    }

    public static string Hex(ReadOnlySpan<byte> bytes, int max = 80) =>
        bytes.IsEmpty ? "-" : Convert.ToHexString(bytes[..Math.Min(bytes.Length, max)]) + (bytes.Length > max ? "..." : string.Empty);

    /// <summary>
    /// Encodes and decodes one-shot against the oracle, with canaries around the output, exact-size and one-too-short destinations, and the counts.
    /// </summary>
    public static void CheckOneShot(string text, string what)
    {
        var encoding = StenoEncoding.Instance;
        byte[] expected = FastReference.Encode(text);

        byte[] buffer = new byte[expected.Length + 64];
        buffer.AsSpan().Fill(Canary);
        int written = encoding.GetBytes(text, buffer.AsSpan(32, expected.Length));

        if (written != expected.Length || !buffer.AsSpan(32, expected.Length).SequenceEqual(expected))
        {
            byte[] actual = encoding.GetBytes(text);
            int at = actual.AsSpan().CommonPrefixLength(expected);

            throw new ShouldAssertException($"{what}: the bytes differ at byte {at}. Text {Describe(text)}\n" +
                $"library   {Hex(actual.AsSpan(Math.Max(0, at - 8)))}\nreference {Hex(expected.AsSpan(Math.Max(0, at - 8)))}");
        }

        CheckCanaries(buffer, 32, expected.Length, $"{what} (exact)");

        if (expected.Length > 0)
        {
            buffer.AsSpan().Fill(Canary);
            Should.Throw<ArgumentException>(() => encoding.GetBytes(text, buffer.AsSpan(32, expected.Length - 1)), $"{what}: {Describe(text)}");
            CheckCanaries(buffer, 32, expected.Length - 1, $"{what} (too short)");
        }

        encoding.GetByteCount(text).ShouldBe(expected.Length, $"{what}: {Describe(text)}");

        char[] chars = new char[text.Length + 64];
        chars.AsSpan().Fill(CharCanary);
        int charCount = encoding.GetChars(expected, chars.AsSpan(32, text.Length));

        if (charCount != text.Length || !chars.AsSpan(32, text.Length).SequenceEqual(text))
        {
            string actual = new(chars.AsSpan(32, charCount));
            int at = actual.AsSpan().CommonPrefixLength(text);
            throw new ShouldAssertException($"{what}: the text differs at char {at}. Text {Describe(text)}\nbytes {Hex(expected)}\ngot {Describe(actual)}");
        }

        CheckCanaries(chars, 32, text.Length, $"{what} (decode)");

        if (text.Length > 0)
        {
            chars.AsSpan().Fill(CharCanary);
            Should.Throw<ArgumentException>(() => encoding.GetChars(expected, chars.AsSpan(32, text.Length - 1)), $"{what}: {Describe(text)}");
            CheckCanaries(chars, 32, text.Length - 1, $"{what} (decode too short)");
        }

        encoding.GetCharCount(expected).ShouldBe(text.Length, $"{what}: {Describe(text)}");
    }

    /// <summary>
    /// Checks a byte string against the oracle: the library accepts it exactly when the oracle does, with the same text (whose encoding is the byte
    /// string again) or for the same reason.
    /// </summary>
    public static void CheckDecoding(byte[] bytes, string what)
    {
        var encoding = StenoEncoding.Instance;
        var (expectedText, expectedReason) = FastReference.Decode(bytes);
        var (text, reason) = TestData.LibraryDecode(bytes);

        if (expectedText is null)
        {
            reason.ShouldBe(expectedReason, $"{what}: {Hex(bytes)}");
            Should.Throw<DecoderFallbackException>(() => encoding.GetCharCount(bytes), $"{what}: {Hex(bytes)}");
            return;
        }

        text.ShouldBe(expectedText, $"{what}: {Hex(bytes)} (rejected for {reason})");
        Hex(encoding.GetBytes(text!)).ShouldBe(Hex(bytes), $"{what}: an accepted byte string encodes back to other bytes");
        encoding.GetCharCount(bytes).ShouldBe(text!.Length, $"{what}: {Hex(bytes)}");
    }

    private static void CheckCanaries(byte[] buffer, int start, int length, string what)
    {
        if (buffer.AsSpan(0, start).ContainsAnyExcept(Canary) || buffer.AsSpan(start + length).ContainsAnyExcept(Canary))
            throw new ShouldAssertException($"{what}: written outside the destination.");
    }

    private static void CheckCanaries(char[] buffer, int start, int length, string what)
    {
        if (buffer.AsSpan(0, start).ContainsAnyExcept(CharCanary) || buffer.AsSpan(start + length).ContainsAnyExcept(CharCanary))
            throw new ShouldAssertException($"{what}: written outside the destination.");
    }
}
