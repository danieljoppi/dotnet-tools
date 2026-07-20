# AGENTS.md

Instructions for AI coding agents (and new humans) working in this repository.

## What this repo is

`DotnetTools.SnapshotCache` — LOH-free snapshot collections for large in-memory table caches:

- **`ChunkedImmutableList<T>`** (`src/DotnetTools.SnapshotCache/ChunkedImmutableList.cs`):
  an immutable list stored as sub-LOH chunks behind a two-level spine; updates copy only touched
  chunks (structural sharing), reads are three array indexings.
- **`SnapshotTable<TKey, TValue>`** (`src/DotnetTools.SnapshotCache/SnapshotTable.cs`):
  a keyed table with wait-free snapshot reads, atomic `ApplyChanges` batches costing O(batch)
  not O(table), a sharded copy-on-write key index, and optional secondary indexes.

The reason the library exists is a single guarantee: **nothing in the structure ever allocates on
the Large Object Heap (arrays ≥ 85,000 bytes), at any table size.** Design rationale lives in
`README.md` and `docs/adrs/`.

## Layout

```
src/DotnetTools.SnapshotCache/            the library (net10.0, zero dependencies)
tests/DotnetTools.SnapshotCache.Tests/    xUnit: correctness, fuzz-vs-model, concurrency,
                                          Category=Performance guardrails (run by CI)
benchmarks/DotnetTools.SnapshotCache.Benchmarks/  BenchmarkDotNet classes + console harnesses
benchmarks/RESULTS.md                     the measured story; every claim links raw output
benchmarks/results/raw/                   committed BenchmarkDotNet reports + harness logs
docs/adrs/                                architecture decision records
.github/workflows/ci.yml                  unit tests, performance tests, ShortRun benchmark
```

## Commands

```bash
dotnet build -c Release

# unit tests (what CI runs first)
dotnet test tests/DotnetTools.SnapshotCache.Tests -c Release --filter "Category!=Performance"

# performance guardrails (zero-alloc reads, O(batch) allocation, no LOH growth)
dotnet test tests/DotnetTools.SnapshotCache.Tests -c Release --filter "Category=Performance"

# quick benchmark pass (what produced RESULTS.md numbers)
dotnet run -c Release --project benchmarks/DotnetTools.SnapshotCache.Benchmarks -- --filter '*' --job short --memory

# console harnesses (LOH studies and large-scale validation; see Program.cs for all flags)
dotnet run -c Release --project benchmarks/DotnetTools.SnapshotCache.Benchmarks -- --bucket-loh 1000000 10000 zipf 10
dotnet run -c Release --project benchmarks/DotnetTools.SnapshotCache.Benchmarks -- --largescale 100000000 20000 10
```

## Measurement policy — what every performance-relevant change must check

Three axes, in this order of non-negotiability, all three always:

1. **LOH**: zero growth on the structure's own allocations, verified with the console harnesses
   (forced Gen2 + compact deltas), not just BenchmarkDotNet Gen2 counts.
2. **Overall memory**: steady-state resident heap and retained-by-snapshot cost (see §5/§9 of
   RESULTS.md) — a fix that trades LOH for 10× small-object heap is not a fix.
3. **Read performance**: reads are the hot path this library serves all day; batch-side wins must
   never be bought with an unmeasured read regression. Point lookups (`ReadBenchmarks`), bucket
   indexing/scans, and snapshot reads count. If a change touches the read path (chunk geometry,
   spine layout, shard probing, comparers), run the read benchmarks before and after.

## Invariants — do not break these

1. **No backing array may reach 85,000 bytes.** Chunks, spine blocks, shards, directory blocks,
   and all copy-on-write bookkeeping must stay sub-LOH at any element count. Tests
   `ChunkArrays_StayBelowLohThreshold` and `SkewedAppend_NoBackingArrayEverReachesLohThreshold`
   enforce this; `BatchUpdate_DoesNotAllocateOnLargeObjectHeap` checks the table end-to-end.
2. **Published snapshots are frozen.** Anything reachable from a `ToImmutable()`d list or a
   published `TableSnapshot` must never be mutated afterwards — builders re-clone on next write
   (ownership bitsets are cleared on publish). Readers rely on this for lock-free consistency.
3. **Batches are atomic.** Readers see the whole `ApplyChanges` batch or none of it — one volatile
   snapshot swap, never intermediate state. Concurrency tests assert this; keep them green.
4. **Batch cost is O(touched), not O(N).** Don't add per-batch work that scales with table size.
5. **Writers serialize on `_writeLock`; readers never take locks.**

## Conventions

- C# with file-scoped namespaces, `sealed` classes, records for row/entity types, expression-bodied
  members where natural. Match the existing XML-doc style: docs explain *why the shape exists*
  (cost model, LOH behavior), not just what a method does.
- Tests: xUnit, one behavior per test, fuzz-vs-reference-model for data structures. The test
  project has `InternalsVisibleTo`, so structural assertions (e.g. `UnsafeBlocks` chunk sharing)
  are fine. Performance guardrails go in `PerformanceTests.cs` with `[Trait("Category", "Performance")]`.
- Benchmarks: `[MemoryDiagnoser]` on every class; baselines marked; mid-width row/entity records,
  no product-specific naming. Shared fixtures for the bucket workloads live in `BucketWorkload.cs`.
- **BenchmarkDotNet cannot report LOH occupancy.** LOH size deltas come from console harnesses
  (`BucketLohStudy`, `LargeScaleValidation`, `MemoryProfile`) using forced compacting GCs and
  `GC.GetGCMemoryInfo` generation 3 — see ADR-0005 before changing the methodology.
- Benchmarks that must restore state between invocations use `[IterationSetup]` (forces
  `InvocationCount=1`; timing gets noisy, alloc columns stay reliable — this is a known,
  documented trade-off).

## Workflow for benchmark changes

1. Add/extend the BenchmarkDotNet class or harness; smoke it with `--job dry` or small parameters.
2. Run the real pass (`--job short --memory`); copy the `-report-github.md` and `-report.csv`
   from `BenchmarkDotNet.Artifacts/results/` into `benchmarks/results/raw/`, and console-harness
   logs as `.txt` there too.
3. Update `benchmarks/RESULTS.md` — keep its voice: honest about where the library *loses*,
   every number traceable to a raw file, LOH columns called out.
4. Timings on shared CI/cloud VMs drift up to ~2×; relative ordering and allocation/GC/LOH columns
   are the stable signals. Never present a single noisy timing as a conclusion.

## Context for ongoing work

- Issue #6 (shared-key → many-values workloads) is measured and documented in RESULTS.md §9–§11.
- Open follow-ups from those findings: #7 (compact single-chunk representation), #8
  (`MultiValueSnapshotTable` helper), #9 (chunked secondary-index buckets), #10 (`Builder.AddRange`,
  public target-bytes sizing), #11 (rekeyed `ApplyChanges` profiling), #12 (bench follow-ups:
  CI LOH guardrail, Server GC + p95 study, timing precision, charts).
- Bucket *read* performance (chunked indexing/enumeration vs contiguous arrays) is measured in
  `BucketReadBenchmarks` — keep it in the loop whenever bucket representations change, and use it
  before tightening the §9 recommendation thresholds.
