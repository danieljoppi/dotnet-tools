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
}
