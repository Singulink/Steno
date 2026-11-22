using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Json;

namespace Singulink.Steno.Benchmarks;

/// <summary>
/// Measures everything that the documentation shows and writes it as data files, Markdown fragments and SVG charts: the encoded sizes of every
/// codec over both corpora, the throughput of the main codecs, the ASCII fast paths, and comparing encoded values without decoding them.
/// </summary>
/// <remarks>
/// <para>
/// The work is split into parts that each write a data file of their own (see <see cref="Parts"/>). A part whose data file is there is not measured
/// again unless <c>--force</c> is given. The render part only reads the data files.</para>
/// <para>
/// The speed, ascii and compare parts run the BenchmarkDotNet benchmarks of Singulink.Steno.Benchmarks.Speed (which must be built in Release), one
/// category of cases at a time, each into a folder of its own under the artifacts folder (by default Benchmarks/results/dotnet-bdn). A category
/// whose results are there is not run again unless <c>--force</c> is given, so a run that stops goes on with the categories it has not done. Then
/// the results are turned into the data files of the report and the common CSV of every implementation (Benchmarks/results/dotnet.csv).</para>
/// </remarks>
internal static class Report
{
    public static readonly string[] Parts = ["machine", "sizes", "speed", "ascii", "compare", "render"];

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // The longest that one category of BenchmarkDotNet cases may take (the longest takes about 9 minutes).
    private static readonly TimeSpan CategoryTimeout = TimeSpan.FromMinutes(40);

    // The categories of the speed benchmarks of each part (all of a category's names must match), as Singulink.Steno.Benchmarks.Speed defines them.
    private static readonly Dictionary<string, string[][]> BenchmarkCategories = new() {
        ["speed"] = [
            ["documents", "steno-utf8"], ["documents", "icu"], ["documents", "compressors"],
            ["values", "steno-utf8"], ["values", "icu"], ["values", "compressors"],
        ],
        ["ascii"] = [["ascii"]],
        ["compare"] = [["compare"]],
    };

    public static int Run(string[] args)
    {
        string root = FindRepoRoot();
        string output = Path.GetFullPath(GetOption(args, "--out") ?? Path.Combine(root, "Docs", "benchmarks"));
        string? wiki = GetOption(args, "--wiki");
        string? wikiDate = GetOption(args, "--wiki-date");
        string[] parts = GetOption(args, "--parts")?.Split(',') ?? Parts;
        bool force = args.Contains("--force");
        bool smoke = args.Contains("--smoke");
        string artifacts = Path.GetFullPath(GetOption(args, "--artifacts") ?? Path.Combine(root, "Benchmarks", "results", "dotnet-bdn"));
        string csv = Path.GetFullPath(GetOption(args, "--csv") ?? Path.Combine(root, "Benchmarks", "results", "dotnet.csv"));
        string? filter = GetOption(args, "--filter");

        foreach (string part in parts)
        {
            if (!Parts.Contains(part))
            {
                Console.WriteLine($"Unknown part '{part}'. The parts are: {string.Join(", ", Parts)}.");
                return 2;
            }
        }

        string data = Path.Combine(output, "data");
        Directory.CreateDirectory(data);
        Console.WriteLine($"Report folder: {output}");

        foreach (string part in parts)
        {
            Console.WriteLine($"== {part} ==");
            var watch = Stopwatch.StartNew();

            int result = part switch {
                "machine" => WriteMachine(data, force),
                "sizes" => WriteSizes(data, wiki, wikiDate, force),
                "speed" or "ascii" or "compare" => RunBenchmarks(part, root, artifacts, data, csv, filter, smoke, force),
                _ => ReportRender.Run(output),
            };

            Console.WriteLine($"   {part}: {(result is 0 ? "done" : "FAILED")} in {watch.Elapsed.TotalSeconds:F0} s");

            if (result is not 0)
                return result;
        }

        return 0;
    }

