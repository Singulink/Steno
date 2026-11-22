package com.singulink.steno.bench;

import com.aayushatharva.brotli4j.Brotli4jLoader;
import com.aayushatharva.brotli4j.decoder.Decoder;
import com.aayushatharva.brotli4j.decoder.DecoderJNI;
import com.aayushatharva.brotli4j.decoder.DirectDecompress;
import com.aayushatharva.brotli4j.encoder.Encoder;
import com.github.luben.zstd.ZstdCompressCtx;
import com.github.luben.zstd.ZstdDecompressCtx;
import com.singulink.steno.Steno;
import com.singulink.steno.StenoDecodeException;
import java.io.IOException;
import java.io.UncheckedIOException;
import java.nio.ByteBuffer;
import java.nio.CharBuffer;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.Charset;
import java.nio.charset.CharsetDecoder;
import java.nio.charset.CharsetEncoder;
import java.nio.charset.CoderResult;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.zip.DataFormatException;
import java.util.zip.Deflater;
import java.util.zip.Inflater;
import net.jpountz.lz4.LZ4Compressor;
import net.jpountz.lz4.LZ4Factory;
import net.jpountz.lz4.LZ4SafeDecompressor;

/**
 * The codecs of the benchmark, each driven the way its documentation shows for many small inputs: one object per codec
 * with its encoder, decoder and buffers made once and reused for every value. Each encodes a {@code String} to a new
 * {@code byte[]} and decodes back to a new {@code String}; the compressors work on the UTF-8 bytes of the text.
 *
 * <p>The benchmark calls the concrete classes directly (each is final, so the calls are monomorphic). The
 * {@link TextCodec} interface is only for the size table and the checks.
 */
final class Codecs {
    /** The alternatives, by their name in the common CSV. */
    static final String[] ALTERNATIVES = {
        "scsu-icu4j",
        "bocu1-icu4j",
        "deflate-fast",
        "deflate-max",
        "zstd-1",
        "zstd-19",
        "brotli-1",
        "brotli-11",
        "lz4",
        "lz4-hc",
    };

    private Codecs() {}

    /** A codec, for the size table. */
    interface TextCodec extends AutoCloseable {
        byte[] encode(String text);

        String decode(byte[] bytes);

        @Override
        default void close() {}
    }

    /**
     * Creates a codec by its name in the common CSV.
     *
     * @param maxUnits the largest number of UTF-16 code units of a value, to size the reused buffers
     */
    static TextCodec create(String name, int maxUnits) {
        switch (name) {
            case "steno":
                return new StenoCodec();
            case "utf8":
                return new JdkCodec(StandardCharsets.UTF_8);
            case "utf16":
                return new JdkCodec(StandardCharsets.UTF_16LE);
            case "scsu-icu4j":
                return new IcuCodec("SCSU", maxUnits);
            case "bocu1-icu4j":
                return new IcuCodec("BOCU-1", maxUnits);
            case "deflate-fast":
                return new DeflateCodec(Deflater.BEST_SPEED, maxUnits);
            case "deflate-max":
                return new DeflateCodec(Deflater.BEST_COMPRESSION, maxUnits);
            case "zstd-1":
                return new ZstdCodec(1, maxUnits);
            case "zstd-19":
                return new ZstdCodec(19, maxUnits);
            case "brotli-1":
                return new BrotliCodec(1);
            case "brotli-11":
                return new BrotliCodec(11);
            case "lz4":
                return new Lz4Codec(false, maxUnits);
            case "lz4-hc":
                return new Lz4Codec(true, maxUnits);
            default:
                throw new IllegalArgumentException("Unknown codec: " + name);
        }
    }

    /** The largest UTF-8 size of {@code units} UTF-16 code units. */
    static int maxUtf8(int units) {
        return 3 * units + 16;
    }

    /** The Steno Encoding, through the one-shot API. */
    static final class StenoCodec implements TextCodec {
        @Override
        public byte[] encode(String text) {
            return Steno.encode(text);
        }

        @Override
        public String decode(byte[] bytes) {
            try {
                return Steno.decode(bytes);
            } catch (StenoDecodeException e) {
                throw new IllegalStateException(e);
            }
        }
    }

    /** A charset of the JDK: {@code String.getBytes} and {@code new String}. */
    static final class JdkCodec implements TextCodec {
        private final Charset charset;

        JdkCodec(Charset charset) {
            this.charset = charset;
        }

        @Override
        public byte[] encode(String text) {
            return text.getBytes(charset);
        }

        @Override
        public String decode(byte[] bytes) {
            return new String(bytes, charset);
        }
    }

    /**
     * An ICU4J charset (SCSU or BOCU-1), found with {@code Charset.forName} through ICU's {@code CharsetProvider}. One
     * {@code CharsetEncoder} and one {@code CharsetDecoder} are reset and reused for every value, with output buffers
     * made once; the result is copied out into a new array or string.
     */
    static final class IcuCodec implements TextCodec {
        private final CharsetEncoder encoder;
        private final CharsetDecoder decoder;
        private final ByteBuffer bytesOut;
        private final CharBuffer charsOut;

        IcuCodec(String name, int maxUnits) {
            Charset charset = Charset.forName(name);

            if (!charset.getClass().getName().startsWith("com.ibm.icu.")) {
                throw new IllegalStateException(name + " does not come from ICU4J: " + charset.getClass());
            }

            encoder = charset.newEncoder();
            decoder = charset.newDecoder();
            bytesOut = ByteBuffer.allocate(4 * maxUnits + 16);
            charsOut = CharBuffer.allocate(maxUnits + 16);
        }

