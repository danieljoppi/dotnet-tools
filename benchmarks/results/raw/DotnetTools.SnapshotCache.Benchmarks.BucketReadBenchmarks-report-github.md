```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.10GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.110
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                       | N       | K     | Skew    | Mean       | Error       | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|--------------------------------------------- |-------- |------ |-------- |-----------:|------------:|----------:|------:|--------:|----------:|------------:|
| **&#39;ImmArray[i] x10k (hot-weighted)&#39;**            | **1000000** | **10000** | **Uniform** |   **191.8 μs** |   **121.69 μs** |   **6.67 μs** |  **1.00** |    **0.04** |         **-** |          **NA** |
| &#39;ChunkedList[i] x10k (hot-weighted)&#39;         | 1000000 | 10000 | Uniform |   381.7 μs |    96.81 μs |   5.31 μs |  1.99 |    0.07 |         - |          NA |
| &#39;SnapshotTable_Rekeyed lookup x10k&#39;          | 1000000 | 10000 | Uniform | 1,787.7 μs | 1,836.44 μs | 100.66 μs |  9.33 |    0.54 |         - |          NA |
| &#39;ImmArray scan all buckets (1M entities)&#39;    | 1000000 | 10000 | Uniform | 5,132.2 μs |   799.35 μs |  43.82 μs | 26.78 |    0.84 |         - |          NA |
| &#39;ChunkedList scan all buckets (1M entities)&#39; | 1000000 | 10000 | Uniform | 5,422.0 μs |   347.38 μs |  19.04 μs | 28.29 |    0.87 |         - |          NA |
|                                              |         |       |         |            |             |           |       |         |           |             |
| **&#39;ImmArray[i] x10k (hot-weighted)&#39;**            | **1000000** | **10000** | **Zipf**    |   **153.0 μs** |    **43.29 μs** |   **2.37 μs** |  **1.00** |    **0.02** |         **-** |          **NA** |
| &#39;ChunkedList[i] x10k (hot-weighted)&#39;         | 1000000 | 10000 | Zipf    |   375.0 μs |    77.98 μs |   4.27 μs |  2.45 |    0.04 |         - |          NA |
| &#39;SnapshotTable_Rekeyed lookup x10k&#39;          | 1000000 | 10000 | Zipf    | 1,733.9 μs | 2,290.57 μs | 125.55 μs | 11.33 |    0.73 |         - |          NA |
| &#39;ImmArray scan all buckets (1M entities)&#39;    | 1000000 | 10000 | Zipf    | 4,468.7 μs | 3,693.77 μs | 202.47 μs | 29.21 |    1.21 |         - |          NA |
| &#39;ChunkedList scan all buckets (1M entities)&#39; | 1000000 | 10000 | Zipf    | 6,155.3 μs |   437.66 μs |  23.99 μs | 40.23 |    0.55 |         - |          NA |
