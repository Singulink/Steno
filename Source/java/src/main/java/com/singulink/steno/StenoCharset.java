package com.singulink.steno;

import java.nio.charset.Charset;
import java.nio.charset.CharsetDecoder;
import java.nio.charset.CharsetEncoder;

/**
 * The Steno Encoding, version 1, as a {@link Charset} named {@value Steno#CHARSET_NAME}, for the APIs that take a
 * charset: {@link String#getBytes(Charset)}, {@link String#String(byte[], Charset)}, {@link java.io.InputStreamReader},
 * {@link java.io.OutputStreamWriter}, {@link java.nio.file.Files#newBufferedReader(java.nio.file.Path, Charset)} and
 * the others.
 *
 * <p>{@link Charset#forName(String) Charset.forName("x-steno")} finds it too, through {@link StenoCharsetProvider},
 * when this library is on the class path or in the module graph.
 *
 * <p><b>One value per encoder or decoder run.</b> The Steno Encoding is stateful, so a {@link CharsetEncoder} or
 * {@link CharsetDecoder} of this charset encodes or decodes one value between two calls of {@code reset()}: over any
 * split of the value into pieces, it gives the same bytes or text as {@link Steno#encode(CharSequence)} and
 * {@link Steno#decode(byte[])} give for the whole value, also when a piece ends inside a surrogate pair or inside the
 * bytes of a code point. A {@link java.io.Writer} or {@link java.io.Reader} of this charset therefore writes or reads
 * the whole stream as one value.
 *
 * <p><b>Encoder.</b> Every sequence of UTF-16 code units is a value, so the encoder never reports malformed or
 * unmappable input, and {@link CharsetEncoder#canEncode(char)} is {@code true} for every char, unpaired surrogates
 * included. A high surrogate at the end of a piece is taken and held until the next piece, which may start with its low
 * surrogate. {@code flush} ends the value, as it does for every Java charset: it writes a held high surrogate as an
 * unpaired surrogate, and then the encoder must be reset before it encodes again. {@link java.io.Writer#flush()} does
 * not flush the encoder, so it can be called at any point of a stream. {@link CharsetEncoder#maxBytesPerChar()} is 3, a
 * true bound for a whole value; one call can write one byte more than 3 per char of its input when it completes a
 * surrogate pair whose high surrogate the call before held.
 *
 * <p><b>Decoder.</b> The bytes of a code point that a piece ends in the middle of are left in the input buffer, as the
 * decoders of the JDK do, for the caller to give again with more bytes. If the input ends there (end of input), that is
 * malformed input: the {@code truncated} error of the specification. Bytes that break another rule of the specification
 * are malformed input too, reported with {@link java.nio.charset.CoderResult#malformedForLength(int)}. The state after
 * invalid data is unknown, so the decoder cannot go on after it: it reports the error once, for all the bytes from the
 * code point that breaks the rule to the end of the input buffer, and then takes and drops every later byte without
 * another report until {@code reset()}. With {@link java.nio.charset.CodingErrorAction#REPORT} the caller gets a
 * {@link java.nio.charset.MalformedInputException}. With {@code REPLACE}, which {@link String#String(byte[], Charset)},
 * {@link Charset#decode(java.nio.ByteBuffer)} and {@link java.io.InputStreamReader} use, the text is the text before
 * the error followed by one U+FFFD, however the bytes arrive; {@code IGNORE} gives the text before the error. The
 * decoder never reports a reason or an offset: use {@link Steno#decode(byte[])} when invalid data must be rejected with
 * its reason. {@link CharsetDecoder#maxCharsPerByte()} is 2.
 *
 * <p>The charset is thread safe. Its encoders and decoders are not, as with every charset.
 */
public final class StenoCharset extends Charset {
    /** The instance of the charset. */
    public static final StenoCharset INSTANCE = new StenoCharset();

    private StenoCharset() {
        super(Steno.CHARSET_NAME, null);
    }

    /**
     * Returns {@code true}: the Steno Encoding encodes every sequence of UTF-16 code units, so it contains every
     * charset.
     *
     * @param cs the other charset
     * @return {@code true}
     */
    @Override
    public boolean contains(Charset cs) {
        return true;
    }

    /**
     * Creates a decoder of one value at a time. See the class description for what it guarantees.
     *
     * @return a new decoder
     */
    @Override
    public CharsetDecoder newDecoder() {
        return new StenoCharsetDecoder(this);
    }

    /**
     * Creates an encoder of one value at a time. See the class description for what it guarantees.
     *
     * @return a new encoder
     */
    @Override
    public CharsetEncoder newEncoder() {
        return new StenoCharsetEncoder(this);
    }
}
