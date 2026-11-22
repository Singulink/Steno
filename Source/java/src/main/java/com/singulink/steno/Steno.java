package com.singulink.steno;

import java.nio.BufferOverflowException;
import java.nio.ByteBuffer;
import java.nio.CharBuffer;
import java.nio.ReadOnlyBufferException;
import java.util.Arrays;
import java.util.Comparator;
import java.util.Objects;

/**
 * Encodes and decodes the Steno Encoding, version 1: a compact encoding of Unicode text whose bytes sort in the code
 * point order of the text.
 *
 * <p>This class is the one-shot API of the Java reference implementation of the Steno Encoding (Steno for short), which
 * is specified in <a href="https://github.com/Singulink/Steno/blob/main/FORMAT.md">Steno Encoding, version 1</a>. Each
 * method encodes or decodes its input as one whole value. For streams, use {@link StenoCharset}.
 *
 * <p>What the bytes guarantee:
 *
 * <ul>
 *   <li><b>Order:</b> the bytes of two values compared as unsigned bytes, with {@link Arrays#compareUnsigned(byte[],
 *       byte[])}, are in the order that {@link #compareCodePoints(CharSequence, CharSequence)} gives their text.
 *   <li><b>Prefixes:</b> the bytes of a prefix of a value are a prefix of the bytes of the value
 *       ({@link Arrays#mismatch(byte[], byte[])} tests it).
 *   <li><b>Uniqueness:</b> a value has exactly one encoding, so two values are equal exactly when their bytes are
 *       ({@link Arrays#equals(byte[], byte[])}).
 *   <li><b>No zero byte:</b> the byte 00 never occurs, so it can end a value inside a larger key.
 *   <li><b>No byte below 08 inside a code point:</b> the bytes 01..07 occur only as the first byte of a C0 control or
 *       the space (02 is tab, 03 line feed, 05 carriage return, 07 space), so line feeds, tabs and spaces can be found
 *       at the byte level. A line still cannot be decoded on its own: decoding starts at the first byte of the value.
 * </ul>
 *
 * <p>What to know before storing bytes:
 *
 * <ul>
 *   <li>The bytes carry no header or version number. Record that they are in the Steno Encoding, version 1, for example
 *       by the name {@value #CHARSET_NAME}.
 *   <li>The encoding is stateful: the bytes of a code point depend on the code points before it in the same value.
 *       Decoding starts at the first byte of a value, and the bytes of two values one after the other are not the bytes
 *       of the two texts together. To keep several values in one buffer, end each with a 00 byte or record its length.
 *   <li>The order is code point order, not the order of a language, and not the order of
 *       {@link String#compareTo(String)}.
 * </ul>
 *
 * <p>Java strings are UTF-16, and every sequence of UTF-16 code units is a value: an unpaired surrogate is a code point
 * of its own (U+D800..U+DFFF), so every {@link String} encodes, and decoding gives back exactly the same {@code char}s.
 * Encoding never fails. Decoding invalid Steno bytes throws a {@link StenoDecodeException} with the reason and the
 * offset.
 *
 * <p>All methods are thread safe. They share a bounded cache of layouts that does not lock (see the README for its size
 * and its worst case).
 */
public final class Steno {
    /** The name of the encoding, {@value}, the name of {@link StenoCharset}. Store it next to Steno bytes. */
    public static final String CHARSET_NAME = "x-steno";

    /**
     * Orders text by code point, the order in which the Steno bytes of the text sort. See
     * {@link #compareCodePoints(CharSequence, CharSequence)}.
     *
     * <p>It throws a {@link NullPointerException} for a {@code null} argument, as {@link String#CASE_INSENSITIVE_ORDER}
     * does; wrap it in {@link Comparator#nullsFirst(Comparator)} to allow {@code null}.
     */
    public static final Comparator<CharSequence> CODE_POINT_ORDER = Steno::compareCodePoints;

    /** The chars that the one-shot methods encode in one piece. */
    private static final int CHUNK = 1024;

    private static final byte[] EMPTY_BYTES = new byte[0];

