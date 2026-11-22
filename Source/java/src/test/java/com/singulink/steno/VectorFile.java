package com.singulink.steno;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.UncheckedIOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;

/** Reads a test vector file of FORMAT.md section 12. */
final class VectorFile {
    /** The published vector file, by relative path from this folder. */
    static final Path PUBLISHED = Path.of("..", "..", "TestVectors", "steno-v1-vectors.txt");

    enum Kind {
        VALID,
        INVALID,
        NOT_A_VALUE
    }

    /** One vector. Fields that the kind does not have are {@code null}. */
    static final class Vector {
        final Kind kind;
        final String name;
        final int[] codePoints;
        final byte[] bytes;
        final StenoDecodeException.Reason reason;
        final int line;

        Vector(Kind kind, String name, int[] codePoints, byte[] bytes, StenoDecodeException.Reason reason, int line) {
            this.kind = kind;
            this.name = name;
            this.codePoints = codePoints;
            this.bytes = bytes;
            this.reason = reason;
            this.line = line;
        }

        @Override
        public String toString() {
            return kind + " " + name + " (line " + line + ")";
        }
    }

    private VectorFile() {}

    static List<Vector> read(Path path) {
        List<Vector> vectors = new ArrayList<>();

        try (BufferedReader reader = Files.newBufferedReader(path, StandardCharsets.UTF_8)) {
            String line;
            int number = 0;

            while ((line = reader.readLine()) != null) {
                number++;

                if (line.isEmpty() || line.startsWith("#")) {
                    continue;
                }

                String[] f = line.split("\t", -1);

                switch (f[0]) {
                    case "valid":
                        vectors.add(new Vector(Kind.VALID, f[1], codePoints(f[2]), bytes(f[3]), null, number));
                        break;
                    case "invalid":
                        vectors.add(new Vector(
                                Kind.INVALID,
                                f[1],
                                null,
                                bytes(f[2]),
                                StenoDecodeException.Reason.fromSpecName(f[3]),
                                number));
                        break;
                    case "not-a-value":
                        vectors.add(new Vector(Kind.NOT_A_VALUE, f[1], codePoints(f[2]), null, null, number));
                        break;
                    default:
                        throw new IllegalStateException("Unknown kind of vector on line " + number + ": " + f[0]);
                }
            }
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }

        return vectors;
    }

    static int[] codePoints(String field) {
        if (field.equals("-")) {
            return new int[0];
        }

        String[] parts = field.split(" ");
        int[] result = new int[parts.length];

        for (int i = 0; i < parts.length; i++) {
            result[i] = Integer.parseInt(parts[i], 16);
        }

        return result;
    }

    static byte[] bytes(String field) {
        if (field.equals("-")) {
            return new byte[0];
        }

        byte[] result = new byte[field.length() / 2];

        for (int i = 0; i < result.length; i++) {
            result[i] = (byte) Integer.parseInt(field.substring(2 * i, 2 * i + 2), 16);
        }

        return result;
    }

    static String hex(byte[] bytes) {
        StringBuilder text = new StringBuilder();

        for (byte b : bytes) {
            text.append(String.format("%02X", b & 0xFF));
        }

        return text.toString();
    }

    /**
     * The UTF-16 form of code points: a code point below U+10000 as one unit, a supplementary one as a pair (FORMAT.md
     * section 3).
     */
    static String utf16(int[] codePoints) {
        StringBuilder text = new StringBuilder();

        for (int c : codePoints) {
            text.appendCodePoint(c);
        }

        return text.toString();
    }
}
