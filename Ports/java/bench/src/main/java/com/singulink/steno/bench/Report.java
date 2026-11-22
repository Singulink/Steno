package com.singulink.steno.bench;

import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.LocalDate;
import java.time.ZoneOffset;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Properties;

/**
 * Turns the JSON results of JMH into {@code bench/results.csv} (the common format of all implementations),
 * {@code bench/sizes.csv}, {@code bench/libraries.csv} and {@code BENCHMARKS.md}, and fills the figures block of the
 * README.
 *
 * <p>Usage: {@code Report <results.json> <results.csv> <BENCHMARKS.md> [README.md]}, from the folder of the Java
 * reference implementation (it reads the workloads to count the values and the UTF-16 code units, and to measure the
 * encoded sizes). {@code sizes.csv} and {@code libraries.csv} go next to {@code results.csv}.
 */
public final class Report {
    /** The codecs of the size table and of the speed tables, by group. */
    private static final String[] BASELINES = {"steno", "utf8", "utf16"};

    private static final String[] ICU = {"steno", "scsu-icu4j", "bocu1-icu4j"};

    private static final String[] COMPRESSORS = {
        "steno", "deflate-fast", "deflate-max", "zstd-1", "zstd-19", "brotli-1", "brotli-11", "lz4", "lz4-hc"
    };

    private Report() {}

    /** One benchmark result. */
    private static final class Result {
        String codec;
        String op;
        String text;
        String kind;
        double ns;
        double error;
    }

    /**
     * Writes the CSV files and the Markdown report, and fills the README block.
     *
     * @param args the JSON file, the results CSV file, the Markdown file and, optionally, the README
     * @throws IOException if a file cannot be read or written
     */
    public static void main(String[] args) throws IOException {
        if (args.length != 3 && args.length != 4) {
            System.err.println("Usage: Report <results.json> <results.csv> <BENCHMARKS.md> [README.md]");
            System.exit(2);
        }

        Object json = new Json(Files.readString(Path.of(args[0]), StandardCharsets.UTF_8)).parse();
        List<Result> results = new ArrayList<>();
        Map<String, Map<String, Object>> runs = new LinkedHashMap<>();

        for (Object item : (List<?>) json) {
            Map<?, ?> entry = (Map<?, ?>) item;
            String benchmark = (String) entry.get("benchmark");
            String type = benchmark.substring(0, benchmark.lastIndexOf('.'));
            type = type.substring(type.lastIndexOf('.') + 1);

            runs.computeIfAbsent(type, k -> {
                Map<String, Object> run = new LinkedHashMap<>();

                for (Map.Entry<?, ?> e : entry.entrySet()) {
                    run.put((String) e.getKey(), e.getValue());
                }

                return run;
            });

            Map<?, ?> params = (Map<?, ?>) entry.get("params");
            Map<?, ?> metric = (Map<?, ?>) entry.get("primaryMetric");

            if (!"ns/op".equals(metric.get("scoreUnit"))) {
                throw new IllegalStateException("Expected ns/op, got " + metric.get("scoreUnit"));
            }

            Result r = new Result();
            String method = benchmark.substring(benchmark.lastIndexOf('.') + 1);

            if (params.get("codec") != null) {
                r.codec = (String) params.get("codec");
                r.op = method;
            } else {
                // StenoBenchmark: stenoEncode, utf8Decode, stenoEncodeIntoBuffer and so on.
                boolean encode = method.contains("Encode");
                r.op = encode ? "encode" : "decode";
                String name = method.substring(0, method.indexOf(encode ? "Encode" : "Decode"));
                r.codec = method.endsWith("IntoBuffer") ? "steno-into-buffer" : name;
            }

            r.text = (String) params.get("text");
            r.kind = (String) params.get("kind");
            r.ns = number(metric.get("score"));
            r.error = number(metric.get("scoreError"));
            results.add(r);
        }

        if (runs.isEmpty()) {
            throw new IllegalStateException("No results in " + args[0]);
        }

        Map<String, long[]> counts = new LinkedHashMap<>();

        for (String text : Workload.TEXTS) {
            for (String kind : new String[] {"document", "values"}) {
                String[] values = Workload.read(text, kind);
                counts.put(text + "/" + kind, new long[] {values.length, Workload.units(values)});
            }
        }

        Path csv = Path.of(args[1]);
        Path folder = csv.toAbsolutePath().getParent();
        Files.createDirectories(folder);
        Properties versions = versions();

        writeCsv(csv, results, counts);
        writeLibraries(folder.resolve("libraries.csv"), versions);
        Map<String, Long> sizes = writeSizes(folder.resolve("sizes.csv"));
        writeMarkdown(Path.of(args[2]), results, counts, sizes, runs, versions);
        System.out.println("Wrote " + args[1] + ", sizes.csv, libraries.csv and " + args[2] + " from " + results.size()
                + " results.");

        if (args.length == 4) {
            updateReadme(
                    Path.of(args[3]),
                    Path.of(args[2]),
                    results,
                    counts,
                    runs.values().iterator().next());
            System.out.println("Updated the figures in " + args[3] + ".");
        }
    }

