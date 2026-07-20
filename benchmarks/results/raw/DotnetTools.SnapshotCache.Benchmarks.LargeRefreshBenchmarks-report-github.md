```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.10GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.110
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method                 | N       | K    | Mean      | Error      | StdDev    | Median    | Ratio | RatioSD | Allocated | Alloc Ratio |
|----------------------- |-------- |----- |----------:|-----------:|----------:|----------:|------:|--------:|----------:|------------:|
| ImmArray_AddRange      | 1000000 | 2000 |  3.318 ms | 32.6731 ms | 1.7909 ms |  2.348 ms |  1.51 |    0.71 |   5.19 MB |        0.78 |
| List_Then_PublishArray | 1000000 | 2000 |  1.754 ms |  1.8674 ms | 0.1024 ms |  1.696 ms |  0.80 |    0.04 |   5.19 MB |        0.78 |
| ChunkedList_Builder    | 1000000 | 2000 |  2.193 ms |  0.9966 ms | 0.0546 ms |  2.207 ms |  1.00 |    0.03 |   6.62 MB |        1.00 |
| SnapshotTable_Rekeyed  | 1000000 | 2000 | 18.548 ms |  4.5901 ms | 0.2516 ms | 18.418 ms |  8.46 |    0.21 |  42.85 MB |        6.48 |
