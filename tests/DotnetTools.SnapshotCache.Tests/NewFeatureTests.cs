using DotnetTools.SnapshotCache;

namespace DotnetTools.SnapshotCache.Tests;

public class ShardMapTests
{
    [Fact]
    public void RandomizedFuzz_MatchesDictionaryModel()
    {
        var rng = new Random(4242);
        var map = new ShardMap<long>(EqualityComparer<long>.Default);
        var model = new Dictionary<long, int>();

        for (int i = 0; i < 50_000; i++)
        {
            long key = rng.Next(2_000);
            switch (rng.Next(3))
            {
                case 0 or 1:
                    int value = rng.Next();
                    map.Set(key, value);
                    model[key] = value;
                    break;
                case 2:
                    Assert.Equal(model.Remove(key), map.Remove(key));
                    break;
            }
            if (i % 1000 == 0)
            {
                Assert.Equal(model.Count, map.Count);
            }
        }

        Assert.Equal(model.Count, map.Count);
        foreach (var (k, v) in model)
        {
            Assert.True(map.TryGetValue(k, out int actual));
            Assert.Equal(v, actual);
        }
        for (long k = 2_000; k < 2_100; k++)
        {
            Assert.False(map.TryGetValue(k, out _));
        }
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        var map = new ShardMap<int>(EqualityComparer<int>.Default);
        map.Set(1, 100);
        var clone = map.Clone();
        clone.Set(1, 200);
        clone.Set(2, 300);
        map.Remove(1);

        Assert.False(map.TryGetValue(1, out _));
        Assert.True(clone.TryGetValue(1, out int v1));
        Assert.Equal(200, v1);
        Assert.True(clone.TryGetValue(2, out int v2));
        Assert.Equal(300, v2);
        Assert.False(map.TryGetValue(2, out _));
    }

    [Fact]
    public void TombstoneChurn_DoesNotDegradeOrLeak()
    {
        // Repeated add/remove at the same size forces tombstone accumulation → rehash must purge.
        var map = new ShardMap<int>(EqualityComparer<int>.Default);
        for (int round = 0; round < 200; round++)
        {
            for (int i = 0; i < 100; i++)
            {
                map.Set(round * 100 + i, i);
            }
            for (int i = 0; i < 100; i++)
            {
                Assert.True(map.Remove(round * 100 + i));
            }
        }
        Assert.Equal(0, map.Count);
        map.Set(7, 7);
        Assert.True(map.TryGetValue(7, out int v));
        Assert.Equal(7, v);
    }
}

public class SecondaryIndexTests
{
    private record Customer(string Name, string Region, int Tier);

    private static (SnapshotTable<long, Customer>, TableIndex<long, Customer, string>, TableIndex<long, Customer, int>) Build()
    {
        var table = new SnapshotTable<long, Customer>();
        var byRegion = table.CreateIndex((_, c) => c.Region, StringComparer.OrdinalIgnoreCase);
        var byTier = table.CreateIndex((_, c) => c.Tier);
        return (table, byRegion, byTier);
    }

    [Fact]
    public void Index_TracksInsertsUpdatesAndRemoves()
    {
        var (table, byRegion, byTier) = Build();
        table.ApplyChanges(
        [
            KeyValuePair.Create(1L, new Customer("a", "BR", 1)),
            KeyValuePair.Create(2L, new Customer("b", "BR", 2)),
            KeyValuePair.Create(3L, new Customer("c", "US", 1)),
        ]);

        var snap = table.GetSnapshot();
        Assert.Equal([1L, 2L], snap.Lookup(byRegion, "br").Order());
        Assert.Equal([3L], snap.Lookup(byRegion, "US"));
        Assert.Equal([1L, 3L], snap.Lookup(byTier, 1).Order());

        // Move customer 1 from BR to US; change tier 2→3; remove customer 3.
        table.ApplyChanges(
            [KeyValuePair.Create(1L, new Customer("a", "US", 1)), KeyValuePair.Create(2L, new Customer("b", "BR", 3))],
            [3L]);

        snap = table.GetSnapshot();
        Assert.Equal([2L], snap.Lookup(byRegion, "BR"));
        Assert.Equal([1L], snap.Lookup(byRegion, "US"));
        Assert.Empty(snap.Lookup(byTier, 2));
        Assert.Equal([2L], snap.Lookup(byTier, 3));
        Assert.Equal([1L], snap.Lookup(byTier, 1));
    }

