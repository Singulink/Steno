using System.Diagnostics;
using System.Reflection;
using System.Text;
using PrefixClassName.MsTest;
using Shouldly;

namespace Singulink.Steno.Tests;

/// <summary>
/// The cost per char of text that reaches a new state at almost every char stays a modest factor of the cost of text that keeps to a few states:
/// both when the states outnumber the layout cache, and when two states share a set of the cache. Timed as the best of several rounds, with
/// generous slack, so that a busy machine does not fail them.
/// </summary>
[PrefixTestClass]
[DoNotParallelize]
public sealed class LayoutCacheTests
{
    private const int TextLength = 20_000;

    private static readonly Encoding Steno = StenoEncoding.Instance;

    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Text that hops among 104 windows reaches about 10,000 states (a main window and a second window), more than the 4,096 layouts that the cache
    /// holds, so nearly every char needs a layout that the cache does not have. Building the tables of the run loops for each one made such text
    /// cost hundreds of times what text that hops among three windows costs; building only the ranges keeps it to a few dozen times.
    /// </summary>
    [TestMethod]
    public void HoppingAmongMoreStatesThanTheCacheHolds_CostsAModestFactorPerChar()
    {
        var random = new Random(1);
        string few = Hop(random, 3);
        string many = Hop(random, 104);

        double fewCost = CostPerChar(few, rounds: 15);
        double manyCost = CostPerChar(many, rounds: 5);
        double ratio = manyCost / fewCost;

        TestContext.WriteLine($"3 windows: {fewCost:F1} ns per char, 104 windows: {manyCost:F1} ns per char, ratio {ratio:F0}");

        // About 30 on an idle machine; it was about 450 when every miss built the tables.
        ratio.ShouldBeLessThan(150, $"3 windows: {fewCost:F1} ns per char, 104 windows: {manyCost:F1} ns per char");
    }

    /// <summary>
    /// Two states that the text goes back and forth between (each char switches the main window to its own window and makes the other window the
    /// second one), whose layouts go in the same set of the cache, cost about the same as two states whose layouts go in different sets: they do not
    /// evict each other at every switch. The pair is found from the hash of the cache itself.
    /// </summary>
    [TestMethod]
    public void StatesThatShareACacheSet_DoNotEvictEachOther()
    {
        var codec = typeof(StenoEncoding).Assembly.GetType("Singulink.Steno.StenoCodec", throwOnError: true)!;
        var cacheSet = codec.GetMethod("CacheSet", BindingFlags.Static | BindingFlags.NonPublic).ShouldNotBeNull()
            .CreateDelegate<Func<int, int, int, int, int>>();

        int general = Constant(codec, "General");
        int asciiLetters = Constant(codec, "AsciiLetters");

        // Windows far apart, so that neither is a neighbour of the other or is covered by a piece of the layout of the other: the layouts are those
        // of (main X, second Y) and (main Y, second X).
        int[] lows = [.. Enumerable.Range(0, 32).Select(i => 0x1000 + (i * 0x80)), .. Enumerable.Range(0, 30).Select(i => 0x2100 + (i * 0x80))];
        int[] highs = [.. Enumerable.Range(0, 64).Select(i => 0xE000 + (i * 0x80))];

        (int X, int Y)? sharing = null;
        (int X, int Y)? apart = null;

        foreach (int x in lows)
        {
            foreach (int y in highs)
            {
                bool same = cacheSet(general, asciiLetters, x, y) == cacheSet(general, asciiLetters, y, x);

                if (same)
                    sharing ??= (x, y);
                else
                    apart ??= (x, y);
            }
        }

        sharing.ShouldNotBeNull("no pair of windows shares a set of the cache");
        apart.ShouldNotBeNull();

        string sharingText = Alternate(sharing.Value.X, sharing.Value.Y);
        string apartText = Alternate(apart.Value.X, apart.Value.Y);

        // Both texts encode and decode correctly, and switch at every char.
        Steno.GetString(Steno.GetBytes(sharingText)).ShouldBe(sharingText);
        Steno.GetString(Steno.GetBytes(apartText)).ShouldBe(apartText);

        double sharingCost = CostPerChar(sharingText, rounds: 15);
        double apartCost = CostPerChar(apartText, rounds: 15);
        double ratio = sharingCost / apartCost;

        TestContext.WriteLine(
            $"windows {sharing.Value.X:X4} and {sharing.Value.Y:X4} share a set: {sharingCost:F1} ns per char; " +
            $"windows {apart.Value.X:X4} and {apart.Value.Y:X4} do not: {apartCost:F1} ns per char");

        // About 1; a rebuild of a layout at every switch would make it 20 or more.
        ratio.ShouldBeLessThan(3, $"sharing a set: {sharingCost:F1} ns per char, apart: {apartCost:F1} ns per char");

        static string Alternate(int x, int y)
        {
            var random = new Random(3);
            var text = new StringBuilder(TextLength);

            for (int i = 0; i < TextLength; i++)
                text.Append((char)((i % 2 == 0 ? x : y) + random.Next(0x80)));

            return text.ToString();
        }
    }

    /// <summary>
    /// Text that hops among the given number of windows above U+1000, outside the CJK and Hangul ranges, at almost every char.
    /// </summary>
    private static string Hop(Random random, int windows)
    {
        int[] starts = [
            .. Enumerable.Range(0, 32).Select(i => 0x1000 + (i * 0x80))
                .Concat(Enumerable.Range(0, 24).Select(i => 0xA000 + (i * 0x80)))
                .Concat(Enumerable.Range(0, 48).Select(i => 0xE000 + (i * 0x80)))
                .OrderBy(_ => random.Next())
                .Take(windows),
        ];

        var text = new StringBuilder(TextLength);

        for (int i = 0; i < TextLength; i++)
            text.Append((char)(starts[random.Next(starts.Length)] + random.Next(0x80)));

        return text.ToString();
    }

    /// <summary>
    /// The best time per char of encoding and decoding the text, over the rounds.
    /// </summary>
    private static double CostPerChar(string text, int rounds)
    {
        byte[] bytes = Steno.GetBytes(text);
        byte[] byteBuffer = new byte[bytes.Length];
        char[] charBuffer = new char[text.Length];
        double best = double.MaxValue;

        for (int round = 0; round < rounds; round++)
        {
            var watch = Stopwatch.StartNew();
            Steno.GetBytes(text, 0, text.Length, byteBuffer, 0);
            Steno.GetChars(bytes, 0, bytes.Length, charBuffer, 0);
            best = Math.Min(best, watch.Elapsed.TotalNanoseconds / text.Length);
        }

        return best;
    }

    private static int Constant(Type type, string name) =>
        (int)type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic).ShouldNotBeNull(name).GetRawConstantValue()!;
}
