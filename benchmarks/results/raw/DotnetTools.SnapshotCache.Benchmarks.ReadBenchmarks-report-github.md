```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.80GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.110
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                           | N       | Mean         | Error          | StdDev      | Ratio | RatioSD | Allocated | Alloc Ratio |
|--------------------------------- |-------- |-------------:|---------------:|------------:|------:|--------:|----------:|------------:|
| &#39;SnapshotTable.TryGetValue x10k&#39; | 1000000 |   679.870 μs |  1,065.7303 μs |  58.4163 μs |  1.00 |    0.10 |         - |          NA |
| &#39;TableSnapshot.TryGetValue x10k&#39; | 1000000 |   802.388 μs |    329.3125 μs |  18.0507 μs |  1.19 |    0.09 |         - |          NA |
| &#39;Dictionary x10k&#39;                | 1000000 |   136.785 μs |      2.5911 μs |   0.1420 μs |  0.20 |    0.01 |         - |          NA |
| &#39;FrozenDictionary x10k&#39;          | 1000000 |   289.172 μs |     25.5697 μs |   1.4016 μs |  0.43 |    0.03 |         - |          NA |
| &#39;ImmutableDictionary x10k&#39;       | 1000000 | 5,852.177 μs | 11,274.2657 μs | 617.9805 μs |  8.65 |    1.00 |         - |          NA |
| &#39;ImmutableList[i] x10k&#39;          | 1000000 | 6,267.221 μs | 13,358.5368 μs | 732.2265 μs |  9.26 |    1.15 |         - |          NA |
| &#39;ImmutableArray[i] x10k&#39;         | 1000000 |     8.921 μs |      0.1889 μs |   0.0104 μs |  0.01 |    0.00 |         - |          NA |
