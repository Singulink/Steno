# Benchmark tool

The source of every number, table and chart of the documentation. It measures the size of the Steno Encoding and the speed of the .NET
reference implementation (the library in `Source/dotnet/Singulink.Steno`) against other encodings and compressors. Run it in Release:

```
dotnet run -c Release --project Source/dotnet/Singulink.Steno.Benchmarks -- <command>
```

Without a command it lists the commands. The main one is `report`, which writes everything to `Docs/benchmarks`:

```
python Source/dotnet/Singulink.Steno.Benchmarks/fetch_wikipedia.py <wiki folder>
dotnet run -c Release --project Source/dotnet/Singulink.Steno.Benchmarks -- report --wiki <wiki folder> --wiki-date <date of the fetch> --force
```

The report is split into parts (machine, sizes, speed, ascii, compare, render) that each write a data file to `Docs/benchmarks/data`. Without
`--force` a part whose data file is there is kept. `--parts render` writes the Markdown and the charts again from the data alone.

## Speed

The speed, ascii and compare parts run the BenchmarkDotNet benchmarks of `Source/dotnet/Singulink.Steno.Benchmarks.Speed` (build the solution in
Release first). Each benchmark method calls the API under test directly: Steno and UTF-8 through `Encoding.GetBytes(ReadOnlySpan<char>,
Span<byte>)` and `GetChars`, ICU's SCSU and BOCU-1 through `icu.dll` (a native call per value), and UTF-8 with Zstd level 1 and Brotli quality 11.
The texts are the shared workloads of every implementation in `Benchmarks/Workloads` (the `workloads` command writes them from the corpus, and
`workloads --check` compares them byte for byte), plus four mixed-content documents of the corpus.

The 406 cases are split into eight categories (documents and values for each of Steno and UTF-8, ICU, and the compressors; ascii; compare), run one
at a time into `Benchmarks/results/dotnet-bdn/<category>`; a category with results is kept unless `--force` is given. The results become the data
files of the report and `Benchmarks/results/dotnet.csv`, the common CSV of the speed benchmarks of every implementation. The job is one process per
case, 6 warm-up and 15 measured iterations of 250 ms: about 50 minutes in all. Run it on a quiet machine:

```
dotnet build -c Release
dotnet run -c Release --project Source/dotnet/Singulink.Steno.Benchmarks -- report --parts machine,speed,ascii,compare
```

SCSU and BOCU-1 come from ICU4C through the `icu.dll` of Windows 10 1903 and later; elsewhere they are left out.

## The Wikipedia corpus

`fetch_wikipedia.py` fetches the plain text of the articles on eight topics in 56 languages (about 390 articles, 15 minutes, paced for the
API's rate limit) and writes a `manifest.tsv` of the revisions it got. The text is Wikipedia's under
[CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/), so it is not in the repository: fetch it yourself. Articles change, and the API
only gives their current text, so another fetch gives somewhat different numbers; the report records the date of the fetch it was made from.
