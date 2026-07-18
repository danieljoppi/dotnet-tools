using BenchmarkDotNet.Running;

BenchmarkSwitcher
    .FromAssembly(typeof(DotnetTools.SnapshotCache.Benchmarks.BatchUpdateBenchmarks).Assembly)
    .Run(args);
