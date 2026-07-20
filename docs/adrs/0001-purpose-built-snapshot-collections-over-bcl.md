# ADR-0001: Purpose-built snapshot collections instead of BCL / off-the-shelf stores

- **Status**: Accepted
- **Date**: 2026-07-18
- **Related**: `README.md` ("Do you need this…"), RESULTS.md §1–§5

## Context

The target workload is a large in-memory reference table (10⁶–10⁸ rows), refreshed every ~30
seconds with a batch touching a small fraction of rows, read constantly by concurrent consumers
that need internally consistent views. Every existing option fails at least one requirement:

- `ImmutableArray`: O(N) full copy per update, allocated on the LOH every refresh (1.49 GiB per
  batch at 100M rows); no keyed lookup.
- `ImmutableList` / `ImmutableDictionary`: LOH-free but ~10× slower reads and 3.5–4× raw memory.
- `Dictionary` / `FrozenDictionary` rebuild + swap: fastest reads, but O(N) rebuild per refresh
  with LOH-resident internals (FrozenDictionary: ~55 ms + 98 MB per refresh at 1M rows; multi-GiB
  at 100M).
- `ConcurrentDictionary`: per-key mutation, no consistent snapshots or atomic batches, LOH internals.
- FASTER/Garnet, BitFaster.Caching: solve persistence/eviction problems this workload doesn't have.
- C++/native: the pain is allocation shape, not managed-code speed; interop marshalling on a
  nanosecond-scale read path costs more than it saves.

The disease is specifically **LOH churn on a cadence**: multi-GiB of ≥85 KB arrays reallocated
every 30 s fragments the Large Object Heap and forces the full/Gen2 collections that pause every
request.

## Decision

Build two purpose-built types — `ChunkedImmutableList<T>` (ADR-0002) and `SnapshotTable<TKey,
TValue>` (ADR-0003) — that together provide the three properties no alternative combines:

1. O(batch) refresh cost, independent of table size;
2. zero LOH allocation at any size, by construction;
3. wait-free, internally consistent snapshot reads during refreshes.

## Consequences

- (+) Measured at the target scale (100M rows / 20k-change batches): 0.0 MiB LOH growth, ~83 MiB
  Gen0/Gen1 allocation per batch, 80 ms apply under Server GC, readers never block.
- (−) Reads cost ~60–70 ns vs ~16 ns for a plain `Dictionary` (shard-directory hop + chunked-row
  hop). This trade is accepted *only because* the alternatives' read speed comes with O(N) LOH
  churn per refresh; see ADR-0005 for the policy that keeps the read premium measured and bounded.
- (−) One writer at a time (single-refresher pattern), unstable iteration order after removes
  (swap-remove), and a codebase to own — mitigated by fuzz-vs-model tests and CI guardrails.
