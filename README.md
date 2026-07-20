# dotnet-tools

High-performance building blocks for .NET services. First tool: **DotnetTools.SnapshotCache** —
low-allocation, LOH-friendly snapshot collections for large in-memory table caches.

## The problem

A common cache shape: tables with **millions of rows** held in memory, refreshed with a **batch of
changes every ~30 seconds**, and read constantly from many threads. Doing this with the BCL
immutable collections hurts in specific, measurable ways:

| Structure | What goes wrong at millions of rows |
|---|---|
| `ImmutableArray<T>` | Any update (`SetItem`, builder) copies the **entire** backing array — O(N) CPU per batch, and the array itself lives on the **Large Object Heap** (anything ≥ 85,000 bytes). Every 30 s you allocate another multi-MB LOH array → LOH fragmentation and expensive Gen2/full GCs. |
| `ImmutableList<T>` | A balanced binary tree: ~2 heap objects' worth of overhead (~40+ bytes) per element, O(log N) pointer-chasing per read (terrible cache locality), and every update allocates O(log N) new tree nodes. Memory footprint is typically 3–5× the raw data. |
| `ImmutableDictionary<K,V>` | Same story as `ImmutableList` (HAMT trie): lookups ~10× slower than `Dictionary`, high per-node overhead, heavy allocation churn on updates. |
| `Dictionary` rebuild + swap | Fast reads, but a full rebuild every 30 s is O(N) CPU + O(N) allocation, and the internal `_entries`/`_buckets` arrays of a million-entry dictionary are firmly on the LOH. |
| `FrozenDictionary<K,V>` | The fastest possible reads, but it is build-once: incorporating a batch means a full O(N) rebuild, with the same LOH-sized internal arrays. Great for tables that change rarely; wrong for a 30-second refresh cycle over millions of rows. |

## The solution in this repo

Two classes in [`src/DotnetTools.SnapshotCache`](src/DotnetTools.SnapshotCache):

### `ChunkedImmutableList<T>`

An immutable (persistent) list stored as small fixed-size **chunks** (default ~4 KB of data,
tunable per instance) reached through a **two-level spine** (8 KB spine blocks of 1024 chunk
references). Compared to `ImmutableArray`:

- **No LOH allocations at any size** — chunks, spine blocks, the top spine, and all builder
  bookkeeping (ownership bitsets) stay below the 85 KB threshold up to `int.MaxValue` elements.
  A 100-million-row list is ~390k small arrays, not one 1.6 GB array.
- **O(touched chunks) updates instead of O(N)** — `SetItem` copies one chunk + one spine block +
  the top spine; a **batch** through `ToBuilder()` copies each touched chunk and spine block at
  most once. Untouched structure is shared between the old and new version (structural sharing),
  so old snapshots stay valid for free.
- **Array-speed reads** — an index read is three array indexings, no tree traversal.
- **Adaptive chunk size** — `SnapshotTable` picks large (~64 KB) chunks for tables up to a few
  million rows (dense batches: fewer, larger copies win) and small (~4 KB) chunks for huge tables
  (sparse batches: 20k random updates over 100M rows copy ~65 MB instead of ~880 MB). Override
  with `SnapshotTableOptions.ChunkRows` / `EmptyWithChunkRows` if your batch pattern differs.

### `SnapshotTable<TKey, TValue>`

The cache class for the "big table + periodic batch refresh" pattern:

- **Wait-free reads.** Readers do one volatile load of the current snapshot; no locks, no torn
  state. `GetSnapshot()` hands out a fully immutable, internally consistent view — iterate a report
  over it while updates keep landing.
- **Atomic batch updates.** `ApplyChanges(upserts, removes)` builds the next snapshot with
  copy-on-write at two granularities — row chunks (via `ChunkedImmutableList`) and a **sharded hash
  index**: up to ~500k small `Dictionary<TKey,int>` shards reached through a two-level directory,
  every piece below the LOH threshold. Only shards containing *inserted or removed* keys (and
  their directory blocks) are cloned; in-place value updates never touch the index at all.
- **Cost proportional to the batch, not the table.** A 20,000-change batch over 100,000,000 rows
  copies ~90 MB of small-object chunks/shards instead of gigabytes — measured: 0.87 s median per
  batch, zero LOH growth (see `benchmarks/RESULTS.md`), against a 30-second budget.
- **Removals are O(1)** via swap-remove (last row moves into the vacated slot; iteration order is
  not stable across removes — irrelevant for keyed cache tables).
- **Compact index shards.** The key → row index uses custom open-addressing shards
  (~22 B/entry vs ~39 B/entry for `Dictionary<TKey,int>`), cutting total footprint from ~3.3× to
  ~2.3× the raw data at 100M rows.
- **Secondary indexes** (`CreateIndex(selector)`) maintained atomically inside every batch —
  query customers by region/status/tier from any snapshot, consistent with that snapshot.
- **Change notifications** — `SnapshotChanged` fires after each atomic swap with the upserted and
  removed keys, so downstream caches or projections can react without diffing.
- **Parallel initial load** — `ResetParallel` builds the index on all cores for unique-key streams
  (duplicate keys are detected and rejected).

