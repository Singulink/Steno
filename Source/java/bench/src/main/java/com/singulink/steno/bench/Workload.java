package com.singulink.steno.bench;

import java.io.IOException;
import java.io.UncheckedIOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Arrays;

/** The shared benchmark workloads in {@code Benchmarks/Workloads} of the repository. */
final class Workload {
    /** The texts, in the order of the common rules. */
    static final String[] TEXTS = {
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
        "korean",
    };

    private Workload() {}

    /**
     * Reads the values of a workload: the document as one value, or the 16-char values one per line.
     *
     * <p>The folder is {@code ../../Benchmarks/Workloads} from the working directory (the folder of the Java reference
     * implementation), or the system property {@code steno.workloads}.
     */
    static String[] read(String text, String kind) {
        Path folder = Path.of(System.getProperty("steno.workloads", "../../Benchmarks/Workloads"));
        Path file = folder.resolve(text + "." + kind + ".txt");
        String content;

        try {
            content = Files.readString(file, StandardCharsets.UTF_8);
        } catch (IOException e) {
            throw new UncheckedIOException("Cannot read the workload " + file.toAbsolutePath(), e);
        }

        // The value is the content without its final LF; values are the lines without their LF. Nothing else changes.
        if (content.endsWith("\n")) {
            content = content.substring(0, content.length() - 1);
        }

        if (kind.equals("document")) {
            return new String[] {content};
        }

        if (!kind.equals("values")) {
            throw new IllegalArgumentException("Unknown kind: " + kind);
        }

        return content.split("\n", -1);
    }

    /** The total number of UTF-16 code units of values. */
    static long units(String[] values) {
        return Arrays.stream(values).mapToLong(String::length).sum();
    }
}
