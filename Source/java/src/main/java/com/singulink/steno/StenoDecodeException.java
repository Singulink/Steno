package com.singulink.steno;

import java.nio.charset.MalformedInputException;

/**
 * Thrown for invalid Steno bytes, with the rule of the specification that they break and where.
 *
 * <p>The reason is the first rule of FORMAT.md section 10.1 that a decoder meets, reading from the first byte in the
 * order of section 9.2. For each code point that is: the lead byte (rules 1 and 2), then each trail byte as it is read
 * (rule 3 before it is read, then rule 1, then rule 6), then the range (rule 4), and last rule 5. The rules are
 * numbered by name, not in that order. The specification makes only the rejection binding; the reason is for diagnosis,
 * but every conforming decoder that reports reasons reports the same one.
 *
 * <p>This is a checked exception, like {@link java.nio.charset.CharacterCodingException}, which the
 * {@code java.nio.charset} API throws for the same kind of error. It extends {@link MalformedInputException}, so code
 * that catches the exceptions of a {@link java.nio.charset.CharsetDecoder} also catches it. {@link #getInputLength()}
 * is the number of bytes of the code point that were read up to the defect.
 */
public final class StenoDecodeException extends MalformedInputException {
    private static final long serialVersionUID = 1L;

    /** The rule of FORMAT.md section 10.1 that the bytes break. */
    private final Reason reason;

    /** The position of the first byte of the code point that breaks the rule. */
    private final long offset;

    /**
     * The rules of FORMAT.md section 10.1, in the order of their numbers (not the order in which a decoder checks
     * them).
     */
    public enum Reason {
        /** Rule 1: a byte 00, as a lead byte or as a trail byte. */
        ZERO_BYTE("zero-byte"),

        /** Rule 2: a lead byte other than 00 that the layout of the current state does not use. */
        UNUSED_LEAD("unused-lead"),

        /** Rule 3: the bytes end in the middle of the encoding of a code point. */
        TRUNCATED("truncated"),

        /** Rule 4: a code point beyond the last code point of its range, which includes every value above U+10FFFF. */
        PAST_RANGE("past-range"),

        /**
         * Rule 5: a low surrogate code point directly after a high surrogate code point, both decoded from the bytes.
         */
        SURROGATE_PAIR("surrogate-pair"),

        /** Rule 6: a byte 01..07 where a trail byte is expected (trail bytes are 08..FF; a byte 00 there is rule 1). */
        INVALID_TRAIL("invalid-trail");

        private final String specName;

        Reason(String specName) {
            this.specName = specName;
        }

        /**
         * Gets the name of the reason in the specification and its test vectors, such as {@code zero-byte}.
         *
         * @return the name of the reason in the specification
         */
        public String specName() {
            return specName;
        }

        /**
         * Gets the number of the rule in FORMAT.md section 10.1, from 1 to 6.
         *
         * @return the number of the rule
         */
        public int rule() {
            return ordinal() + 1;
        }

        /**
         * Gets the reason with the given name in the specification.
         *
         * @param specName the name, such as {@code past-range}
         * @return the reason with that name
         * @throws IllegalArgumentException if no reason has that name
         */
        public static Reason fromSpecName(String specName) {
            for (Reason reason : values()) {
                if (reason.specName.equals(specName)) {
                    return reason;
                }
            }

            throw new IllegalArgumentException("Not a reason of the Steno Encoding: " + specName);
        }
    }

    StenoDecodeException(Reason reason, long offset, int inputLength) {
        super(inputLength);
        this.reason = reason;
        this.offset = offset;
    }

    /**
     * Gets the rule of the specification that the bytes break.
     *
     * @return the reason
     */
    public Reason getReason() {
        return reason;
    }

    /**
     * Gets where the code point that breaks the rule starts, as the number of bytes before it in the value (from the
     * start of the range that was decoded, not of the array).
     *
     * @return the offset of the first byte of the code point
     */
    public long getOffset() {
        return offset;
    }

    /**
     * Gets a message that names the reason and the offset. Its wording is not part of the contract.
     *
     * @return the message
     */
    @Override
    public String getMessage() {
        return "Not valid Steno bytes: " + reason.specName() + " (rule " + reason.rule()
                + ") in the code point at byte offset " + offset + ".";
    }
}
