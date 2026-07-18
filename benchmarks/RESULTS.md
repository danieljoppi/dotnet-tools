# Benchmark results

Measured with BenchmarkDotNet 0.15.8 (`--job short --memory`, 3 warmup + 3 measurement
iterations) on this environment:

```
Linux Ubuntu 24.04.4 LTS, Intel Xeon 2.10GHz, 4 physical cores
.NET SDK 10.0.110, .NET 10.0.10, X64 RyuJIT x86-64-v4, Concurrent Workstation GC
```

ShortRun numbers are indicative; re-run without `--job short` for publication-grade statistics.
**Environment variance caveat**: this is a shared cloud VM whose absolute timings drift up to ~2x
between sessions (the identical `Dictionary rebuild` code measured 11.4-14.8 ms across runs).
Relative ordering and the allocation/GC columns are stable across every run and are the numbers
to trust. Raw reports (CSV + markdown) are in [`results/raw/`](results/raw/), the plotting script
is [`plot_results.py`](plot_results.py).

## Scenario counts (what each benchmark actually does)

The suite models the real workload — a large table in memory, refreshed in batches, read hot:

| Phase | Counts | Benchmark class |
|---|---|---|
| Initial full load | 1,000,000 rows inserted once | `InitialLoadBenchmarks` |
| Periodic refresh (the every-30-seconds batch) | 5,000 upserts, keys uniformly random over the 1M key space | `BatchUpdateBenchmarks` |
| Hot read path | 10,000 random point lookups per invocation | `ReadBenchmarks` |

Row type for the batch scenario: `record Row(long Id, string Name, decimal Balance, DateTime UpdatedAt)`
keyed by `long`. Read/load scenarios use `long → long` to isolate structure cost from row size.

> **Core-version note**: sections 1–3 were measured on the v0.1 core (`Dictionary`-based index
> shards, 870 ms apply at 100M). Sections 2 and 4–7 reflect the current v0.2 core (compact
> open-addressing shards, parallel load); v0.2 is equal or better on every metric.

## 1. The 30-second refresh batch (the reason this library exists)

![Batch update time](results/charts/batch-update-time.png)

![Batch update allocation](results/charts/batch-update-alloc.png)

| Method | Mean | Allocated | Gen2/LOH collections |
|---|---:|---:|---:|
| ImmutableList.SetItem ×B (keyed rows)¹ | 5.96 ms | 1.82 MB | 0 |
| Dictionary rebuild + swap | 11.59 ms | 31.05 MB | yes |
| ImmutableArray.SetItem ×B via builder | 11.90 ms | 30.52 MB | yes — full array copy on LOH |
| **SnapshotTable.ApplyChanges** | **12.45 ms** | 15.33 MB | **0** |
| ImmutableDictionary.SetItems | 14.59 ms | 2.07 MB | 0 |
| FrozenDictionary rebuild | 59.92 ms | 98.45 MB | yes |

¹ Flattered: charged only for row replacement, assuming a free pre-existing key→index map —
maintaining that index is exactly what `SnapshotTable` does and `ImmutableList` doesn't.

**How to read this**: at 1M rows a uniformly random 5k batch touches nearly every 64 KB chunk, so
`SnapshotTable`'s apply time lands in the same band as an O(N) rebuild — the batch is simply not
sparse relative to this table. Its differentiators at this scale are the columns, not the row:
**zero Gen2/LOH activity** (every full-rebuild option hits the LOH each cycle) and dictionary-class
reads (the two structures that allocate less per batch, `ImmutableList`/`ImmutableDictionary`,
read ~9-11× slower and carry 3-5× the steady-state memory). The apply-time advantage grows with
table size as the batch becomes sparse: at 100M rows it is ~5× faster than a rebuild could ever be
(section 4) — and clustered (non-random) batches copy proportionally less at any scale.

## 2. Point lookups (hot path)

![Read time](results/charts/read-time.png)

| Method | Mean (10k lookups) | Per lookup | Allocated |
|---|---:|---:|---:|
| ImmutableArray[i]² | 11.0 μs | ~1 ns | 0 |
| Dictionary | 157 μs | ~16 ns | 0 |
| FrozenDictionary | 261 μs | ~26 ns | 0 |
| **TableSnapshot.TryGetValue** | **586 μs** | **~59 ns** | **0** |
| **SnapshotTable.TryGetValue** | **632 μs** | **~63 ns** | **0** |
| ImmutableList[i] | 7,059 μs | ~706 ns | 0 |
| ImmutableDictionary | 6,506 μs | ~651 ns | 0 |

² Positional index only — not a keyed lookup; included as the raw-array speed floor.

The honest trade-off: a plain `Dictionary` reads ~4× faster per lookup (~16 ns vs ~63 ns; the
extra cost is the shard-directory hop plus the chunked-row hop). That premium buys lock-free
consistent snapshots and LOH-free refreshes. Against the structures it replaces
(`ImmutableList`/`ImmutableDictionary`) it reads **~10× faster**. (Measured on the v0.2
open-addressing shard core.)