    [Fact]
    public void Index_ValueUpdateWithSameIndexKey_DoesNotChurnBuckets()
    {
        var (table, byRegion, _) = Build();
        table.Upsert(1, new Customer("a", "BR", 1));
        var before = table.GetSnapshot().Lookup(byRegion, "BR");
        table.Upsert(1, new Customer("a-renamed", "BR", 9));
        var after = table.GetSnapshot().Lookup(byRegion, "BR");
        Assert.Same(before, after); // bucket array untouched → structurally shared
    }

    [Fact]
    public void Index_SnapshotIsolation()
    {
        var (table, byRegion, _) = Build();
        table.Upsert(1, new Customer("a", "BR", 1));
        var old = table.GetSnapshot();
        table.Upsert(1, new Customer("a", "US", 1));

        Assert.Equal([1L], old.Lookup(byRegion, "BR"));
        Assert.Empty(old.Lookup(byRegion, "US"));
        Assert.Equal([1L], table.GetSnapshot().Lookup(byRegion, "US"));
    }

    [Fact]
    public void Index_LookupRows_ResolvesAgainstSameSnapshot()
    {
        var (table, byRegion, _) = Build();
        table.ApplyChanges(Enumerable.Range(0, 50).Select(i =>
            KeyValuePair.Create((long)i, new Customer($"c{i}", i % 2 == 0 ? "BR" : "US", i % 3))));

        var rows = table.GetSnapshot().LookupRows(byRegion, "BR").ToList();
        Assert.Equal(25, rows.Count);
        Assert.All(rows, kv => Assert.Equal("BR", kv.Value.Region));
    }

    [Fact]
    public void Index_MustBeRegisteredBeforeRows()
    {
        var table = new SnapshotTable<long, Customer>();
        table.Upsert(1, new Customer("a", "BR", 1));
        Assert.Throws<InvalidOperationException>(() => table.CreateIndex((_, c) => c.Region));
    }

    [Fact]
    public void Index_SurvivesResetAndClear()
    {
        var (table, byRegion, _) = Build();
        table.Reset([KeyValuePair.Create(1L, new Customer("a", "BR", 1))]);
        Assert.Equal([1L], table.GetSnapshot().Lookup(byRegion, "BR"));

        table.Reset([KeyValuePair.Create(2L, new Customer("b", "US", 1))]);
        var snap = table.GetSnapshot();
        Assert.Empty(snap.Lookup(byRegion, "BR"));
        Assert.Equal([2L], snap.Lookup(byRegion, "US"));

        table.Clear();
        Assert.Empty(table.GetSnapshot().Lookup(byRegion, "US"));
    }

    [Fact]
    public void Index_RandomizedFuzz_MatchesModel()
    {
        var (table, byRegion, _) = Build();
        var regions = new[] { "BR", "US", "DE", "JP", "IN" };
        var model = new Dictionary<long, string>();
        var rng = new Random(777);

        for (int round = 0; round < 100; round++)
        {
            var upserts = new List<KeyValuePair<long, Customer>>();
            var removes = new List<long>();
            for (int i = 0; i < rng.Next(1, 30); i++)
            {
                long key = rng.Next(200);
                if (rng.Next(4) == 0)
                {
                    removes.Add(key);
                }
                else
                {
                    upserts.Add(KeyValuePair.Create(key, new Customer($"c{key}", regions[rng.Next(regions.Length)], 1)));
                }
            }
            foreach (var (k, v) in upserts)
            {
                model[k] = v.Region;
            }
            foreach (var k in removes)
            {
                model.Remove(k);
            }
            table.ApplyChanges(upserts, removes);
        }

        var snapshot = table.GetSnapshot();
        foreach (var region in regions)
        {
            var expected = model.Where(kv => kv.Value == region).Select(kv => kv.Key).Order().ToArray();
            Assert.Equal(expected, snapshot.Lookup(byRegion, region).Order().ToArray());
        }
    }
}