```csharp
var customers = new SnapshotTable<long, Customer>(new SnapshotTableOptions<long>
{
    CapacityHint = 100_000_000,   // sizes the sharded index and picks the chunk size
    // ChunkRows = 256,           // optional: override the adaptive chunk-size default
});
var byRegion = customers.CreateIndex((id, c) => c.Region);  // optional secondary index
customers.ResetParallel(LoadAllFromDatabase());            // initial full load, all cores

customers.SnapshotChanged += change =>                     // optional change feed
    InvalidateDownstream(change.UpsertedKeys, change.RemovedKeys);

// every 30 seconds:
customers.ApplyChanges(upserts: changedRows, removes: deletedIds);  // O(batch), atomic

// hot path, any thread, no locks:
if (customers.TryGetValue(id, out var customer)) { ... }

// consistent multi-read / full scan / secondary index query:
var snap = customers.GetSnapshot();
foreach (var (id, customer) in snap) { ... }               // never sees a half-applied batch
foreach (var (id, customer) in snap.LookupRows(byRegion, "BR")) { ... }
```

Install from the packaged build (`dotnet pack src/DotnetTools.SnapshotCache`; CI uploads the
`.nupkg` as an artifact on every merge request).

## Do you need this, or does something off-the-shelf fit?

Checked before building (ordered from "try first"):

1. **`FrozenDictionary` (.NET 8+)** — if a table refreshes *rarely* (minutes/hours) or is small
   enough that an O(N) rebuild every 30 s is acceptable, rebuild + atomic reference swap is the
   simplest correct answer and has the fastest reads. It loses only when N is large **and** the
   refresh is frequent — exactly the workload here.
2. **Plain `Dictionary` + reference swap** — same trade-off, cheaper rebuild than frozen, slightly
   slower reads. Still O(N) per refresh and LOH-resident internals.
3. **[Microsoft FASTER / Garnet (Tsavorite)](https://github.com/microsoft/garnet)** — excellent
   larger-than-memory key-value store with checkpointing. Heavier operationally (log, sessions);
   worth it if you need persistence or data larger than RAM, overkill for a pure in-memory
   read-mostly table.
4. **[BitFaster.Caching](https://github.com/bitfaster/BitFaster.Caching)** — best-in-class
   `ConcurrentLru`/`ConcurrentLfu` for *eviction-style* caches (bounded size, hit-rate driven).
   Different problem: a reference table wants *all* rows resident, no eviction.
5. **`ConcurrentDictionary`** — fine for per-key mutation, but there are no consistent snapshots or
   atomic batches; readers can observe half-applied refreshes, and its internals also land on the LOH.

None of these gives *all three* of: O(batch) refresh cost, LOH-free steady state, and consistent
lock-free snapshots — which is why `SnapshotTable` exists.

### What about C++ / native code?

Not recommended, and not built here, deliberately:

- The pain (LOH churn, GC pauses, O(N) copies) comes from **allocation shape**, not from managed
  code being slow. Chunking + copy-on-write + snapshot swap removes the GC pressure while staying
  100% safe C#.
- P/Invoke costs ~1–10 ns per call and, worse, a native table returning managed-visible data means
  marshalling or pinning on every read — that overhead exceeds what a native hash map would save on
  a nanosecond-scale lookup.
- Modern .NET already offers the "native" escape hatches in-language when profiling demands them:
  `NativeMemory.Alloc` for unmanaged buffers of fixed-size structs (zero GC involvement),
  `struct`-of-arrays layouts, `Span<T>`/`Unsafe`, and NativeAOT. If a table of blittable rows ever
  needs to leave the GC heap entirely, that is the path — same performance as C++, no interop seam,
  no second toolchain, no cross-platform build matrix.

## Layout

```
src/DotnetTools.SnapshotCache/           the library (net8.0;net10.0, zero dependencies)
tests/DotnetTools.SnapshotCache.Tests/   xUnit: correctness, fuzz-vs-model, snapshot isolation,
                                         concurrency stress, LOH + performance guardrails (41 tests)
benchmarks/DotnetTools.SnapshotCache.Benchmarks/   BenchmarkDotNet comparisons
```

## Running tests and benchmarks

```bash
dotnet test tests/DotnetTools.SnapshotCache.Tests -c Release

# full benchmark run (slow, accurate):
dotnet run -c Release --project benchmarks/DotnetTools.SnapshotCache.Benchmarks -- --filter '*'

# quick pass:
dotnet run -c Release --project benchmarks/DotnetTools.SnapshotCache.Benchmarks -- --filter '*' --job short
```

The suite covers the three phases of the workload — `InitialLoadBenchmarks` (full load of 1M rows),
`BatchUpdateBenchmarks` (a 5k-change refresh applied to a 1M-row table, the every-30-seconds cost),
and `ReadBenchmarks` (10k random point lookups) — each against `ImmutableArray`, `ImmutableList`,
`ImmutableDictionary`, `Dictionary` rebuild + swap, and `FrozenDictionary` rebuild, with
`[MemoryDiagnoser]` reporting allocations. Results from this environment are in
[`benchmarks/RESULTS.md`](benchmarks/RESULTS.md).

## Guarantees & limits

- Chunk arrays, spine blocks, index shards, directory blocks, and per-batch bookkeeping all stay
  below the 85,000-byte LOH threshold by construction (tested), up to `int.MaxValue` rows.
  Validated with a real 100M-row / 20k-batch run: zero LOH growth, zero Gen2 collections
  (`benchmarks/RESULTS.md`).
- One writer at a time (writers serialize on a lock; readers never block). Designed for the
  single-refresher pattern, not for high-frequency per-key contention.
- Iteration order is insertion order until a remove occurs (swap-remove); treat enumeration as
  unordered, like a dictionary.
- `TableSnapshot` instances pin the chunks/shards of their version; drop them when done so old
  versions can be collected. Holding one snapshot costs only the *delta* vs the current version.
