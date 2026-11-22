using System.Text;
using Singulink.Steno.Benchmarks;

Console.OutputEncoding = Encoding.UTF8;

string command = args.Length > 0 ? args[0] : "help";
string[] rest = args.Length > 0 ? args[1..] : [];

switch (command)
{
    case "report":
        return Report.Run(rest);

    case "sizes":
        return SizeReport.Run(rest);

    case "ordered-lab":
        return OrderedLab.Run(rest);

    case "vectors":
        return TestVectors.Run(rest);

    case "ordered-fit":
        return OrderedFit.Run(rest);

    case "workloads":
        return Workloads.Write(Report.GetOption(rest, "--out") ?? Workloads.Folder, rest.Contains("--check")) is 0 || !rest.Contains("--check") ? 0 : 1;

    default:
        Console.WriteLine("""
            Usage: Singulink.Steno.Benchmarks <command> [options]

              report [--out <folder>] [--wiki <folder>] [--wiki-date <date>] [--parts <list>] [--force]
                     [--artifacts <folder>] [--csv <path>] [--filter <patterns>] [--smoke]
                  Measures everything that the documentation shows and writes data files, Markdown tables and SVG charts, by default to
                  Docs/benchmarks. The parts are machine, sizes, speed, ascii, compare and render (all by default); a part whose data file is
                  there is kept unless --force is given. --wiki adds the Wikipedia corpus that fetch_wikipedia.py fetched.
                  The speed, ascii and compare parts run the BenchmarkDotNet benchmarks of Singulink.Steno.Benchmarks.Speed (build the
                  solution in Release first), one category at a time into --artifacts (default Benchmarks/results/dotnet-bdn); a category
                  with results is kept unless --force is given, so a stopped run goes on where it stopped. The results become the data files
                  and the common CSV (--csv, default Benchmarks/results/dotnet.csv). --filter passes BenchmarkDotNet filters (comma-separated
                  globs); --smoke runs BenchmarkDotNet's dry job, to check that everything works, not to measure.

              sizes [--filter <text>] [--wiki <folder>] [--details]
                  Prints the encoded sizes of Steno, UTF-8, UTF-16, ICU's SCSU and BOCU-1 and the compressors on UTF-8 over the corpus,
                  group by group, and checks that every codec gives every value back.

              workloads [--check] [--out <folder>]
                  Writes the shared workload files of the speed benchmarks of every implementation (Benchmarks/Workloads) from the corpus;
                  --check only compares them with the files that are there, byte for byte.

              ordered-lab [--details] [--filter <text>] [--candidates <names>] [--corpus <folder of .txt files>]
                  The research record of the byte-ordered formats that led to Steno, version 1: sizes of the candidates next to ICU's BOCU-1
                  and SCSU and Steno, with the order and the round trip checked, and candidate Z checked against the library byte for byte.

              vectors [--out <path>] [--random-count <n>] [--examples]
                  Writes the test vectors of Steno, version 1 (FORMAT.md) from the reference codec, by default to
                  TestVectors/steno-v1-vectors.txt, and checks the library against them; --examples prints the worked examples and the initial
                  layout instead.

              ordered-fit
                  Builds the layout of every state of Steno, version 1, with every second window, and checks that each one fits in the 255
                  lead byte values (on 4 threads, about a minute).
            """);
        return 2;
}
