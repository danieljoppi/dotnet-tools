using DotnetTools.SnapshotCache;

namespace DotnetTools.SnapshotCache.Tests;

/// <summary>
/// <see cref="MultiValueSnapshotTable{TKey, TEntity}"/> (issue #8): the packaged shared-key →
/// many-values store. Covers the issue-#6 correctness shapes — snapshot isolation, atomic batches
/// under concurrent readers, hybrid promotion, LOH safety — plus a randomized model check.
/// </summary>
public class MultiValueSnapshotTableTests
{
    private const int Promote = MultiValueSnapshotTable<long, string>.ArrayBucketMaxLength;

    [Fact]
    public void ApplyChanges_AppendReplaceRemove_RoundTrip()
    {
        var table = new MultiValueSnapshotTable<long, string>();
        table.ApplyChanges(
        [
            BucketChange.Append(1L, "a", "b"),
            BucketChange.Append(2L, "x"),
            BucketChange.ReplaceAt(1L, (1, "B")),
            BucketChange.ReplaceBucket(3L, "only"),
        ]);

        Assert.Equal(3, table.KeyCount);
        Assert.Equal(["a", "B"], table.Lookup(1L));
        Assert.Equal(["x"], table.Lookup(2L));
        Assert.Equal(["only"], table.Lookup(3L));
        Assert.Empty(table.Lookup(99L));

        table.ApplyChanges([BucketChange.Remove<long, string>(2L)]);
        Assert.Equal(2, table.KeyCount);
        Assert.False(table.ContainsKey(2L));
        Assert.Throws<KeyNotFoundException>(() =>
            table.ApplyChanges([BucketChange.ReplaceAt(99L, (0, "nope"))]));
    }

    [Fact]
    public void Snapshot_IsIsolatedFromLaterBatches()
    {
        var table = new MultiValueSnapshotTable<long, string>();
        table.ApplyChanges([BucketChange.Append(1L, "v0")]);
        var held = table.GetSnapshot();

        for (int i = 1; i <= 50; i++)
        {
            table.ApplyChanges(
            [
                BucketChange.Append(1L, $"v{i}"),
                BucketChange.ReplaceBucket(2L, $"gen{i}"),
            ]);
        }

        Assert.Equal(["v0"], held.Lookup(1L));
        Assert.Equal(1, held.KeyCount);
        Assert.Equal(51, table.Lookup(1L).Count);
        Assert.Equal(["gen50"], table.Lookup(2L));
    }