public class NotificationAndParallelResetTests
{
    [Fact]
    public void SnapshotChanged_ReportsBatchKeys()
    {
        var table = new SnapshotTable<int, int>();
        table.ApplyChanges([KeyValuePair.Create(1, 1), KeyValuePair.Create(2, 2)]);

        SnapshotTable<int, int>.SnapshotChangedEventArgs? seen = null;
        table.SnapshotChanged += args => seen = args;
        table.ApplyChanges([KeyValuePair.Create(2, 22), KeyValuePair.Create(3, 3)], [1, 999]);

        Assert.NotNull(seen);
        Assert.False(seen.IsFullReload);
        Assert.Equal([2, 3], seen.UpsertedKeys);
        Assert.Equal([1], seen.RemovedKeys); // 999 didn't exist → not reported
        Assert.Equal(22, seen.Snapshot.TryGetValue(2, out int v) ? v : -1);
        Assert.Equal(2, seen.Snapshot.Count);
    }

    [Fact]
    public void SnapshotChanged_FullReloadFlag()
    {
        var table = new SnapshotTable<int, int>();
        var events = new List<bool>();
        table.SnapshotChanged += args => events.Add(args.IsFullReload);
        table.Reset([KeyValuePair.Create(1, 1)]);
        table.Upsert(2, 2);
        table.Clear();
        Assert.Equal([true, false, true], events);
    }

    [Fact]
    public void ResetParallel_MatchesSequentialReset()
    {
        const int n = 200_000;
        var rows = Enumerable.Range(0, n).Select(i => KeyValuePair.Create((long)i, (long)-i)).ToArray();

        var table = new SnapshotTable<long, long>(capacityHint: n);
        table.ResetParallel(rows, degreeOfParallelism: 4);

        Assert.Equal(n, table.Count);
        var rng = new Random(5);
        for (int i = 0; i < 5_000; i++)
        {
            long k = rng.Next(n);
            Assert.True(table.TryGetValue(k, out long v));
            Assert.Equal(-k, v);
        }
        Assert.False(table.ContainsKey(n + 1L));

        // And the table stays fully functional for subsequent batches.
        table.ApplyChanges([KeyValuePair.Create(5L, 555L)], [6L]);
        Assert.Equal(555L, table[5L]);
        Assert.False(table.ContainsKey(6L));
        Assert.Equal(n - 1, table.Count);
    }

    [Fact]
    public void ResetParallel_RejectsDuplicateKeys()
    {
        var table = new SnapshotTable<long, long>();
        var duplicated = new[]
        {
            KeyValuePair.Create(1L, 1L),
            KeyValuePair.Create(2L, 2L),
            KeyValuePair.Create(1L, 99L),
        };
        Assert.Throws<InvalidOperationException>(() => table.ResetParallel(duplicated));
    }

    [Fact]
    public void ResetParallel_BuildsSecondaryIndexes()
    {
        var table = new SnapshotTable<long, string>();
        var byLength = table.CreateIndex((_, v) => v.Length);
        table.ResetParallel(Enumerable.Range(0, 1_000).Select(i =>
            KeyValuePair.Create((long)i, new string('x', i % 7))));

        var snap = table.GetSnapshot();
        for (int len = 0; len < 7; len++)
        {
            Assert.Equal(Enumerable.Range(0, 1_000).Count(i => i % 7 == len), snap.Lookup(byLength, len).Count);
        }
    }
}
