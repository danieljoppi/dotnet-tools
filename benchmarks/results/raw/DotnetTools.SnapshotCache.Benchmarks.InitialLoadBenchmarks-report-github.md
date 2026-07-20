```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.80GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.110
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                              | N       | Mean      | Error      | StdDev   | Ratio | RatioSD | Gen0      | Gen1      | Gen2      | Allocated | Alloc Ratio |
|------------------------------------ |-------- |----------:|-----------:|---------:|------:|--------:|----------:|----------:|----------:|----------:|------------:|
| SnapshotTable.Reset                 | 1000000 | 100.89 ms | 134.525 ms | 7.374 ms |  1.00 |    0.09 | 2800.0000 | 2600.0000 |  800.0000 |  34.48 MB |        1.00 |
| SnapshotTable.ResetParallel         | 1000000 |  34.14 ms |  33.016 ms | 1.810 ms |  0.34 |    0.03 | 3083.3333 | 3000.0000 |  916.6667 |  34.49 MB |        1.00 |
| ImmutableList.CreateRange           | 1000000 | 101.87 ms |  79.671 ms | 4.367 ms |  1.01 |    0.07 | 4400.0000 | 4200.0000 | 1200.0000 |  53.41 MB |        1.55 |
| ImmutableDictionary.CreateRange     | 1000000 | 321.89 ms | 101.198 ms | 5.547 ms |  3.20 |    0.20 | 4500.0000 | 4000.0000 | 1000.0000 |  61.04 MB |        1.77 |
| FrozenDictionary.ToFrozenDictionary | 1000000 |  77.84 ms |  77.933 ms | 4.272 ms |  0.77 |    0.06 | 1428.5714 | 1428.5714 | 1428.5714 | 139.35 MB |        4.04 |
| Dictionary                          | 1000000 |  13.33 ms |   4.466 ms | 0.245 ms |  0.13 |    0.01 |  484.3750 |  484.3750 |  484.3750 |  31.05 MB |        0.90 |
