```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.10GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.110
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                             | N       | K     | Skew    | Mean         | Error        | StdDev      | Ratio    | RatioSD | Allocated | Alloc Ratio |
|--------------------------------------------------- |-------- |------ |-------- |-------------:|-------------:|------------:|---------:|--------:|----------:|------------:|
| **&#39;ImmArray[i] x10k (hot-weighted)&#39;**                  | **1000000** | **10000** | **Uniform** |     **182.6 μs** |    **113.02 μs** |     **6.19 μs** |     **1.00** |    **0.04** |         **-** |          **NA** |
| &#39;ChunkedList[i] x10k (hot-weighted)&#39;               | 1000000 | 10000 | Uniform |     384.6 μs |    121.78 μs |     6.68 μs |     2.11 |    0.07 |         - |          NA |
| &#39;SnapshotTable_Rekeyed lookup x10k&#39;                | 1000000 | 10000 | Uniform |   1,884.3 μs |    492.36 μs |    26.99 μs |    10.33 |    0.33 |         - |          NA |
| &#39;ImmArray scan all buckets (1M entities)&#39;          | 1000000 | 10000 | Uniform |   4,499.9 μs |    530.81 μs |    29.10 μs |    24.66 |    0.75 |         - |          NA |
| &#39;ChunkedList scan all buckets (1M entities)&#39;       | 1000000 | 10000 | Uniform |   5,625.1 μs |  1,425.12 μs |    78.12 μs |    30.82 |    1.00 |         - |          NA |
| &#39;SnapshotTable group scan via index (1M entities)&#39; | 1000000 | 10000 | Uniform | 149,203.8 μs | 85,666.26 μs | 4,695.66 μs |   817.62 |   33.12 |  320006 B |          NA |
|                                                    |         |       |         |              |              |             |          |         |           |             |
| **&#39;ImmArray[i] x10k (hot-weighted)&#39;**                  | **1000000** | **10000** | **Zipf**    |     **136.4 μs** |     **12.26 μs** |     **0.67 μs** |     **1.00** |    **0.01** |         **-** |          **NA** |
| &#39;ChunkedList[i] x10k (hot-weighted)&#39;               | 1000000 | 10000 | Zipf    |     310.2 μs |    377.43 μs |    20.69 μs |     2.27 |    0.13 |         - |          NA |
| &#39;SnapshotTable_Rekeyed lookup x10k&#39;                | 1000000 | 10000 | Zipf    |   1,791.2 μs |  3,227.38 μs |   176.90 μs |    13.13 |    1.12 |         - |          NA |
| &#39;ImmArray scan all buckets (1M entities)&#39;          | 1000000 | 10000 | Zipf    |   4,464.2 μs |    366.58 μs |    20.09 μs |    32.72 |    0.19 |         - |          NA |
| &#39;ChunkedList scan all buckets (1M entities)&#39;       | 1000000 | 10000 | Zipf    |   4,674.4 μs |  8,393.82 μs |   460.09 μs |    34.26 |    2.92 |         - |          NA |
| &#39;SnapshotTable group scan via index (1M entities)&#39; | 1000000 | 10000 | Zipf    | 137,434.1 μs | 16,202.49 μs |   888.11 μs | 1,007.31 |    7.09 |  321590 B |          NA |
