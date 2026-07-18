# Benchmark results

Measured with BenchmarkDotNet 0.15.8 (`--job short --memory`, 3 warmup + 3 measurement
iterations) on this environment:

```
Linux Ubuntu 24.04.4 LTS, Intel Xeon 2.10GHz, 4 physical cores
.NET SDK 10.0.110, .NET 10.0.10, X64 RyuJIT x86-64-v4, Concurrent Workstation GC
```

ShortRun numbers are indicative; re-run without `--job short` for publication-grade statistics.
Raw reports (CSV + markdown) are in [`results/raw/`](results/raw/), the plotting script is
[`plot_results.py`](plot_results.py).

## Scenario counts (what each benchmark actually does)

The suite models the real workload — a large table in memory, refreshed in batches, read hot:

| Phase | Counts | Benchmark class |
|---|---|---|
| Initial full load | 1,000,000 rows inserted once | `InitialLoadBenchmarks` |
| Periodic refresh (the every-30-seconds batch) | 5,000 upserts, keys uniformly random over the 1M key space | `BatchUpdateBenchmarks` |
| Hot read path | 10,000 random point lookups per invocation | `ReadBenchmarks` |

Row type for the batch scenario: `record Row(long Id, string Name, decimal Balance, DateTime UpdatedAt)`
keyed by `long`. Read/load scenarios use `long → long` to isolate structure cost from row size.

## 1. The 30-second refresh batch (the reason this library exists)

![Batch update time](results/charts/batch-update-time.png)

![Batch update allocation](results/charts/batch-update-alloc.png)

| Method | Mean | Allocated | Gen2/LOH collections |
|---|---:|---:|---:|
| **SnapshotTable.ApplyChanges** | **4.87 ms** | 15.33 MB | **0** |
| ImmutableList.SetItem ×B (keyed rows)¹ | 5.13 ms | 1.82 MB | 0 |
| ImmutableDictionary.SetItems | 7.60 ms | 2.07 MB | 0 |
| ImmutableArray.SetItem ×B via builder | 8.80 ms | 30.52 MB | yes — full array copy on LOH |
| Dictionary rebuild + swap | 14.76 ms | 31.05 MB | yes |
| FrozenDictionary rebuild | 55.27 ms | 98.45 MB | yes |

¹ Flattered: charged only for row replacement, assuming a free pre-existing key→index map —
maintaining that index is exactly what `SnapshotTable` does and `ImmutableList` doesn't.

**Read together with the read chart below**: the two BCL structures that allocate less per batch
(`ImmutableList`, `ImmutableDictionary`) are the two that read **11× slower** on the hot path, and
their steady-state memory footprint is a multiple of the raw data (tree/trie nodes per element).
`SnapshotTable` is the only keyed option that is simultaneously the fastest to refresh, **zero
Gen2/LOH activity**, and dictionary-class on reads. Its 15 MB per batch is chunk copy-on-write
for a *uniformly random* batch (5k keys touch ~all 245 chunks ≈ 16 MB of rows); clustered batches
copy proportionally less, and all of it is small-object-heap, collected in Gen0/Gen1.

## 2. Point lookups (hot path)

![Read time](results/charts/read-time.png)

| Method | Mean (10k lookups) | Per lookup | Allocated |
|---|---:|---:|---:|
| ImmutableArray[i]² | 11.2 μs | ~1 ns | 0 |
| Dictionary | 100 μs | ~10 ns | 0 |
| FrozenDictionary | 197 μs | ~20 ns | 0 |
| **TableSnapshot.TryGetValue** | **439 μs** | **~44 ns** | **0** |
| **SnapshotTable.TryGetValue** | **462 μs** | **~46 ns** | **0** |
| ImmutableList[i] | 5,022 μs | ~502 ns | 0 |
| ImmutableDictionary | 5,240 μs | ~524 ns | 0 |

² Positional index only — not a keyed lookup; included as the raw-array speed floor.

The honest trade-off: a plain `Dictionary` reads ~4.6× faster per lookup (~10 ns vs ~46 ns).
`SnapshotTable`'s extra ~35 ns buys lock-free consistent snapshots and O(batch) LOH-free
refreshes. Against the structures it replaces (`ImmutableList`/`ImmutableDictionary`) it reads
**~11× faster**.

## 3. Initial full load (one-time cost)

![Initial load time](results/charts/initial-load-time.png)

| Method | Mean | Allocated |
|---|---:|---:|
| Dictionary | 13.2 ms | 31.05 MB |
| ImmutableList.CreateRange | 44.6 ms | 53.41 MB |
| **SnapshotTable.Reset** | **65.7 ms** | **120.63 MB** |
| FrozenDictionary.ToFrozenDictionary | 69.0 ms | 139.35 MB |
| ImmutableDictionary.CreateRange | 241.7 ms | 241.72 MB |

Load is `SnapshotTable`'s worst phase (shard dictionaries grow by doubling), but it happens once
at startup; the transient allocation is collected immediately after. If startup is critical, a
future `Reset` overload taking a count hint can pre-size shards.

## Reproducing

```bash
# full statistics (slow):
dotnet run -c Release --project benchmarks/DotnetTools.SnapshotCache.Benchmarks -- --filter '*'

# what produced these numbers:
dotnet run -c Release --project benchmarks/DotnetTools.SnapshotCache.Benchmarks -- --filter '*' --job short --memory

# regenerate the charts from the artifacts:
python3 benchmarks/plot_results.py
```
