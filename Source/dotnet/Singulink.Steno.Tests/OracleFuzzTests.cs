using System.Collections.Concurrent;
using System.Text;
using PrefixClassName.MsTest;
using Shouldly;

namespace Singulink.Steno.Tests;

/// <summary>
/// The library against the fast oracle on generated text, for a time budget each (see <see cref="Fuzz"/>): one-shot coding, the edges of the vector
/// blocks, every window, the slices, threads sharing the layout cache, the Try methods, and the comparer and the ASCII check by brute force.
/// </summary>
[PrefixTestClass]
public sealed class OracleFuzzTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(101)]
    [DataRow(102)]
    public void Runs_OneShot(int seed)
    {
        var random = new Random(seed);

        long count = Fuzz.Run(i => {
            int max = random.Next(4) switch { 0 => 40, 1 => 200, 2 => 1200, _ => 3000 };
            Fuzz.CheckOneShot(KindText.Runs(random, max), $"seed {seed}, text {i}");
        });

        TestContext.WriteLine($"{count} texts");
    }

    /// <summary>
    /// A change of kind of code point at every offset 0..40 after a run, and runs of 0..40 after it: the edges of the 4, 8, 16 and 32 blocks.
    /// </summary>
    [TestMethod]
    [DataRow(201)]
    [DataRow(202)]
    public void BlockBoundaries(int seed)
    {
        var random = new Random(seed);

        long count = Fuzz.Run(_ => {
            // A lead-in that puts the state somewhere (a wide context, a main window, a kana slot), then the run and the change.
            var leadKind = KindText.RandomKind(random);
            var aKind = KindText.RandomKind(random);
            var bKind = KindText.RandomKind(random);
            int leadWindow = KindText.RandomWindow(random);
            int aWindow = random.Next(2) == 0 ? leadWindow : KindText.RandomWindow(random);
            int bWindow = KindText.RandomWindow(random);
            string lead = KindText.Of(random, leadKind, leadWindow, random.Next(0, 4));
            int bLength = random.Next(1, 3);

            for (int k = 0; k <= 40; k++)
            {
                string a = KindText.Of(random, aKind, aWindow, k);
                string b = KindText.Of(random, bKind, bWindow, bLength);
                int m = random.Next(0, 41);
                string after = KindText.Of(random, random.Next(2) == 0 ? aKind : bKind, random.Next(2) == 0 ? aWindow : bWindow, m);

                Fuzz.CheckOneShot(lead + a + b + after, $"seed {seed}, lead {leadKind}, a {aKind} x {k}, b {bKind} x {bLength}, after {m}");
            }
        });

        TestContext.WriteLine($"{count} rounds of 41 texts");
    }

    /// <summary>
    /// Every window as the main window, and as the second window of each context, followed by mixed text. In the suite the windows are taken in a
    /// random order until the budget is spent; a long run takes all 7,356.
    /// </summary>
    [TestMethod]
    public void EveryWindowAsMainAndSecond()
    {
        var random = new Random(301);
        int[] windows = [.. KindText.Windows.OrderBy(_ => random.Next())];
        var watch = System.Diagnostics.Stopwatch.StartNew();
        int count = 0;

        foreach (int window in windows)
        {
            if (!Fuzz.IsLong && watch.Elapsed > Fuzz.Budget)
                break;

            int end = KindText.WindowEnd(window);
            var start = new StringBuilder();

            // Two of the window switch to it; then runs of it and around it, long enough for the vector blocks.
            for (int i = 0; i < 2; i++)
                KindText.Append(start, window + random.Next(end - window + 1));

            for (int variant = 0; variant < 3; variant++)
            {
                var text = new StringBuilder().Append(start);
                int other = KindText.RandomWindow(random);

                for (int run = 0; run < 6; run++)
                {
                    int length = random.Next(0, 40);
                    var kind = random.Next(3) switch { 0 => KindText.Kind.AnyWindow, 1 => KindText.Kind.NearWindow, _ => KindText.RandomKind(random) };
                    int runWindow = random.Next(3) == 0 ? other : window;

                    for (int i = 0; i < length; i++)
                        KindText.Append(text, random.Next(4) == 0 ? KindText.Pick(random, KindText.Kind.Ascii, 0) : KindText.Pick(random, kind, runWindow));
                }

                Fuzz.CheckOneShot(text.ToString(), $"main window {window:X}");
            }

            string s = start.ToString();
            Fuzz.CheckOneShot("一" + s + "丁" + s + "ああ" + s, $"CJK with window {window:X}");
            Fuzz.CheckOneShot("가" + s + "각" + s, $"Hangul with window {window:X}");
            Fuzz.CheckOneShot("ЖЖ" + s + "Ж" + s + s + "ЖЗ", $"Cyrillic with window {window:X}");
            count++;
        }

        TestContext.WriteLine($"{count} windows");
    }

    /// <summary>
    /// Values that straddle the 512 and 1024 slices of the counting and UTF-8 paths, with a change at the slice edges.
    /// </summary>
    [TestMethod]
    public void SliceEdges()
    {
        var random = new Random(401);

        long count = Fuzz.Run(_ => {
            int slice = random.Next(2) == 0 ? 512 : 1024;
            int length = (slice * random.Next(1, 4)) + random.Next(-3, 4);
            var kindA = KindText.RandomKind(random);
            var kindB = KindText.RandomKind(random);
            int window = KindText.RandomWindow(random);
            var text = new StringBuilder();

            while (text.Length < length)
                KindText.Append(text, KindText.Pick(random, kindA, window));

            if (text.Length > length && !char.IsHighSurrogate(text[length - 1]))
                text.Length = length;

            KindText.Append(text, KindText.Pick(random, kindB, window));

            for (int i = random.Next(10); i > 0; i--)
                KindText.Append(text, KindText.Pick(random, kindA, window));

            string what = $"slice {slice}, length {length}, {kindA}/{kindB}";
            Fuzz.CheckOneShot(text.ToString(), what);
            StreamingFuzzTests.CheckUtf8(text.ToString(), what);
        });

        TestContext.WriteLine($"{count} texts");
    }

    /// <summary>
    /// Values of megabytes, one kind each (the long runs of the vector loops), and a mix. Only in a long run: they need too much memory and time for
    /// the suite.
    /// </summary>
    [TestMethod]
    public void HugeValues()
    {
        if (!Fuzz.IsLong)
            return;

        var random = new Random(501);
        KindText.Kind[] kinds = [KindText.Kind.Ascii, KindText.Kind.Ideograph, KindText.Kind.Hangul, KindText.Kind.Hiragana, KindText.Kind.AnyWindow,
            KindText.Kind.Emoji, KindText.Kind.Bmp];

        foreach (var kind in kinds)
        {
            var text = new StringBuilder();
            int window = kind == KindText.Kind.AnyWindow ? 0x400 : 0x1F600;

            if (kind == KindText.Kind.Emoji)
                text.Append("😀😁");

            while (text.Length < 1_500_000)
            {
                KindText.Append(text, kind == KindText.Kind.Emoji ? 0x1F600 + random.Next(0x80) : KindText.Pick(random, kind, window));

                if (random.Next(5000) == 0)
                    text.Append(' ');
            }

            Fuzz.CheckOneShot(text.ToString(), $"huge {kind}");
        }

        Fuzz.CheckOneShot(KindText.Runs(new Random(502), 2_000_000), "huge mixed");
    }

    /// <summary>
    /// Four threads encode and decode texts with many windows (more layouts than the cache holds), against answers computed before.
    /// </summary>
    [TestMethod]
    public void ThreadsShareTheLayoutCache()
    {
        var random = new Random(1401);
        string[] texts = new string[200];
        byte[][] expected = new byte[texts.Length][];

        for (int i = 0; i < texts.Length; i++)
        {
            var text = new StringBuilder();

            for (int j = 0; j < 200; j++)
            {
                int window = KindText.RandomWindow(random);
                var kind = KindText.RandomKind(random);

                for (int k = random.Next(1, 4); k > 0; k--)
                    KindText.Append(text, KindText.Pick(random, random.Next(2) == 0 ? KindText.Kind.AnyWindow : kind, window));
            }

            texts[i] = text.ToString();
            expected[i] = FastReference.Encode(texts[i]);
        }

        var failures = new ConcurrentBag<string>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        long rounds = 0;

        var threads = Enumerable.Range(0, 4).Select(t => new Thread(() => {
            var threadRandom = new Random(t);

            while (watch.Elapsed < Fuzz.Budget && failures.IsEmpty)
            {
                int i = threadRandom.Next(texts.Length);

                if (!StenoEncoding.Instance.GetBytes(texts[i]).AsSpan().SequenceEqual(expected[i]))
                    failures.Add($"encode {i} on thread {t}");

                if (StenoEncoding.Instance.GetString(expected[i]) != texts[i])
                    failures.Add($"decode {i} on thread {t}");

                Interlocked.Increment(ref rounds);
            }
        })).ToList();

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        TestContext.WriteLine($"{rounds} rounds on 4 threads");
        failures.ShouldBeEmpty();
    }

    /// <summary>
    /// The Try methods with every destination size from 0 to the exact size and beyond.
    /// </summary>
    [TestMethod]
    public void TryMethods_EverySize()
    {
        var random = new Random(1801);

        long count = Fuzz.Run(_ => {
            string text = KindText.Runs(random, 30);
            byte[] expected = FastReference.Encode(text);

            for (int size = 0; size <= expected.Length + 2; size++)
            {
                byte[] buffer = new byte[size + 16];
                buffer.AsSpan().Fill(0xCC);
                bool ok = StenoEncoding.Instance.TryGetBytes(text, buffer.AsSpan(8, size), out int written);

                ok.ShouldBe(size >= expected.Length, $"TryGetBytes, size {size} of {expected.Length}");
                written.ShouldBe(ok ? expected.Length : 0);

                if (ok)
                    Fuzz.Hex(buffer.AsSpan(8, written)).ShouldBe(Fuzz.Hex(expected));

                (buffer.AsSpan(0, 8).ContainsAnyExcept((byte)0xCC) || buffer.AsSpan(8 + size).ContainsAnyExcept((byte)0xCC)).ShouldBeFalse();
            }

            for (int size = 0; size <= text.Length + 2; size++)
            {
                char[] buffer = new char[size + 16];
                bool ok = StenoEncoding.Instance.TryGetChars(expected, buffer.AsSpan(8, size), out int written);

                ok.ShouldBe(size >= text.Length, $"TryGetChars, size {size} of {text.Length}");

                if (ok)
                    new string(buffer, 8, written).ShouldBe(text);
            }
        });

        TestContext.WriteLine($"{count} texts");
    }

    /// <summary>
    /// The comparer against code point by code point comparison and the order of the bytes, for all strings of up to 3 units over an alphabet with
    /// every kind of surrogate and the code units around them, and random strings that share long prefixes.
    /// </summary>
    [TestMethod]
    public void CompareCodePoints_BruteForce()
    {
        char[] alphabet = ['a', 'é', '퟿', '\uD800', '\uD801', '\uDBFF', '\uDC00', '\uDC01', '\uDFFF', '', '｡', '￿'];
        var strings = new List<string> { string.Empty };

        for (int length = 1; length <= 3; length++)
        {
            var next = new List<string>();

            foreach (string s in strings.Where(s => s.Length == length - 1))
                next.AddRange(alphabet.Select(c => s + c));

            strings.AddRange(next);
        }

        byte[][] bytes = [.. strings.Select(s => StenoEncoding.Instance.GetBytes(s))];
        var codePoints = strings.Select(TestData.ToCodePoints).ToArray();

        for (int i = 0; i < strings.Count; i++)
        {
            for (int j = 0; j < strings.Count; j++)
            {
                int expected = Math.Sign(Compare(codePoints[i], codePoints[j]));

                if (Math.Sign(StenoEncoding.CompareCodePoints(strings[i], strings[j])) != expected ||
                    Math.Sign(StenoEncoding.CodePointComparer.Compare(strings[i], strings[j])) != expected ||
                    Math.Sign(bytes[i].AsSpan().SequenceCompareTo(bytes[j])) != expected)
                {
                    throw new ShouldAssertException($"{Fuzz.Describe(strings[i])} against {Fuzz.Describe(strings[j])}: expected {expected}");
                }
            }
        }

        var random = new Random(1501);

        Fuzz.Run(_ => {
            string prefix = KindText.Runs(random, 40);
            string x = prefix + new string([.. Enumerable.Range(0, random.Next(4)).Select(_ => alphabet[random.Next(alphabet.Length)])]);
            string y = prefix + new string([.. Enumerable.Range(0, random.Next(4)).Select(_ => alphabet[random.Next(alphabet.Length)])]);
            int expected = Math.Sign(Compare(TestData.ToCodePoints(x), TestData.ToCodePoints(y)));

            Math.Sign(StenoEncoding.CompareCodePoints(x, y)).ShouldBe(expected, $"{Fuzz.Describe(x)} against {Fuzz.Describe(y)}");
        });

        static int Compare(List<int> a, List<int> b)
        {
            for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
            {
                if (a[i] != b[i])
                    return a[i].CompareTo(b[i]);
            }

            return a.Count.CompareTo(b.Count);
        }
    }

    /// <summary>
    /// IsAsciiText against its definition (decode with the oracle, then look at the code points), for every byte string of 2 bytes and random strings
    /// of the byte values that it accepts with damage.
    /// </summary>
    [TestMethod]
    public void IsAsciiText_BruteForce()
    {
        for (int a = 0; a < 256; a++)
        {
            for (int b = 0; b < 256; b++)
            {
                byte[] bytes = [(byte)a, (byte)b];

                if (StenoEncoding.IsAsciiText(bytes) != Definition(bytes))
                    throw new ShouldAssertException($"IsAsciiText({a:X2}{b:X2})");
            }
        }

        var random = new Random(1601);
        byte[] good = [.. Enumerable.Range(0, 256).Where(b => b is 2 or 3 or 5 or (>= 7 and <= 0x65)).Select(b => (byte)b)];

        Fuzz.Run(_ => {
            byte[] bytes = new byte[random.Next(0, 70)];

            for (int j = 0; j < bytes.Length; j++)
                bytes[j] = good[random.Next(good.Length)];

            if (bytes.Length > 0 && random.Next(2) == 0)
                bytes[random.Next(bytes.Length)] = (byte)random.Next(256);

            StenoEncoding.IsAsciiText(bytes).ShouldBe(Definition(bytes), Fuzz.Hex(bytes));
        });

        static bool Definition(byte[] bytes) =>
            FastReference.Decode(bytes).Text is { } text && text.All(c => c is (>= ' ' and <= '~') or '\t' or '\n' or '\r');
    }
}
