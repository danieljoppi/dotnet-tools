using BenchmarkDotNet.Running;
using DotnetTools.SnapshotCache.Benchmarks;

if (args.Length > 2 && args[0] == "--memory-profile")
{
    MemoryProfile.Run(args[1], int.Parse(args[2]));
    return;
}

if (args.Length > 0 && args[0] == "--largescale")
{
    int rows = args.Length > 1 ? int.Parse(args[1]) : 100_000_000;
    int batch = args.Length > 2 ? int.Parse(args[2]) : 20_000;
    int cycles = args.Length > 3 ? int.Parse(args[3]) : 10;
    LargeScaleValidation.Run(rows, batch, cycles);
    return;
}

BenchmarkSwitcher
    .FromAssembly(typeof(BatchUpdateBenchmarks).Assembly)
    .Run(args);
