// Several performance guardrails measure process-global GC state — the Large Object Heap size
// from GC.GetGCMemoryInfo, which has no per-thread or per-scope variant. Running other tests
// (which build 1M-row tables and 60k-object buckets) in parallel pollutes that reading and makes
// the LOH assertions flake in a full-suite run. The suite is small, so serializing it is the
// clean fix and also gives the concurrency stress tests the cores to themselves.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