    private static Properties versions() throws IOException {
        Properties versions = new Properties();

        try (InputStream in = Report.class.getResourceAsStream("/libraries.properties")) {
            versions.load(in);
        }

        return versions;
    }

    private static void writeCsv(Path path, List<Result> results, Map<String, long[]> counts) throws IOException {
        StringBuilder csv = new StringBuilder(
                "implementation,text,kind,operation,form,codec,ns_per_operation,error_ns,values,utf16_units\n");

        for (Result r : results) {
            if (r.codec.equals("steno-into-buffer")) {
                // Not in the common format: the into-buffer form is only in BENCHMARKS.md.
                continue;
            }

            long[] c = counts.get(r.text + "/" + r.kind);
            csv.append(String.format(
                    Locale.ROOT,
                    "java,%s,%s,%s,native,%s,%.3f,%s,%d,%d\n",
                    r.text,
                    r.kind,
                    r.op,
                    r.codec,
                    r.ns,
                    Double.isNaN(r.error) ? "" : String.format(Locale.ROOT, "%.3f", r.error),
                    c[0],
                    c[1]));
        }

        Files.writeString(path, csv.toString(), StandardCharsets.UTF_8);
    }

    /** The library and version of each codec. */
    private static String[] library(String codec, Properties versions) {
        if (codec.equals("steno")) {
            return new String[] {"com.singulink:steno (this folder)", versions.getProperty("steno")};
        }

        if (codec.equals("utf8") || codec.equals("utf16") || codec.startsWith("deflate")) {
            return new String[] {
                "JDK (" + (codec.startsWith("deflate") ? "java.util.zip" : "java.lang.String") + ")",
                System.getProperty("java.version")
            };
        }

        if (codec.endsWith("icu4j")) {
            return new String[] {"com.ibm.icu:icu4j-charset", versions.getProperty("icu4j")};
        }

        if (codec.startsWith("zstd")) {
            return new String[] {"com.github.luben:zstd-jni", versions.getProperty("zstd-jni")};
        }

        if (codec.startsWith("brotli")) {
            return new String[] {"com.aayushatharva.brotli4j:brotli4j", versions.getProperty("brotli4j")};
        }

        return new String[] {"at.yawk.lz4:lz4-java", versions.getProperty("lz4-java")};
    }

    private static List<String> allCodecs() {
        List<String> codecs = new ArrayList<>(List.of(BASELINES));
        codecs.addAll(List.of(Codecs.ALTERNATIVES));
        return codecs;
    }