    private static final int MAX_ARRAY = Integer.MAX_VALUE - 8;

    private static final long ASCII_LO_WORD = Swar.repeat(0x07);
    private static final long ASCII_END_WORD = Swar.repeat(0x66);

    private Steno() {}

    // ---------- Encoding ----------

    /**
     * Encodes text as one value into a new byte array.
     *
     * @param text the text to encode; an unpaired surrogate is encoded as a code point of its own
     * @return the Steno bytes of the text
     */
    public static byte[] encode(CharSequence text) {
        int n = text.length();

        if (n == 0) {
            return EMPTY_BYTES;
        }

        // A short string of ASCII text: bytes only, 8 at a time. Tried when the text starts with ASCII; a string that
        // turns out not to be all ASCII text costs one extra copy, which is why longer text goes the general way.
        if (n <= Scratch.CHARS && text instanceof String && text.charAt(0) < 0x80) {
            byte[] ascii = AsciiText.encode((String) text);

            if (ascii != null) {
                return ascii;
            }
        }

        if (n <= Scratch.CHARS) {
            Scratch scratch = Scratch.take();

            if (scratch != null) {
                try {
                    // Room for the worst case: the encoder never stops for room.
                    // The scratch encoder is in the initial state: every use resets it when it ends.
                    EncoderCore encoder = scratch.encoder;
                    copy(text, 0, n, scratch.chars);
                    encoder.encode(scratch.chars, 0, n, scratch.bytes, 0, scratch.bytes.length);
                    encoder.flush(scratch.bytes, encoder.dstPos, scratch.bytes.length);
                    return Arrays.copyOf(scratch.bytes, encoder.dstPos);
                } finally {
                    scratch.encoder.reset();
                    scratch.inUse = false;
                }
            }
        }

        // Small text gets room for the worst case. Larger text gets room for 1.5 bytes per char and grows if that is
        // not enough.
        long capacity = n <= CHUNK ? 3L * n : n + (n >> 1) + 16L;
        byte[] out = new byte[(int) Math.min(capacity, MAX_ARRAY)];
        EncoderCore encoder = new EncoderCore();
        char[] chunk = new char[Math.min(n, CHUNK)];
        int dp = 0;

        for (int pos = 0; pos < n; pos += CHUNK) {
            int len = Math.min(CHUNK, n - pos);
            copy(text, pos, len, chunk);
            int sp = 0;

            while (!encoder.encode(chunk, sp, len, out, dp, out.length)) {
                sp = encoder.srcPos;
                dp = encoder.dstPos;
                out = grow(out, n - pos - sp);
            }

            dp = encoder.dstPos;
        }

        while (!encoder.flush(out, dp, out.length)) {
            out = grow(out, 1);
        }

        dp = encoder.dstPos;
        return dp == out.length ? out : Arrays.copyOf(out, dp);
    }

    /**
     * Encodes text as one value into a byte array.
     *
     * <p>A destination with {@link #maxEncodedLength(int) maxEncodedLength(text.length())} bytes of room is always
     * large enough.
     *
     * @param text the text to encode
     * @param dst the destination
     * @param offset where in {@code dst} to write the first byte
     * @return the number of bytes written
     * @throws IndexOutOfBoundsException if {@code offset} is outside {@code dst}, or if the bytes do not fit; the bytes
     *     of {@code dst} from {@code offset} may then have been written
     */
    public static int encode(CharSequence text, byte[] dst, int offset) {
        Objects.checkFromToIndex(offset, dst.length, dst.length);
        int written = encodeBounded(text, dst, offset, dst.length);

        if (written < 0) {
            throw new IndexOutOfBoundsException("The destination is too small for the encoded value.");
        }

        return written;
    }