        @Override
        public byte[] encode(String text) {
            encoder.reset();
            bytesOut.clear();
            check(encoder.encode(CharBuffer.wrap(text), bytesOut, true));
            check(encoder.flush(bytesOut));
            return Arrays.copyOf(bytesOut.array(), bytesOut.position());
        }

        @Override
        public String decode(byte[] bytes) {
            decoder.reset();
            charsOut.clear();
            check(decoder.decode(ByteBuffer.wrap(bytes), charsOut, true));
            check(decoder.flush(charsOut));
            return new String(charsOut.array(), 0, charsOut.position());
        }

        private static void check(CoderResult result) {
            if (!result.isUnderflow()) {
                try {
                    result.throwException();
                } catch (CharacterCodingException e) {
                    throw new IllegalStateException(e);
                }
            }
        }
    }

    /**
     * Deflate (zlib format) from {@code java.util.zip}: one {@code Deflater} and one {@code Inflater}, reset for every
     * value, with buffers made once.
     */
    static final class DeflateCodec implements TextCodec {
        private final Deflater deflater;
        private final Inflater inflater = new Inflater();
        private final byte[] compressed;
        private final byte[] utf8;

        DeflateCodec(int level, int maxUnits) {
            deflater = new Deflater(level);
            utf8 = new byte[maxUtf8(maxUnits)];
            compressed = new byte[utf8.length + utf8.length / 100 + 64];
        }

        @Override
        public byte[] encode(String text) {
            deflater.reset();
            deflater.setInput(text.getBytes(StandardCharsets.UTF_8));
            deflater.finish();
            int n = deflater.deflate(compressed);
            return Arrays.copyOf(compressed, n);
        }

        @Override
        public String decode(byte[] bytes) {
            inflater.reset();
            inflater.setInput(bytes);

            try {
                return new String(utf8, 0, inflater.inflate(utf8), StandardCharsets.UTF_8);
            } catch (DataFormatException e) {
                throw new IllegalStateException(e);
            }
        }

        @Override
        public void close() {
            deflater.end();
            inflater.end();
        }
    }

    /** Zstandard from zstd-jni: one compression context with its level and one decompression context, reused. */
    static final class ZstdCodec implements TextCodec {
        private final ZstdCompressCtx compressor = new ZstdCompressCtx();
        private final ZstdDecompressCtx decompressor = new ZstdDecompressCtx();
        private final byte[] utf8;

        ZstdCodec(int level, int maxUnits) {
            compressor.setLevel(level);
            utf8 = new byte[maxUtf8(maxUnits)];
        }

        @Override
        public byte[] encode(String text) {
            return compressor.compress(text.getBytes(StandardCharsets.UTF_8));
        }

        @Override
        public String decode(byte[] bytes) {
            int n = decompressor.decompressByteArray(utf8, 0, utf8.length, bytes, 0, bytes.length);
            return new String(utf8, 0, n, StandardCharsets.UTF_8);
        }

        @Override
        public void close() {
            compressor.close();
            decompressor.close();
        }
    }

    /**
     * Brotli from brotli4j: its one-shot {@code Encoder.compress} with parameters made once, and
     * {@code Decoder.decompress}. brotli4j has no reusable one-shot encoder or decoder object, so each call sets up the
     * native state, as its documentation shows.
     */
    static final class BrotliCodec implements TextCodec {
        private final Encoder.Parameters parameters;

        BrotliCodec(int quality) {
            Brotli4jLoader.ensureAvailability();
            parameters = new Encoder.Parameters().setQuality(quality);
        }

        @Override
        public byte[] encode(String text) {
            try {
                return Encoder.compress(text.getBytes(StandardCharsets.UTF_8), parameters);
            } catch (IOException e) {
                throw new UncheckedIOException(e);
            }
        }

        @Override
        public String decode(byte[] bytes) {
            try {
                DirectDecompress result = Decoder.decompress(bytes);

                if (result.getResultStatus() != DecoderJNI.Status.DONE) {
                    throw new IllegalStateException("Brotli: " + result.getResultStatus());
                }

                return new String(result.getDecompressedData(), StandardCharsets.UTF_8);
            } catch (IOException e) {
                throw new UncheckedIOException(e);
            }
        }
    }

    /**
     * LZ4 block format from lz4-java: {@code LZ4Factory.fastestInstance()}, its fast or high-compression compressor and
     * its safe decompressor, made once; the decompressor writes into a buffer made once.
     */
    static final class Lz4Codec implements TextCodec {
        private final LZ4Compressor compressor;
        private final LZ4SafeDecompressor decompressor;
        private final byte[] utf8;

        Lz4Codec(boolean high, int maxUnits) {
            LZ4Factory factory = LZ4Factory.fastestInstance();
            compressor = high ? factory.highCompressor() : factory.fastCompressor();
            decompressor = factory.safeDecompressor();
            utf8 = new byte[maxUtf8(maxUnits)];
        }

        @Override
        public byte[] encode(String text) {
            return compressor.compress(text.getBytes(StandardCharsets.UTF_8));
        }

        @Override
        public String decode(byte[] bytes) {
            int n = decompressor.decompress(bytes, 0, bytes.length, utf8, 0, utf8.length);
            return new String(utf8, 0, n, StandardCharsets.UTF_8);
        }
    }
}
