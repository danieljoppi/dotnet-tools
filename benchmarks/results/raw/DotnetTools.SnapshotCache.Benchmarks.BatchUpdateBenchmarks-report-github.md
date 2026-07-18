```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.10GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.110
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                  | N       | BatchSize | Mean      | Error     | StdDev    | Ratio | RatioSD | Gen0     | Gen1     | Gen2     | Allocated | Alloc Ratio |
|---------------------------------------- |-------- |---------- |----------:|----------:|----------:|------:|--------:|---------:|---------:|---------:|----------:|------------:|
| SnapshotTable.ApplyChanges              | 1000000 | 5000      |  4.874 ms |  2.440 ms | 0.1337 ms |  1.00 |    0.03 | 187.5000 | 179.6875 |        - |  15.33 MB |        1.00 |
| ImmutableDictionary.SetItems            | 1000000 | 5000      |  7.597 ms |  4.027 ms | 0.2207 ms |  1.56 |    0.05 |  23.4375 |   7.8125 |        - |   2.07 MB |        0.14 |
| &#39;ImmutableList.SetItem xB (keyed rows)&#39; | 1000000 | 5000      |  5.127 ms |  1.922 ms | 0.1053 ms |  1.05 |    0.03 |  15.6250 |   7.8125 |        - |   1.82 MB |        0.12 |
| &#39;ImmutableArray.SetItem xB via builder&#39; | 1000000 | 5000      |  8.801 ms |  3.216 ms | 0.1763 ms |  1.81 |    0.05 |  31.2500 |  31.2500 |  31.2500 |  30.52 MB |        1.99 |
| &#39;Dictionary rebuild + swap&#39;             | 1000000 | 5000      | 14.759 ms |  3.470 ms | 0.1902 ms |  3.03 |    0.08 |  31.2500 |  31.2500 |  31.2500 |  31.05 MB |        2.03 |
| &#39;FrozenDictionary rebuild&#39;              | 1000000 | 5000      | 55.270 ms | 26.389 ms | 1.4464 ms | 11.35 |    0.37 | 125.0000 | 125.0000 | 125.0000 |  98.45 MB |        6.42 |
