```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.10GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.110
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                             | N       | BatchSize | Mean        | Error       | StdDev      | Ratio | RatioSD | Gen0     | Gen1     | Gen2     | Allocated    | Alloc Ratio |
|----------------------------------- |-------- |---------- |------------:|------------:|------------:|------:|--------:|---------:|---------:|---------:|-------------:|------------:|
| &#39;SnapshotTable random 5k batch&#39;    | 1000000 | 5000      |  7,883.8 μs |  7,014.8 μs |   384.51 μs |  1.00 |    0.06 | 187.5000 | 171.8750 |        - |  15694.35 KB |       1.000 |
| &#39;SnapshotTable clustered 5k batch&#39; | 1000000 | 5000      |    486.8 μs |    973.9 μs |    53.38 μs |  0.06 |    0.01 |   1.4648 |   0.4883 |        - |    136.66 KB |       0.009 |
| &#39;Dictionary rebuild + swap&#39;        | 1000000 | 5000      | 28,525.4 μs | 18,973.6 μs | 1,040.01 μs |  3.62 |    0.19 |  62.5000 |  62.5000 |  62.5000 |  31792.34 KB |       2.026 |
| &#39;FrozenDictionary rebuild&#39;         | 1000000 | 5000      | 88,051.7 μs | 39,362.4 μs | 2,157.59 μs | 11.19 |    0.52 | 166.6667 | 166.6667 | 166.6667 | 100814.04 KB |       6.424 |