    private static void writeLibraries(Path path, Properties versions) throws IOException {
        StringBuilder csv = new StringBuilder("codec,library,version\n");

        for (String codec : allCodecs()) {
            String[] lib = library(codec, versions);
            csv.append(codec)
                    .append(',')
                    .append(lib[0])
                    .append(',')
                    .append(lib[1])
                    .append('\n');
        }

        Files.writeString(path, csv.toString(), StandardCharsets.UTF_8);
    }

    /** Encodes every workload with every codec and writes the total sizes; returns them by codec/text/kind. */
    private static Map<String, Long> writeSizes(Path path) throws IOException {
        Map<String, Long> sizes = new LinkedHashMap<>();
        StringBuilder csv = new StringBuilder("implementation,text,kind,codec,bytes\n");

        for (String text : Workload.TEXTS) {
            for (String kind : new String[] {"document", "values"}) {
                String[] values = Workload.read(text, kind);
                int maxUnits = 0;

                for (String value : values) {
                    maxUnits = Math.max(maxUnits, value.length());
                }

                for (String codec : allCodecs()) {
                    long total = 0;

                    try (Codecs.TextCodec c = Codecs.create(codec, maxUnits)) {
                        for (String value : values) {
                            byte[] bytes = c.encode(value);

                            if (!c.decode(bytes).equals(value)) {
                                throw new IllegalStateException(codec + " does not round-trip " + text + " " + kind);
                            }

                            total += bytes.length;
                        }
                    }

                    sizes.put(codec + "/" + text + "/" + kind, total);
                    csv.append("java,")
                            .append(text)
                            .append(',')
                            .append(kind)
                            .append(',')
                            .append(codec)
                            .append(',')
                            .append(total)
                            .append('\n');
                }
            }
        }

        Files.writeString(path, csv.toString(), StandardCharsets.UTF_8);
        return sizes;
    }

