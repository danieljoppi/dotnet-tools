```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.80GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.110
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                  | N       | BatchSize | Mean      | Error      | StdDev    | Ratio | RatioSD | Gen0     | Gen1     | Gen2     | Allocated | Alloc Ratio |
|---------------------------------------- |-------- |---------- |----------:|-----------:|----------:|------:|--------:|---------:|---------:|---------:|----------:|------------:|
| SnapshotTable.ApplyChanges              | 1000000 | 5000      | 12.446 ms |  9.2672 ms | 0.5080 ms |  1.00 |    0.05 | 906.2500 | 875.0000 |        - |  15.33 MB |        1.00 |
| ImmutableDictionary.SetItems            | 1000000 | 5000      | 14.592 ms | 41.7708 ms | 2.2896 ms |  1.17 |    0.16 | 125.0000 | 109.3750 |        - |   2.07 MB |        0.14 |
| &#39;ImmutableList.SetItem xB (keyed rows)&#39; | 1000000 | 5000      |  5.964 ms |  6.2579 ms | 0.3430 ms |  0.48 |    0.03 | 109.3750 |  46.8750 |        - |   1.82 MB |        0.12 |
| &#39;ImmutableArray.SetItem xB via builder&#39; | 1000000 | 5000      | 11.900 ms |  7.4071 ms | 0.4060 ms |  0.96 |    0.04 |  15.6250 |  15.6250 |  15.6250 |  30.52 MB |        1.99 |
| &#39;Dictionary rebuild + swap&#39;             | 1000000 | 5000      | 11.594 ms |  0.7852 ms | 0.0430 ms |  0.93 |    0.03 |  31.2500 |  31.2500 |  31.2500 |  31.05 MB |        2.03 |
| &#39;FrozenDictionary rebuild&#39;              | 1000000 | 5000      | 59.916 ms |  7.8817 ms | 0.4320 ms |  4.82 |    0.17 | 125.0000 | 125.0000 | 125.0000 |  98.45 MB |        6.42 |
