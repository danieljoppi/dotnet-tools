```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.10GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.110
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                           | N       | Mean        | Error       | StdDev     | Ratio | RatioSD | Allocated | Alloc Ratio |
|--------------------------------- |-------- |------------:|------------:|-----------:|------:|--------:|----------:|------------:|
| &#39;SnapshotTable.TryGetValue x10k&#39; | 1000000 |   631.85 μs |   305.79 μs |  16.762 μs |  1.00 |    0.03 |         - |          NA |
| &#39;TableSnapshot.TryGetValue x10k&#39; | 1000000 |   586.04 μs |   254.44 μs |  13.947 μs |  0.93 |    0.03 |         - |          NA |
| &#39;Dictionary x10k&#39;                | 1000000 |   157.05 μs |    63.14 μs |   3.461 μs |  0.25 |    0.01 |         - |          NA |
| &#39;FrozenDictionary x10k&#39;          | 1000000 |   260.71 μs |   123.99 μs |   6.796 μs |  0.41 |    0.01 |         - |          NA |
| &#39;ImmutableDictionary x10k&#39;       | 1000000 | 6,505.95 μs | 7,849.42 μs | 430.253 μs | 10.30 |    0.64 |         - |          NA |
| &#39;ImmutableList[i] x10k&#39;          | 1000000 | 7,058.84 μs | 5,947.96 μs | 326.028 μs | 11.18 |    0.52 |         - |          NA |
| &#39;ImmutableArray[i] x10k&#39;         | 1000000 |    11.03 μs |    16.45 μs |   0.902 μs |  0.02 |    0.00 |         - |          NA |
