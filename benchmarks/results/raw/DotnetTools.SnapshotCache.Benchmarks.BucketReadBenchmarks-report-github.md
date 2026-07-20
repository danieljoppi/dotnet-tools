```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.80GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.110
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                             | N       | K     | Skew    | Mean         | Error         | StdDev       | Ratio  | RatioSD | Allocated | Alloc Ratio |
|--------------------------------------------------- |-------- |------ |-------- |-------------:|--------------:|-------------:|-------:|--------:|----------:|------------:|
| **&#39;ImmArray[i] x10k (hot-weighted)&#39;**                  | **1000000** | **10000** | **Uniform** |     **265.1 μs** |     **146.87 μs** |      **8.05 μs** |   **1.00** |    **0.04** |         **-** |          **NA** |
| &#39;ChunkedList[i] x10k (hot-weighted)&#39;               | 1000000 | 10000 | Uniform |     334.3 μs |     139.48 μs |      7.65 μs |   1.26 |    0.04 |         - |          NA |
| &#39;ImmList[i] x10k (hot-weighted)&#39;                   | 1000000 | 10000 | Uniform |   1,239.3 μs |      40.45 μs |      2.22 μs |   4.68 |    0.12 |         - |          NA |
| &#39;SnapshotTable_Rekeyed lookup x10k&#39;                | 1000000 | 10000 | Uniform |   1,267.2 μs |   1,232.07 μs |     67.53 μs |   4.78 |    0.25 |         - |          NA |
| &#39;ImmArray scan all buckets (1M entities)&#39;          | 1000000 | 10000 | Uniform |   9,977.6 μs |   9,391.07 μs |    514.76 μs |  37.67 |    1.94 |         - |          NA |
| &#39;ChunkedList scan all buckets (1M entities)&#39;       | 1000000 | 10000 | Uniform |  10,092.3 μs |   3,397.31 μs |    186.22 μs |  38.10 |    1.16 |         - |          NA |
| &#39;ImmList scan all buckets (1M entities)&#39;           | 1000000 | 10000 | Uniform |  30,214.2 μs |   6,689.16 μs |    366.66 μs | 114.06 |    3.18 |         - |          NA |
| &#39;MultiValueTable bucket[i] x10k (hot-weighted)&#39;    | 1000000 | 10000 | Uniform |     438.4 μs |     232.55 μs |     12.75 μs |   1.65 |    0.06 |         - |          NA |
| &#39;MultiValueTable scan all buckets (1M entities)&#39;   | 1000000 | 10000 | Uniform |  11,619.0 μs |  20,881.03 μs |  1,144.56 μs |  43.86 |    3.91 |         - |          NA |
| &#39;SnapshotTable group scan via index (1M entities)&#39; | 1000000 | 10000 | Uniform | 175,978.1 μs | 345,267.88 μs | 18,925.30 μs | 664.32 |   64.23 |  320000 B |          NA |
|                                                    |         |       |         |              |               |              |        |         |           |             |
| **&#39;ImmArray[i] x10k (hot-weighted)&#39;**                  | **1000000** | **10000** | **Zipf**    |     **235.3 μs** |      **76.10 μs** |      **4.17 μs** |   **1.00** |    **0.02** |         **-** |          **NA** |
| &#39;ChunkedList[i] x10k (hot-weighted)&#39;               | 1000000 | 10000 | Zipf    |     290.9 μs |      80.17 μs |      4.39 μs |   1.24 |    0.02 |         - |          NA |
| &#39;ImmList[i] x10k (hot-weighted)&#39;                   | 1000000 | 10000 | Zipf    |   2,562.2 μs |   1,398.47 μs |     76.65 μs |  10.89 |    0.33 |         - |          NA |
| &#39;SnapshotTable_Rekeyed lookup x10k&#39;                | 1000000 | 10000 | Zipf    |   1,211.0 μs |     602.56 μs |     33.03 μs |   5.15 |    0.14 |         - |          NA |
| &#39;ImmArray scan all buckets (1M entities)&#39;          | 1000000 | 10000 | Zipf    |  10,027.9 μs |   3,879.18 μs |    212.63 μs |  42.62 |    1.02 |         - |          NA |
| &#39;ChunkedList scan all buckets (1M entities)&#39;       | 1000000 | 10000 | Zipf    |  10,031.6 μs |   7,438.69 μs |    407.74 μs |  42.64 |    1.64 |         - |          NA |
| &#39;ImmList scan all buckets (1M entities)&#39;           | 1000000 | 10000 | Zipf    |  31,725.2 μs |  16,266.13 μs |    891.60 μs | 134.84 |    3.87 |         - |          NA |
| &#39;MultiValueTable bucket[i] x10k (hot-weighted)&#39;    | 1000000 | 10000 | Zipf    |     545.2 μs |      51.96 μs |      2.85 μs |   2.32 |    0.04 |         - |          NA |
| &#39;MultiValueTable scan all buckets (1M entities)&#39;   | 1000000 | 10000 | Zipf    |  15,957.5 μs |   6,749.46 μs |    369.96 μs |  67.82 |    1.71 |         - |          NA |
| &#39;SnapshotTable group scan via index (1M entities)&#39; | 1000000 | 10000 | Zipf    | 169,521.5 μs | 109,153.96 μs |  5,983.10 μs | 720.51 |   24.62 |  321584 B |          NA |