    /**
     * Encodes text as one value into a byte buffer, from its position.
     *
     * @param text the text to encode
     * @param dst the destination; its position moves past the bytes written
     * @return the number of bytes written
     * @throws BufferOverflowException if the bytes do not fit in the remaining room; the position of {@code dst} is
     *     then unchanged, but the bytes after it may have been written
     * @throws java.nio.ReadOnlyBufferException if {@code dst} is read-only
     */
    public static int encode(CharSequence text, ByteBuffer dst) {
        if (dst.isReadOnly()) {
            throw new ReadOnlyBufferException();
        }

        if (dst.hasArray()) {
            // Encode into the array, but only up to the limit of the buffer.
            int base = dst.arrayOffset();
            int written = encodeBounded(text, dst.array(), base + dst.position(), base + dst.limit());

            if (written < 0) {
                throw new BufferOverflowException();
            }

            dst.position(dst.position() + written);
            return written;
        }

        byte[] bytes = encode(text);

        if (bytes.length > dst.remaining()) {
            throw new BufferOverflowException();
        }

        dst.put(bytes);
        return bytes.length;
    }

    /**
     * Encodes a value given as code points into a new byte array.
     *
     * <p>Use it for values that are not text in a {@link CharSequence}. Each code point takes at most 4 bytes.
     *
     * @param codePoints the code points of the value, each in U+0000..U+10FFFF; surrogate code points are allowed, but
     *     not a high surrogate code point directly followed by a low one
     * @return the Steno bytes of the value
     * @throws IllegalArgumentException if a number is not a code point, or if the code points are not a value
     *     (FORMAT.md section 3)
     */
    public static byte[] encodeCodePoints(int[] codePoints) {
        for (int i = 0; i < codePoints.length; i++) {
            int c = codePoints[i];

            if (c < 0 || c > Layout.MAX_CODE_POINT) {
                throw new IllegalArgumentException("Not a code point at index " + i + ": " + c + ".");
            }

            if (i > 0 && c >= 0xDC00 && c <= 0xDFFF && codePoints[i - 1] >= 0xD800 && codePoints[i - 1] <= 0xDBFF) {
                throw new IllegalArgumentException(
                        "Not a value: a high surrogate code point is directly followed by a low surrogate "
                                + "code point at index " + i + ".");
            }
        }

        return encodeCodePoints(codePoints, (int) Math.min(4L * codePoints.length, MAX_ARRAY));
    }

    /**
     * Encodes checked code points into an array of {@code capacity} bytes; at 4 bytes per code point it always fits.
     */
    static byte[] encodeCodePoints(int[] codePoints, int capacity) {
        byte[] out = new byte[capacity];
        EncoderCore encoder = new EncoderCore();
        int dp = 0;

        for (int c : codePoints) {
            dp = encoder.encodeCodePoint(c, out, dp, out.length);

            if (dp < 0) {
                throw new OutOfMemoryError("The encoded value does not fit in a byte array.");
            }
        }

        return Arrays.copyOf(out, dp);
    }

    /**
     * Gets the largest number of bytes that the encoding of a value of {@code charCount} UTF-16 code units can take.
     *
     * <p>It is {@code 3 * charCount}: a code point below U+10000 takes at most 3 bytes, and a surrogate pair at most 4.
     *
     * @param charCount the number of UTF-16 code units
     * @return the largest number of bytes
     * @throws IllegalArgumentException if {@code charCount} is negative or the result does not fit in an {@code int}
     */
    public static int maxEncodedLength(int charCount) {
        if (charCount < 0 || charCount > Integer.MAX_VALUE / 3) {
            throw new IllegalArgumentException("The char count is out of range: " + charCount + ".");
        }

        return charCount * 3;
    }

    // ---------- Decoding ----------

    /**
     * Decodes bytes as one value into a string.
     *
     * @param bytes the Steno bytes of a whole value
     * @return the text
     * @throws StenoDecodeException if the bytes are invalid Steno bytes
     */
    public static String decode(byte[] bytes) throws StenoDecodeException {
        return decode(bytes, 0, bytes.length);
    }

