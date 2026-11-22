package com.singulink.steno;

import java.nio.ByteBuffer;
import java.nio.CharBuffer;
import java.nio.charset.CharsetEncoder;
import java.nio.charset.CoderResult;

/**
 * The encoder of {@link StenoCharset}: encodes one value given in pieces. What it guarantees is documented on
 * {@link StenoCharset}.
 */
final class StenoCharsetEncoder extends CharsetEncoder {
    /** The chars that the path for buffers without arrays copies at a time. */
    private static final int CHUNK = 512;

    /** The Steno bytes of "?", the default replacement. Encoding never fails, so it is never used. */
    private static final byte[] REPLACEMENT = {0x26};

    private final EncoderCore core = new EncoderCore();

    private char[] chars;
    private byte[] bytes;

    StenoCharsetEncoder(StenoCharset charset) {
        super(charset, 1.1f, 3.0f, REPLACEMENT);
    }

    @Override
    public boolean canEncode(char c) {
        return true;
    }

    @Override
    public boolean canEncode(CharSequence cs) {
        return true;
    }

    @Override
    public boolean isLegalReplacement(byte[] repl) {
        // Any bytes are a legal replacement: encoding never fails, so the replacement is never written.
        return true;
    }

    @Override
    protected CoderResult encodeLoop(CharBuffer in, ByteBuffer out) {
        if (in.hasArray() && out.hasArray()) {
            int inBase = in.arrayOffset();
            int outBase = out.arrayOffset();
            boolean done = core.encode(
                    in.array(),
                    inBase + in.position(),
                    inBase + in.limit(),
                    out.array(),
                    outBase + out.position(),
                    outBase + out.limit());
            in.position(core.srcPos - inBase);
            out.position(core.dstPos - outBase);
            return done ? CoderResult.UNDERFLOW : CoderResult.OVERFLOW;
        }

        if (chars == null) {
            chars = new char[CHUNK];
            bytes = new byte[3 * CHUNK + 4];
        }

        // Copy pieces in and out. The bytes buffer holds the bytes of a whole piece, so the room of out is the only
        // limit.
        do {
            int n = Math.min(in.remaining(), CHUNK);
            in.get(in.position(), chars, 0, n);
            int room = Math.min(out.remaining(), bytes.length);
            boolean done = core.encode(chars, 0, n, bytes, 0, room);
            in.position(in.position() + core.srcPos);
            out.put(bytes, 0, core.dstPos);

            if (!done) {
                return CoderResult.OVERFLOW;
            }
        } while (in.hasRemaining());

        return CoderResult.UNDERFLOW;
    }

    @Override
    protected CoderResult implFlush(ByteBuffer out) {
        if (core.heldHigh == 0) {
            return CoderResult.UNDERFLOW;
        }

        // A held high surrogate is a code point of its own, of at most 3 bytes.
        byte[] buffer = new byte[3];

        if (!core.flush(buffer, 0, Math.min(out.remaining(), buffer.length))) {
            return CoderResult.OVERFLOW;
        }

        out.put(buffer, 0, core.dstPos);
        return CoderResult.UNDERFLOW;
    }

    @Override
    protected void implReset() {
        core.reset();
    }
}
