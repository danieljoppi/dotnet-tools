# ADR-0005: Measurement policy — LOH + overall memory always, read performance first-class

- **Status**: Accepted
- **Date**: 2026-07-20
- **Related**: `AGENTS.md` (Measurement policy), `BucketLohStudy.cs`, `MemoryProfile.cs`,
  `LargeScaleValidation.cs`, RESULTS.md throughout

## Context

This library's value proposition is a *memory-behavior* guarantee, and its main ongoing risk is a
quiet regression on the read path: the structures deliberately pay indirections (chunks, spine,
shard directory) to buy LOH freedom, so every structural change can shift the read/write/memory
balance. Two measurement pitfalls shaped this policy:

1. **BenchmarkDotNet cannot report LOH occupancy.** `[MemoryDiagnoser]` gives allocated bytes and
   Gen0/1/2 *counts* — a structure can deposit hundreds of MiB of dead LOH arrays while showing
   `Gen2 = 0` (measured: `ImmArray_AddRange` at +251 MiB uncompacted LOH with zero Gen2 collects,
   RESULTS.md §9).
2. **Un-forced GC hides the steady state.** The LOH is not compacted by default; the honest
   number is the *uncompacted* LOH after warm batches (what a service actually carries), alongside
   the after-forced-Gen2+compact number (live bytes).

## Decision

Every performance-relevant change is checked on **three axes, all of them, in this order of
non-negotiability**:

1. **LOH** — zero growth on the structures' own allocations, measured by console harnesses
   (`--bucket-loh`, `--largescale`, `--memory-profile`) using forced compacting GC deltas and
   `GC.GetGCMemoryInfo` generation 3, reporting both uncompacted and compacted sizes. CI runs the
   `Category=Performance` LOH guardrail tests on every PR.
2. **Overall memory** — steady-state resident heap, per-batch allocated bytes, and
   retained-by-held-snapshot cost. Trading LOH for a multiple of small-object heap is a regression
   (measured example: chunked per-bucket overhead at K=100k, RESULTS.md §9).
3. **Read performance — first-class, not an afterthought.** Reads are the hot path the cache
   serves continuously; batch-side or memory-side wins must never be bought with an unmeasured
   read regression. Any change touching chunk geometry, spine layout, shard probing, comparers, or
   bucket representations runs the read benchmarks (`ReadBenchmarks`, `BucketReadBenchmarks`)
   before/after, covering point lookups, positional indexing, and full-bucket scans.

Reporting rules: ShortRun timings on shared VMs drift ~2× — relative ordering and the
allocation/GC/LOH columns are the publishable signals; every RESULTS.md claim links its raw
output under `benchmarks/results/raw/`; numbers where the library loses are reported with the
same prominence as the wins.

## Consequences

- (+) Regressions on any of the three axes are visible before merge, and the docs stay credible
  (the honest-costs sections exist because of this policy).
- (−) More harness surface to maintain (BenchmarkDotNet + console studies), and benchmarks using
  `[IterationSetup]` state restoration accept `InvocationCount=1` timing noise in exchange for
  clean allocation columns — documented where it applies.
