```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.10GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.110
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                            | N       | K     | Skew    | Mean      | Error      | StdDev    | Allocated |
|-------------------------------------------------- |-------- |------ |-------- |----------:|-----------:|----------:|----------:|
| **&#39;ImmArray scan all buckets (1M entities)&#39;**         | **1000000** | **10000** | **Uniform** |  **4.342 ms** |  **1.3538 ms** | **0.0742 ms** |         **-** |
| &#39;ChunkedList scan all buckets (1M entities)&#39;      | 1000000 | 10000 | Uniform |  5.464 ms |  9.7509 ms | 0.5345 ms |         - |
| &#39;ChunkedList scan via Chunks spans (1M entities)&#39; | 1000000 | 10000 | Uniform |  4.485 ms |  1.2248 ms | 0.0671 ms |         - |
| &#39;ImmList scan all buckets (1M entities)&#39;          | 1000000 | 10000 | Uniform | 19.680 ms | 17.7875 ms | 0.9750 ms |         - |
| &#39;MultiValueTable scan all buckets (1M entities)&#39;  | 1000000 | 10000 | Uniform |  5.520 ms |  2.9414 ms | 0.1612 ms |         - |
| **&#39;ImmArray scan all buckets (1M entities)&#39;**         | **1000000** | **10000** | **Zipf**    |  **4.821 ms** |  **3.4956 ms** | **0.1916 ms** |         **-** |
| &#39;ChunkedList scan all buckets (1M entities)&#39;      | 1000000 | 10000 | Zipf    |  5.407 ms |  0.6036 ms | 0.0331 ms |         - |
| &#39;ChunkedList scan via Chunks spans (1M entities)&#39; | 1000000 | 10000 | Zipf    |  5.034 ms |  1.4693 ms | 0.0805 ms |         - |
| &#39;ImmList scan all buckets (1M entities)&#39;          | 1000000 | 10000 | Zipf    | 20.452 ms | 12.1067 ms | 0.6636 ms |         - |
| &#39;MultiValueTable scan all buckets (1M entities)&#39;  | 1000000 | 10000 | Zipf    |  8.468 ms |  3.3030 ms | 0.1810 ms |         - |
