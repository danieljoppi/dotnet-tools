```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.80GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.110
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                              | N       | Mean      | Error      | StdDev    | Ratio | RatioSD | Gen0      | Gen1      | Gen2      | Allocated | Alloc Ratio |
|------------------------------------ |-------- |----------:|-----------:|----------:|------:|--------:|----------:|----------:|----------:|----------:|------------:|
| SnapshotTable.Reset                 | 1000000 | 111.74 ms | 250.265 ms | 13.718 ms |  1.01 |    0.15 | 4600.0000 | 4400.0000 | 1400.0000 |  54.52 MB |        1.00 |
| ImmutableList.CreateRange           | 1000000 | 109.21 ms |  36.037 ms |  1.975 ms |  0.99 |    0.10 | 4400.0000 | 4200.0000 | 1200.0000 |  53.41 MB |        0.98 |
| ImmutableDictionary.CreateRange     | 1000000 | 346.07 ms | 278.454 ms | 15.263 ms |  3.13 |    0.33 | 4500.0000 | 4000.0000 | 1000.0000 |  61.04 MB |        1.12 |
| FrozenDictionary.ToFrozenDictionary | 1000000 |  82.11 ms |  71.825 ms |  3.937 ms |  0.74 |    0.08 | 1428.5714 | 1428.5714 | 1428.5714 | 139.35 MB |        2.56 |
| Dictionary                          | 1000000 |  15.41 ms |   4.590 ms |  0.252 ms |  0.14 |    0.01 |  484.3750 |  484.3750 |  484.3750 |  31.05 MB |        0.57 |
