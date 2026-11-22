package com.singulink.steno.bench;

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
import org.openjdk.jmh.annotations.TearDown;
import org.openjdk.jmh.annotations.Warmup;
import org.openjdk.jmh.infra.Blackhole;

/**
 * The alternatives that a Java developer would use, on the same workloads: SCSU and BOCU-1 from ICU4J, and Deflate,
 * Zstandard, Brotli and LZ4 on the UTF-8 bytes of each value. See {@link Codecs} for how each library is driven.
 *
 * <p>One operation processes every value of the workload once. The codec object (encoder, decoder, buffers) is made
 * once per trial in {@link #setUp()}. The method picks the codec once per operation; the loop over the values calls the
 * concrete class directly. The alternatives run on 10 of the 14 texts, with shorter iterations than
 * {@link StenoBenchmark}, so that the full run fits in about 25 minutes.
 */
@State(Scope.Benchmark)
@BenchmarkMode(Mode.AverageTime)
@OutputTimeUnit(TimeUnit.NANOSECONDS)
@Warmup(iterations = 2, time = 200, timeUnit = TimeUnit.MILLISECONDS)
@Measurement(iterations = 3, time = 300, timeUnit = TimeUnit.MILLISECONDS)
@Fork(
        value = 1,
        jvmArgsAppend = {"-Xms512m", "-Xmx512m"})
public class AlternativesBenchmark {
    /** The text of the workload: the representative subset of the common rules. */
    @Param({"english", "french", "russian", "arabic", "hindi", "thai", "amharic", "chinese", "japanese", "korean"})
    public String text;

    /** {@code document} or {@code values}. */
    @Param({"document", "values"})
    public String kind;

    /** The codec, by its name in the common CSV. */
    @Param({
        "scsu-icu4j",
        "bocu1-icu4j",
        "deflate-fast",
        "deflate-max",
        "zstd-1",
        "zstd-19",
        "brotli-1",
        "brotli-11",
        "lz4",
        "lz4-hc"
    })
    public String codec;

    private String[] values;
    private byte[][] encoded;
    private Codecs.IcuCodec icu;
    private Codecs.DeflateCodec deflate;
    private Codecs.ZstdCodec zstd;
    private Codecs.BrotliCodec brotli;
    private Codecs.Lz4Codec lz4;
    private Codecs.TextCodec any;

    /** Reads the workload, makes the codec and prepares the encoded inputs of the decode benchmark. */
    @Setup(Level.Trial)
    public void setUp() {
        values = Workload.read(text, kind);
        int maxUnits = 0;

        for (String value : values) {
            maxUnits = Math.max(maxUnits, value.length());
        }

        any = Codecs.create(codec, maxUnits);

        if (any instanceof Codecs.IcuCodec) {
            icu = (Codecs.IcuCodec) any;
        } else if (any instanceof Codecs.DeflateCodec) {
            deflate = (Codecs.DeflateCodec) any;
        } else if (any instanceof Codecs.ZstdCodec) {
            zstd = (Codecs.ZstdCodec) any;
        } else if (any instanceof Codecs.BrotliCodec) {
            brotli = (Codecs.BrotliCodec) any;
        } else {
            lz4 = (Codecs.Lz4Codec) any;
        }

        encoded = new byte[values.length][];

        for (int i = 0; i < values.length; i++) {
            encoded[i] = any.encode(values[i]);

            if (!any.decode(encoded[i]).equals(values[i])) {
                throw new IllegalStateException(codec + " does not round-trip " + text + " " + kind);
            }
        }
    }

    /** Frees the native state of the codec. */
    @TearDown(Level.Trial)
    public void tearDown() {
        any.close();
    }

    /**
     * Text to bytes, every value once.
     *
     * @param bh the blackhole
     */
    @Benchmark
    public void encode(Blackhole bh) {
        String[] v = values;

        if (icu != null) {
            for (String value : v) {
                bh.consume(icu.encode(value));
            }
        } else if (deflate != null) {
            for (String value : v) {
                bh.consume(deflate.encode(value));
            }
        } else if (zstd != null) {
            for (String value : v) {
                bh.consume(zstd.encode(value));
            }
        } else if (brotli != null) {
            for (String value : v) {
                bh.consume(brotli.encode(value));
            }
        } else {
            for (String value : v) {
                bh.consume(lz4.encode(value));
            }
        }
    }

    /**
     * Bytes to text, every value once.
     *
     * @param bh the blackhole
     */
    @Benchmark
    public void decode(Blackhole bh) {
        byte[][] e = encoded;

        if (icu != null) {
            for (byte[] bytes : e) {
                bh.consume(icu.decode(bytes));
            }
        } else if (deflate != null) {
            for (byte[] bytes : e) {
                bh.consume(deflate.decode(bytes));
            }
        } else if (zstd != null) {
            for (byte[] bytes : e) {
                bh.consume(zstd.decode(bytes));
            }
        } else if (brotli != null) {
            for (byte[] bytes : e) {
                bh.consume(brotli.decode(bytes));
            }
        } else {
            for (byte[] bytes : e) {
                bh.consume(lz4.decode(bytes));
            }
        }
    }
}
