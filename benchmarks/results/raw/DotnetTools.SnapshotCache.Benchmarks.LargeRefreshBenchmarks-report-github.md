```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.80GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.110
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method                  | N       | K    | Mean      | Error      | StdDev    | Ratio | RatioSD | Gen0      | Gen1      | Allocated | Alloc Ratio |
|------------------------ |-------- |----- |----------:|-----------:|----------:|------:|--------:|----------:|----------:|----------:|------------:|
| ImmArray_AddRange       | 1000000 | 2000 |  3.343 ms |  26.967 ms | 1.4781 ms |  1.59 |    0.61 |         - |         - |   5.19 MB |        0.78 |
| List_Then_PublishArray  | 1000000 | 2000 |  1.925 ms |   1.873 ms | 0.1027 ms |  0.91 |    0.05 |         - |         - |   5.19 MB |        0.78 |
| ImmList_Builder         | 1000000 | 2000 |  6.053 ms |   3.463 ms | 0.1898 ms |  2.88 |    0.13 |         - |         - |   1.37 MB |        0.21 |
| ChunkedList_Builder     | 1000000 | 2000 |  2.108 ms |   1.643 ms | 0.0900 ms |  1.00 |    0.05 |         - |         - |   6.62 MB |        1.00 |
| SnapshotTable_Rekeyed   | 1000000 | 2000 | 34.467 ms | 138.857 ms | 7.6112 ms | 16.37 |    3.19 | 2000.0000 | 1000.0000 |  43.18 MB |        6.53 |
| MultiValueSnapshotTable | 1000000 | 2000 |  1.431 ms |   3.100 ms | 0.1699 ms |  0.68 |    0.07 |         - |         - |   3.27 MB |        0.49 |