    public static string? GetOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    public static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FORMAT.md")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("The repository (the folder with FORMAT.md) is not above the tool.");
    }

    private static int WriteMachine(string data, bool force)
    {
        string path = Path.Combine(data, "machine.json");

        if (File.Exists(path) && !force)
        {
            Console.WriteLine("   kept (use --force to measure again)");
            return 0;
        }

        var machine = new MachineInfo(
            DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ProcessorName(),
            Environment.ProcessorCount,
            RuntimeInformation.OSDescription,
            $".NET {Environment.Version} ({RuntimeInformation.ProcessArchitecture})",
            IcuConverter.IsAvailable ? $"ICU4C {IcuConverter.Version} (icu.dll of Windows)" : "not available",
            $"AVX2 {Avx2.IsSupported}, AVX-512 {Avx512F.IsSupported}, SSSE3 {Ssse3.IsSupported}",
            GitCommit());

        File.WriteAllText(path, JsonSerializer.Serialize(machine, Json));
        Console.WriteLine($"   {machine.Cpu}, {machine.Os}, {machine.Runtime}");
        return 0;
    }

    private static int WriteSizes(string data, string? wiki, string? wikiDate, bool force)
    {
        int failures = 0;

        foreach ((string file, string corpus) in (ReadOnlySpan<(string, string)>)[("sizes-builtin.json", "built-in"), ("sizes-wikipedia.json", "Wikipedia")])
        {
            string path = Path.Combine(data, file);

            if (File.Exists(path) && !force)
            {
                Console.WriteLine($"   {corpus}: kept (use --force to measure again)");
                continue;
            }

            if (corpus is "Wikipedia" && wiki is null)
            {
                Console.WriteLine("   Wikipedia: no --wiki folder given, skipped");
                continue;
            }

            var entries = corpus is "Wikipedia" ? Corpus.LoadWikipedia(wiki!) : Corpus.LoadForReport();
            Console.WriteLine($"   {corpus}: {entries.Count} sets, {entries.Sum(e => e.Units.Count):N0} values, {entries.Sum(e => e.CharCount):N0} chars");

            (var rows, long failed) = SizeData.Measure(entries, Codecs.All, Console.WriteLine);
            failures += (int)Math.Min(failed, 1);

            string fetched = wikiDate is null ? string.Empty : " on " + wikiDate;
            string? source = corpus is "Wikipedia"
                ? $"{Directory.EnumerateFiles(wiki!, "*.txt").Count()} articles fetched by fetch_wikipedia.py{fetched}"
                : "the Corpus folder of the tool";

            var sizes = new SizeFile(corpus, source, [.. Codecs.All.Select(c => c.Name)], rows, failed);
            File.WriteAllText(path, JsonSerializer.Serialize(sizes, Json));

            using (var csv = new StreamWriter(Path.ChangeExtension(path, ".csv"), false, new UTF8Encoding(false)) { NewLine = "\n" })
            {
                csv.WriteLine("group,name,values,chars," + string.Join(',', sizes.Codecs.Select(Csv)));

                foreach (var row in rows)
                    csv.WriteLine($"{Csv(row.Group)},{Csv(row.Name)},{row.Values},{row.Chars}," + string.Join(',', sizes.Codecs.Select(c => row.Bytes[c])));
            }

            Console.WriteLine(failed is 0 ? "   every codec gave every value back" : $"   !! {failed} values did not come back");
        }

        return failures;
    }

    /// <summary>
    /// Runs the BenchmarkDotNet categories of a part that have no results yet (or all of them with --force), then turns all the results there are
    /// into the data files.
    /// </summary>
    private static int RunBenchmarks(string part, string root, string artifacts, string data, string csv, string? filter, bool smoke, bool force)
    {
        string project = Path.Combine(root, "Benchmarks", "Singulink.Steno.Benchmarks.Speed");
        string program = Path.Combine(project, "bin", "Release", "net10.0", "Singulink.Steno.Benchmarks.Speed.exe");

        if (!File.Exists(program))
        {
            Console.WriteLine($"   !! {program} is not there: build the solution in Release first.");
            return 1;
        }

        int failures = 0;

        foreach (string[] categories in BenchmarkCategories[part])
        {
            string folder = Path.Combine(artifacts, string.Join('-', categories));
            bool done = Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*-report-full.json", SearchOption.AllDirectories).Any();

            if (done && !force)
            {
                Console.WriteLine($"   {string.Join(", ", categories)}: kept (use --force to run again)");
                continue;
            }

            if (done)
                Directory.Delete(folder, recursive: true);

            Console.WriteLine($"   {string.Join(", ", categories)}: running BenchmarkDotNet");

            string[] arguments = [
                "--allCategories", .. categories, "--artifacts", folder, "--join",
                .. filter is null ? [] : (string[])["--filter", .. filter.Split(',')],
                .. smoke ? (string[])["--smoke"] : [],
            ];

            if (RunProcess(program, project, arguments) is not 0)
                failures++;
        }

        string file = part is "speed" ? "speed.jsonl" : part + ".json";
        BenchmarkImport.Run(artifacts, data, csv, speed: part is "speed", ascii: part is "ascii", compare: part is "compare");
        Console.WriteLine($"   {file} written from the results");

        return failures is 0 ? 0 : 1;
    }

    /// <summary>
    /// Runs a program with a time limit, in the given folder (BenchmarkDotNet finds the project to build its cases from there).
    /// </summary>
    private static int RunProcess(string program, string folder, string[] arguments)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, WorkingDirectory = folder };

        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;

        if (!process.WaitForExit(CategoryTimeout))
        {
            process.Kill(entireProcessTree: true);
            Console.WriteLine($"   !! timed out: {string.Join(' ', arguments)}");
            return 1;
        }

        return process.ExitCode;
    }

    private static string ProcessorName()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");

            if (key?.GetValue("ProcessorNameString") is string name)
                return name.Trim();
        }

        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown";
    }

    private static string GitCommit()
    {
        try
        {
            var start = new ProcessStartInfo("git", "rev-parse --short HEAD") {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                WorkingDirectory = FindRepoRoot(),
            };

            using var process = Process.Start(start)!;
            string commit = process.StandardOutput.ReadToEnd().Trim();
            return process.WaitForExit(10_000) && process.ExitCode is 0 ? commit : "unknown";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "unknown";
        }
    }

    private static string Csv(string value) => value.Contains(',') || value.Contains('"') ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}

internal sealed record MachineInfo(string Date, string Cpu, int LogicalProcessors, string Os, string Runtime, string Icu, string Intrinsics, string Commit);

internal sealed record SizeFile(string Corpus, string Source, string[] Codecs, List<SizeRow> Rows, long RoundTripFailures);