    private static void writeMarkdown(
            Path path,
            List<Result> results,
            Map<String, long[]> counts,
            Map<String, Long> sizes,
            Map<String, Map<String, Object>> runs,
            Properties versions)
            throws IOException {
        Map<String, Result> byKey = new LinkedHashMap<>();

        for (Result r : results) {
            byKey.put(key(r.codec, r.op, r.text, r.kind), r);
        }

        Map<String, Object> first = runs.values().iterator().next();
        StringBuilder md = new StringBuilder();
        md.append("# Speed of the Java Reference Implementation\n\n");
        md.append(
                        "Generated by `bench/run.sh` from `bench/results.json`; do not edit by hand. The common CSV files are ")
                .append("`bench/results.csv`, `bench/sizes.csv` and `bench/libraries.csv`.\n\n");
        md.append("- **Tool:** JMH ").append(first.get("jmhVersion")).append(", mode average time.\n");

        for (Map.Entry<String, Map<String, Object>> e : runs.entrySet()) {
            Map<String, Object> run = e.getValue();
            md.append("- **`")
                    .append(e.getKey())
                    .append("`:** ")
                    .append(intOf(run.get("forks")))
                    .append(" fork(s), warm-up ")
                    .append(intOf(run.get("warmupIterations")))
                    .append(" × ")
                    .append(run.get("warmupTime"))
                    .append(", measurement ")
                    .append(intOf(run.get("measurementIterations")))
                    .append(" × ")
                    .append(run.get("measurementTime"))
                    .append(" per benchmark.\n");
        }

        md.append("- **Runtime:** ")
                .append(first.get("vmName"))
                .append(' ')
                .append(first.get("vmVersion"))
                .append(" (JDK ")
                .append(first.get("jdkVersion"))
                .append("), options `")
                .append(String.join(" ", stringList(first.get("jvmArgs"))))
                .append("`.\n");
        md.append("- **Machine:** ").append(machine()).append(".\n");
        md.append("- **Date:** ").append(LocalDate.now(ZoneOffset.UTC)).append(".\n\n");

        md.append("""
                One operation processes every value of a workload once, in a plain loop. Java strings are immutable, \
                so every codec encodes a `String` to a new byte array and decodes to a new `String`. The compressors \
                work on UTF-8: their encode includes `getBytes(UTF_8)` and their decode `new String(bytes, UTF_8)`, \
                because that is the path from text to stored bytes and back. "vs UTF-8" is the speed of Steno \
                divided by the speed of the JDK's UTF-8: above 1, Steno is faster. The ± figure is JMH's 99.9% \
                confidence interval, in percent of the time. Sizes are total bytes divided by UTF-16 code units, \
                every value encoded on its own.

                The alternatives to Steno, UTF-8 and UTF-16 run on 10 of the 14 texts (english, french, russian, \
                arabic, hindi, thai, amharic, chinese, japanese, korean), with shorter iterations, so that the full \
                run fits in about 25 minutes.

                ## Libraries and How Each Is Driven

                | Codec | Library | Version | How it is driven |
                |---|---|---|---|
                """);

        for (String codec : allCodecs()) {
            String[] lib = library(codec, versions);
            md.append("| ")
                    .append(codec)
                    .append(" | `")
                    .append(lib[0])
                    .append("` | ")
                    .append(lib[1])
                    .append(" | ")
                    .append(driven(codec))
                    .append(" |\n");
        }

        md.append("\n## Steno, UTF-8 and UTF-16\n\n");
        md.append("### 16-Character Values: Nanoseconds per Value\n\nLower is better.\n\n");
        baselineTable(md, byKey, counts, "values", true);
        md.append("### Whole Documents: Millions of UTF-16 Code Units per Second\n\nHigher is better.\n\n");
        baselineTable(md, byKey, counts, "document", false);

        md.append("""
                ### Steno Encode into a Byte Array Allocated Once

                `Steno.encode(CharSequence, byte[], int)`, against `Steno.encode(CharSequence)`, which returns a new \
                array.

                | Text | Values: ns/value, into buffer | Values: ns/value, new array | Documents: M units/s, into \
                buffer | Documents: M units/s, new array |
                |---|--:|--:|--:|--:|
                """);

        for (String text : Workload.TEXTS) {
            Result vb = byKey.get(key("steno-into-buffer", "encode", text, "values"));
            Result vn = byKey.get(key("steno", "encode", text, "values"));
            Result db = byKey.get(key("steno-into-buffer", "encode", text, "document"));
            Result dn = byKey.get(key("steno", "encode", text, "document"));

            if (vb == null && vn == null && db == null && dn == null) {
                continue;
            }

            md.append("| ")
                    .append(text)
                    .append(" | ")
                    .append(perValue(vb, counts))
                    .append(" | ")
                    .append(perValue(vn, counts))
                    .append(" | ")
                    .append(rate(db, counts))
                    .append(" | ")
                    .append(rate(dn, counts))
                    .append(" |\n");
        }

        md.append("\n## SCSU and BOCU-1 (ICU4J)\n\n");
        groupTables(md, byKey, counts, sizes, ICU);
        md.append("## General Compressors on UTF-8\n\n");
        groupTables(md, byKey, counts, sizes, COMPRESSORS);

        md.append("## Sizes of Steno, UTF-8 and UTF-16\n\nBytes per UTF-16 code unit; lower is better.\n\n");
        sizeTable(md, sizes, counts, "values", BASELINES);
        sizeTable(md, sizes, counts, "document", BASELINES);

        md.append("""
                ## Caveats

                - One machine, one run. The machine may have been doing other work; the confidence intervals show how \
                much the figures moved.
                - Values are 16-character values (at most 16 UTF-16 code units); the time per value includes the \
                allocation of the result, which a short value makes a large part of the work.
                - The JIT compiles the loop of each benchmark for one workload. Real code that mixes scripts may be \
                slower.
                - The JDK's UTF-8 and UTF-16 are intrinsified and vectorised in the JVM. This implementation is \
                scalar Java with word-at-a-time fast paths.
                - Deflate, Zstandard, Brotli and LZ4 compress each value on its own, as a database stores a field; \
                none of them keeps the order or the prefixes of the text. brotli4j sets up its native state on every \
                call, which its API has no way to avoid; the other libraries reuse their objects.
                """);

        Files.writeString(path, md.toString(), StandardCharsets.UTF_8);
    }

