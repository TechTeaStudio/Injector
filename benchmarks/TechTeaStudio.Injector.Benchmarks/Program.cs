using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

namespace TechTeaStudio.Injector.Benchmarks;

/// <summary>
/// Entry point. Runs every benchmark class in the assembly under a short job so
/// the whole suite finishes in a few minutes while still producing real,
/// directionally-correct numbers. Pass standard BenchmarkDotNet filter args
/// (e.g. <c>--filter *TransientResolve*</c>) to narrow the run.
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        // ShortRun-style job: a handful of warmup + measurement iterations.
        // Enough to separate the containers without publication-grade precision.
        var config = DefaultConfig.Instance
            .AddJob(Job.ShortRun
                .WithWarmupCount(3)
                .WithIterationCount(5)
                .WithLaunchCount(1));

        BenchmarkSwitcher
            .FromAssembly(typeof(Program).Assembly)
            .Run(args, config);
    }
}
