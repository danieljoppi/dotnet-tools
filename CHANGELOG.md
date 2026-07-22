# Changelog

All notable changes to **DotnetTools.SnapshotCache** are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project aims to
follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **`MultiValueSnapshotTable<TKey, TEntity>`** — a packaged shared-key → many-values (one-to-many
  bucket) store. Hybrid buckets: a flat array while small, promoted to `ChunkedImmutableList` past
  ~1,024 entities, so hot buckets append at O(chunk) cost and no bucket array ever reaches the LOH.
  Atomic `ApplyChanges` batches (`BucketChange.Append` / `ReplaceAt` / `ReplaceBucket` / `Remove`),
  wait-free `Lookup`.
- **`ChunkedImmutableList<T>` query/scan API**: `Chunks` (per-chunk `ReadOnlySpan<T>` enumeration,
  making full scans array-speed), `CopyTo(Span<T>)`, fast `ToArray()`, `IndexOf`/`Contains`,
  `CreateRange(ReadOnlySpan<T>)`, list-level `AddRange`, and `Builder.AddRange` overloads. Debugger
  display/proxy on the list and builder.
- **Secondary-index hybrid buckets** — index buckets promote to `ChunkedImmutableList` past 1,024
  members, so `SnapshotTable` + a group index scales to 100k+ member groups with zero LOH growth.
- **`SnapshotChanged` delivered outside the write lock** (ordered drain), plus `TryRemove(out value)`
  and a strictly increasing snapshot `Version`.
- **`CreateIndex` on a populated table** — a secondary index may now be registered after rows are
  loaded; it is backfilled by a one-time O(rows) scan and published atomically (existing rows never
  move). The handle is queryable only on snapshots taken after the call. Replaces the previous
  "must register before load" restriction.
- **`SnapshotTable.ResetParallel`**, public `EmptyWithTargetBytes` chunk sizing.
- Benchmark coverage for shared-key buckets, large refreshes, bucket reads, and a `--bucket-loh`
  LOH/endurance console study (p50/p95, workstation + Server GC); charts and results in
  `benchmarks/RESULTS.md`.
- **Cold-load footgun guardrail for `MultiValueSnapshotTable` (issue #43)** — `ApplyChanges`/`Reset`
  XML docs now spell out that a batch costs O(touched shard occupancy), not O(1) per key, and that
  cold load must use `Reset` (or one batched `ApplyChanges`), never a per-key `ApplyChanges` loop
  (which is O(N²) — the production "never Ready" failure in issue #42). A `Category=Performance`
  test asserts the per-key path allocates >20× `Reset`, and `ColdLoadBenchmarks` measures all three
  population paths across N ∈ {10k, 100k, 1M}. README and `RESULTS.md §16` updated.
- **Single-entity `BucketChange.Append(key, entity)`** — holds the one entity inline instead of
  allocating a one-element `TEntity[]` per change, cutting the per-change allocation on the
  incremental-refresh path. Combined with streaming changes as a lazy `IEnumerable` (rather than a
  materialized `BucketChange[]`), the batch input no longer contributes to the LOH: array-wrapped
  14.21 MB → inline 11.16 MB → lazy stream 7.34 MB at 100k one-entity appends (`RESULTS.md §17`,
  issue #45).

### Changed

- **Compact `ChunkedImmutableList` representation** — spine blocks grow by doubling and published
  tail chunks are trimmed to the exact element count, cutting a 100k-small-bucket store from
  ~1.30 GiB to ~137 MiB (1.1× a flat-array store) with the read path untouched.
- **Comparer devirtualization** — value-type keys with the default comparer inline hash/equality
  instead of interface-dispatching, cutting the 10M rekeyed batch from ~220 ms to ~119 ms.
- Reference-free chunk allocations skip the CLR zero-fill (`GC.AllocateUninitializedArray`); no
  allocation change, a small time saving that scales with data volume.

### Documented

- **Production validation of `MultiValueSnapshotTable` captured in ADR-0007** (issue #42): the
  cold-load rule (#43), the lean-input decision (#45), and a measured negative result — a parallel
  `ResetParallel` cold-loader was 1.19×–1.39× *slower* than sequential `Reset` (memory-bandwidth
  bound) and was not shipped (#46). A `Lookup` zero-allocation guardrail covers the read path for
  both flat-array and promoted chunked buckets. Benchmarks in RESULTS.md §16–§18.

### Fixed

- **Byte-aware bucket promotion (`MultiValueSnapshotTable`, issue #44)** — the flat-array →
  chunked promotion cap is now `min(1,024 elements, 84,000 bytes / sizeof(TEntity))`, so a flat
  `TEntity[]` bucket can no longer reach the Large Object Heap for wide **value-type** entities
  (previously a 1,024-element bucket of a ~1 KB struct built a 1.1 MB LOH array). No-op for
  reference entities and narrow structs, which keep the 1,024-element ceiling. (Raising the cap for
  small elements to reclaim per-chunk overhead remains a separate, measurement-gated change.)
- Full-suite test flake: LOH guardrails read a process-global counter that parallel test
  allocation polluted — test parallelization is now disabled so the reading is clean.

## [0.2.0]

- Compact open-addressing index shards (~22 B/entry vs ~39 B for `Dictionary<TKey,int>`), parallel
  load, two-level spine and shard directory — validated LOH-free at 100,000,000 rows with 20,000
  changes per 30-second cycle (0.0 MiB LOH growth over the run).

## [0.1.0]

- Initial `ChunkedImmutableList<T>` and `SnapshotTable<TKey, TValue>`: LOH-free chunked storage,
  wait-free snapshot reads, atomic copy-on-write batch refresh, secondary indexes, change
  notifications.

[Unreleased]: https://github.com/danieljoppi/dotnet-tools/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/danieljoppi/dotnet-tools/releases/tag/v0.2.0
[0.1.0]: https://github.com/danieljoppi/dotnet-tools/releases/tag/v0.1.0
