```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.10GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.110
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                              | N       | Mean      | Error    | StdDev   | Ratio | RatioSD | Gen0      | Gen1      | Gen2      | Allocated | Alloc Ratio |
|------------------------------------ |-------- |----------:|---------:|---------:|------:|--------:|----------:|----------:|----------:|----------:|------------:|
| SnapshotTable.Reset                 | 1000000 |  65.73 ms | 81.54 ms | 4.469 ms |  1.00 |    0.08 | 1375.0000 | 1250.0000 |  500.0000 | 120.63 MB |        1.00 |
| ImmutableList.CreateRange           | 1000000 |  44.59 ms | 16.88 ms | 0.925 ms |  0.68 |    0.04 |  666.6667 |  600.0000 |         - |  53.41 MB |        0.44 |
| ImmutableDictionary.CreateRange     | 1000000 | 241.72 ms | 46.41 ms | 2.544 ms |  3.69 |    0.22 |  666.6667 |  333.3333 |         - |  61.04 MB |        0.51 |
| FrozenDictionary.ToFrozenDictionary | 1000000 |  69.02 ms | 58.98 ms | 3.233 ms |  1.05 |    0.08 | 1250.0000 | 1250.0000 | 1250.0000 | 139.35 MB |        1.16 |
| Dictionary                          | 1000000 |  13.21 ms | 15.17 ms | 0.831 ms |  0.20 |    0.02 |  484.3750 |  484.3750 |  484.3750 |  31.05 MB |        0.26 |
