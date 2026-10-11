using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Splunk.Extensions.Logging.Benchmarks;

// Compares the working tree with a prebuilt baseline library when SPLUNK_BASELINE_DLL is set:
//   dotnet build -c Release <baseline checkout>
//   $env:SPLUNK_BASELINE_DLL = "<path>\Splunk.Extensions.Logging.dll"
//   dotnet run -c Release --project benchmarks/Splunk.Extensions.Logging.Benchmarks -- --filter "*"
var config = ManualConfig.Create(DefaultConfig.Instance).AddDiagnoser(MemoryDiagnoser.Default);
var baseline = Environment.GetEnvironmentVariable("SPLUNK_BASELINE_DLL");

if (string.IsNullOrEmpty(baseline))
{
    config.AddJob(Job.Default.WithId("current"));
}
else
{
    config.AddJob(Job.Default.WithId("baseline").AsBaseline()
        .WithArguments([new MsBuildArgument($"/p:SplunkLibraryDll={baseline}")]));
    config.AddJob(Job.Default.WithId("current"));
}

BenchmarkSwitcher.FromAssembly(typeof(LoggingBenchmarks).Assembly).Run(args, config);
