```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.10GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.110
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                           | N       | Mean        | Error        | StdDev     | Ratio | RatioSD | Allocated | Alloc Ratio |
|--------------------------------- |-------- |------------:|-------------:|-----------:|------:|--------:|----------:|------------:|
| &#39;SnapshotTable.TryGetValue x10k&#39; | 1000000 |   462.48 μs |   311.596 μs |  17.080 μs |  1.00 |    0.04 |         - |          NA |
| &#39;TableSnapshot.TryGetValue x10k&#39; | 1000000 |   438.70 μs |   222.323 μs |  12.186 μs |  0.95 |    0.04 |         - |          NA |
| &#39;Dictionary x10k&#39;                | 1000000 |   100.44 μs |    18.068 μs |   0.990 μs |  0.22 |    0.01 |         - |          NA |
| &#39;FrozenDictionary x10k&#39;          | 1000000 |   196.97 μs |    77.409 μs |   4.243 μs |  0.43 |    0.02 |         - |          NA |
| &#39;ImmutableDictionary x10k&#39;       | 1000000 | 5,239.75 μs | 2,772.602 μs | 151.976 μs | 11.34 |    0.46 |         - |          NA |
| &#39;ImmutableList[i] x10k&#39;          | 1000000 | 5,021.57 μs | 3,164.948 μs | 173.481 μs | 10.87 |    0.47 |         - |          NA |
| &#39;ImmutableArray[i] x10k&#39;         | 1000000 |    11.22 μs |     1.530 μs |   0.084 μs |  0.02 |    0.00 |         - |          NA |
