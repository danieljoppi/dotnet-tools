```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.10GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.110
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method                                 | N      | Mean     | Error     | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|--------------------------------------- |------- |---------:|----------:|----------:|------:|--------:|----------:|------------:|
| &#39;Inline, materialized batch&#39;           | 100000 | 30.37 ms |  76.76 ms |  4.208 ms |  1.01 |    0.17 |  11.16 MB |        1.00 |
| &#39;Array-wrapped, materialized batch&#39;    | 100000 | 37.33 ms | 200.70 ms | 11.001 ms |  1.24 |    0.35 |  14.21 MB |        1.27 |
| &#39;Inline, lazy stream (no batch array)&#39; | 100000 | 30.29 ms |  67.33 ms |  3.691 ms |  1.01 |    0.16 |   7.34 MB |        0.66 |
