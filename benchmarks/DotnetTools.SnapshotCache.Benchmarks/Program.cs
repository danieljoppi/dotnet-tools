using BenchmarkDotNet.Running;
using DotnetTools.SnapshotCache.Benchmarks;

if (args.Length > 2 && args[0] == "--memory-profile")
{
    MemoryProfile.Run(args[1], int.Parse(args[2]));
    return;
}

if (args.Length > 1 && args[0] == "--tail-latency")
{
    TailLatencyStudy.Run(
        args[1],
        rows: args.Length > 2 ? int.Parse(args[2]) : 10_000_000,
        seconds: args.Length > 3 ? int.Parse(args[3]) : 60,
        batchSize: args.Length > 4 ? int.Parse(args[4]) : 20_000,
        refreshEveryMs: args.Length > 5 ? int.Parse(args[5]) : 3_000);
    return;
}

if (args.Length > 0 && args[0] == "--immutablearray-study")
{
    int studyRows = args.Length > 1 ? int.Parse(args[1]) : 10_000_000;
    int studyBatch = args.Length > 2 ? int.Parse(args[2]) : 20_000;
    ImmutableArrayStudy.Run(studyRows, studyBatch);
    return;
}

if (args.Length > 0 && args[0] == "--soak")
{
    int soakRows = args.Length > 1 ? int.Parse(args[1]) : 20_000_000;
    int soakCycles = args.Length > 2 ? int.Parse(args[2]) : 400;
    int soakBatch = args.Length > 3 ? int.Parse(args[3]) : 20_000;
    SoakValidation.Run(soakRows, soakCycles, soakBatch);
    return;
}

if (args.Length > 0 && args[0] == "--bucket-loh")
{
    BucketLohStudy.Run(
        entities: args.Length > 1 ? int.Parse(args[1]) : 1_000_000,
        buckets: args.Length > 2 ? int.Parse(args[2]) : 10_000,
        skew: args.Length > 3 ? args[3] : "zipf",
        cycles: args.Length > 4 ? int.Parse(args[4]) : 10,
        tableChunkRows: args.Length > 5 ? int.Parse(args[5]) : 0,
        onlyApproach: args.Length > 6 ? args[6] : null);
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