    private static String driven(String codec) {
        switch (codec) {
            case "steno":
                return "`Steno.encode(String)`, `Steno.decode(byte[])`";
            case "utf8":
                return "`String.getBytes(UTF_8)`, `new String(bytes, UTF_8)`";
            case "utf16":
                return "`String.getBytes(UTF_16LE)`, `new String(bytes, UTF_16LE)`";
            case "scsu-icu4j":
            case "bocu1-icu4j":
                return "`Charset.forName` through ICU's provider; one `CharsetEncoder` and `CharsetDecoder`, reset per "
                        + "value, with output buffers made once; result copied out";
            case "deflate-fast":
            case "deflate-max":
                return "one `Deflater` (level " + (codec.endsWith("fast") ? "1" : "9")
                        + ", zlib format) and `Inflater`, reset per value, with buffers made once";
            case "zstd-1":
            case "zstd-19":
                return "one `ZstdCompressCtx` (level " + codec.substring(5)
                        + ") and `ZstdDecompressCtx`, reused; decompress into a buffer made once";
            case "brotli-1":
            case "brotli-11":
                return "`Encoder.compress(bytes, parameters)` (quality " + codec.substring(7)
                        + ", parameters made once) and `Decoder.decompress(bytes)`; no reusable object exists";
            default:
                return "`LZ4Factory.fastestInstance()`, its " + (codec.equals("lz4") ? "fast" : "high")
                        + " compressor and safe decompressor made once; decompress into a buffer made once";
        }
    }

    private static String key(String codec, String op, String text, String kind) {
        return codec + "/" + op + "/" + text + "/" + kind;
    }

    private static void baselineTable(
            StringBuilder md, Map<String, Result> byKey, Map<String, long[]> counts, String kind, boolean perValue) {
        String unit = perValue ? "ns/value" : "M units/s";
        md.append("| Text | Steno encode, ")
                .append(unit)
                .append(" | UTF-8 encode | UTF-16 encode | Encode vs UTF-8 | Steno decode, ")
                .append(unit)
                .append(" | UTF-8 decode | UTF-16 decode | Decode vs UTF-8 |\n");
        md.append("|---|--:|--:|--:|--:|--:|--:|--:|--:|\n");

        for (String text : Workload.TEXTS) {
            Result se = byKey.get(key("steno", "encode", text, kind));
            Result ue = byKey.get(key("utf8", "encode", text, kind));
            Result we = byKey.get(key("utf16", "encode", text, kind));
            Result sd = byKey.get(key("steno", "decode", text, kind));
            Result ud = byKey.get(key("utf8", "decode", text, kind));
            Result wd = byKey.get(key("utf16", "decode", text, kind));

            if (se == null && ue == null && sd == null && ud == null) {
                continue;
            }

            md.append("| ")
                    .append(text)
                    .append(" | ")
                    .append(figure(se, counts, perValue))
                    .append(" | ")
                    .append(figure(ue, counts, perValue))
                    .append(" | ")
                    .append(figure(we, counts, perValue))
                    .append(" | ")
                    .append(ratio(se, ue))
                    .append(" | ")
                    .append(figure(sd, counts, perValue))
                    .append(" | ")
                    .append(figure(ud, counts, perValue))
                    .append(" | ")
                    .append(figure(wd, counts, perValue))
                    .append(" | ")
                    .append(ratio(sd, ud))
                    .append(" |\n");
        }

        md.append('\n');
    }