    [Fact]
    public void Buckets_PromoteToChunksAtThreshold_AndKeepContents()
    {
        var table = new MultiValueSnapshotTable<long, string>(keyCapacityHint: 4);
        var expected = new List<string>();
        var rng = new Random(5);
        long id = 0;
        while (expected.Count < 3 * Promote)
        {
            var run = Enumerable.Range(0, rng.Next(1, 60)).Select(_ => $"e{id++}").ToArray();
            expected.AddRange(run);
            table.ApplyChanges([BucketChange.Append(7L, run)]);
        }
        Assert.Equal(expected, table.Lookup(7L));

        // Replacements still target the right positions after promotion.
        table.ApplyChanges([BucketChange.ReplaceAt(7L, (0, "first"), (expected.Count - 1, "last"))]);
        var bucket = table.Lookup(7L);
        Assert.Equal("first", bucket[0]);
        Assert.Equal("last", bucket[^1]);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void HotBucketAppends_NeverTouchTheLargeObjectHeap()
    {
        var table = new MultiValueSnapshotTable<long, object>(keyCapacityHint: 64);
        // Seed one bucket well past the LOH bar for a reference array (~10,625 refs).
        table.ApplyChanges([BucketChange.Append(1L, Enumerable.Range(0, 60_000).Select(_ => new object()).ToArray())]);

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long lohBefore = GC.GetGCMemoryInfo(GCKind.FullBlocking).GenerationInfo[3].SizeAfterBytes;

        for (int batch = 0; batch < 20; batch++)
        {
            table.ApplyChanges(
            [
                BucketChange.Append(1L, Enumerable.Range(0, 50).Select(_ => new object()).ToArray()),
                BucketChange.ReplaceAt(1L, (batch * 100, new object())),
            ]);
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long lohAfter = GC.GetGCMemoryInfo(GCKind.FullBlocking).GenerationInfo[3].SizeAfterBytes;
        Assert.True(lohAfter - lohBefore < 85_000,
            $"LOH grew by {lohAfter - lohBefore} bytes during hot-bucket batches");
        Assert.Equal(61_000, table.Lookup(1L).Count);
    }

    [Fact]
    public async Task ConcurrentReaders_NeverSeeAPartialBatch()
    {
        // Each batch rewrites key A's bucket and key B's bucket to the same generation stamp;
        // a consistent snapshot must never observe mixed generations.
        var table = new MultiValueSnapshotTable<int, int>();
        table.ApplyChanges([BucketChange.Append(1, 0), BucketChange.Append(2, 0)]);

        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var snapshot = table.GetSnapshot();
                Assert.Equal(snapshot.Lookup(1)[0], snapshot.Lookup(2)[0]);
            }
        })).ToArray();

        for (int generation = 1; generation <= 2_000; generation++)
        {
            table.ApplyChanges(
            [
                BucketChange.ReplaceBucket(1, generation),
                BucketChange.ReplaceBucket(2, generation),
            ]);
        }
        stop.Cancel();
        await Task.WhenAll(readers);
    }

    [Fact]
    public void RandomizedFuzz_MatchesDictionaryOfListsModel()
    {
        var rng = new Random(1234);
        var model = new Dictionary<long, List<int>>();
        var table = new MultiValueSnapshotTable<long, int>(keyCapacityHint: 32);
        int value = 0;

        for (int round = 0; round < 300; round++)
        {
            var batch = new List<BucketChange<long, int>>();
            int ops = rng.Next(1, 8);
            for (int i = 0; i < ops; i++)
            {
                long key = rng.Next(12);
                switch (rng.Next(4))
                {
                    case 0:
                    {
                        var run = Enumerable.Range(0, rng.Next(1, 20)).Select(_ => value++).ToArray();
                        batch.Add(BucketChange.Append(key, run));
                        (model.TryGetValue(key, out var list) ? list : model[key] = []).AddRange(run);
                        break;
                    }
                    case 1 when model.TryGetValue(key, out var list) && list.Count > 0:
                    {
                        int index = rng.Next(list.Count);
                        batch.Add(BucketChange.ReplaceAt(key, (index, -value)));
                        list[index] = -value++;
                        break;
                    }
                    case 2:
                    {
                        var run = Enumerable.Range(0, rng.Next(0, 10)).Select(_ => value++).ToArray();
                        batch.Add(BucketChange.ReplaceBucket(key, run));
                        model[key] = [.. run];
                        break;
                    }
                    case 3:
                    {
                        batch.Add(BucketChange.Remove<long, int>(key));
                        model.Remove(key);
                        break;
                    }
                }
            }
            table.ApplyChanges(batch);
        }

        Assert.Equal(model.Count, table.KeyCount);
        var snapshot = table.GetSnapshot();
        Assert.Equal(model.Keys.Order().ToArray(), snapshot.Keys.Order().ToArray());
        foreach (var (key, list) in model)
        {
            Assert.Equal(list, snapshot.Lookup(key));
        }
    }

    [Fact]
    public void ManyAppendsToOnePromotedKey_InOneBatch_PublishesOnce()
    {
        // Issue #31, deterministic form: N Appends to one promoted key in one batch fold into a
        // single builder, so the batch pays exactly one chunked publish — while the same appends
        // as N batches pay one each.
        var table = new MultiValueSnapshotTable<long, string>(keyCapacityHint: 4);
        table.Reset([KeyValuePair.Create(7L, (IReadOnlyList<string>)Enumerable.Range(0, Promote + 200).Select(i => $"e{i}").ToArray())]);
        var appends = Enumerable.Range(0, 50).Select(i => BucketChange.Append(7L, $"n{i}")).ToArray();

        table.ApplyChanges(appends);
        Assert.Equal(1, table.PromotedPublishesInLastBatch);

        int publishes = 0;
        foreach (var change in appends)
        {
            table.ApplyChanges([change]);
            publishes += table.PromotedPublishesInLastBatch;
        }
        Assert.Equal(50, publishes);
        Assert.Equal(Promote + 200 + 100, table.Lookup(7L).Count);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void ManyAppendsToOnePromotedKey_InOneBatch_SinglePublish()
    {
        // Issue #31: N Appends to one promoted key in one batch fold into a single builder and
        // publish once. Observable as allocation: the batched path must cost far less than N
        // single-append batches, each of which pays its own ToBuilder → ToImmutable round-trip.
        // (The deterministic form of this assertion is ..._PublishesOnce above; this is the
        // end-to-end allocation guardrail.)
        var expected = Enumerable.Range(0, Promote + 200).Select(i => $"e{i}").ToList();
        var single = MakePromotedTable(expected);
        var batched = MakePromotedTable(expected);
        var appends = Enumerable.Range(0, 50).Select(i => $"n{i}").ToArray();
        expected.AddRange(appends);

        long AppendAndMeasure(MultiValueSnapshotTable<long, string> table, bool oneBatch)
        {
            var changes = appends.Select(a => BucketChange.Append(7L, a)).ToArray();
            long before = GC.GetAllocatedBytesForCurrentThread();
            if (oneBatch)
            {
                table.ApplyChanges(changes);
            }
            else
            {
                foreach (var change in changes)
                {
                    table.ApplyChanges([change]);
                }
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        // Warm both paths once (JIT + dictionary growth) on throwaway tables before measuring.
        AppendAndMeasure(MakePromotedTable(expected), oneBatch: true);
        AppendAndMeasure(MakePromotedTable(expected), oneBatch: false);

        long batchedBytes = AppendAndMeasure(batched, oneBatch: true);
        long singleBytes = AppendAndMeasure(single, oneBatch: false);

        Assert.Equal(expected, batched.Lookup(7L));
        Assert.Equal(expected, single.Lookup(7L));
        Assert.True(batchedBytes * 4 < singleBytes,
            $"50 same-key appends in one batch allocated {batchedBytes} bytes vs {singleBytes} across 50 batches — expected the batched path to publish once, not 50 times");

        static MultiValueSnapshotTable<long, string> MakePromotedTable(IEnumerable<string> seed)
        {
            var table = new MultiValueSnapshotTable<long, string>(keyCapacityHint: 4);
            table.Reset([KeyValuePair.Create(7L, (IReadOnlyList<string>)seed.ToArray())]);
            return table;
        }
    }

    [Fact]
    public void AppendReplaceAppend_SameKey_OneBatch_MatchesModel()
    {
        // The ReplaceAt lands between two Appends to the same promoted key, targeting both the
        // pre-batch region and an element appended earlier in the same batch.
        var model = Enumerable.Range(0, Promote + 10).Select(i => $"e{i}").ToList();
        var table = new MultiValueSnapshotTable<long, string>();
        table.Reset([KeyValuePair.Create(1L, (IReadOnlyList<string>)model.ToArray())]);

        model.AddRange(["a0", "a1"]);
        model[0] = "patched-old";
        model[^1] = "patched-new";
        model.Add("a2");
        table.ApplyChanges(
        [
            BucketChange.Append(1L, "a0", "a1"),
            BucketChange.ReplaceAt(1L, (0, "patched-old"), (model.Count - 2, "patched-new")),
            BucketChange.Append(1L, "a2"),
        ]);

        Assert.Equal(model, table.Lookup(1L));
    }

    [Fact]
    public void AppendThenRemove_SameKey_OneBatch_KeyGone()
    {
        var table = new MultiValueSnapshotTable<long, string>();
        // Both a promoted key (open builder discarded by Remove) and a fresh key created and
        // removed within the same batch.
        table.Reset([KeyValuePair.Create(1L, (IReadOnlyList<string>)Enumerable.Range(0, Promote + 5).Select(i => $"e{i}").ToArray())]);

        table.ApplyChanges(
        [
            BucketChange.Append(1L, "doomed"),
            BucketChange.Append(2L, "also-doomed"),
            BucketChange.Remove<long, string>(1L),
            BucketChange.Remove<long, string>(2L),
        ]);

        Assert.Equal(0, table.KeyCount);
        Assert.False(table.ContainsKey(1L));
        Assert.False(table.ContainsKey(2L));
    }

    [Fact]
    public void RemoveThenAppend_SameKey_OneBatch_KeyReappears()
    {
        var table = new MultiValueSnapshotTable<long, string>();
        table.Reset([KeyValuePair.Create(1L, (IReadOnlyList<string>)Enumerable.Range(0, Promote + 5).Select(i => $"e{i}").ToArray())]);

        table.ApplyChanges(
        [
            BucketChange.Remove<long, string>(1L),
            BucketChange.Append(1L, "reborn"),
        ]);

        Assert.Equal(1, table.KeyCount);
        Assert.Equal(["reborn"], table.Lookup(1L));
    }

    [Fact]
    public void AppendThenReplaceBucket_SameKey_OneBatch_LastChangeWins()
    {
        var table = new MultiValueSnapshotTable<long, string>();
        table.Reset([KeyValuePair.Create(1L, (IReadOnlyList<string>)Enumerable.Range(0, Promote + 5).Select(i => $"e{i}").ToArray())]);

        table.ApplyChanges(
        [
            BucketChange.Append(1L, "discarded"),
            BucketChange.ReplaceBucket(1L, "fresh"),
            BucketChange.Append(1L, "kept"),
        ]);

        Assert.Equal(1, table.KeyCount);
        Assert.Equal(["fresh", "kept"], table.Lookup(1L));
    }

    [Fact]
    public void PromotionMidBatch_ThenMoreAppends_FoldIntoOneBuilder()
    {
        // Grows an array bucket past the threshold and keeps appending in the same batch: the
        // promote path must open a builder that later same-batch changes (including ReplaceAt)
        // keep folding into.
        var model = new List<string>();
        var batch = new List<BucketChange<long, string>>();
        for (int i = 0; model.Count < Promote + 100; i++)
        {
            var run = Enumerable.Range(0, 90).Select(j => $"e{model.Count + j}").ToArray();
            model.AddRange(run);
            batch.Add(BucketChange.Append(9L, run));
        }
        model[0] = "first";
        model[^1] = "last";
        batch.Add(BucketChange.ReplaceAt(9L, (0, "first"), (model.Count - 1, "last")));

        var table = new MultiValueSnapshotTable<long, string>();
        table.ApplyChanges(batch);

        Assert.Equal(model, table.Lookup(9L));
    }

    [Fact]
    public void Reset_ReplacesEverything_AndClearEmpties()
    {
        var table = new MultiValueSnapshotTable<long, string>();
        table.ApplyChanges([BucketChange.Append(1L, "old")]);
        table.Reset(
        [
            KeyValuePair.Create(5L, (IReadOnlyList<string>)["a", "b"]),
            KeyValuePair.Create(6L, (IReadOnlyList<string>)Enumerable.Range(0, Promote + 10).Select(i => $"i{i}").ToArray()),
        ]);
        Assert.Equal(2, table.KeyCount);
        Assert.False(table.ContainsKey(1L));
        Assert.Equal(["a", "b"], table.Lookup(5L));
        Assert.Equal(Promote + 10, table.Lookup(6L).Count);
        table.Clear();
        Assert.Equal(0, table.KeyCount);
    }

    // --- Single-entity Append overload (issue #45): stores the one entity inline with no backing
    // array, the lean path for one-entity refreshes. ---

    [Fact]
    public void SingleEntityAppend_MatchesArrayAppend_IncludingThroughPromotion()
    {
        // Append single entities one at a time — via the inline overload — from empty, through the
        // array→chunked promotion boundary, and past it. Must match a plain list model exactly.
        var model = new List<string>();
        var table = new MultiValueSnapshotTable<long, string>();
        for (int i = 0; i < Promote + 50; i++)
        {
            model.Add($"e{i}");
            table.ApplyChanges([BucketChange.Append(1L, $"e{i}")]); // single-entity overload
        }
        Assert.Equal(model, table.Lookup(1L));

        // Same key, single-entity appends folded with an array append in one batch.
        table.ApplyChanges([BucketChange.Append(1L, "solo"), BucketChange.Append(1L, "arr1", "arr2")]);
        model.AddRange(["solo", "arr1", "arr2"]);
        Assert.Equal(model, table.Lookup(1L));
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void SingleEntityAppend_AllocatesNoPerChangeArray()
    {
        // #45's target: building a batch of one-entity appends must not allocate a backing array
        // per change. Compare the inline overload against wrapping each entity in a one-element
        // array; the array form allocates one small array per change on top of the batch array,
        // so it must allocate strictly (and substantially) more for identical content.
        const int m = 5_000;
        var values = Enumerable.Range(0, m).Select(i => $"v{i}").ToArray();

        long BuildInline()
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var batch = new BucketChange<long, string>[m];
            for (int i = 0; i < m; i++)
            {
                batch[i] = BucketChange.Append((long)i, values[i]); // inline, no array
            }
            GC.KeepAlive(batch);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        long BuildArrayWrapped()
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var batch = new BucketChange<long, string>[m];
            for (int i = 0; i < m; i++)
            {
                batch[i] = BucketChange.Append((long)i, new[] { values[i] }); // one array per change
            }
            GC.KeepAlive(batch);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        BuildInline();       // warm up JIT
        BuildArrayWrapped();

        long inline = BuildInline();
        long wrapped = BuildArrayWrapped();

        Assert.True(inline < wrapped,
            $"Inline single-entity batch allocated {inline} bytes vs array-wrapped {wrapped}; the " +
            "inline overload must not allocate a per-change array.");
    }

    // --- Read-path guardrail (issue #46). ---

    [Fact]
    [Trait("Category", "Performance")]
    public void Lookup_IsAllocationFree_ForArrayAndPromotedBuckets()
    {
        // The #46 read-path guardrail, deterministic form: Lookup + indexing must not allocate,
        // whether the bucket is a flat array or a promoted ChunkedImmutableList. (Wall-clock bands
        // vs ImmutableArray live in BucketReadBenchmarks, reported not asserted — CI timing is too
        // noisy to gate, per ADR-0005.)
        var table = new MultiValueSnapshotTable<long, long>(keyCapacityHint: 8);
        table.Reset(
        [
            KeyValuePair.Create(1L, (IReadOnlyList<long>)Enumerable.Range(0, 4).Select(i => (long)i).ToArray()),
            KeyValuePair.Create(2L, (IReadOnlyList<long>)Enumerable.Range(0, Promote + 500).Select(i => (long)i).ToArray()),
        ]);
        var snapshot = table.GetSnapshot();
        // Warm up any one-time lazy allocation.
        _ = snapshot.Lookup(1L)[0];
        _ = snapshot.Lookup(2L)[Promote];

        long before = GC.GetAllocatedBytesForCurrentThread();
        long sum = 0;
        for (int i = 0; i < 50_000; i++)
        {
            sum += snapshot.Lookup(1L)[i % 4];          // flat-array bucket
            sum += snapshot.Lookup(2L)[i % (Promote + 500)]; // promoted chunked bucket
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated == 0, $"100k bucket lookups+indexing allocated {allocated} bytes; expected 0. (sum={sum})");
    }
}
