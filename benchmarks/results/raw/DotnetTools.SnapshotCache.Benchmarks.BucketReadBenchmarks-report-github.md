```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.80GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.110
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                       | N       | K     | Skew    | Mean        | Error       | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|--------------------------------------------- |-------- |------ |-------- |------------:|------------:|----------:|------:|--------:|----------:|------------:|
| **&#39;ImmArray[i] x10k (hot-weighted)&#39;**            | **1000000** | **10000** | **Uniform** |    **269.6 μs** |    **67.40 μs** |   **3.69 μs** |  **1.00** |    **0.02** |         **-** |          **NA** |
| &#39;ChunkedList[i] x10k (hot-weighted)&#39;         | 1000000 | 10000 | Uniform |    706.7 μs |    63.85 μs |   3.50 μs |  2.62 |    0.03 |         - |          NA |
| &#39;SnapshotTable_Rekeyed lookup x10k&#39;          | 1000000 | 10000 | Uniform |  1,239.7 μs |   252.66 μs |  13.85 μs |  4.60 |    0.07 |         - |          NA |
| &#39;ImmArray scan all buckets (1M entities)&#39;    | 1000000 | 10000 | Uniform |  7,649.1 μs | 2,586.55 μs | 141.78 μs | 28.37 |    0.56 |         - |          NA |
| &#39;ChunkedList scan all buckets (1M entities)&#39; | 1000000 | 10000 | Uniform | 11,607.4 μs | 9,277.35 μs | 508.52 μs | 43.06 |    1.71 |         - |          NA |
|                                              |         |       |         |             |             |           |       |         |           |             |
| **&#39;ImmArray[i] x10k (hot-weighted)&#39;**            | **1000000** | **10000** | **Zipf**    |    **222.0 μs** |    **44.41 μs** |   **2.43 μs** |  **1.00** |    **0.01** |         **-** |          **NA** |
| &#39;ChunkedList[i] x10k (hot-weighted)&#39;         | 1000000 | 10000 | Zipf    |    448.1 μs |   242.83 μs |  13.31 μs |  2.02 |    0.06 |         - |          NA |
| &#39;SnapshotTable_Rekeyed lookup x10k&#39;          | 1000000 | 10000 | Zipf    |  1,230.2 μs |   438.39 μs |  24.03 μs |  5.54 |    0.11 |         - |          NA |
| &#39;ImmArray scan all buckets (1M entities)&#39;    | 1000000 | 10000 | Zipf    |  8,631.6 μs | 2,256.48 μs | 123.69 μs | 38.89 |    0.61 |         - |          NA |
| &#39;ChunkedList scan all buckets (1M entities)&#39; | 1000000 | 10000 | Zipf    | 12,165.7 μs | 6,821.73 μs | 373.92 μs | 54.81 |    1.55 |         - |          NA |
