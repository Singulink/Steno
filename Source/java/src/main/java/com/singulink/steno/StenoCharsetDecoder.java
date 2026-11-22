package com.singulink.steno;

import java.nio.ByteBuffer;
import java.nio.CharBuffer;
import java.nio.charset.CharsetDecoder;
import java.nio.charset.CoderResult;
import java.nio.charset.CodingErrorAction;

/**
 * The decoder of {@link StenoCharset}: decodes one value given in pieces. What it guarantees is documented on
 * {@link StenoCharset}.
 */
final class StenoCharsetDecoder extends CharsetDecoder {
    /** The bytes that the path for buffers without arrays copies at a time. */
    private static final int CHUNK = 512;

    private final DecoderCore core = new DecoderCore();

    /** Whether the decoder met invalid data since the last reset. */
    private boolean failed;

    private byte[] bytes;
    private char[] chars;

    StenoCharsetDecoder(StenoCharset charset) {
        super(charset, 1.0f, 2.0f);
    }

    /** Gets the reason of the invalid data that the decoder met, or {@code null}. For tests. */
    StenoDecodeException.Reason failure() {
        return failed ? core.errorReason : null;
    }

    @Override
    protected CoderResult decodeLoop(ByteBuffer in, CharBuffer out) {
        if (failed) {
            // The state after invalid data is unknown: the decoder takes and drops every later byte until reset(), so
            // that REPLACE gives one U+FFFD however the bytes arrive.
            in.position(in.limit());
            return CoderResult.UNDERFLOW;
        }

        if (in.hasArray() && out.hasArray()) {
            int inBase = in.arrayOffset();
            int outBase = out.arrayOffset();
            int status = core.decode(
                    in.array(),
                    inBase + in.position(),
                    inBase + in.limit(),
                    out.array(),
                    outBase + out.position(),
                    outBase + out.limit());
            in.position(core.srcPos - inBase);
            out.position(core.dstPos - outBase);
            return result(status, in, out);
        }

        if (bytes == null) {
            bytes = new byte[CHUNK];
            chars = new char[2 * CHUNK];
        }

        // Copy pieces in and out. The chars buffer holds the chars of a whole piece, so the room of out is the only
        // limit.
        while (in.hasRemaining()) {
            int remaining = in.remaining();
            int n = Math.min(remaining, CHUNK);
            in.get(in.position(), bytes, 0, n);
            int status = core.decode(bytes, 0, n, chars, 0, Math.min(out.remaining(), chars.length));
            in.position(in.position() + core.srcPos);
            out.put(chars, 0, core.dstPos);

            if (status != DecoderCore.UNDERFLOW) {
                return result(status, in, out);
            }

            // An unfinished code point at the end of the input is left in it. One at the end of a piece goes on in the
            // next piece; a piece of CHUNK bytes always holds a whole code point first, so each piece makes progress.
            if (core.srcPos < n && n == remaining) {
                return CoderResult.UNDERFLOW;
            }
        }

        return CoderResult.UNDERFLOW;
    }

    private CoderResult result(int status, ByteBuffer in, CharBuffer out) {
        switch (status) {
            case DecoderCore.UNDERFLOW:
                return CoderResult.UNDERFLOW;
            case DecoderCore.OVERFLOW:
                return CoderResult.OVERFLOW;
            default:
                // With REPLACE, report the error only when the output has room for the replacement: otherwise
                // CharsetDecoder would return OVERFLOW and call again, and the replacement of a decoder that already
                // stopped would be lost. The input stays at the code point that breaks the rule, which the next call
                // meets again.
                if (malformedInputAction() == CodingErrorAction.REPLACE
                        && out.remaining() < replacement().length()) {
                    return CoderResult.OVERFLOW;
                }

                failed = true;
                return CoderResult.malformedForLength(in.remaining());
        }
    }

    @Override
    protected void implReset() {
        core.reset();
        failed = false;
    }
}
