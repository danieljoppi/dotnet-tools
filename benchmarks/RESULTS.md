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
| Batch-size sweep with removes (§11) | 1k / 10k / 50k upserts, 0% / 10% removes, mid-width row | `UniqueKeyBatchBenchmarks` |
| Shared-key one-to-many buckets (§9) | 1M entities over 10k / 100k shared keys, uniform + Zipf | `SharedKeyBucketBenchmarks` |
| Bucket read path (§9) | 10k hot-weighted indexed reads + full 1M-entity scans | `BucketReadBenchmarks` |
| Single large refresh over skewed buckets (§10) | ~12k entity ops across 800 of 2,000 keys, 15 LOH-sized buckets | `LargeRefreshBenchmarks` |

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

Re-validated on the current core (compact single-chunk representation, hybrid buckets, comparer
devirtualization). The invariants — heap size and **0.0 MiB LOH growth** — are host-independent
and reproduce exactly; the absolute *timings* below are from a session whose box ran ~3× slower on
load than the original run (2.8M vs 8.5M rows/s), a vivid instance of the ~2× VM-drift caveat, so
treat the memory columns as the durable result and the times as an upper-ish bound. Fresh outputs:
[`largescale-100m-rerun.txt`](results/raw/largescale-100m-rerun.txt),
[`largescale-100m-rerun-servergc.txt`](results/raw/largescale-100m-rerun-servergc.txt).

| Metric | Workstation GC | Server GC | Budget / context |
|---|---:|---:|---|
| Heap for 100M rows + index | **3.39 GiB** | **3.39 GiB** | 2.26× raw data |
| **LOH size after load** | **0.0 MiB** | **0.0 MiB** | entirely small-object |
| Apply 20k-change batch (median) | 720 ms | **122 ms** | 30,000 ms budget (0.4%) |
| Allocation per batch | ~83.5 MiB, Gen0/Gen1 only | ~83.5 MiB | vs multi-GiB LOH for rebuilds |
| **LOH growth over 10 cycles** | **0.0 MiB** | **0.0 MiB** | the headline guarantee |
| GC collections over 10 cycles | Gen0=51 Gen1=50 **Gen2=1** | Gen0=3 Gen1=0 **Gen2=0** | zero forced full GCs |
| Concurrent reads during refreshes | 1.53 M lookups/s | 1.95 M lookups/s | readers never block |

Production services should run Server GC, where the batch apply is **122 ms median (0.4% of the
budget)** and the ten cycles triggered **zero Gen2 collections**. On a faster host the same run has
measured ~80 ms median / 8.5M rows-per-second loads (prior session,
[`largescale-100m.txt`](results/raw/largescale-100m.txt)); the guarantee that does not move with
the host is the LOH column.

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
| ImmutableArray | 16 MiB (1.0×) | 153 MiB (1.0×) | 1.49 GiB (1.0×) | **100%** | positional only | O(N) LOH copy |
| Dictionary | 37 MiB (2.4×) | 320 MiB (2.1×) | 5.01 GiB (3.4×)² | **~100%** | ~14 ns | O(N) LOH rebuild |
| FrozenDictionary | 55 MiB (3.6×) | 459 MiB (3.0×) | 2.61 GiB (1.75×) | **~100%** | ~29 ns | O(N) LOH rebuild, slowest |
| **SnapshotTable** | **36 MiB (2.26×)** | **365 MiB (2.28×)** | **3.38 GiB (2.26×)** | **0%** | ~70 ns | **O(batch), no LOH** |
| ImmutableList | 53 MiB (3.5×) | 534 MiB (3.5×) | 5.22 GiB (3.5×) | 0% | no key index³ | O(touched nodes) |
| ConcurrentDictionary | 56 MiB (3.7×) | 549 MiB (3.6×) | 5.90 GiB (4.0×) | partial (buckets) | ~fast | per-key only, no snapshots |
| ImmutableDictionary | 61 MiB (4.0×) | 610 MiB (4.0×) | 5.96 GiB (4.0×) | 0% | ~585 ns | O(B log N) |

¹ All seven structures were also measured at the 100M tier (the earlier report only ran the
top candidates there). Per-row cost is scale-invariant for every structure — `ImmutableArray`
stays exactly 1.0× raw at 100M and works as a *read-only* container at any size; what rules it
out for this workload is unchanged at every scale: 100% LOH residency from ~85 KB upward, a full
O(N) LOH copy per refresh (1.49 GiB per batch at 100M), and no keyed lookup.
² `Dictionary` built from an enumerable (no count) grows by prime-doubling and lands over-sized —
at 100M rows its capacity overshoot alone wastes ~1.5 GiB. Pre-sizing fixes that but does not get
it off the LOH.
³ `ImmutableList` needs a separate key→index map for keyed access; add one `Dictionary` row above
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

### Why steady-state size misleads: the churn analysis

`ImmutableArray`'s 1.0× footprint makes it look like the winner above — until the 30-second
cadence enters the analysis. Resident size is what a structure holds *between* refreshes; a cache
is judged by what happens *at* each refresh:

| Per-cycle behavior (100M rows, every 30 s) | ImmutableArray | SnapshotTable |
|---|---:|---:|
| Newly allocated per refresh | **1.49 GiB, all LOH** | ~83 MiB, all small-object |
| Peak during refresh (old + new alive) | ~3 GiB | ~3.5 GiB (only +83 MiB is new) |
| Garbage generated per hour (120 cycles) | **~180 GiB through the LOH** | ~10 GiB through Gen0/Gen1 |
| Reclaimed by | full/Gen2 collections (pause the app) | cheap ephemeral GCs |

~180 GiB/hour of LOH turnover is the disease this library exists to cure: the LOH is not
compacted by default, so it fragments, and reclaiming it takes the full collections that pause
every request. And the 1.49 GiB is not the real total anyway — `ImmutableArray` has no keyed
lookup, so a real cache pairs it with a `Dictionary<TKey,int>` index (multi-GiB, ~100% LOH at
100M, see the table above) that must also be maintained. The honest totals for a keyed workload:

- `ImmutableArray` + required index: **~4.5–6.5 GiB, nearly all LOH, churned every 30 s**
- `SnapshotTable` (index included): **3.38 GiB, 0% LOH, ~83 MiB touched per refresh**

**Rule of thumb**: `ImmutableArray` is the right tool for load-once, read-only, positional data
at any size — its compactness there is unbeatable. For a keyed table on a refresh cadence it
loses on every metric the cadence touches. Section 8 measures how far tuning can push it.

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

## 8. Investigation: can the ImmutableArray approach be improved?

Measured with `--immutablearray-study` (10M rows, 20k-change batch, 15 cycles per variant, p50;
raw output: [`immutablearray-study.txt`](results/raw/immutablearray-study.txt)):