## 3. Initial full load (one-time cost)

![Initial load time](results/charts/initial-load-time.png)

| Method | Mean | Allocated |
|---|---:|---:|
| Dictionary | 15.4 ms | 31.05 MB |
| FrozenDictionary.ToFrozenDictionary | 82.1 ms | 139.35 MB |
| ImmutableList.CreateRange | 109.2 ms | 53.41 MB |
| **SnapshotTable.Reset** | **111.7 ms** | **54.52 MB** |
| ImmutableDictionary.CreateRange | 346.1 ms | 61.04 MB |

Load is `SnapshotTable`'s weakest phase (~7× a raw `Dictionary` fill), but it happens once at
startup and shards are pre-sized from `CapacityHint`, so allocation is now only ~1.8× the raw
data. At 100M rows the measured load rate is 1.4M rows/s (section 4).

## 4. The target workload: 100,000,000 rows, 20,000 changes every 30 seconds

Run end-to-end with the console harness (`--largescale`), workstation GC, 4-core Linux VM, while
two reader threads continuously issued point lookups. Batch mix: 80% updates of random existing
rows, 20% inserts of new rows. Full output: [`results/raw/largescale-100m.txt`](results/raw/largescale-100m.txt).

| Metric | Workstation GC | Server GC | Budget / context |
|---|---:|---:|---|
| Initial load (`ResetParallel`, one-time) | 11.8 s (8.5M rows/s) | 9.6 s (10.4M rows/s) | was 72.5 s in v0.1 |
| Heap for 100M rows + index | **3.38 GiB** | **3.38 GiB** | 2.26× raw data |
| **LOH size after load** | **0.0 MiB** | **0.0 MiB** | entirely small-object |
| Apply 20k-change batch (median) | 452 ms | **80 ms** | 30,000 ms budget |
| Allocation per batch | ~83 MiB, Gen0/Gen1 only | ~83 MiB | vs multi-GiB LOH for rebuilds |
| **LOH growth over 10 cycles** | **0.0 MiB** | **0.0 MiB** | the headline guarantee |
| Concurrent reads during refreshes | 2.45 M lookups/s | 2.48 M lookups/s | readers never block |

Full outputs: [`largescale-100m.txt`](results/raw/largescale-100m.txt),
[`largescale-100m-servergc.txt`](results/raw/largescale-100m-servergc.txt). Production services
should run Server GC, where the batch apply drops to ~80 ms — 0.3% of the budget.

At this scale no BCL alternative completes the workload cleanly: `ImmutableArray` would copy a
1.6 GB LOH array per batch, a `Dictionary`/`FrozenDictionary` rebuild would allocate ~5 GiB of
LOH-resident structures every 30 seconds, and `ImmutableList`/`ImmutableDictionary` would need
roughly 3-5x the memory and ~11x slower reads.

The structure itself is LOH-free by construction at any row count up to `int.MaxValue`: row chunks
(~4 KB), spine blocks (8 KB), index shards (a few KB each, ~500k of them), directory blocks
(8 KB), and all copy-on-write bookkeeping (bitsets) sit far below the 85,000-byte threshold.

## 5. Overall memory analysis: steady-state footprint and where it lives

Speed and per-batch allocation only tell half the story — this measures what each structure
*costs to keep resident*, and how much of that sits on the Large Object Heap. Method: build the
structure holding N `long → long` rows in a fresh process, force a full compacting GC (including
explicit LOH compaction), and report live heap deltas. Raw data: 16 B/row. CSV:
[`results/raw/memory-footprint.csv`](results/raw/memory-footprint.csv), harness: `--memory-profile`.

![Memory footprint](results/charts/memory-footprint.png)

| Structure | 1M rows | 10M rows | 100M rows | On LOH | Keyed reads (§2) | 30 s refresh (§1/§4) |
|---|---:|---:|---:|---|---:|---|
| ImmutableArray | 16 MiB (1.0×) | 153 MiB (1.0×) | — | **100%** | positional only | O(N) LOH copy |
| Dictionary | 37 MiB (2.4×) | 320 MiB (2.1×) | 5.01 GiB (3.4×)¹ | **~100%** | ~14 ns | O(N) LOH rebuild |
| FrozenDictionary | 55 MiB (3.6×) | 459 MiB (3.0×) | 2.61 GiB (1.75×) | **~100%** | ~29 ns | O(N) LOH rebuild, slowest |
| **SnapshotTable** | **36 MiB (2.26×)** | **365 MiB (2.28×)** | **3.38 GiB (2.26×)** | **0%** | ~70 ns | **O(batch), no LOH** |
| ImmutableList | 53 MiB (3.5×) | 534 MiB (3.5×) | — | 0% | no key index² | O(touched nodes) |
| ConcurrentDictionary | 56 MiB (3.7×) | 549 MiB (3.6×) | — | partial (buckets) | ~fast | per-key only, no snapshots |
| ImmutableDictionary | 61 MiB (4.0×) | 610 MiB (4.0×) | — | 0% | ~585 ns | O(B log N) |

