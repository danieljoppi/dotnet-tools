# ADR-0003: Sharded copy-on-write key index + atomic snapshot publication

- **Status**: Accepted
- **Date**: 2026-07-18 (two-level shard directory added when scaling to 100M rows)
- **Related**: `SnapshotTable.cs`, `ShardMap.cs`, RESULTS.md §1–§4

## Context

`SnapshotTable` needs a key → row-index map that (a) stays off the LOH at hundreds of millions of
keys, (b) supports copy-on-write batches costing O(touched keys), (c) reads with one volatile load
and no locks. A single hash map fails (a); per-key persistent maps fail read speed.

## Decision

- **Shard the index** into many small `ShardMap`s (~256 entries target, compact open addressing),
  selected by Fibonacci-hashed key bits, reached through a **two-level directory** (8 KB blocks of
  1024 shard refs, up to 512k shards). Shards and directory blocks are cloned copy-on-write, at
  most once per batch, tracked in bitsets — the same pattern as ADR-0002, so nothing the writer
  touches is ever LOH-sized.
- **Publish atomically**: a snapshot is one immutable object (rows + directory + secondary
  indexes). `ApplyChanges` builds the next snapshot behind a single writer lock and publishes it
  with one `Volatile.Write`; readers do one `Volatile.Read`. Readers never lock, never see a
  half-applied batch, and a held `TableSnapshot` stays internally consistent forever.
- **In-place value updates skip the index** (only inserts/removes touch shards), and **removes use
  swap-remove** so the row store stays dense.
- **Full reloads** (`Reset` / `ResetParallel`) rebuild fresh structures and publish the same way;
  `ResetParallel` partitions shard ownership across cores (requires unique keys, verified).

## Consequences

- (+) O(batch) refresh with zero LOH at 100M rows (RESULTS.md §4); wait-free reads at ~60–70 ns;
  concurrent readers sustained ~2.5M lookups/s during refreshes.
- (−) The shard-directory hop is most of the read premium vs a flat `Dictionary` — bounded and
  tracked per ADR-0005.
- (−) Writers serialize; this is a deliberate non-goal (single-refresher pattern).
- (−) Index shards dominate steady-state footprint (~2.26× raw at 100M) — acceptable; a denser
  shard layout is a known possible follow-up if memory becomes binding.
