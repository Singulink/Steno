using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Perfolizer.Horology;
using Singulink.Steno.Benchmarks;
using Singulink.Steno.Benchmarks.Speed;

// The speed benchmarks of the .NET implementation, with BenchmarkDotNet. Usually started by the report command of Singulink.Steno.Benchmarks (one
// category at a time); BenchmarkDotNet's own options (--filter, --anyCategories, --allCategories, --artifacts, --list flat) all work. --smoke uses
// BenchmarkDotNet's dry job: one iteration, to check that everything runs, not to measure.
bool smoke = args.Contains("--smoke");
args = [.. args.Where(a => a is not "--smoke")];

var job = smoke ? Job.Dry : Categories.MeasureJob;

// The processes that BenchmarkDotNet starts for each case find the workload files through the repository.
job = job.WithEnvironmentVariables(new EnvironmentVariable("STENO_REPO", Workloads.Repository)).AsDefault();

var config = DefaultConfig.Instance
    .AddJob(job)
    .AddExporter(JsonExporter.Full)
    .WithSummaryStyle(DefaultConfig.Instance.SummaryStyle.WithTimeUnit(TimeUnit.Nanosecond));

BenchmarkSwitcher.FromAssembly(typeof(DocumentBenchmarks).Assembly).Run(args, config);
return 0;