| Variant | Apply p50 | Alloc/cycle | Gen2 in 15 cycles | Caveat |
|---|---:|---:|---:|---|
| Naive `ToBuilder`/`ToImmutable` | 70 ms | 305 MiB (LOH) | 9 | two full copies, fresh LOH array every cycle |
| Builder + `MoveToImmutable` | 36 ms | 153 MiB (LOH) | 5 | the *safe* single-copy publish — see finding 2 |
| `GC.AllocateUninitializedArray` + `AsImmutableArray` | 18 ms | 153 MiB (LOH) | 5 | one copy, skips zeroing — still one LOH alloc/cycle |
| Extract (`AsArray`) + mutate in place | ~0 ms | 0 | 0 | **no atomicity at all** — see finding 4 |
| **Pooled double-buffer + `AsImmutableArray`** | **18 ms** | **0** | **0** | see finding 3 |
| SnapshotTable.ApplyChanges (reference) | 64 ms | 62 MiB (SOH) | 3 | — |

**Findings, honestly stated:**

1. **Yes, it can be improved — dramatically.** `ImmutableCollectionsMarshal.AsImmutableArray`
   lets two persistent buffers alternate: copy current → standby, apply the batch, wrap, swap.
   Zero steady-state allocation, zero Gen2, and at 10M rows the linear 160 MB memcpy (18 ms) is
   actually *faster* than SnapshotTable's scattered chunk copies (65 ms). Sequential memory
   bandwidth is hard to beat.
2. **Builder + `MoveToImmutable` is the best *fully safe* variant.** `CreateBuilder(N)` +
   `AddRange` + apply + `MoveToImmutable()` publishes the builder's array by ownership transfer —
   no second copy, real immutability, half the naive cost (36 ms vs 70 ms). But it still allocates
   one N-sized LOH array every cycle (153 MiB here, 1.49 GiB at 100M), so the churn disease — the
   reason this analysis exists — is halved, not cured. If a team must stay on ImmutableArray with
   zero safety compromises, this is the ceiling: ~2× faster, same LOH cadence.
3. **What the double-buffer trick costs.** (a) *Immutability becomes a promise, not a guarantee*: the array
   behind a published "immutable" snapshot is silently overwritten one cycle later, so no reader
   may hold a snapshot across a refresh — one slow report holding last cycle's view reads torn
   data. (b) *No keyed lookup*: the paired `Dictionary` index is still multi-GiB LOH, and keeping
   it consistent with in-place buffer swaps reintroduces the coherence problem the snapshot design
   solves. (c) *Growth reallocates*: inserts beyond capacity force new LOH buffers.
   (d) *O(N) copy time per cycle regardless of batch size*: ~18 ms at 10M scales to ~200+ ms at
   100M and grows linearly forever, while SnapshotTable's 80 ms (Server GC) tracks the batch, not
   the table.
4. **Extract-and-mutate is not an ImmutableArray design at all.** `AsArray` + in-place writes is
   the fastest possible (sub-millisecond, zero alloc) because it skips the copy entirely — and
   with it every guarantee: readers observe half-applied batches *during* the write, and the
   "immutable" value everyone holds silently changes. It is a plain mutable array wearing an
   `ImmutableArray` type signature. Legitimate only when readers tolerate incoherent views
   (some telemetry caches do); never for a table where a report must see one consistent version.
5. **Verdict**: for a *positional, fixed-size, updates-only* table whose consumers never hold a
   snapshot across a cycle, a double-buffered flat array is an excellent design — and if that is
   your workload, use it. The moment you need keyed lookup, safe long-lived snapshots, inserts/
   removes, or table sizes where O(N) copies bite, each fix you bolt on walks the design one step
   closer to chunked copy-on-write — which is `SnapshotTable`. That is not a coincidence:
   `ChunkedImmutableList` *is* the improved ImmutableArray.

## 9. Shared key → many values: one-to-many buckets (Workload B, issue #6)

