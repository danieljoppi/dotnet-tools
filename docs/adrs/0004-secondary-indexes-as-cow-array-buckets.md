# ADR-0004: Secondary indexes as copy-on-write array buckets

- **Status**: Accepted — with a documented limitation, remediation tracked in #9
- **Date**: 2026-07-18
- **Related**: `SecondaryIndex.cs`, RESULTS.md §9 (API gaps), issue #9

## Context

Consumers need reverse lookups (index key → primary keys) maintained atomically with each batch —
e.g. region → customers. Expected cardinality at design time: moderate (buckets up to the low
thousands), changing rarely relative to value updates.

## Decision

Store each index as sharded dictionaries of `TIndexKey → TKey[]` flat array buckets, cloned
per change (add/remove copies the bucket array; a value update whose index key is unchanged
touches nothing). Buckets are resolved against the owning snapshot at query time, so index reads
are consistent with the rows by construction.

Flat arrays were chosen over chunked buckets because, at the designed cardinality, they are the
best *read* representation (contiguous scan, zero overhead — per ADR-0005 reads come first) and
the O(bucket) copy per membership change is a few KB.

## Consequences

- (+) Simple, snapshot-consistent, fast to enumerate; value-only updates are free.
- (−) **Does not scale to large buckets**: beyond ~10,625 8-byte keys a bucket is itself a LOH
  array, copied per entity add/remove — measured as the blocker for using a group index on the
  shared-key workload's hot Zipf buckets (10k–100k+ entities, RESULTS.md §9). The XML docs state
  the envelope ("moderate-cardinality attributes").
- (→) #9 tracks backing large buckets with the chunked representation above a size threshold,
  which would make `SnapshotTable` + group index a complete answer for one-to-many shapes while
  keeping flat arrays (and their read speed) for the moderate-cardinality majority.