    /**
     * Decodes a range of bytes as one value into a string.
     *
     * @param bytes the array that holds the bytes
     * @param offset the first byte of the value
     * @param length the number of bytes of the value
     * @return the text
     * @throws StenoDecodeException if the bytes are invalid Steno bytes; its offset counts from {@code offset}
     * @throws IndexOutOfBoundsException if the range is outside {@code bytes}
     */
    public static String decode(byte[] bytes, int offset, int length) throws StenoDecodeException {
        Objects.checkFromIndexSize(offset, length, bytes.length);

        if (length == 0) {
            return "";
        }

        int end = offset + length;
        int first = bytes[offset];

        if (length <= Scratch.BYTES && first >= 0x02 && first <= 0x65) {
            String ascii = AsciiText.decode(bytes, offset, length);

            if (ascii != null) {
                return ascii;
            }
        }

        if (length <= Scratch.BYTES) {
            Scratch scratch = Scratch.take();

            if (scratch != null) {
                try {
                    // Room for the worst case: the decoder never stops for room.
                    // The scratch decoder is in the initial state: every use resets it when it ends.
                    DecoderCore decoder = scratch.decoder;
                    int status = decoder.decode(bytes, offset, end, scratch.decoded, 0, scratch.decoded.length);

                    if (status == DecoderCore.ERROR) {
                        throw decoder.exception(decoder.consumed);
                    }

                    if (decoder.srcPos < end) {
                        throw new StenoDecodeException(
                                StenoDecodeException.Reason.TRUNCATED, decoder.consumed, end - decoder.srcPos);
                    }

                    return new String(scratch.decoded, 0, decoder.dstPos);
                } finally {
                    scratch.decoder.reset();
                    scratch.inUse = false;
                }
            }
        }

        DecoderCore decoder = new DecoderCore();

        // Small input gets room for the worst case, larger input room for 1 char per byte; it grows if that is not
        // enough.
        char[] out = new char[(int) Math.min(length <= CHUNK ? 2L * length : length + 16L, MAX_ARRAY)];
        int sp = offset;
        int dp = 0;

        while (true) {
            int status = decoder.decode(bytes, sp, end, out, dp, out.length);
            sp = decoder.srcPos;
            dp = decoder.dstPos;

            if (status == DecoderCore.UNDERFLOW) {
                break;
            }

            if (status == DecoderCore.ERROR) {
                throw decoder.exception(decoder.consumed);
            }

            out = Arrays.copyOf(out, grownLength(out.length, (long) out.length + Math.max(out.length >> 1, 2)));
        }

        if (sp < end) {
            throw new StenoDecodeException(StenoDecodeException.Reason.TRUNCATED, decoder.consumed, end - sp);
        }

        return new String(out, 0, dp);
    }

    /**
     * Decodes the remaining bytes of a buffer as one value into a string.
     *
     * @param bytes the buffer; on success its position moves to its limit
     * @return the text
     * @throws StenoDecodeException if the bytes are invalid Steno bytes (the position of {@code bytes} is then
     *     unchanged); its offset counts from the position
     */
    public static String decode(ByteBuffer bytes) throws StenoDecodeException {
        String text;

        if (bytes.hasArray()) {
            text = decode(bytes.array(), bytes.arrayOffset() + bytes.position(), bytes.remaining());
        } else {
            byte[] copy = new byte[bytes.remaining()];
            bytes.duplicate().get(copy);
            text = decode(copy);
        }

        bytes.position(bytes.limit());
        return text;
    }

    /**
     * Decodes bytes as one value into its code points.
     *
     * <p>A surrogate code point stands for itself. The result never has a high surrogate code point directly followed
     * by a low one.
     *
     * @param bytes the Steno bytes of a whole value
     * @return the code points of the value
     * @throws StenoDecodeException if the bytes are invalid Steno bytes
     */
    public static int[] decodeCodePoints(byte[] bytes) throws StenoDecodeException {
        // A value never has a high surrogate code point directly followed by a low one, so its UTF-16 form reads back
        // as the same code points (FORMAT.md section 3).
        return decode(bytes).codePoints().toArray();
    }

