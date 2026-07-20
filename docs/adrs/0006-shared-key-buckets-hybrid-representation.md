# ADR-0006: Shared-key one-to-many buckets — hybrid array/chunked representation

- **Status**: Accepted — implemented as `MultiValueSnapshotTable<TKey, TEntity>` (issue #8), with
  the same hybrid applied to secondary-index buckets (issue #9)
- **Date**: 2026-07-20
- **Related**: issue #6 measurements (RESULTS.md §9–§10, `bucket-loh-*.txt`), issues #7, #8, #9

## Context

The shared-key → many-values shape (one key → a bucket of entities, Zipf-skewed sizes) was
measured across four representations (issue #6). The results split cleanly by bucket size:

- **Small buckets (≲2k entities)**: flat arrays win every column — batch time, allocation,
  memory, and reads (contiguous scans). LOH is structurally out of reach.
- **Large buckets (≥ ~10,625 references ≈ 85 KB)**: every full-copy update is a LOH allocation.
  Over 10 warm batches: `ImmutableArray.AddRange` +21.5 MiB (1M entities) / +251 MiB (10M) of
  uncompacted LOH; `List` → publish-array is *worse* (two LOH copies per hot bucket); chunked
  buckets and the rekeyed `SnapshotTable`: exactly 0.0 MiB at both scales.
- **Chunked everywhere is not free**: ~8–12 KB fixed overhead per `ChunkedImmutableList` instance
  put the K=100k chunked store at 1.30 GiB vs 125 MiB for arrays, and chunked reads/scans carry
  the indirection premium (ADR-0005 makes that a first-class concern).

No single representation wins both regimes, and real populations contain both at once.

## Decision

Adopt the hybrid as the recommended (and eventually packaged, #8) design for one-to-many stores:

- **Flat array** per bucket below a size threshold (default ~2k entities) — best reads, zero
  overhead, sub-LOH by construction.
- **`ChunkedImmutableList`** per bucket above the threshold — zero LOH at any size, O(touched
  chunks) warm batches, cheap held snapshots.
- Threshold crossing converts once, on the batch that grows past it; conversion cost is one
  final full copy (sub-LOH from then on).
- When atomic cross-key batches / consistent multi-bucket snapshots are required, the rekeyed
  `SnapshotTable` `(sharedKey, uniqueKey) → entity` is the fallback, accepting its higher batch
  cost; group *enumeration* additionally needs #9.

The exact threshold is tuning, not architecture: it must be justified by the read-side numbers
(`BucketReadBenchmarks`) as well as the batch/LOH numbers, per ADR-0005.

## Consequences

- (+) Each regime gets its best structure; the LOH guarantee holds exactly where LOH is reachable;
  read cost is paid only where the alternative is LOH churn.
- (−) Two representations behind one API (branch per access, conversion logic to test) — the
  price of packaging this once in #8 instead of every consumer hand-rolling it.
- (+) #7 (compact representation: trimmed tail chunks, grow-by-doubling spine blocks) has landed:
  chunked-everywhere now costs ~1.1× arrays at rest, so the threshold is driven by the remaining
  read premium (~2× indexing on small buckets) and per-batch allocation, no longer by resident
  memory. A lower threshold — or none, where scans dominate reads — is now defensible.
