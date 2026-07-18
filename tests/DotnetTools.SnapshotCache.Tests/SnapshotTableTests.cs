using DotnetTools.SnapshotCache;

namespace DotnetTools.SnapshotCache.Tests;

public class SnapshotTableTests
{
    [Fact]
    public void NewTable_IsEmpty()
    {
        var table = new SnapshotTable<long, string>();
        Assert.Equal(0, table.Count);
        Assert.False(table.TryGetValue(1, out _));
    }

    [Fact]
    public void Upsert_InsertsAndReplaces()
    {
        var table = new SnapshotTable<long, string>();
        table.Upsert(1, "a");
        table.Upsert(2, "b");
        table.Upsert(1, "a2");

        Assert.Equal(2, table.Count);
        Assert.Equal("a2", table[1]);
        Assert.Equal("b", table[2]);
    }

    [Fact]
    public void Indexer_MissingKey_Throws()
    {
        var table = new SnapshotTable<long, string>();
        Assert.Throws<KeyNotFoundException>(() => table[42]);
    }

    [Fact]
    public void Remove_ExistingAndMissing()
    {
        var table = new SnapshotTable<long, string>();
        table.Upsert(1, "a");
        Assert.True(table.Remove(1));
        Assert.False(table.Remove(1));
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void Remove_MiddleRow_SwapRemoveKeepsOtherRowsReachable()
    {
        var table = new SnapshotTable<int, int>();
        table.ApplyChanges(Enumerable.Range(0, 100).Select(i => KeyValuePair.Create(i, i * 10)));
        table.Remove(50);

        Assert.Equal(99, table.Count);
        Assert.False(table.ContainsKey(50));
        for (int i = 0; i < 100; i++)
        {
            if (i != 50)
            {
                Assert.Equal(i * 10, table[i]);
            }
        }
    }

    [Fact]
    public void ApplyChanges_BatchIsAtomic_LastWriteWins()
    {
        var table = new SnapshotTable<int, string>();
        table.ApplyChanges(
            upserts:
            [
                KeyValuePair.Create(1, "one"),
                KeyValuePair.Create(2, "two"),
                KeyValuePair.Create(1, "one-final"),
            ],
            removes: [2, 999]);

        Assert.Single(table);
        Assert.Equal("one-final", table[1]);
        Assert.False(table.ContainsKey(2));
    }

    [Fact]
    public void ApplyChanges_RemoveThenReAddInNextBatch()
    {
        var table = new SnapshotTable<int, int>();
        table.ApplyChanges(Enumerable.Range(0, 10).Select(i => KeyValuePair.Create(i, i)));
        table.ApplyChanges(null, [3, 7]);
        table.ApplyChanges([KeyValuePair.Create(3, 33)]);

        Assert.Equal(9, table.Count);
        Assert.Equal(33, table[3]);
        Assert.False(table.ContainsKey(7));
    }

    [Fact]
    public void Snapshot_IsIsolatedFromLaterWrites()
    {
        var table = new SnapshotTable<int, string>();
        table.Upsert(1, "before");
        var snapshot = table.GetSnapshot();

        table.Upsert(1, "after");
        table.Upsert(2, "new");

        Assert.Single(snapshot);
        Assert.True(snapshot.TryGetValue(1, out var value));
        Assert.Equal("before", value);
        Assert.False(snapshot.ContainsKey(2));
        Assert.Equal("after", table[1]);
    }

    [Fact]
    public void Snapshot_EnumerationMatchesContent()
    {
        var table = new SnapshotTable<int, int>(capacityHint: 100_000);
        table.ApplyChanges(Enumerable.Range(0, 5_000).Select(i => KeyValuePair.Create(i, -i)));
        var snapshot = table.GetSnapshot();

        var seen = snapshot.ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal(5_000, seen.Count);
        Assert.All(seen, kv => Assert.Equal(-kv.Key, kv.Value));
    }

    [Fact]
    public void Reset_ReplacesEntireContent_AndDeduplicatesKeys()
    {
        var table = new SnapshotTable<int, string>();
        table.ApplyChanges(Enumerable.Range(0, 100).Select(i => KeyValuePair.Create(i, "old")));

        table.Reset(
        [
            KeyValuePair.Create(1, "x"),
            KeyValuePair.Create(2, "y"),
            KeyValuePair.Create(1, "x-final"),
        ]);

        Assert.Equal(2, table.Count);
        Assert.Equal("x-final", table[1]);
        Assert.Equal("y", table[2]);
        Assert.False(table.ContainsKey(3));
    }

    [Fact]
    public void Clear_EmptiesTable()
    {
        var table = new SnapshotTable<int, int>();
        table.ApplyChanges(Enumerable.Range(0, 1000).Select(i => KeyValuePair.Create(i, i)));
        table.Clear();
        Assert.Empty(table);
        Assert.False(table.ContainsKey(1));
    }

    [Fact]
    public void CustomComparer_IsRespected()
    {
        var table = new SnapshotTable<string, int>(comparer: StringComparer.OrdinalIgnoreCase);
        table.Upsert("Customer", 1);
        Assert.True(table.TryGetValue("CUSTOMER", out int value));
        Assert.Equal(1, value);
        table.Upsert("customer", 2);
        Assert.Single(table);
        Assert.Equal(2, table["Customer"]);
    }

    [Fact]
    public void MillionRows_BatchUpdate_OnlyTouchedKeysChange()
    {
        const int n = 1_000_000;
        var table = new SnapshotTable<long, long>(capacityHint: n);
        table.Reset(Enumerable.Range(0, n).Select(i => KeyValuePair.Create((long)i, (long)i)));
        Assert.Equal(n, table.Count);

        var before = table.GetSnapshot();
        // Simulate the periodic "customer changes" batch: 5k updates, 1k inserts, 1k deletes.
        var upserts = Enumerable.Range(0, 5_000).Select(i => KeyValuePair.Create((long)(i * 100), -1L))
            .Concat(Enumerable.Range(0, 1_000).Select(i => KeyValuePair.Create((long)(n + i), -2L)));
        var removes = Enumerable.Range(0, 1_000).Select(i => (long)(i * 997 + 1));
        table.ApplyChanges(upserts, removes);

        Assert.Equal(n + 1_000 - 1_000, table.Count);
        Assert.Equal(-1L, table[100L]);
        Assert.Equal(-2L, table[n + 5L]);
        Assert.False(table.ContainsKey(1L + 997L));
        // Old snapshot still sees the pre-batch world.
        Assert.Equal(n, before.Count);
        Assert.Equal(100L, before.TryGetValue(100L, out var old) ? old : -999);
    }

    [Fact]
    public void RandomizedFuzz_MatchesDictionaryModel()
    {
        var rng = new Random(987654);
        var model = new Dictionary<int, int>();
        var table = new SnapshotTable<int, int>();

        for (int round = 0; round < 150; round++)
        {
            var upserts = new List<KeyValuePair<int, int>>();
            var removes = new List<int>();
            int ops = rng.Next(1, 60);
            for (int i = 0; i < ops; i++)
            {
                int key = rng.Next(500);
                if (rng.Next(4) == 0)
                {
                    removes.Add(key);
                }
                else
                {
                    upserts.Add(KeyValuePair.Create(key, rng.Next()));
                }
            }
            foreach (var (k, v) in upserts)
            {
                model[k] = v;
            }
            foreach (var k in removes)
            {
                model.Remove(k);
            }
            table.ApplyChanges(upserts, removes);

            Assert.Equal(model.Count, table.Count);
        }

        var snapshot = table.GetSnapshot();
        Assert.Equal(model.Count, snapshot.Count);
        foreach (var (k, v) in model)
        {
            Assert.True(snapshot.TryGetValue(k, out int actual));
            Assert.Equal(v, actual);
        }
        Assert.Equal(model.OrderBy(kv => kv.Key), snapshot.OrderBy(kv => kv.Key));
    }

    [Fact]
    public async Task ConcurrentReaders_AlwaysSeeConsistentSnapshots()
    {
        // Invariant: every batch writes {generation, generation, ...} for all keys, so within one
        // snapshot every value must be identical. A torn read (mixing generations) fails the test.
        const int keys = 2_000;
        var table = new SnapshotTable<int, int>(capacityHint: keys);
        table.Reset(Enumerable.Range(0, keys).Select(i => KeyValuePair.Create(i, 0)));

        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            var rng = new Random(Environment.CurrentManagedThreadId);
            while (!stop.IsCancellationRequested)
            {
                var snapshot = table.GetSnapshot();
                Assert.True(snapshot.TryGetValue(0, out int generation));
                for (int i = 0; i < 50; i++)
                {
                    Assert.True(snapshot.TryGetValue(rng.Next(keys), out int value));
                    Assert.Equal(generation, value);
                }
                Assert.Equal(keys, snapshot.Count);
            }
        })).ToArray();

        var writer = Task.Run(() =>
        {
            for (int generation = 1; generation <= 300; generation++)
            {
                int g = generation;
                table.ApplyChanges(Enumerable.Range(0, keys).Select(i => KeyValuePair.Create(i, g)));
            }
        });

        await writer;
        stop.Cancel();
        await Task.WhenAll(readers);

        Assert.Equal(300, table[123]);
    }

    [Fact]
    public async Task ConcurrentWriters_AreSerialized_NoLostUpdates()
    {
        var table = new SnapshotTable<int, int>();
        var tasks = Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                table.Upsert(w * 10_000 + i, i);
            }
        })).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(8 * 500, table.Count);
        Assert.Equal(499, table[7 * 10_000 + 499]);
    }
}
