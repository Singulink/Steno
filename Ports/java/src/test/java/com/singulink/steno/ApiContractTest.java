package com.singulink.steno;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.nio.BufferOverflowException;
import java.nio.ByteBuffer;
import java.nio.CharBuffer;
import java.nio.ReadOnlyBufferException;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.atomic.AtomicInteger;
import org.junit.jupiter.api.Test;

/** Tests of the API contract, re-entrancy, the per-thread scratch, concurrency and memory. */
class ApiContractTest {
    /** A CharSequence whose charAt encodes and decodes other values on the same thread. */
    static final class Reentrant implements CharSequence {
        final String s;
        final AtomicInteger calls = new AtomicInteger();

        Reentrant(String s) {
            this.s = s;
        }

        @Override
        public int length() {
            return s.length();
        }

        @Override
        public char charAt(int index) {
            calls.incrementAndGet();
            // Nested calls of every kind, short and long.
            String inner = "Ж" + index + "日本" + "😀";
            byte[] b = Steno.encode(inner);
            try {
                assertEquals(inner, Steno.decode(b));
            } catch (StenoDecodeException e) {
                throw new AssertionError(e);
            }
            assertArrayEquals(OracleCodec.encode(inner), b);
            return s.charAt(index);
        }

        @Override
        public CharSequence subSequence(int start, int end) {
            return s.subSequence(start, end);
        }

        @Override
        public String toString() {
            return Steno.encode("x").length + s;
        }
    }

    @Test
    void reentrantCharSequence() {
        ValueGenerator g = new ValueGenerator(41);
        for (int i = 0; i < 300; i++) {
            String text = ValueGenerator.utf16(g.value(i % 2 == 0 ? 50 : 600));
            Reentrant r = new Reentrant(text);
            assertArrayEquals(OracleCodec.encode(text), Steno.encode(r), text);
            byte[] dst = new byte[Steno.maxEncodedLength(text.length())];
            int n = Steno.encode(r, dst, 0);
            assertArrayEquals(OracleCodec.encode(text), Arrays.copyOf(dst, n));
            assertTrue(Steno.compareCodePoints(r, text) == 0);
        }
    }

    /** A CharSequence that throws at a given index. */
    static final class Throwing implements CharSequence {
        final String s;
        final int at;

        Throwing(String s, int at) {
            this.s = s;
            this.at = at;
        }

        @Override
        public int length() {
            return s.length();
        }

        @Override
        public char charAt(int index) {
            if (index == at) throw new IllegalStateException("boom");
            return s.charAt(index);
        }

        @Override
        public CharSequence subSequence(int start, int end) {
            return s.subSequence(start, end);
        }
    }

    @Test
    void callThatThrowsLeavesNothingBehind() throws StenoDecodeException {
        ValueGenerator g = new ValueGenerator(42);
        for (int i = 0; i < 500; i++) {
            String text = ValueGenerator.utf16(g.value(100));
            if (text.isEmpty()) continue;
            assertThrows(
                    IllegalStateException.class, () -> Steno.encode(new Throwing(text, g.r.nextInt(text.length()))));
            // An invalid decode in the middle.
            byte[] bad = Arrays.copyOf(OracleCodec.encode(text), OracleCodec.encode(text).length + 1);
            bad[bad.length - 1] = 0;
            assertThrows(StenoDecodeException.class, () -> Steno.decode(bad));
            String next = ValueGenerator.utf16(g.value(100));
            assertArrayEquals(OracleCodec.encode(next), Steno.encode(next));
            assertArrayEquals(OracleCodec.encode(next), Steno.encode(new StringBuilder(next)));
            assertEquals(next, Steno.decode(OracleCodec.encode(next)));
        }
    }

    @Test
    void charSequenceThatChangesWhileRead() {
        // A StringBuilder that another call shortens: no crash other than an exception, and later calls are right.
        StringBuilder sb = new StringBuilder("abcdefgh日本語");
        CharSequence shrinking = new CharSequence() {
            int reads;

            @Override
            public int length() {
                return reads++ == 0 ? 50 : sb.length();
            }

            @Override
            public char charAt(int index) {
                return sb.charAt(index);
            }

            @Override
            public CharSequence subSequence(int start, int end) {
                return sb.subSequence(start, end);
            }
        };
        assertThrows(IndexOutOfBoundsException.class, () -> Steno.encode(shrinking));
        assertArrayEquals(OracleCodec.encode("abc"), Steno.encode("abc"));
    }

