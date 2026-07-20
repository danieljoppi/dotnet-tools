# Architecture Decision Records

Decisions that shaped `DotnetTools.SnapshotCache`, in the standard Context / Decision /
Consequences format. Each record states what was decided, the evidence behind it (benchmarks are
in [`benchmarks/RESULTS.md`](../../benchmarks/RESULTS.md), raw outputs in
[`benchmarks/results/raw/`](../../benchmarks/results/raw/)), and what it costs.

| # | Title | Status |
|---|---|---|
| [0001](0001-purpose-built-snapshot-collections-over-bcl.md) | Purpose-built snapshot collections instead of BCL / off-the-shelf stores | Accepted |
| [0002](0002-chunked-two-level-spine-storage.md) | Chunked storage behind a two-level spine, all arrays sub-LOH | Accepted |
| [0003](0003-sharded-cow-index-and-atomic-snapshot-publication.md) | Sharded copy-on-write key index + atomic snapshot publication | Accepted |
| [0004](0004-secondary-indexes-as-cow-array-buckets.md) | Secondary indexes as copy-on-write hybrid buckets | Accepted (amended: chunked past 1,024 elements, #9) |
| [0005](0005-measurement-policy-loh-memory-reads.md) | Measurement policy: LOH + overall memory always, read performance first-class | Accepted |
| [0006](0006-shared-key-buckets-hybrid-representation.md) | Shared-key one-to-many buckets: hybrid array/chunked representation | Proposed |

Adding a new ADR: copy the section structure of an existing one, number sequentially, add a row
here, and link the measurements that justify the decision.