    /**
     * Gets the largest number of UTF-16 code units that the decoding of {@code byteCount} bytes can give.
     *
     * <p>It is {@code 2 * byteCount}: a code point takes at least 1 byte and at most 2 code units.
     *
     * @param byteCount the number of bytes
     * @return the largest number of UTF-16 code units
     * @throws IllegalArgumentException if {@code byteCount} is negative or the result does not fit in an {@code int}
     */
    public static int maxDecodedLength(int byteCount) {
        if (byteCount < 0 || byteCount > Integer.MAX_VALUE / 2) {
            throw new IllegalArgumentException("The byte count is out of range: " + byteCount + ".");
        }

        return byteCount * 2;
    }

    // ---------- Helpers on encoded bytes ----------

    /**
     * Determines whether Steno bytes encode a value that has only ASCII text in it: tab, line feed, carriage return and
     * the printable characters U+0020..U+007E.
     *
     * <p>The answer comes from the bytes alone, without decoding. The bytes are not ASCII bytes: U+0020..U+007E are
     * {@code 07}..{@code 65} (the code point minus 0x19), and tab, line feed and carriage return are {@code 02},
     * {@code 03} and {@code 05} (FORMAT.md section 14.1). Bytes that are all of these values are valid.
     *
     * @param bytes the Steno bytes of a whole value
     * @return {@code true} if the value has only those characters (the empty value included); {@code false} otherwise,
     *     also for invalid Steno bytes
     */
    public static boolean isAsciiText(byte[] bytes) {
        return isAsciiText(bytes, 0, bytes.length);
    }

    /**
     * Determines whether a range of Steno bytes encodes a value that has only ASCII text in it. See
     * {@link #isAsciiText(byte[])}.
     *
     * @param bytes the array that holds the bytes
     * @param offset the first byte of the value
     * @param length the number of bytes of the value
     * @return {@code true} if the value has only tab, line feed, carriage return and U+0020..U+007E
     * @throws IndexOutOfBoundsException if the range is outside {@code bytes}
     */
    public static boolean isAsciiText(byte[] bytes, int offset, int length) {
        Objects.checkFromIndexSize(offset, length, bytes.length);
        int i = offset;
        int end = offset + length;

        // 8 bytes at a time: a word of bytes in 07..65 needs no more checks; a word with tab, line feed or carriage
        // return (or a byte that is not ASCII text) is checked byte by byte.
        for (; end - i >= 8; i += 8) {
            if (Swar.inRun((long) Swar.LONGS.get(bytes, i), ASCII_LO_WORD, ASCII_END_WORD) != Swar.HIGH) {
                for (int j = i; j < i + 8; j++) {
                    if (!isAsciiTextByte(bytes[j])) {
                        return false;
                    }
                }
            }
        }

        for (; i < end; i++) {
            if (!isAsciiTextByte(bytes[i])) {
                return false;
            }
        }

        return true;
    }

    private static boolean isAsciiTextByte(int b) {
        return (b >= 0x07 && b <= 0x65) || b == 0x02 || b == 0x03 || b == 0x05;
    }

    // ---------- Code point order ----------