    @Test
    void byteBufferContract() throws StenoDecodeException {
        // Read-only destination: the documented exception is ReadOnlyBufferException.
        ByteBuffer ro = ByteBuffer.allocate(100).asReadOnlyBuffer();
        assertThrows(ReadOnlyBufferException.class, () -> Steno.encode("abc", ro));
        ByteBuffer roSmall = ByteBuffer.allocate(1).asReadOnlyBuffer();
        assertThrows(ReadOnlyBufferException.class, () -> Steno.encode("abcdef", roSmall), "read-only and too small");

        // An exception of the text itself is not reported as a destination that is too small.
        CharSequence broken = new CharSequence() {
            @Override
            public int length() {
                return 4;
            }

            @Override
            public char charAt(int index) {
                if (index == 2) {
                    throw new IndexOutOfBoundsException("the text changed");
                }

                return 'a';
            }

            @Override
            public CharSequence subSequence(int start, int end) {
                throw new UnsupportedOperationException();
            }
        };

        IndexOutOfBoundsException fromText =
                assertThrows(IndexOutOfBoundsException.class, () -> Steno.encode(broken, ByteBuffer.allocate(100)));
        assertEquals("the text changed", fromText.getMessage());
        assertEquals(
                "the text changed",
                assertThrows(IndexOutOfBoundsException.class, () -> Steno.encode(broken, new byte[100], 0))
                        .getMessage());

        // Too small: BufferOverflowException, position unchanged.
        ByteBuffer small = ByteBuffer.allocate(4);
        small.position(1);
        assertThrows(BufferOverflowException.class, () -> Steno.encode("abcd", small));
        assertEquals(1, small.position());
        ByteBuffer dsmall = ByteBuffer.allocateDirect(4);
        assertThrows(BufferOverflowException.class, () -> Steno.encode("abcdefg", dsmall));
        assertEquals(0, dsmall.position());

        // Decoding a buffer: error offset counts from the position; position unchanged on error.
        byte[] b = {0x48, 0x48, 0x48, 0x00};
        ByteBuffer in = ByteBuffer.wrap(new byte[] {0x00, 0x00, 0x48, 0x48, 0x48, 0x00}, 2, 4);
        StenoDecodeException e = assertThrows(StenoDecodeException.class, () -> Steno.decode(in));
        assertEquals(3, e.getOffset());
        assertEquals(2, in.position());
        ByteBuffer din = ByteBuffer.allocateDirect(6);
        din.put(new byte[] {0x00, 0x00}).put(b).position(2);
        e = assertThrows(StenoDecodeException.class, () -> Steno.decode(din));
        assertEquals(3, e.getOffset());
        assertEquals(2, din.position());
        assertEquals("aaa", Steno.decode(ByteBuffer.wrap(b, 0, 3)));
    }

    @Test
    void argumentChecks() {
        byte[] dst = new byte[10];
        assertThrows(IndexOutOfBoundsException.class, () -> Steno.encode("a", dst, -1));
        assertThrows(IndexOutOfBoundsException.class, () -> Steno.encode("a", dst, 11));
        assertEquals(0, Steno.encode("", dst, 10));
        assertThrows(IndexOutOfBoundsException.class, () -> Steno.encode("a", dst, 10));
        assertThrows(IndexOutOfBoundsException.class, () -> Steno.decode(dst, 5, 6));
        assertThrows(IndexOutOfBoundsException.class, () -> Steno.decode(dst, -1, 1));
        assertThrows(NullPointerException.class, () -> Steno.encode((CharSequence) null));
        assertThrows(NullPointerException.class, () -> Steno.decode((byte[]) null));
        assertThrows(IllegalArgumentException.class, () -> Steno.encodeCodePoints(new int[] {-1}));
        assertThrows(IllegalArgumentException.class, () -> Steno.encodeCodePoints(new int[] {0x110000}));
        assertThrows(IllegalArgumentException.class, () -> Steno.encodeCodePoints(new int[] {0xDBFF, 0xDC00}));
        assertArrayEquals(
                OracleCodec.encode(new int[] {0xDC00, 0xDBFF}), Steno.encodeCodePoints(new int[] {0xDC00, 0xDBFF}));
        assertThrows(IllegalArgumentException.class, () -> Steno.maxEncodedLength(-1));
        assertEquals(3 * (Integer.MAX_VALUE / 3), Steno.maxEncodedLength(Integer.MAX_VALUE / 3));
        assertThrows(IllegalArgumentException.class, () -> Steno.maxEncodedLength(Integer.MAX_VALUE / 3 + 1));
        assertThrows(IllegalArgumentException.class, () -> Steno.maxDecodedLength(Integer.MAX_VALUE / 2 + 1));
    }

    @Test
    void compareCodePointsAgainstOracle() {
        ValueGenerator g = new ValueGenerator(OracleOneShotTest.SEED + 20);
        long start = System.nanoTime();
        int count = 0;
        while (OracleOneShotTest.timeLeft(start)) {
            String a = g.units(6);
            String b = g.r.nextBoolean() ? a + g.units(3) : g.units(6);
            if (g.r.nextBoolean() && !a.isEmpty()) {
                b = a.substring(0, g.r.nextInt(a.length() + 1)) + g.units(3);
            }
            int[] x = OracleCodec.codePoints(a);
            int[] y = OracleCodec.codePoints(b);
            int want = Integer.signum(Arrays.compare(x, y));
            assertEquals(want, Integer.signum(Steno.compareCodePoints(a, b)), a + " | " + b);
            assertEquals(-want, Integer.signum(Steno.compareCodePoints(b, a)));
            assertEquals(
                    want, Integer.signum(Steno.CODE_POINT_ORDER.compare(new StringBuilder(a), CharBuffer.wrap(b))));
            assertEquals(want, Integer.signum(Arrays.compareUnsigned(OracleCodec.encode(x), OracleCodec.encode(y))));
            count++;
        }
        System.out.println("[fuzz] compareCodePoints: " + count + " pairs");
    }