    /** The speed tables (encode and decode, values and documents) and the size tables of a group of codecs. */
    private static void groupTables(
            StringBuilder md,
            Map<String, Result> byKey,
            Map<String, long[]> counts,
            Map<String, Long> sizes,
            String[] codecs) {
        for (String op : new String[] {"encode", "decode"}) {
            for (String kind : new String[] {"values", "document"}) {
                boolean perValue = kind.equals("values");
                md.append("### ")
                        .append(op.equals("encode") ? "Encode" : "Decode")
                        .append(perValue ? ", 16-Character Values: Nanoseconds per Value" : ", Whole Documents: ")
                        .append(perValue ? "" : "Millions of UTF-16 Code Units per Second")
                        .append("\n\n")
                        .append(perValue ? "Lower is better.\n\n" : "Higher is better.\n\n");
                header(md, codecs);

                for (String text : Workload.TEXTS) {
                    StringBuilder row = new StringBuilder("| ").append(text);
                    boolean any = false;

                    for (String codec : codecs) {
                        Result r = byKey.get(key(codec, op, text, kind));
                        any |= r != null && !codec.equals("steno");
                        row.append(" | ").append(figure(r, counts, perValue));
                    }

                    if (any) {
                        md.append(row).append(" |\n");
                    }
                }

                md.append('\n');
            }
        }

        md.append("### Sizes\n\nBytes per UTF-16 code unit, on all 14 texts; lower is better.\n\n");
        sizeTable(md, sizes, counts, "values", codecs);
        sizeTable(md, sizes, counts, "document", codecs);
    }

    private static void header(StringBuilder md, String[] codecs) {
        md.append("| Text");

        for (String codec : codecs) {
            md.append(" | ").append(codec);
        }

        md.append(" |\n|---");

        for (int i = 0; i < codecs.length; i++) {
            md.append("|--:");
        }

        md.append("|\n");
    }

    private static void sizeTable(
            StringBuilder md, Map<String, Long> sizes, Map<String, long[]> counts, String kind, String[] codecs) {
        md.append(kind.equals("values") ? "16-character values:\n\n" : "Whole documents:\n\n");
        header(md, codecs);

        for (String text : Workload.TEXTS) {
            md.append("| ").append(text);
            long units = counts.get(text + "/" + kind)[1];

            for (String codec : codecs) {
                Long bytes = sizes.get(codec + "/" + text + "/" + kind);
                md.append(" | ")
                        .append(bytes == null ? "" : String.format(Locale.ROOT, "%.2f", bytes / (double) units));
            }

            md.append(" |\n");
        }

        md.append('\n');
    }

    private static String figure(Result r, Map<String, long[]> counts, boolean perValue) {
        return perValue ? perValue(r, counts) : rate(r, counts);
    }

    private static String perValue(Result r, Map<String, long[]> counts) {
        if (r == null) {
            return "";
        }

        long values = counts.get(r.text + "/" + r.kind)[0];
        return String.format(Locale.ROOT, perValueFormat(r.ns / values), r.ns / values) + spread(r);
    }

    private static String perValueFormat(double ns) {
        return ns < 1000 ? "%.1f" : "%,.0f";
    }

    private static String rate(Result r, Map<String, long[]> counts) {
        if (r == null) {
            return "";
        }

        long units = counts.get(r.text + "/" + r.kind)[1];
        double rate = units * 1000.0 / r.ns;
        return String.format(Locale.ROOT, rate < 10 ? "%.2f" : "%,.0f", rate) + spread(r);
    }

    private static String spread(Result r) {
        return Double.isNaN(r.error) ? "" : String.format(Locale.ROOT, " ± %.0f%%", 100 * r.error / r.ns);
    }

    /** The texts of the summary in the README; BENCHMARKS.md has all of them. */
    private static final String[] README_TEXTS = {"english", "russian", "hindi", "chinese", "japanese", "korean"};