Everything so far keyed rows uniquely. A second production shape keeps **buckets**: one shared
key → a list of entities, with heavy skew (a few keys hold 10k–100k+ entities). An array of
10,625+ references crosses the 85,000-byte LOH threshold, so every full-copy update of a hot
bucket is a Large Object allocation. `SharedKeyBucketBenchmarks` + the `--bucket-loh` console
study measure four bucket representations (plus the packaged `MultiValueSnapshotTable`, #8)
under one harness (`ImmutableList` joined the same matrix later — §14):

![Bucket LOH growth](results/charts/bucket-loh-growth.png)

| Name | Store per shared key | Warm update |
|---|---|---|
| `ImmArray_AddRange` | `ImmutableArray<Entity>` | `AddRange` (appends) / single-copy patch (replaces) |
| `List_Then_PublishArray` | mutable `List<Entity>` master | mutate in place, publish one array per touched key |
| `ChunkedList_Builder` | `ChunkedImmutableList<Entity>` | builder → `ToImmutable`, copies only touched chunks |
| `SnapshotTable_Rekeyed` | one table, `(groupId, entityId) → entity` | one `ApplyChanges` batch |

Population: 1M (and 10M) mid-width entities (`record Entity(long Id, long GroupId, int Kind,
int Version, string Label, decimal Amount, DateTime UpdatedAt)`) over K = 10k / 100k shared keys,
sized uniformly or Zipf (s=1 — at K=10k/N=1M the hottest bucket holds ~102k entities ≈ 800 KB as
a reference array; 9 buckets sit past the LOH bar, 96 at N=10M). Warm batch: touch ~1% of keys
(sampled ∝ bucket size under skew — the hot keys are the ones that keep changing); per touched
key either append 1–50 entities or replace ~1% of the bucket.

### Batch cost (BenchmarkDotNet, ShortRun, medians; state restored between iterations)

![Bucket batch time](results/charts/bucket-batch-time.png)

![Bucket batch allocation](results/charts/bucket-batch-alloc.png)

| K / skew | ImmArray_AddRange | List_Then_PublishArray | ChunkedList_Builder | SnapshotTable_Rekeyed | MultiValueSnapshotTable (#8) |
|---|---:|---:|---:|---:|---:|
| 10k / Uniform (buckets ≈100) | **88 μs / 91 KB** | 109 μs / 91 KB | 250 μs / 535 KB | 3.9 ms / 10.8 MB | 266 μs / 316 KB |
| 10k / Zipf (hot ≈102k) | 1.36 ms / 3.24 MB | 1.22 ms / 3.24 MB | **0.52 ms / 1.42 MB** | 5.3 ms / 12.9 MB | 0.54 ms / 1.39 MB |
| 100k / Uniform (buckets ≈10) | **0.41 ms / 199 KB** | 0.50 ms / 199 KB | 1.62 ms / 4.64 MB | 19.5 ms / 45.8 MB | 2.09 ms / 2.64 MB |
| 100k / Zipf | 1.76 ms / 4.06 MB | **1.78 ms / 4.06 MB** | 2.40 ms / 7.02 MB | 21.7 ms / 48.6 MB | 3.29 ms / 5.58 MB |

(Numbers are from the current core, which includes issue #7's compact small-list representation —
trimmed tail chunks + grow-by-doubling spine blocks. It halved the chunked columns across the
board vs the first measurement pass; pre-#7 tables are in the git history of this file.)

(Timings on this shared VM carry the usual ~2× session noise — `Allocated` and the LOH table
below are the stable columns.)

### The LOH columns (`--bucket-loh`, 10 warm batches, workstation GC, forced-compaction deltas)

Zipf, K=10k — **1M entities**:

| Approach | LOH after build | LOH after 10 batches (uncompacted) | LOH after forced Gen2+compact | Extra heap retained by holding the pre-cycle snapshot |
|---|---:|---:|---:|---:|
| ImmArray_AddRange | 2.2 MiB | **23.7 MiB** | 4.4 MiB | 7.3 MiB |
| List_Then_PublishArray | 4.4 MiB | **31.4 MiB** | 10.0 MiB | 7.3 MiB |
| **ChunkedList_Builder** | **0.0 MiB** | **0.0 MiB** | **0.0 MiB** | 7.0 MiB |
| **SnapshotTable_Rekeyed** | **0.0 MiB** | **0.0 MiB** | **0.0 MiB** | 52.0 MiB |
| **MultiValueSnapshotTable** | **0.0 MiB** | **0.0 MiB** | **0.0 MiB** | 7.3 MiB |

Zipf, K=10k — **10M entities** (hottest bucket ~1.02M entities ≈ 7.8 MiB per copy):

| Approach | LOH after build | LOH after 10 batches (uncompacted) | LOH after forced Gen2+compact | Batch median |
|---|---:|---:|---:|---:|
| ImmArray_AddRange | 40.1 MiB | **291.6 MiB** | 79.4 MiB | 106.3 ms |
| List_Then_PublishArray | 80.2 MiB | **406.6 MiB** | 159.9 MiB | 23.7 ms¹ |
| **ChunkedList_Builder** | **0.0 MiB** | **0.0 MiB** | **0.0 MiB** | **5.1 ms** |
| **SnapshotTable_Rekeyed** | **0.0 MiB** | **0.0 MiB** | **0.0 MiB** | 212.7 ms¹ |
| **MultiValueSnapshotTable** | **0.0 MiB** | **0.0 MiB** | **0.0 MiB** | 8.1 ms |

¹ Timings on the array and rekeyed rows swing hard between sessions on this shared VM (List
23.7–135 ms median, rekeyed 118.8–219.6 ms across runs; §12's controlled before/after pair is
the devirtualization comparison). The chunked/multi-value ~5–8 ms medians and every LOH column
are stable across all runs.

At 10M the chunked buckets are no longer just the LOH-safe option — they are ~20× faster per
batch than either array approach, because a warm batch copies a few 4 KB chunks per hot bucket
instead of an ~8 MiB contiguous array. (Array-approach timings drift with session GC pressure —
14–129 ms across runs; the chunked 6.8 ms and the LOH columns are stable.)

Full outputs, including the uniform controls (where nothing touches the LOH and the array
approaches win everything): [`bucket-loh-1m-10k-zipf.txt`](results/raw/bucket-loh-1m-10k-zipf.txt),
[`bucket-loh-10m-10k-zipf.txt`](results/raw/bucket-loh-10m-10k-zipf.txt),
[`bucket-loh-1m-10k-uniform.txt`](results/raw/bucket-loh-1m-10k-uniform.txt),
[`bucket-loh-1m-100k-uniform.txt`](results/raw/bucket-loh-1m-100k-uniform.txt).

**Pass criteria from the ticket, checked:**

- ✅ **Chunked / Snapshot: LOH growth ≈ 0 on skewed keys with bucket payload ≥ 85 KB** — measured
  exactly 0.0 MiB at every scale, uncompacted and compacted, 10 batches.
- ✅ **`ImmArray_AddRange`: a large LOH step** — +21.5 MiB uncompacted (1M), +251 MiB (10M) over
  10 batches; the LOH only returns toward baseline after a *forced* Gen2 with LOH compaction,
  which .NET does not run on its own.
- ✅ **`List_Then_PublishArray`: one LOH alloc per large touched key per batch** — and it is the
  *worst* LOH citizen here, not a mitigation: mutating the master list costs nothing, but every
  publish materializes a fresh full-size array, so hot buckets put **two** copies (master `List`
  backing array + published array) on the LOH and re-publish per batch (uncompacted step +27 MiB
  at 1M, +326 MiB at 10M — larger than `AddRange`, which at least reuses nothing).

### Honest costs on the other side

- **Small buckets still read and batch cheaper as arrays, but the chunked memory penalty is
  gone.** The first measurement pass found ~11.7 KB of fixed overhead per `ChunkedImmutableList`
  (8 KB spine block + a full-size tail chunk): 100k ten-entity buckets cost **1.30 GiB vs
  125 MiB** as arrays. Issue #7 (trimmed tail chunks + grow-by-doubling spine blocks, no change
  to the three-hop read path) fixed it: the same store now builds to **137.3 MiB (1.10× arrays)**,
  and held-snapshot retention fell from 114.6 to 3.5 MiB. What remains is per-batch: arrays still
  allocate ~23× less for tiny-bucket updates (199 KB vs 4.6 MB at K=100k uniform) and read
  ~2× faster (below), so arrays remain the right call where buckets are reliably small.
- **Scattered wide replaces approach a full copy.** Replacing 1% of a 100k-entity bucket at
  random indexes touches nearly all of its 512-row chunks, so the chunked copy volume converges
  on the array copy — still allocated as sub-LOH chunks (the guarantee holds), but the "extra
  retained by held snapshot" column (14.4 vs 7.3 MiB) shows the copied-chunk overhead. Appends
  and clustered replaces are where structural sharing shines.
- **`SnapshotTable_Rekeyed` buys atomicity, not speed.** One `ApplyChanges` gives a consistent
  cross-key snapshot and point reads by `(groupId, entityId)`, at 3–10× the batch cost of the
  chunked buckets and the highest snapshot-retention cost (index shards + directory clones).
  Enumerating *all* entities of a group needs a secondary index; since issue #9 the index's
  buckets are hybrid (flat arrays ≤1,024 elements, chunked beyond), so hot 10k–100k+ entity
  groups index-append at O(chunk) with zero LOH and group enumeration is supported at any bucket
  size — at the read cost measured in the table above.

### The read side — what the LOH win costs per lookup and per scan

![Bucket read time](results/charts/bucket-read-time.png)

Reads are the path a cache serves all day, so the batch/LOH tables above are only half the
decision (ADR-0005). `BucketReadBenchmarks` measures the same populations read-only (no state
restore → stable timings, zero allocation on every row):

| Access pattern (1M entities, K=10k) | ImmutableArray | ChunkedImmutableList | SnapshotTable_Rekeyed | MultiValueSnapshotTable (#8) |
|---|---:|---:|---:|---:|
| 10k random indexed reads, hot-weighted (Zipf) | **136 μs (~14 ns)** | 310 μs (~31 ns, 2.3×) | 1,791 μs (~179 ns)³ | 700 μs (~70 ns)⁵ |
| 10k random indexed reads (Uniform) | **183 μs (~18 ns)** | 385 μs (~38 ns, 2.1×) | 1,884 μs (~188 ns)³ | 597 μs (~60 ns)⁵ |
| Scan all 1M entities (Zipf) | **4.8 ms (~4.8 ns/entity)** | 5.4 ms enumerator / **5.0 ms via `Chunks` (1.04×)**⁶ | 137 ms via group index⁴ | 9.5 ms⁵ |
| Scan all 1M entities (Uniform) | **4.3 ms (~4.3 ns/entity)** | 5.5 ms enumerator / **4.5 ms via `Chunks` (1.03×)**⁶ | 149 ms via group index⁴ | 5.2 ms⁵ |

³ Table-lookup timings carried a wide error band in this run (±2 ms); §2's steadier measurement
puts the per-lookup cost at ~60–120 ns. The 4–10× ratio band vs a contiguous array is the signal.

⁴ Group enumeration through the hybrid secondary index (issue #9): index key → primary keys →
row lookups against one consistent snapshot. It is point-lookup-bound (~140 ns/entity), ~30×
a direct chunked-bucket scan, allocating only ~320 KB of enumerators for all 10k groups — the
price of full atomicity when the entities live in one re-keyed table rather than per-key buckets.

⁵ `MultiValueSnapshotTable.Lookup` returns `IReadOnlyList<TEntity>` (array or chunked behind the
interface), so element access pays interface dispatch: ~2× raw chunked on hot-weighted indexing,
and scans land between the array and doubled-chunked cost depending on how many probes hit
promoted buckets. A typed scan API (pattern-matching the concrete bucket) is the obvious
follow-up if scan-heavy consumers adopt the packaged type.

⁶ Issue #22: `ChunkedImmutableList.Chunks` yields each chunk as a `ReadOnlySpan<T>`, so a scan
runs as a handful of tight span loops (one `MoveNext` per chunk) instead of the per-element
`IEnumerator` (one `MoveNext` + `Current` per entity). That closes the scan gap vs a contiguous
`ImmutableArray` from ~1.1–1.25× to **~1.03–1.04×** — the layout was never the cost, the
per-element call overhead was. `CopyTo(Span)` and a fast `ToArray()` are built on the same path.

Raw: [`BucketReadBenchmarks-report-github.md`](results/raw/DotnetTools.SnapshotCache.Benchmarks.BucketReadBenchmarks-report-github.md).
(Re-measured after issues #7/#9; the array/chunked ratios were identical before — the compact
representation does not touch the read path.)

How to read it: the chunked list's three-array-hop indexer costs ~2–2.5× a contiguous array on
random access; sequential scans through the **`Chunks` span path (#22)** run at ~1.03–1.04× the
contiguous array (the per-element enumerator was 1.1–1.25× — the span loop removes that overhead).
The rekeyed table pays the full
shard-directory + chunk hop per lookup and cannot enumerate one group at all without the
secondary index (#9). None of the read premiums allocate. This is the quantified case for the
hybrid recommendation below: keep small buckets as arrays (best reads, LOH unreachable), pay the
chunked read premium only where the alternative is LOH churn.

### Recommendation: which shape fits which store

| Your buckets | Use |
|---|---|
| Reliably small (≤ ~2k entities, payload « 85 KB) | `ImmutableArray.AddRange` or `List` → publish — cheapest on every metric, LOH never involved |
| Can grow past ~10k references / 85 KB (skewed one-to-many) | `ChunkedImmutableList` builder per bucket — zero LOH at any size, O(touched chunks) batches, cheap held snapshots for append-shaped churn |
| Real Zipf population (most buckets tiny, a hot head) | **`MultiValueSnapshotTable` (issue #8)** — the packaged hybrid: arrays ≤1,024 entities, chunked beyond, atomic `ApplyChanges` batches, wait-free snapshots. Resident heap 1.02× arrays, batch cost at chunked parity, 0.0 MiB LOH; reads pay interface dispatch (~2× raw chunked on hot buckets) |
| Need atomic multi-key batches, consistent snapshots, keyed point reads | `SnapshotTable` re-keyed to `(sharedKey, uniqueKey)` — highest batch cost, zero LOH, wait-free readers |

### API gaps found (candidate follow-ups)

1. **No shared-key/multi-value helper.** The winning pattern (dictionary of per-key
   `ChunkedImmutableList` + hybrid small-bucket representation + atomic publish) is hand-rolled
   in this harness; a `MultiValueSnapshotTable<TKey, TEntity>` packaging it would close the gap.
2. ~~**`ChunkedImmutableList` fixed per-instance overhead**~~ — **fixed** (issue #7, in this
   core): trimmed tail chunks + grow-by-doubling spine blocks cut the K=100k build heap from
   1.30 GiB to 137.3 MiB with the read path untouched.
3. ~~**`Builder` has no `AddRange`**~~ — **fixed** (issue #10, in this core): span/enumerable
   overloads copy chunk-sized runs; `EmptyWithTargetBytes` is public for bytes-based chunk tuning.
4. ~~**Secondary-index buckets are flat copied arrays**~~ — **fixed** (issue #9, in this core):
   buckets stay flat arrays up to 1,024 elements (best reads, cheap copies) and promote to
   `ChunkedImmutableList` beyond, so hot 10k–100k+ entity groups append at O(chunk) with zero
   LOH (guardrail test `HotBucketAppends_DoNotTouchTheLargeObjectHeap`). `SnapshotTable` + group
   index is now a complete answer for one-to-many enumeration — measured in the read table above.

## 10. Single large refresh over skewed buckets (Workload C)

One batch of **~12k entity operations across 800 of 2,000 keys** (touched keys weighted to the
hot head; alternating append-shaped and replace-shaped keys) against a population where 15 buckets already hold 30k
entities (234 KB as reference arrays — past the LOH bar) and the rest hold ~350.
`LargeRefreshBenchmarks` + `--bucket-loh 1000000 2000 refresh 10`:

| Approach | Batch (BDN median) | Allocated | LOH after 10 refreshes (uncompacted) | LOH compacted | Gen2+compact pause |
|---|---:|---:|---:|---:|---:|
| ImmArray_AddRange | 2.3 ms | 5.19 MB | 9.4 MiB | 6.9 MiB | 227 ms |
| List_Then_PublishArray | **1.7 ms** | 5.19 MB | **35.5 MiB** | 13.7 MiB | 255 ms |
| ChunkedList_Builder | 2.2 ms | 6.62 MB | **0.0 MiB** | **0.0 MiB** | 216 ms |
| SnapshotTable_Rekeyed | 18.4 ms | 42.85 MB | **0.0 MiB** | **0.0 MiB** | 249 ms |

Same verdict at refresh-event scale, and after issue #7 the trade is barely a trade: the chunked
buckets now land between the two array approaches on wall time (2.2 ms) at 1.3× their allocation
— all of it ephemeral small-object garbage that ordinary Gen0/Gen1 collections recycle — while
every array-approach event deposits another layer of dead LOH-sized arrays that only a forced
compacting Gen2 (≈200–260 ms pause at this heap size) claws back.
Raw: [`bucket-loh-1m-refresh.txt`](results/raw/bucket-loh-1m-refresh.txt).

## 11. Batch-size sweep with removes (Workload A extension)

`UniqueKeyBatchBenchmarks`: N=1M mid-width rows, B ∈ {1k, 10k, 50k} uniformly random upserts,
0% or 10% of B removed in the same batch, warm steady state (each batch re-inserts what the
previous one removed). ShortRun medians, 0%-removes rows shown (10% adds ~15–25% to
`SnapshotTable` and is invisible for the rebuild approaches — full tables in
[`results/raw/`](results/raw/DotnetTools.SnapshotCache.Benchmarks.UniqueKeyBatchBenchmarks-report-github.md)):

| Method | B=1k | B=10k | B=50k | Allocated (1k → 50k) | LOH |
|---|---:|---:|---:|---:|---|
| SnapshotTable.ApplyChanges | 15.8 ms | 14.0 ms | 33.9 ms | 15.0 → 15.3 MB (flat) | none |
| + held snapshot across batch | 14.9 ms | 14.1 ms | 18.4 ms | identical | none |
| Dictionary rebuild + swap | 12.8 ms | 24.2 ms | 14.4 ms | 31.0 MB (flat) | yes, every batch |
| ImmutableDictionary.SetItems | **1.6 ms** | 27.4 ms | 104.9 ms | 0.55 → 11.7 MB (∝B) | none |
| FrozenDictionary rebuild | 70.0 ms | 81.5 ms | 91.8 ms | ~98 MB (flat) | yes, every batch |

What the sweep shows:

- **`SnapshotTable`'s batch cost is capacity-shaped, not B-shaped, once random batches saturate
  the chunks.** At 1M rows there are only ~244 64 KB chunks, and a uniformly random 1k batch
  already lands in ~98% of them — so 1k, 10k and 50k batches all copy essentially every chunk
  and cost the same ~14–34 ms / ~15 MB. This is the saturation worst case by design; sparse
  batches (large N, §4) and clustered change feeds (§7: 16× less) are where O(batch) shows.
- **`ImmutableDictionary` is the small-batch champion** (1.6 ms at B=1k, pure O(B log N)) — the
  reason it still loses the workload is §2: its reads are ~10× slower and its resident footprint
  4× raw, paid on every lookup all day long.
- **Holding the previous snapshot across a batch is free on the timing side** (identical
  medians/allocation; the retained-memory side is §9's "extra retained" column) — the structural
  sharing claim, now measured in the sweep too.
- **Cold loads**: `ResetParallel` cuts the 1M-row cold load from 101 ms to **34 ms** (§3 table
  re-measured this run; `Dictionary` fill remains 13 ms, `FrozenDictionary` 78 ms with a 139 MB
  LOH bill).

## 12. Comparer devirtualization and batched index-bucket builders (#11 first suspect)

Two write/read-path refinements on top of the #7/#9/#10 work, measured with the §9 harness:

- **Hash/equality devirtualization.** `ShardMap`, `SnapshotTable.ShardOf`, and the secondary
  index call `EqualityComparer<T>.Default` directly when the key is a value type using the
  default comparer — a JIT intrinsic that inlines the hash and equality per probe instead of
  interface-dispatching (the `typeof(TKey).IsValueType` guard folds away at JIT time, so
  reference-type keys keep the old path with no extra branch). This was #11's first suspect for
  the rekeyed `(sharedKey, entityId)` composite-key workload: `--bucket-loh 10000000 10000 zipf`
  batch median dropped **219.6 → 118.8 ms** with identical allocation (39.6 MiB) and LOH (0.0)
  ([before](results/raw/bucket-loh-10m-10k-zipf.txt) →
  [after](results/raw/bucket-loh-10m-10k-zipf-devirt.txt); the after-run's VM was slower on the
  unaffected `ChunkedList_Builder` control, so the win is not machine drift). The remaining #11
  gap vs raw bucket stores — index-writer bookkeeping and chunk scatter — stays open.
- **One builder per touched promoted bucket per batch.** The index writer folds all of a batch's
  membership changes to the same promoted (chunked) bucket through a single
  `ChunkedImmutableList` builder, published once on freeze — previously each change paid its own
  ToBuilder/publish round-trip (top-spine copy + tail re-expand + trim). With the Zipf hot head
  seeing 1–50 appends per touched key per batch, this removes O(changes) spine copies per hot
  group; consistency through mixed add/remove/move batches (including a bucket emptying out
  mid-batch) is covered by `HybridIndexBucketTests`.

## 13. 100 warm batches: p50/p95, Server GC, and what the LOH looks like after an hour-shaped run

The 10-batch studies above show the mechanism; this is the endurance version (issue #12): **100
warm batches** per approach, workstation *and* Server GC, with real p50/p95 per batch. Buckets
grow as batches accumulate, so this is also the honest "what does month-two look like" shape.
Raw: [`bucket-loh-10m-10k-zipf-100c.txt`](results/raw/bucket-loh-10m-10k-zipf-100c.txt),
[`-servergc`](results/raw/bucket-loh-10m-10k-zipf-100c-servergc.txt) (and the 1M pair).

10M entities, K=10k Zipf, 100 batches:

| Approach | p50 / p95 (Workstation) | p50 / p95 (Server) | **Uncompacted LOH after 100 batches (Wks / Server)** | Alloc/batch |
|---|---:|---:|---:|---:|
| ImmArray_AddRange | 43.9 / 241.1 ms | 13.3 / 27.1 ms | **493 MiB / 664 MiB** | 30.1 MiB |
| List_Then_PublishArray | 13.7 / 206.7 ms | 14.6 / 28.6 ms | **1,121 MiB / 1,424 MiB** | 31.3 MiB |
| ChunkedList_Builder | 27.6 / 70.2 ms | **5.7 / 25.8 ms** | 2.3 MiB / 1.6 MiB | 15.1 MiB |
| SnapshotTable_Rekeyed | 119.4 / 425.3 ms | 34.8 / 134.3 ms | 7.2 MiB / 30.3 MiB | 40.2 MiB |
| MultiValueSnapshotTable | 33.4 / 77.8 ms | **5.2 / 54.2 ms** | 1.7 MiB / 1.6 MiB | 15.2 MiB |

What 100 cycles add to the 10-cycle story:

- **The array approaches' LOH debt compounds.** At 10 batches the uncompacted step was ~292/407
  MiB; at 100 batches `List_Then_PublishArray` is sitting on **1.1 GiB (workstation) / 1.4 GiB
  (Server)** of dead-but-resident large objects — Server GC makes it *worse*, because background
  GC feels even less pressure to collect the LOH. Reclaiming it took a 0.9–2.8 s forced
  compacting pause. The chunked structures' equivalents: 1.6–7 MiB.
- **Server GC is the production recommendation for every approach** — batch p50 drops 2–5×
  across the board (rekeyed 119 → 35 ms, chunked 28 → 5.7 ms) because background GC absorbs the
  Gen0/Gen1 churn — but it does not fix the array approaches' LOH accumulation; only the
  allocation shape does.
- **p95 vs p50 is where the GC tax shows.** Workstation chunked p50 is sub-ms at 1M yet p95 is
  ~12 ms — those are the batches that ate an ephemeral GC. Under Server GC the 1M p95 falls to
  ~4–5 ms. The rekeyed table's p95 remains the weakest number (#11's remaining gap).

The same run at 1M entities shows the identical pattern at smaller magnitude
(`List` 81 MiB uncompacted after 100 batches on workstation, chunked/table/multi-value 0.0).

### #11 addendum: chunk size is *not* the rekeyed bottleneck

Hypothesis tested: since a group's entities load adjacently, a rekeyed batch's replacements
cluster, so larger row chunks might cut copy count. An **interleaved A/B** (default 128-row
chunks vs 2048-row, two rounds each, 20 batches, 10M Zipf —
[`bucket-loh-10m-ab-chunkrows.txt`](results/raw/bucket-loh-10m-ab-chunkrows.txt)) falsifies it:

| ChunkRows | p50 (round 1 / 2) | Alloc/batch | Retained by held snapshot |
|---|---:|---:|---:|
| 128 (adaptive default) | 95.8 / 94.2 ms | **39.8 MiB** | **283 MiB** |
| 2048 | 93.7 / 98.5 ms | 53.2 MiB (+34%) | 308 MiB (+9%) |

Wall time is identical within noise while bigger chunks cost more on both memory axes — the
adaptive default stands, and the earlier one-shot runs suggesting a 3× chunk-size win were VM
drift (which is why A/Bs here must interleave). #11's remaining gap vs the raw bucket stores is
therefore in the per-entity index probing/bookkeeping, not row-chunk geometry.

## 14. `ImmutableList` joins the shared-key matrix; same-key appends batch through one builder (#31, #32)

Two follow-ups measured together on the current core:

- **#31** — `MultiValueSnapshotTable.ApplyChanges` now folds every `Append`/`ReplaceAt` to the
  same promoted key (including the promote-from-array transition) through **one**
  `ChunkedImmutableList` builder per batch, published once when the batch swaps in — the §12
  secondary-index builder cache, applied to the packaged bucket store. `Remove`/`ReplaceBucket`
  discard the key's open builder (last change wins, semantics unchanged).
- **#32** — `ImmutableList<T>`, whose unique-key story is §1–§2 (reads ~10× slower than
  `SnapshotTable`, ~3.5× steady memory, LOH-free node sharing), joins the shared-key matrix as
  `ImmList_Builder`: one `ImmutableList<Entity>` per key, warm updates via `ToBuilder` →
  mutate → `ToImmutable`.

### #31: one publish per touched key per batch

The §9 harness coalesces changes (one `Append`/`ReplaceAt` per touched key per batch), so it
never exposed the pattern; re-running it confirms no regression — identical allocation
(2.1 MiB/batch at 1M Zipf, 15.0 MiB at 10M) and timing parity (0.8 → 0.9 ms and 8.1 → 9.2 ms
medians, within this VM's session noise;
[before](results/raw/bucket-loh-1m-10k-zipf.txt) →
[after](results/raw/bucket-loh-1m-10k-zipf-immlist.txt), same pair for
[10M](results/raw/bucket-loh-10m-10k-zipf-immlist.txt)).

The win is for callers that emit one `Append` per entity (or several chunks) instead of
coalescing. A/B on the same VM, same shape
([raw](results/raw/multivalue-samekey-builder-ab.txt)): one batch of **50 one-entity Appends
to a 100k-entity promoted bucket** cost the pre-#31 core 0.10 ms / 422 KB per batch — every
change paid its own ToBuilder → ToImmutable round-trip. The folded path costs
**0.01 ms / 9 KB: ~10× less time, ~47× less allocation**. (Emitting the same 50 changes as 50
separate batches still costs 0.12 ms / 455 KB — each batch legitimately pays its own shard
clone and snapshot swap — so the table now makes the one-batch path cheap whether or not the
caller coalesces entities into one change.)

Verified by test: 50 same-key appends in one batch must allocate under a quarter of the same
appends applied as 50 batches (`ManyAppendsToOnePromotedKey_InOneBatch_SinglePublish`), plus
mixed append/replace/remove-vs-model batches and mid-batch promotion; the concurrent-reader
and LOH guardrails are unchanged and green. En route this closed a latent LOH hole: a first
`Append` larger than 1,024 entities to an absent key used to materialize one full-size
(possibly LOH-sized) array before any later append promoted it — it now promotes straight to
chunks.

### Batch cost with ImmList (BenchmarkDotNet, ShortRun, medians; state restored between iterations)

Same harness and populations as §9–§10, all six approaches re-run in one session
([Workload B raw](results/raw/DotnetTools.SnapshotCache.Benchmarks.SharedKeyBucketBenchmarks-report-github.md),
[Workload C raw](results/raw/DotnetTools.SnapshotCache.Benchmarks.LargeRefreshBenchmarks-report-github.md)):

| K / skew | ImmArray_AddRange | List_Then_PublishArray | ImmList_Builder | ChunkedList_Builder | SnapshotTable_Rekeyed | MultiValueSnapshotTable |
|---|---:|---:|---:|---:|---:|---:|
| 10k / Uniform (buckets ≈100) | **113 μs / 91 KB** | 122 μs / 91 KB | 390 μs / 96 KB | 255 μs / 535 KB | 4.8 ms / 10.9 MB | 263 μs / 316 KB |
| 10k / Zipf (hot ≈102k) | 1.41 ms / 3.24 MB | 1.02 ms / 3.24 MB | 2.18 ms / **397 KB** | **0.50 ms** / 1.42 MB | 5.0 ms / 12.9 MB | 0.64 ms / 1.40 MB |
| 100k / Uniform (buckets ≈10) | 0.69 ms / **199 KB** | **0.61 ms** / 199 KB | 2.81 ms / 793 KB | 1.74 ms / 4.64 MB | 24.5 ms / 45.8 MB | 2.15 ms / 2.64 MB |
| 100k / Zipf | 1.95 ms / 4.06 MB | 1.70 ms / 4.06 MB | 6.80 ms / **1.56 MB** | 2.35 ms / 7.02 MB | 33.2 ms / 48.6 MB | **2.14 ms** / 5.59 MB |
| Workload C (2k keys, ~800 touched) | 3.34 ms / 5.19 MB | 1.93 ms / 5.19 MB | 6.05 ms / **1.37 MB** | 2.11 ms / 6.62 MB | 34.5 ms / 43.2 MB | **1.43 ms** / 3.27 MB |

The pattern is consistent: **ImmList allocates the least of any approach on skewed workloads**
(an AVL update allocates the O(log n) spine path plus the appended nodes, where the chunked
builder copies whole touched 4 KB chunks and the array approaches copy whole buckets) — and
pays for it with the slowest LOH-safe wall time, 3–4× the chunked builder / MultiValue table.

### The LOH columns (`--bucket-loh`, 10 warm batches, workstation GC, forced-compaction deltas)

Zipf, K=10k — **1M entities** ([raw](results/raw/bucket-loh-1m-10k-zipf-immlist.txt)):

| Approach | Heap after build | LOH after build | Batch p50 | Alloc/batch | LOH after 10 batches (uncompacted) | LOH after Gen2+compact | Gen2+compact pause | Extra retained by held snapshot |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| ImmArray_AddRange | 122.4 MiB | 2.2 MiB | 2.4 ms | 3.0 MiB | **23.7 MiB** | 4.4 MiB | 225 ms | 7.3 MiB |
| List_Then_PublishArray | 130.6 MiB | 4.4 MiB | 3.2 ms | 3.8 MiB | **31.4 MiB** | 10.0 MiB | 266 ms | 7.3 MiB |
| **ImmList_Builder** | 160.5 MiB | **0.0 MiB** | 4.0 ms | **0.6 MiB** | **0.0 MiB** | **0.0 MiB** | 320 ms | 6.2 MiB |
| **ChunkedList_Builder** | 123.6 MiB | **0.0 MiB** | **0.8 ms** | 2.1 MiB | **0.0 MiB** | **0.0 MiB** | 206 ms | 7.0 MiB |
| SnapshotTable_Rekeyed | 168.2 MiB | 0.0 MiB | 79.0 ms | 15.4 MiB | 0.0 MiB | 0.0 MiB | 261 ms | 52.2 MiB |
| **MultiValueSnapshotTable** | 122.7 MiB | **0.0 MiB** | **0.9 ms** | 2.1 MiB | **0.0 MiB** | **0.0 MiB** | 202 ms | 7.3 MiB |

Zipf, K=10k — **10M entities** (hottest bucket ~1.02M entities;
[raw](results/raw/bucket-loh-10m-10k-zipf-immlist.txt)):

| Approach | Heap after build | Batch p50 | Alloc/batch | LOH after 10 batches (uncompacted) | Gen2+compact pause |
|---|---:|---:|---:|---:|---:|
| ImmArray_AddRange | 1,289.7 MiB | 144.8 ms | 30.1 MiB | **291.6 MiB** | 1,971 ms |
| List_Then_PublishArray | 1,366.6 MiB | 155.6 ms | 38.3 MiB | **406.6 MiB** | 2,387 ms |
| **ImmList_Builder** | 1,671.1 MiB | 38.5 ms | **5.1 MiB** | **0.0 MiB** | 3,125 ms |
| **ChunkedList_Builder** | 1,291.4 MiB | **6.8 ms** | 14.9 MiB | **0.0 MiB** | 1,763 ms |
| SnapshotTable_Rekeyed | 1,759.7 MiB | 235.7 ms | 39.6 MiB | 0.0 MiB | 2,282 ms |
| **MultiValueSnapshotTable** | 1,290.5 MiB | **9.2 ms** | 15.0 MiB | **0.0 MiB** | 1,829 ms |

The uniform and refresh-shaped controls and a 100-batch endurance run tell the same story
([uniform](results/raw/bucket-loh-1m-10k-uniform-immlist.txt),
[refresh](results/raw/bucket-loh-1m-refresh-immlist.txt),
[100 cycles](results/raw/bucket-loh-1m-10k-zipf-100c-immlist.txt)): **ImmList is LOH-zero at
every scale and checkpoint** — uncompacted after cycles included — with the smallest per-batch
allocation in the matrix. Everything else about it costs more: **+30% resident heap at rest**
vs the chunked/hybrid stores (160.5 vs 122.7–123.6 MiB at 1M; 1,671 vs ~1,290 MiB at 10M — a
pointer-heavy node per entity), **5–6× the chunked builder's warm-batch wall time** at both
scales (tree rebalancing and pointer chasing vs span copies), and the **worst forced-Gen2
pause** in the matrix (3.1 s vs 1.8 s at 10M — the collector traces one node per element).

### The read side with ImmList

Same read harness as §9 ([raw](results/raw/DotnetTools.SnapshotCache.Benchmarks.BucketReadBenchmarks-report-github.md);
this session's VM ran ~2× slower than the §9 pass on the unchanged approaches — the ratios
against ImmutableArray in the same run are the signal):

| Access pattern (1M entities, K=10k) | ImmutableArray | ChunkedImmutableList | ImmList | MultiValueSnapshotTable |
|---|---:|---:|---:|---:|
| 10k random indexed reads, hot-weighted (Zipf) | **235 μs (1.0×)** | 291 μs (1.24×) | 2,562 μs (**10.9×**) | 545 μs (2.3×) |
| 10k random indexed reads (Uniform) | **265 μs (1.0×)** | 334 μs (1.26×) | 1,239 μs (**4.7×**) | 438 μs (1.65×) |
| Scan all 1M entities (Zipf) | **10.0 ms (1.0×)** | 10.0 ms (1.00×) | 31.7 ms (**3.2×**) | 16.0 ms (1.6×) |
| Scan all 1M entities (Uniform) | **10.0 ms (1.0×)** | 10.1 ms (1.01×) | 30.2 ms (**3.0×**) | 11.6 ms (1.16×) |

`ImmutableList[i]` walks the AVL tree — O(log n) node hops per probe — so its indexed-read
premium *grows with bucket size*: ~4.7× a contiguous array on uniform ~100-entity buckets
(tree depth ~7) but ~11× under Zipf where hot-weighted probes land in the ~102k-entity head
(depth ~17). Full scans pay ~3× at any shape (the enumerator maintains an explicit node
stack). Compare the chunked list, whose indexer is three flat array hops at any size: its
premium stays 1.2–1.3× and its scans are array-parity. None of the read paths allocate.

### What the ImmList columns say (the ticket's decision criteria)

1. **LOH growth ≈ 0?** Yes — exactly 0.0 MiB at every scale and checkpoint, uncompacted and
   compacted, including 100 batches of endurance.
2. **Reads vs the alternatives:** the worst LOH-safe reader by far — 4.7–10.9× a contiguous
   array on indexed reads (vs 1.2–1.3× chunked, 1.7–2.3× MultiValue) and ~3× on scans, with
   the premium growing on exactly the hot buckets a skewed workload reads most.
3. **Retained / steady-state memory vs ChunkedList:** +30% resident at rest (160.5 vs
   123.6 MiB at 1M; 1,671 vs 1,291 MiB at 10M). Held-snapshot retention is the one memory
   axis where ImmList edges ahead (6.2 vs 7.0 MiB at 1M Zipf) — append-shaped churn shares
   all but the O(log n) spine path. The node-per-entity graph also makes it the slowest
   structure to trace: the worst compacting-Gen2 pause in the matrix at 10M.
4. **When is ImmList preferable?** Only when per-batch allocation volume is the binding
   constraint (e.g. an allocation-rate-throttled host), reads are rare, and the resident-heap
   premium is acceptable — a narrow niche. It is the simplest LOH-free option in the BCL, and
   that convenience is what its read and memory columns price in.

**Recommendation for a read-hot, lock-free-publish, LOH-sensitive multi-value store:** stay
with the hybrid array/`ChunkedImmutableList` shape or the packaged `MultiValueSnapshotTable`.
They match ImmList's 0.0 LOH while reading 4–9× faster on the hot buckets, batching 3–6×
faster, resting 30% smaller, and compacting with shorter pauses; ImmList's lone advantage
(lowest alloc/batch) buys nothing here because the chunked builders' allocation is already
ephemeral Gen0 garbage that never reaches the LOH. `ImmutableList` remains what §1–§2 found
for unique keys: the zero-dependency fallback when you cannot take a library, priced in reads
and resident memory.

## 15. Secondary-index creation cost (backfill after load, issue #20)

Registering a secondary index on an already-populated table backfills it with a one-time
O(rows) scan of the current snapshot. `SecondaryIndexBenchmarks`, 1M rows, region attribute
(8 distinct values):

| Operation | Time | Allocated |
|---|---:|---:|
| `CreateIndex` backfill on a loaded table | 34 ms | **40 MB** (the index) |
| `Reset` with the index registered up front | 134 ms | 74.5 MB (load + index end-to-end) |

The backfill is a one-time admin cost in the band of a full `Reset`, allocating only the index's
own buckets. Those buckets chunk once they pass 1,024 members, so the index is **sub-LOH by
construction** even for the ~125k-member region buckets here — but note that `[MemoryDiagnoser]`
reports allocated bytes and Gen0/1/2 *counts*, not LOH occupancy (ADR-0005, pitfall #1), so that is
a structural guarantee, not a figure this benchmark measures. The `--bucket-loh` console study and
the `Category=Performance` guardrails are what verify the zero-LOH property. The eager row shows the
load+index total for reference — not directly comparable, since its per-row index work is folded
into `Reset`. Raw:
[`SecondaryIndexBenchmarks-report-github.md`](results/raw/DotnetTools.SnapshotCache.Benchmarks.SecondaryIndexBenchmarks-report-github.md).

## 16. Cold-load cost: batch vs the per-key footgun (issues #42/#43)

`MultiValueSnapshotTable` must be cold-loaded with `Reset` or a single batched `ApplyChanges`.
Loading it with one `ApplyChanges` call per key is **O(N²)** in shard occupancy: every call clones
the shard directory and copy-on-writes a whole shard dictionary. In a production A/B (issue #42)
this flooded the LOH — sampled allocation rose ~108 GB → ~2504 GB — and the process never reached
Ready. `ColdLoadBenchmarks` contrasts the three population paths at N ∈ {10k, 100k, 1M} keys
(4 values each), pinned to a one-shot `ColdStart` job so each build is measured once; the per-key
path is capped at 100k because at O(N²) the 1M case runs for minutes (which is the symptom).

The **allocation column** is the signal here (timings on a shared runner are noisy per ADR-0005,
and the per-key path's cost is dominated by repeated shard-dictionary clones, not wall time). The
per-key path allocates orders of magnitude more than `Reset` for the same final table; `Reset` and
batched `ApplyChanges` sit in the same O(N) band.

> Numbers are produced on a benchmark host (`dotnet run -c Release --project
> benchmarks/DotnetTools.SnapshotCache.Benchmarks -- --filter '*ColdLoad*' --job short`) and the
> raw report committed under `results/raw/`; this table is pending that run. The **deterministic**
> gate does not wait for it: the `Category=Performance` guardrail
> `ColdLoad_PerKeyApplyChanges_AllocatesOrdersOfMagnitudeMoreThanReset` asserts on allocated bytes
> (>20× `Reset`, measured ~30–40×) and runs on every merge request — matching #42's lesson that the
> failure was "never Ready," not "2% slower," so a boolean CI gate, not only a BenchmarkDotNet
> trend, guards it.

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

# shared-key bucket workloads (§9-§11):
dotnet run -c Release --project benchmarks/DotnetTools.SnapshotCache.Benchmarks -- --filter '*SharedKeyBucket*' --job short --memory
dotnet run -c Release --project benchmarks/DotnetTools.SnapshotCache.Benchmarks -- --filter '*BucketRead*' --job short --memory
dotnet run -c Release --project benchmarks/DotnetTools.SnapshotCache.Benchmarks -- --filter '*LargeRefresh*' --job short --memory
dotnet run -c Release --project benchmarks/DotnetTools.SnapshotCache.Benchmarks -- --filter '*UniqueKeyBatch*' --job short --memory

# LOH size / retention study behind the §9-§10 tables (entities, keys, skew, cycles):
dotnet run -c Release --project benchmarks/DotnetTools.SnapshotCache.Benchmarks -- --bucket-loh 1000000 10000 zipf 10
dotnet run -c Release --project benchmarks/DotnetTools.SnapshotCache.Benchmarks -- --bucket-loh 10000000 10000 zipf 10
dotnet run -c Release --project benchmarks/DotnetTools.SnapshotCache.Benchmarks -- --bucket-loh 1000000 2000 refresh 10
```