¹ `Dictionary` built from an enumerable (no count) grows by prime-doubling and lands over-sized —
at 100M rows its capacity overshoot alone wastes ~1.5 GiB. Pre-sizing fixes that but does not get
it off the LOH.
² `ImmutableList` needs a separate key→index map for keyed access; add one `Dictionary` row above
to its cost for a fair keyed-workload comparison (which also puts it on the LOH).

**What the overall picture says:**

- **Every structure with dictionary-class read speed except `SnapshotTable` keeps its bulk on the
  LOH** — `Dictionary`, `FrozenDictionary`, `ConcurrentDictionary` (buckets), `ImmutableArray`.
  Their steady-state *size* is fine; the problem is that their refresh path reallocates those
  LOH structures every cycle, which is what fragments the LOH and drives Gen2/full GCs.
- **The LOH-free BCL options pay for it in reads and per-element overhead**:
  `ImmutableDictionary`/`ImmutableList` are 3.5-4× raw with ~9× slower lookups.
- **`FrozenDictionary` is the most compact keyed structure at scale** (1.75× raw at 100M — its
  arrays are exactly sized). If a table refreshes rarely and a 100%-LOH resident footprint is
  acceptable, it is the best read-mostly choice; its disqualifier here is the 30-second full
  rebuild (~2.6-5 GiB of LOH churn per cycle at 100M).
- **`SnapshotTable` sits at 3.25-3.6× raw — the same band as the BCL keyed structures — with 0%
  on the LOH at every scale**, and it is the only one whose refresh cost doesn't scale with the
  table. Most of its footprint is the sharded key→row index (~35 B/row of `Dictionary` entries);
  the row store itself is only ~1.05× raw. A denser custom shard (open addressing, int-only) could
  cut total to ~2× raw if memory ever becomes the binding constraint — tracked as a possible
  follow-up, not needed to meet the current budget (4.84 GiB for 100M rows fits comfortably).

## 6. Soak: 400 refresh cycles under load

`--soak`: 20M rows, 400 cycles of 20k changes (70% update / 15% insert / 15% remove), two reader
threads, and a rotating window of 5 held old snapshots simulating in-flight reports. Full output:
[`soak-20m-400.txt`](results/raw/soak-20m-400.txt).

| Metric | Measured |
|---|---:|
| Managed heap after 400 cycles | **flat: 696 → 696 MiB** (+1 MiB) |
| **Table LOH growth** | **0.0 MiB** |
| Apply p50 / max | 112 ms / 364 ms |
| Reads sustained throughout | 1.63 M lookups/s |
| Verdict | **PASS** — no fragmentation creep, no leak through held snapshots |

(The few MiB of transient LOH visible at mid-run checkpoints belong to the harness's own
unbounded removed-keys `Queue`, not the table; it collects to zero at the end.)

## 7. Production-shaped rows (strings + decimal payload)

`RealisticRowBenchmarks`: 1M rows of `record CustomerRow(long Id, string Name, string Email,
string Region, decimal Balance, int Status, DateTime CreatedAt, DateTime UpdatedAt)`, 5k-change
batches in both key distributions. Raw: [`results/raw/`](results/raw/).

| Method | Mean | Allocated | Gen2/LOH |
|---|---:|---:|---:|
| **SnapshotTable, clustered 5k batch** | **0.49 ms** | **137 KB** | 0 |
| **SnapshotTable, random 5k batch** | **7.9 ms** | 15.7 MB | 0 |
| Dictionary rebuild + swap | 28.5 ms | 31.8 MB | yes |
| FrozenDictionary rebuild | 88.1 ms | 100.8 MB | yes |

With reference-type rows the rebuild approaches slow down (every entry is a pointer the GC must
trace through LOH-resident arrays) while `SnapshotTable` pulls further ahead — and **clustered
batches, the common shape of real change feeds, cost 16× less than random ones** (137 KB per
refresh) because consecutive keys share chunks.

## Reproducing

```bash
# full statistics (slow):
dotnet run -c Release --project benchmarks/DotnetTools.SnapshotCache.Benchmarks -- --filter '*'

# what produced these numbers:
dotnet run -c Release --project benchmarks/DotnetTools.SnapshotCache.Benchmarks -- --filter '*' --job short --memory

# regenerate the charts from the artifacts:
python3 benchmarks/plot_results.py

# steady-state memory profile (one structure per process):
dotnet benchmarks/DotnetTools.SnapshotCache.Benchmarks/bin/Release/net10.0/DotnetTools.SnapshotCache.Benchmarks.dll --memory-profile SnapshotTable 10000000
```