    /**
     * Replaces the block between {@code <!-- benchmarks:start -->} and {@code <!-- benchmarks:end -->} in the README
     * with a summary of the results and a link to the full report.
     */
    private static void updateReadme(
            Path readme, Path report, List<Result> results, Map<String, long[]> counts, Map<String, Object> run)
            throws IOException {
        String start = "<!-- benchmarks:start -->";
        String end = "<!-- benchmarks:end -->";
        String text = Files.readString(readme, StandardCharsets.UTF_8);
        int a = text.indexOf(start);
        int b = text.indexOf(end);

        if (a < 0 || b < a) {
            throw new IllegalStateException("The README has no " + start + " ... " + end + " block.");
        }

        Map<String, Result> byKey = new LinkedHashMap<>();

        for (Result r : results) {
            byKey.put(key(r.codec, r.op, r.text, r.kind), r);
        }

        StringBuilder block = new StringBuilder(start).append('\n');
        block.append("Measured with JMH ")
                .append(run.get("jmhVersion"))
                .append(" on JDK ")
                .append(run.get("jdkVersion"))
                .append(", on one machine (")
                .append(machine())
                .append("), on ")
                .append(LocalDate.now(ZoneOffset.UTC))
                .append(". \"vs UTF-8\" is the speed of Steno divided by the speed of the JDK's UTF-8. All texts, ")
                .append("SCSU, BOCU-1 and the compressors, the confidence intervals and the caveats are in [")
                .append(report.getFileName())
                .append("](")
                .append(report.getFileName())
                .append(").\n\n");
        block.append("| Text | 16-character values: encode, ns/value (vs UTF-8) | decode | Documents: encode, M ")
                .append("units/s (vs UTF-8) | decode |\n");
        block.append("|---|--:|--:|--:|--:|\n");

        for (String t : README_TEXTS) {
            Result ve = byKey.get(key("steno", "encode", t, "values"));
            Result vd = byKey.get(key("steno", "decode", t, "values"));
            Result de = byKey.get(key("steno", "encode", t, "document"));
            Result dd = byKey.get(key("steno", "decode", t, "document"));

            if (ve == null || vd == null || de == null || dd == null) {
                continue;
            }

            block.append("| ")
                    .append(t)
                    .append(" | ")
                    .append(summary(ve, byKey.get(key("utf8", "encode", t, "values")), counts, true))
                    .append(" | ")
                    .append(summary(vd, byKey.get(key("utf8", "decode", t, "values")), counts, true))
                    .append(" | ")
                    .append(summary(de, byKey.get(key("utf8", "encode", t, "document")), counts, false))
                    .append(" | ")
                    .append(summary(dd, byKey.get(key("utf8", "decode", t, "document")), counts, false))
                    .append(" |\n");
        }

        block.append(end);
        Files.writeString(
                readme, text.substring(0, a) + block + text.substring(b + end.length()), StandardCharsets.UTF_8);
    }

    private static String summary(Result steno, Result utf8, Map<String, long[]> counts, boolean perValue) {
        long[] c = counts.get(steno.text + "/" + steno.kind);
        String figure = perValue
                ? String.format(Locale.ROOT, "%.1f", steno.ns / c[0])
                : String.format(Locale.ROOT, "%,.0f", c[1] * 1000.0 / steno.ns);
        return utf8 == null ? figure : figure + " (" + ratio(steno, utf8) + ")";
    }

    private static String ratio(Result steno, Result utf8) {
        if (steno == null || utf8 == null) {
            return "";
        }

        return String.format(Locale.ROOT, "%.2f", utf8.ns / steno.ns);
    }

    private static String machine() {
        String cpu = System.getenv("PROCESSOR_IDENTIFIER");

        try {
            Path cpuinfo = Path.of("/proc/cpuinfo");

            if (Files.isReadable(cpuinfo)) {
                for (String line : Files.readAllLines(cpuinfo)) {
                    if (line.startsWith("model name")) {
                        cpu = line.substring(line.indexOf(':') + 1).trim();
                        break;
                    }
                }
            }
        } catch (IOException e) {
            // Leave the CPU unknown.
        }

        return (cpu == null ? "unknown CPU" : cpu) + ", " + Runtime.getRuntime().availableProcessors()
                + " logical processors, " + System.getProperty("os.name") + " " + System.getProperty("os.arch");
    }

