# ADR-0007: Production validation of `MultiValueSnapshotTable` — cold-load and batch-input allocation

- **Status**: Accepted — implemented across issues #43 (P0), #45, #46 (sub-issues of #42)
- **Date**: 2026-07-21
- **Related**: issue #42 (production validation), RESULTS.md §16–§18, ADR-0005 (measurement policy),
  ADR-0006 (the hybrid bucket representation being validated), issue #24 (unique-key parallel-load
  finding this mirrors)

## Context

`MultiValueSnapshotTable` (ADR-0006) was put into a production tax-calculation content cache. It
surfaced allocation behaviour that the original benchmarks — which measured warm incremental
batches — never exercised:

- **Cold load was O(N²).** The table was populated by calling `ApplyChanges` **once per key**.
  Each call clones the shard directory and copy-on-writes each shard it touches, so looping it over
  N keys re-copies structures that already hold the first N−1 keys. At production scale this
  allocated on the order of thousands of GB and the process **never reached Ready** — a liveness
  failure, not a slowdown. (RESULTS.md §16: the per-key loop allocates 145× `Reset` at 10k keys and
  276× at 20k — the ratio itself grows with N.)
- **The batch input was the next-largest LOH adder.** `ApplyChanges` only enumerates its argument,
  so the LOH came from the caller side: the per-change one-element `TEntity[]` that
  `Append(key, new[]{entity})` allocated, plus the materialized `BucketChange[]` for large batches.
- **A parallel cold-loader was hypothesised** to speed initial load by building shards across cores.

## Decision

1. **`Reset` is the cold-load and full-refresh path; `ApplyChanges` is for deltas only.** `Reset`
   builds every shard once in O(N) with no per-key copy-on-write. This is enforced by documentation
   (XML remarks on `ApplyChanges`/`Reset`, README) **and** a deterministic `Category=Performance`
   guardrail (`ColdLoadFootgunTests`) that fails the build if a per-key loop stops being ≥25×
   `Reset` in allocation — the failure mode was "never Ready," so a BenchmarkDotNet report alone is
   insufficient; the gate must be a hard CI assertion.

2. **Keep the batch input lean.** A single-entity `BucketChange.Append(key, entity)` overload holds
   the entity inline (no per-change array); `ApplyChanges` takes `IEnumerable`, so changes stream
   lazily instead of materializing a `BucketChange[]`. Combined, the leanest input allocates ~48%
   less than the pre-#45 array-wrapped batch, none of it on the LOH (RESULTS.md §17).

3. **No parallel cold-loader.** `ResetParallel` was implemented and measured; it was 1.19×–1.39×
   **slower** than sequential `Reset` at 1M entities / 10k buckets. Bucket cold load is
   memory-bandwidth-bound (cheap array/chunk copies over a shared bus), so extra cores contend
   rather than help, and a dominant Zipf bucket cannot be split across workers. Per ADR-0005 (don't
   ship unproven perf changes) it was reverted; the negative result is recorded in RESULTS.md §18.
   This mirrors the unique-key finding in issue #24.

The single-entity struct grows `BucketChange` by one field (the inline entity), so array-append
batches carry a slightly larger element; this is accepted because the production pain was
single-entity-heavy and the alternative (a per-change one-element array) is strictly worse there.

## Consequences

- (+) The O(N²) liveness footgun is impossible to hit silently: docs steer to `Reset` and CI fails
  on the quadratic shape.
- (+) The incremental-refresh path — the steady-state workload — allocates measurably less and stays
  off the LOH, on both the per-change and batch-array axes.
- (+) A measured negative result (`ResetParallel`) is documented so it is not re-attempted blindly.
- (−) One more overload and one wider struct field to maintain; `BucketChange`'s inline-vs-array
  append is a branch in the apply path (covered by fuzz-vs-model and a dedicated correctness test).
- (+) The promotion threshold is now **byte-aware** (issue #44): the flat-array cap is
  `min(1,024 elements, 84,000 bytes / sizeof(TEntity))`, so a flat `TEntity[]` bucket can no longer
  reach the LOH even for wide value-type entities (a 1 KB struct promotes at ~82 elements instead of
  building a 1.1 MB array at 1,024). Byte-awareness only *tightens* the cap; it is a no-op for
  reference entities and narrow structs, which keep the 1,024 ceiling.
- (open, issue #44) *Raising* the cap for small (reference) elements toward the LOH-safe ~10,000 to
  reclaim per-`ChunkedImmutableList` overhead (the +11 GiB in the production A/B) is still deferred:
  it trades memory for per-append copy cost and needs the production per-shared-key bucket-size
  distribution to size, per ADR-0005. This ADR does not decide it.
