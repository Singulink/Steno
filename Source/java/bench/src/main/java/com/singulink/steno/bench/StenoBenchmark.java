package com.singulink.steno.bench;

import com.singulink.steno.Steno;
import com.singulink.steno.StenoDecodeException;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.TimeUnit;
import org.openjdk.jmh.annotations.Benchmark;
import org.openjdk.jmh.annotations.BenchmarkMode;
import org.openjdk.jmh.annotations.Fork;
import org.openjdk.jmh.annotations.Level;
import org.openjdk.jmh.annotations.Measurement;
import org.openjdk.jmh.annotations.Mode;
import org.openjdk.jmh.annotations.OutputTimeUnit;
import org.openjdk.jmh.annotations.Param;
import org.openjdk.jmh.annotations.Scope;
import org.openjdk.jmh.annotations.Setup;
import org.openjdk.jmh.annotations.State;
import org.openjdk.jmh.annotations.Warmup;
import org.openjdk.jmh.infra.Blackhole;

/**
 * Encode and decode of the shared workloads, with the Steno Encoding and with the JDK's UTF-8 and UTF-16 as the
 * baselines.
 *
 * <p>One operation processes every value of the workload once (one value for a document), in a plain loop. The inputs
 * are prepared in {@link #setUp()}. Each result goes to the {@link Blackhole}. Java strings are immutable, so encode
 * returns a new byte array and decode a new string, for both codecs; {@link #stenoEncodeIntoBuffer} also measures the
 * Steno form that writes into a byte array allocated once.
 *
 * <p>The settings below are those of the full run (about 13 minutes for its 196 benchmarks, of the about 25 minutes of
 * the whole run); the command line can override them.
 */
@State(Scope.Benchmark)
@BenchmarkMode(Mode.AverageTime)
@OutputTimeUnit(TimeUnit.NANOSECONDS)
@Warmup(iterations = 3, time = 400, timeUnit = TimeUnit.MILLISECONDS)
@Measurement(iterations = 5, time = 500, timeUnit = TimeUnit.MILLISECONDS)
@Fork(
        value = 1,
        jvmArgsAppend = {"-Xms512m", "-Xmx512m"})
public class StenoBenchmark {
    /** The text of the workload. */
    @Param({
        "english",
        "french",
        "polish",
        "vietnamese",
        "russian",
        "greek",
        "arabic",
        "urdu",
        "hindi",
        "thai",
        "amharic",
        "chinese",
        "japanese",
        "korean"
    })
    public String text;

    /** {@code document} (the whole document as one value) or {@code values} (values of at most 16 UTF-16 units). */
    @Param({"document", "values"})
    public String kind;

    private String[] values;
    private byte[][] stenoBytes;
    private byte[][] utf8Bytes;
    private byte[][] utf16Bytes;
    private byte[] buffer;

    /**
     * Reads the workload and prepares the encoded inputs of the decode benchmarks.
     *
     * @throws StenoDecodeException never: the bytes come from the encoder
     */
    @Setup(Level.Trial)
    public void setUp() throws StenoDecodeException {
        values = Workload.read(text, kind);
        stenoBytes = new byte[values.length][];
        utf8Bytes = new byte[values.length][];
        utf16Bytes = new byte[values.length][];
        int max = 0;

        for (int i = 0; i < values.length; i++) {
            stenoBytes[i] = Steno.encode(values[i]);
            utf8Bytes[i] = values[i].getBytes(StandardCharsets.UTF_8);
            utf16Bytes[i] = values[i].getBytes(StandardCharsets.UTF_16LE);
            max = Math.max(max, Steno.maxEncodedLength(values[i].length()));

            if (!Steno.decode(stenoBytes[i]).equals(values[i])) {
                throw new IllegalStateException("The workload does not round-trip: " + text + " " + kind);
            }
        }

        buffer = new byte[max];
    }

    /**
     * Steno: {@code String} to a new byte array.
     *
     * @param bh the blackhole
     */
    @Benchmark
    public void stenoEncode(Blackhole bh) {
        for (String value : values) {
            bh.consume(Steno.encode(value));
        }
    }

    /**
     * Steno: {@code String} into a byte array allocated once.
     *
     * @return the total number of bytes written
     */
    @Benchmark
    public int stenoEncodeIntoBuffer() {
        byte[] out = buffer;
        int total = 0;

        for (String value : values) {
            total += Steno.encode(value, out, 0);
        }

        return total;
    }

    /**
     * Steno: bytes to a new {@code String}.
     *
     * @param bh the blackhole
     * @throws StenoDecodeException never: the bytes are valid
     */
    @Benchmark
    public void stenoDecode(Blackhole bh) throws StenoDecodeException {
        for (byte[] bytes : stenoBytes) {
            bh.consume(Steno.decode(bytes));
        }
    }

    /**
     * Baseline: {@code String.getBytes(UTF_8)}.
     *
     * @param bh the blackhole
     */
    @Benchmark
    public void utf8Encode(Blackhole bh) {
        for (String value : values) {
            bh.consume(value.getBytes(StandardCharsets.UTF_8));
        }
    }

    /**
     * Baseline: {@code new String(bytes, UTF_8)}.
     *
     * @param bh the blackhole
     */
    @Benchmark
    public void utf8Decode(Blackhole bh) {
        for (byte[] bytes : utf8Bytes) {
            bh.consume(new String(bytes, StandardCharsets.UTF_8));
        }
    }

    /**
     * Baseline: {@code String.getBytes(UTF_16LE)}.
     *
     * @param bh the blackhole
     */
    @Benchmark
    public void utf16Encode(Blackhole bh) {
        for (String value : values) {
            bh.consume(value.getBytes(StandardCharsets.UTF_16LE));
        }
    }

    /**
     * Baseline: {@code new String(bytes, UTF_16LE)}.
     *
     * @param bh the blackhole
     */
    @Benchmark
    public void utf16Decode(Blackhole bh) {
        for (byte[] bytes : utf16Bytes) {
            bh.consume(new String(bytes, StandardCharsets.UTF_16LE));
        }
    }
}