    private static double number(Object value) {
        if (value instanceof Double) {
            return (Double) value;
        }

        // JMH writes "NaN" as a string when it cannot compute the error (one iteration).
        return Double.NaN;
    }

    private static int intOf(Object value) {
        return value instanceof Double ? (int) (double) (Double) value : 0;
    }

    private static List<String> stringList(Object value) {
        List<String> list = new ArrayList<>();

        if (value instanceof List) {
            for (Object item : (List<?>) value) {
                list.add(String.valueOf(item));
            }
        }

        return list;
    }

    /** A minimal JSON reader: objects, arrays, strings, numbers, true, false and null. */
    private static final class Json {
        private final String s;
        private int i;

        Json(String s) {
            this.s = s;
        }

        Object parse() {
            Object value = value();
            skip();

            if (i != s.length()) {
                throw error("trailing text");
            }

            return value;
        }

        private Object value() {
            skip();

            if (i >= s.length()) {
                throw error("unexpected end");
            }

            char c = s.charAt(i);

            if (c == '{') {
                Map<String, Object> map = new LinkedHashMap<>();
                i++;
                skip();

                if (s.charAt(i) == '}') {
                    i++;
                    return map;
                }

                while (true) {
                    skip();
                    String key = string();
                    skip();
                    expect(':');
                    map.put(key, value());
                    skip();

                    if (s.charAt(i) == ',') {
                        i++;
                    } else {
                        expect('}');
                        return map;
                    }
                }
            }

            if (c == '[') {
                List<Object> list = new ArrayList<>();
                i++;
                skip();

                if (s.charAt(i) == ']') {
                    i++;
                    return list;
                }

                while (true) {
                    list.add(value());
                    skip();

                    if (s.charAt(i) == ',') {
                        i++;
                    } else {
                        expect(']');
                        return list;
                    }
                }
            }

            if (c == '"') {
                return string();
            }

            if (s.startsWith("true", i)) {
                i += 4;
                return Boolean.TRUE;
            }

            if (s.startsWith("false", i)) {
                i += 5;
                return Boolean.FALSE;
            }

            if (s.startsWith("null", i)) {
                i += 4;
                return null;
            }

            int start = i;

            while (i < s.length() && "+-0123456789.eE".indexOf(s.charAt(i)) >= 0) {
                i++;
            }

            if (start == i) {
                throw error("unexpected character");
            }

            return Double.parseDouble(s.substring(start, i));
        }

        private String string() {
            expect('"');
            StringBuilder out = new StringBuilder();

            while (true) {
                char c = s.charAt(i++);

                if (c == '"') {
                    return out.toString();
                }

                if (c != '\\') {
                    out.append(c);
                    continue;
                }

                char e = s.charAt(i++);

                switch (e) {
                    case 'n':
                        out.append('\n');
                        break;
                    case 't':
                        out.append('\t');
                        break;
                    case 'r':
                        out.append('\r');
                        break;
                    case 'b':
                        out.append('\b');
                        break;
                    case 'f':
                        out.append('\f');
                        break;
                    case 'u':
                        out.append((char) Integer.parseInt(s.substring(i, i + 4), 16));
                        i += 4;
                        break;
                    default:
                        out.append(e);
                        break;
                }
            }
        }

        private void expect(char c) {
            if (i >= s.length() || s.charAt(i) != c) {
                throw error("expected '" + c + "'");
            }

            i++;
        }

        private void skip() {
            while (i < s.length() && Character.isWhitespace(s.charAt(i))) {
                i++;
            }
        }

        private IllegalStateException error(String message) {
            return new IllegalStateException("Bad JSON at " + i + ": " + message);
        }
    }
}