    @Test
    void manyThreadsWithCacheEvictions() throws Exception {
        // Values that each visit many different layouts (random windows as main and second windows), far more than the
        // 1,024 cache entries, encoded and decoded on many platform and virtual threads at once.
        ValueGenerator g = new ValueGenerator(OracleOneShotTest.SEED + 21);
        List<String> texts = new ArrayList<>();
        List<byte[]> want = new ArrayList<>();
        for (int i = 0; i < 3000; i++) {
            int[] cps = new int[1 + g.r.nextInt(400)];
            for (int j = 0; j < cps.length; j++) {
                int x = g.r.nextInt(10);
                cps[j] = x < 4
                        ? g.anyWindowCp()
                        : x < 6
                                ? 0x61
                                : x < 7
                                        ? 0x4E00 + g.r.nextInt(100)
                                        : x < 8
                                                ? 0xAC00
                                                : g.cpFrom(
                                                        ValueGenerator.POOLS[g.r.nextInt(ValueGenerator.POOLS.length)]);
            }
            ValueGenerator.fix(cps);
            String s = ValueGenerator.utf16(cps);
            texts.add(s);
            want.add(OracleCodec.encode(cps));
        }
        int threads = Math.max(4, Runtime.getRuntime().availableProcessors());
        AtomicInteger bad = new AtomicInteger();
        long millis = Math.max(500, OracleOneShotTest.MILLIS);
        for (boolean virtual : new boolean[] {false, true}) {
            ExecutorService pool = virtual ? newVirtual() : Executors.newFixedThreadPool(threads);
            if (pool == null) continue;
            long end = System.nanoTime() + millis * 1_000_000L;
            List<Future<Integer>> fs = new ArrayList<>();
            for (int t = 0; t < (virtual ? 64 : threads); t++) {
                final int seed = t;
                fs.add(pool.submit(() -> {
                    java.util.SplittableRandom r = new java.util.SplittableRandom(seed);
                    int n = 0;
                    while (System.nanoTime() < end) {
                        int i = r.nextInt(texts.size());
                        byte[] got = Steno.encode(texts.get(i));
                        if (!Arrays.equals(got, want.get(i))) bad.incrementAndGet();
                        if (!Steno.decode(want.get(i)).equals(texts.get(i))) bad.incrementAndGet();
                        if (!new String(want.get(i), StenoCharset.INSTANCE).equals(texts.get(i))) bad.incrementAndGet();
                        if (virtual && (n & 15) == 0) Thread.yield();
                        n++;
                    }
                    return n;
                }));
            }
            int total = 0;
            for (Future<Integer> f : fs) total += f.get();
            pool.shutdown();
            System.out.println("[fuzz] concurrency " + (virtual ? "virtual" : "platform") + ": " + total
                    + " round trips, mismatches " + bad.get());
        }
        assertEquals(0, bad.get());
    }

    private static ExecutorService newVirtual() {
        try {
            return (ExecutorService)
                    Executors.class.getMethod("newVirtualThreadPerTaskExecutor").invoke(null);
        } catch (ReflectiveOperationException e) {
            return null; // Java 17
        }
    }

    @Test
    void memoryStaysBounded() throws Exception {
        // Many distinct layouts for a while; the retained heap after GC must not grow.
        ValueGenerator g = new ValueGenerator(OracleOneShotTest.SEED + 22);
        Runtime rt = Runtime.getRuntime();
        long[] samples = new long[6];
        for (int round = 0; round < samples.length; round++) {
            for (int i = 0; i < (OracleOneShotTest.LONG ? 4000 : 1500); i++) {
                int[] cps = new int[60];
                for (int j = 0; j < cps.length; j++) {
                    cps[j] = g.r.nextInt(3) == 0 ? g.anyWindowCp() : g.r.nextInt(2) == 0 ? 0x3042 : 0xAC01;
                }
                ValueGenerator.fix(cps);
                byte[] b = Steno.encode(ValueGenerator.utf16(cps));
                Steno.decode(b);
            }
            System.gc();
            Thread.sleep(50);
            System.gc();
            samples[round] = rt.totalMemory() - rt.freeMemory();
        }
        System.out.println("[fuzz] retained heap after rounds (KB): "
                + Arrays.toString(Arrays.stream(samples).map(x -> x / 1024).toArray()));
        assertTrue(samples[samples.length - 1] < samples[1] + 4 * 1024 * 1024);
        int filled = 0;
        long bytes = 0;
        java.lang.reflect.Field f = Layout.class.getDeclaredField("CACHE");
        f.setAccessible(true);
        for (Layout l : (Layout[]) f.get(null)) {
            if (l != null) {
                filled++;
                bytes += 16L * l.count + 256 + (l.cjkByte != null ? 256 + 512 : 0) + 200;
            }
        }
        System.out.println("[fuzz] layout cache: " + filled + " entries, about " + bytes / 1024 + " KB");
        assertTrue(filled <= 1024);
    }
}
