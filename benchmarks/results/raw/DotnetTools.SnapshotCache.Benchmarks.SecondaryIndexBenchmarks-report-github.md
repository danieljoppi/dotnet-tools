```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.10GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.110
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                     | InvocationCount | UnrollFactor | N       | Mean      | Error     | StdDev    | Gen0     | Gen1     | Allocated |
|------------------------------------------- |---------------- |------------- |-------- |----------:|----------:|----------:|---------:|---------:|----------:|
| &#39;Reset with the index registered up front&#39; | Default         | 16           | 1000000 | 133.86 ms | 310.61 ms | 17.026 ms | 750.0000 | 250.0000 |  74.52 MB |
| &#39;CreateIndex backfill on a loaded table&#39;   | 1               | 1            | 1000000 |  34.32 ms |  21.05 ms |  1.154 ms |        - |        - |  39.98 MB |
