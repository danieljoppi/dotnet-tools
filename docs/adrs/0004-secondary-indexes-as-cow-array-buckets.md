# ADR-0004: Secondary indexes as copy-on-write hybrid buckets

- **Status**: Accepted — amended 2026-07-20: large buckets promote to chunked lists (issue #9)
- **Date**: 2026-07-18
- **Related**: `SecondaryIndex.cs`, RESULTS.md §9, issue #9, ADR-0006

## Context

Consumers need reverse lookups (index key → primary keys) maintained atomically with each batch —
e.g. region → customers. Expected cardinality at design time: moderate (buckets up to the low
thousands), changing rarely relative to value updates.

## Decision

Store each index as sharded dictionaries of `TIndexKey → bucket`, cloned copy-on-write per
change; a value update whose index key is unchanged touches nothing. Buckets are resolved
against the owning snapshot at query time, so index reads are consistent with the rows by
construction.

Buckets are **hybrid** (the ADR-0006 pattern, applied here since issue #9): a flat `TKey[]` up
to 1,024 elements — the best read representation (contiguous scan, zero overhead — per ADR-0005
reads come first) with a cheap ≤8 KB whole-array copy per membership change — promoted to a
`ChunkedImmutableList<TKey>` beyond that, where each add/remove copies one chunk + spine
(swap-remove semantics, bucket order unspecified) instead of the whole bucket.

## Consequences

- (+) Snapshot-consistent, fast to enumerate at moderate cardinality; value-only updates free.
- (+) Scales to hot one-to-many groups: a 100k-member bucket appends at O(chunk) with zero LOH
  (originally each add copied an ~800 KB LOH array). Guardrail test:
  `HotBucketAppends_DoNotTouchTheLargeObjectHeap`; group-scan read cost measured in RESULTS.md §9.
- (−) Promoted buckets read through the chunked indirections and removes pay an O(bucket)
  equality scan (compare-only, no allocation) to locate the key — acceptable for the
  append-dominated one-to-many shape; a per-bucket position map would be the next step if
  remove-heavy indexes ever appear.
- (−) Buckets never demote after shrinking below the threshold (avoids ping-pong; a shrunken
  chunked bucket is small and harmless).
