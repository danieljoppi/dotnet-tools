```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.80GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.110
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method                 | N       | K    | Mean      | Error      | StdDev    | Median    | Ratio | RatioSD | Gen0      | Gen1      | Allocated | Alloc Ratio |
|----------------------- |-------- |----- |----------:|-----------:|----------:|----------:|------:|--------:|----------:|----------:|----------:|------------:|
| ImmArray_AddRange      | 1000000 | 2000 |  3.395 ms | 30.5466 ms | 1.6744 ms |  2.458 ms |  0.98 |    0.42 |         - |         - |   5.19 MB |        0.47 |
| List_Then_PublishArray | 1000000 | 2000 |  1.896 ms |  0.6594 ms | 0.0361 ms |  1.887 ms |  0.55 |    0.01 |         - |         - |   5.19 MB |        0.47 |
| ChunkedList_Builder    | 1000000 | 2000 |  3.470 ms |  0.9955 ms | 0.0546 ms |  3.492 ms |  1.00 |    0.02 |         - |         - |  11.05 MB |        1.00 |
| SnapshotTable_Rekeyed  | 1000000 | 2000 | 27.270 ms | 22.6041 ms | 1.2390 ms | 27.230 ms |  7.86 |    0.33 | 2000.0000 | 1000.0000 |  43.14 MB |        3.91 |
