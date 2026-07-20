# ADR-0002: Chunked storage behind a two-level spine, all arrays sub-LOH

- **Status**: Accepted
- **Date**: 2026-07-18 (two-level spine and adaptive chunk size superseded the v0.1 flat spine)
- **Related**: `ChunkedImmutableList.cs`, RESULTS.md §4, `SpineSharingTests`

## Context

An immutable list for this workload must (a) never allocate an array ≥ 85,000 bytes at any element
count, (b) make a batch update cost proportional to what it touches, (c) keep reads at array speed
— per ADR-0005, read performance is first-class, which rules out tree structures like
`ImmutableList<T>` (~11× slower indexing). A single flat spine of chunk references breaks (a) at
scale: 100M rows ÷ 4 KB chunks needs ~390k chunk refs ≈ 3 MB — itself a LOH array cloned per batch.

## Decision

Store elements in fixed-size power-of-two chunks reached through a **two-level spine**: top spine →
spine blocks (1024 chunk refs = 8 KB each) → chunks. All three levels stay sub-LOH up to
`int.MaxValue` elements. An index read is three array indexings and a shift/mask — no traversal.

- **Copy-on-write at every level**: `SetItem` copies one chunk + one spine block + the top spine;
  a `Builder` clones each touched chunk/block at most once per batch, tracked in small ownership
  bitsets (never LOH). `ToImmutable()` freezes by clearing ownership, so a reused builder re-clones.
- **Adaptive chunk size**: ~64 KB chunks below 8M rows (dense batches → fewer, larger memcpys),
  ~4 KB above (a sparse 20k batch over 100M rows copies ~65 MB instead of ~880 MB). Tunable per
  instance; a hard cap keeps any chunk below the LOH bar even for large structs.
- **Swap-remove support** (`RemoveLast` + index fix-up in the table) keeps removal O(chunk).

## Consequences

- (+) LOH-free at any size (tested by walking every backing array), O(touched chunks) batches,
  structural sharing makes held snapshots cost only the delta.
- (−) Reads pay two extra indirections vs a contiguous array — acceptable per ADR-0005 but must
  stay measured (`ReadBenchmarks`, `BucketReadBenchmarks`), especially for scan-heavy consumers.
- (−) Scattered wide updates converge on a full copy (a random 1% replace of a 100k-element list
  touches nearly every 512-row chunk) — still sub-LOH, but no copy savings; clustered batches and
  appends are the sweet spot (RESULTS.md §7, §9).
- (−→+) Fixed per-instance overhead (top spine + 8 KB spine block + full-size tail chunk) was
  wasteful for tiny lists — ~11.7 KB/bucket, 1.30 GiB vs 125 MiB at K=100k. Resolved by #7:
  spine blocks grow by doubling (4 → 1024 refs) and published tail chunks are trimmed to the
  element count, bringing the same store to 137.3 MiB (1.10× arrays) with the read path untouched.
