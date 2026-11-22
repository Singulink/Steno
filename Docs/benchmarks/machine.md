# How the Numbers Were Measured

- **Machine:** AMD Ryzen 7 9700X 8-Core Processor, 16 logical processors, supports AVX2, AVX-512 and SSSE3; Microsoft Windows 10.0.26200
- **Sizes:** measured on 2026-11-21 by the benchmark tool of the repository on .NET 10.0.14 (X64), at the commit b70511b.
- **SCSU and BOCU-1 sizes:** ICU's converters, from ICU4C 72.1, the icu.dll of Windows. SCSU leaves choices to the encoder that change the size, so another SCSU encoder can give other sizes.
- **Compressor sizes:** System.IO.Compression for Deflate (raw, without a zlib header) and Brotli, K4os.Compression.LZ4 1.3.8 for LZ4 blocks, and ZstdSharp.Port 0.8.6 for Zstandard (Zstd) frames, each on the UTF-8 of each value by itself. An LZ4 block does not record its decompressed length, and the LZ4 sizes do not include one; a store would have to keep it. A Zstandard frame records it. Deflate and Brotli streams mark their own end.
- **Built-in corpus:** the [Corpus](https://github.com/Singulink/Singulink.Steno/tree/main/Benchmarks/Singulink.Steno.Benchmarks/Corpus) folder of the benchmark tool: the UDHR in 47 languages, mixed content, and names, places, titles and UI strings.
- **Wikipedia corpus:** 388 articles in 56 languages, fetched on 2026-11-20. Wikipedia text is under CC BY-SA and is not in the repository. Articles change, so a new fetch gives other numbers.
- **Round trip:** every codec decoded every value back to its text.

A codec here is an encoding (Steno, UTF-8, UTF-16, SCSU, BOCU-1), or UTF-8 followed by a general compressor (Deflate, Brotli, LZ4 and Zstandard, each at a fast and a strong level): 13 in all. Sizes are exact. Steno, UTF-8, UTF-16 and BOCU-1 give one encoding per text, so their sizes are the same in every correct implementation; SCSU leaves choices to the encoder, and its sizes are those of ICU's encoder.

## Speed

Every implementation is measured with the benchmarking tool of its own ecosystem, on the same workloads: the files in [Benchmarks/Workloads](https://github.com/Singulink/Singulink.Steno/tree/main/Benchmarks/Workloads), 14 texts, each as one whole document and as values of at most 16 UTF-16 code units. One operation encodes or decodes every value of a text once, in a plain loop that calls the library directly, and the time per value is the tool's time per operation divided by the number of values. All the benchmarks ran on one machine that was doing other work at the same time, so the figures are indicative, and their error margins are part of the result. C has no benchmark tool of its own: the C implementation was measured in the Criterion harness of the Rust implementation's native benchmark, through its C interface, once per value, compiled with clang at -O2, in the same process and on the same inputs as Steno (Rust), so the ratio between the two is direct.

| Implementation | Tool | Error margin | Settings and versions |
|---|---|---|---|
| .NET | BenchmarkDotNet | the half-width of the 99.9% confidence interval of the mean | BenchmarkDotNet 0.15.8, one launch, 6 warm-up iterations and 15 measured iterations of 250 ms per benchmark, on .NET 10.0.14 (10.0.14, 10.0.1426.52326). |
| Rust | Criterion | the half-width of the 95% confidence interval of the mean | After the benchmark run. |
| C | Criterion, in the native benchmark of the Rust implementation | the half-width of the 95% confidence interval of the mean | After the benchmark run. |
| Go | the benchmarks of Go's `testing` package | the sample standard deviation of the runs, around their median | After the benchmark run. |
| Java | JMH | the half-width of the 99.9% confidence interval of the mean | After the benchmark run. |
| TypeScript | mitata | the half-width of the 99.9% confidence interval of the mean | After the benchmark run. |
| Python | pyperf | the standard deviation of the runs, around their mean | After the benchmark run. |

The headline figures, on the comparison pages and on [Cost per Value and Speed](../articles/benchmarks/speed.md), are those of the Rust reference implementation, which has a C interface, against native libraries: no call crosses a language boundary per value, and native code is what a database engine would embed. What makes that comparison unfair in either direction:

- Steno's UTF-8 form takes a Rust `&str`, which is valid UTF-8 already, so it does no validation of its own; the validated UTF-8 copy does.
- ICU's converters are called through ICU's C API once per value. Each call resets the converter and checks its arguments, which costs most on short values, and ICU works on UTF-16 only.
- The compressors work on each value by itself, as a database stores a field. Their frame headers and set-up cost most on short values: one-shot Brotli sets up an encoder on every call, while Zstd and Deflate reuse a context and LZ4 needs none.
- The compressors are the C reference libraries, built with the default settings of the Rust crates that bundle them.

#### How the Figures Are Shown

The pages show ratios against a stated baseline, not raw timings: "2.3× faster" means that the other codec takes 2.3 times as long as Steno; "40% slower" means that Steno takes 1.4 times as long as the other. Below a factor of 2 the difference is given in percent. Each ratio is rounded to two significant figures, or to one where the combined relative error margin of the two figures is above 10%. Where the difference between the two times lies within the sum of their error margins, the cell says "about the same", not a win for either. A summary over the texts shows the median text (of the two middle texts, the one less favourable to Steno) and how many texts went each way. Each table has one absolute figure for scale, Steno's own time or throughput on the median text, rounded the same way. The error margins are those the tools report, which differ in kind (see the table above).

#### Left Out

These figures are left out of the pages, each for the reason given; the data files still have them.

| Implementation | Text | Kind | Operation | Form | Codec | Figures left out | Reason |
|---|---|---|---|---|---|--:|---|
| dotnet | any | any | ascii | any | Ascii.IsValid | 0 | The figure for .NET's Ascii.IsValid looked implausible next to the copy of the same bytes in the earlier measurement; to be checked against the new one |

## Derived Figures

Two figures on these pages are calculated from the measurements:

- **Nanoseconds per value** = the tool's time for one operation, which encodes or decodes every value of a text once, ÷ the number of values. The values are pieces of at most 16 characters, so most are 16 and the last of each paragraph is shorter.
- **Values per 8 KB page** = 8,192 ÷ the mean bytes per 16-character value, rounded down, with the mean taken over all the values of the script's languages. It ignores what a store adds to each record, such as lengths, headers and slot arrays, so it is a calculation from the measured sizes, not a measurement of any database.