    /**
     * Compares two texts in code point order, the order in which their Steno bytes sort.
     *
     * <p>Code point order compares the texts code point by code point: a surrogate pair as the one supplementary code
     * point (U+10000..U+10FFFF) that it stands for, and an unpaired surrogate as a code point of its own
     * (U+D800..U+DFFF). It is the order of the Steno bytes of the texts compared as unsigned bytes and, for text
     * without unpaired surrogates, also the order of their UTF-8 bytes.
     *
     * <p>It differs from {@link String#compareTo(String)}, which compares UTF-16 code units. In text without unpaired
     * surrogates they differ in exactly one case: where the texts first differ, one has a supplementary character (a
     * surrogate pair) and the other has a character in U+E000..U+FFFF. {@code String.compareTo} puts the supplementary
     * character first, because its first code unit is in U+D800..U+DBFF; code point order puts it last. For example
     * {@code "すごい！"} (with U+FF01) comes after {@code "すごい😀"} (with U+1F600) in {@code String.compareTo} order, but
     * before it in code point order. Everything else is ordered the same way by both.
     *
     * <p>An unpaired surrogate adds cases of the same kind: code point order puts it before every supplementary
     * character, while {@code String.compareTo} compares its code unit with the high surrogate of the character.
     *
     * <p>Like {@code String.compareTo}, it ignores language. It does not allocate.
     *
     * @param x the first text
     * @param y the second text
     * @return a negative number if {@code x} comes first, zero if the texts are equal, and a positive number if
     *     {@code x} comes last
     */
    public static int compareCodePoints(CharSequence x, CharSequence y) {
        int xn = x.length();
        int yn = y.length();
        int n = Math.min(xn, yn);
        int i = 0;

        while (i < n && x.charAt(i) == y.charAt(i)) {
            i++;
        }

        // One text is a prefix of the other in code units: the shorter comes first. That is code point order too, even
        // if the shorter one ends in a high surrogate that the longer one pairs (as a code point, the high surrogate is
        // below every supplementary one).
        if (i == n) {
            return xn - yn;
        }

        // If a shared high surrogate comes before the first difference and either text pairs it here, the code points
        // that differ start at that high surrogate.
        if (i > 0
                && Character.isHighSurrogate(x.charAt(i - 1))
                && (Character.isLowSurrogate(x.charAt(i)) || Character.isLowSurrogate(y.charAt(i)))) {
            i--;
        }

        return codePointAt(x, i) - codePointAt(y, i);
    }

    // ---------- Internals ----------

    private static int codePointAt(CharSequence text, int index) {
        char c = text.charAt(index);

        if (Character.isHighSurrogate(c) && index + 1 < text.length()) {
            char d = text.charAt(index + 1);

            if (Character.isLowSurrogate(d)) {
                return Character.toCodePoint(c, d);
            }
        }

        return c;
    }

    /**
     * Encodes text into {@code dst[start..end)}; returns the number of bytes written, or -1 if they do not fit. An
     * exception from the text itself (a {@link CharSequence} that changes while it is read) passes through unchanged.
     */
    private static int encodeBounded(CharSequence text, byte[] dst, int start, int end) {
        int n = text.length();
        EncoderCore encoder = new EncoderCore();
        char[] chunk = new char[Math.min(n, CHUNK)];
        int dp = start;

        for (int pos = 0; pos < n; pos += CHUNK) {
            int len = Math.min(CHUNK, n - pos);
            copy(text, pos, len, chunk);

            if (!encoder.encode(chunk, 0, len, dst, dp, end)) {
                return -1;
            }

            dp = encoder.dstPos;
        }

        if (!encoder.flush(dst, dp, end)) {
            return -1;
        }

        return encoder.dstPos - start;
    }

    /** Copies {@code len} chars of text from {@code pos} into the start of {@code chunk}. */
    static void copy(CharSequence text, int pos, int len, char[] chunk) {
        if (text instanceof String) {
            ((String) text).getChars(pos, pos + len, chunk, 0);
        } else if (text instanceof StringBuilder) {
            ((StringBuilder) text).getChars(pos, pos + len, chunk, 0);
        } else if (text instanceof CharBuffer) {
            CharBuffer buffer = (CharBuffer) text;
            buffer.get(buffer.position() + pos, chunk, 0, len);
        } else {
            for (int i = 0; i < len; i++) {
                chunk[i] = text.charAt(pos + i);
            }
        }
    }

    private static byte[] grow(byte[] out, int charsLeft) {
        // The chars left take at most 3 bytes each, plus 3 for a held high surrogate.
        long needed =
                Math.min((long) out.length + 3L * charsLeft + 3, (long) out.length + Math.max(out.length >> 1, 16));

        return Arrays.copyOf(out, grownLength(out.length, needed));
    }

    /**
     * Gets the length of an array that grows from {@code length} towards {@code wanted}: at least one more, at most the
     * largest array.
     *
     * @throws OutOfMemoryError if the array has the largest length already
     */
    static int grownLength(int length, long wanted) {
        if (length >= MAX_ARRAY) {
            throw new OutOfMemoryError("The value does not fit in an array.");
        }

        return (int) Math.min(Math.max(wanted, length + 1L), MAX_ARRAY);
    }
}
