// Runnable quickstart for DotnetTools.SnapshotCache.
//   dotnet run --project samples/Quickstart
//
// Three parts: the keyed SnapshotTable (the "big table, periodic batch refresh, hot reads"
// pattern), the shared-key MultiValueSnapshotTable (one key -> many values), and the
// ChunkedImmutableList building block on its own.

using DotnetTools.SnapshotCache;

SnapshotTableDemo();
MultiValueDemo();
ChunkedListDemo();

static void SnapshotTableDemo()
{
    Console.WriteLine("== SnapshotTable<long, Customer> ==");

    var customers = new SnapshotTable<long, Customer>(capacityHint: 1_000);

    // Optional secondary index (index key -> primary keys), maintained atomically with every batch.
    var byRegion = customers.CreateIndex((_, c) => c.Region);

    // Optional change feed, delivered outside the write lock after each atomic publish.
    customers.SnapshotChanged += e =>
        Console.WriteLine($"  published v{e.Version}: +{e.UpsertedKeys.Count} / -{e.RemovedKeys.Count}");

    // Initial load.
    customers.Reset(Enumerable.Range(0, 5).Select(i =>
        KeyValuePair.Create((long)i, new Customer(i, $"Customer {i}", i % 2 == 0 ? "BR" : "US"))));

    // A periodic refresh: upserts + removes, applied atomically.
    customers.ApplyChanges(
        upserts: [KeyValuePair.Create(1L, new Customer(1, "Renamed", "US")),
                  KeyValuePair.Create(9L, new Customer(9, "New", "BR"))],
        removes: [3L]);

    // Wait-free point read (no locks, any thread).
    if (customers.TryGetValue(1L, out var c))
    {
        Console.WriteLine($"  key 1 -> {c.Name} ({c.Region})");
    }

    // A consistent snapshot: every read below sees one version, even while writers keep landing.
    var snapshot = customers.GetSnapshot();
    var brKeys = snapshot.Lookup(byRegion, "BR");
    Console.WriteLine($"  BR customers in snapshot v{snapshot.Version}: [{string.Join(", ", brKeys)}]");
    Console.WriteLine($"  total rows: {snapshot.Count}");
    Console.WriteLine();
}

static void MultiValueDemo()
{
    Console.WriteLine("== MultiValueSnapshotTable<long, Order> (one instrument -> many orders) ==");

    var book = new MultiValueSnapshotTable<long, Order>(keyCapacityHint: 100);

    book.ApplyChanges(
    [
        BucketChange.Append(1L, new Order(101, 10.5m), new Order(102, 11.0m)), // append to a bucket
        BucketChange.Append(2L, new Order(201, 9.9m)),
    ]);

    // Replace one position; append more; drop a whole bucket -- all atomic.
    book.ApplyChanges(
    [
        BucketChange.ReplaceAt(1L, (0, new Order(101, 10.75m))),
        BucketChange.Append(1L, new Order(103, 11.25m)),
    ]);

    IReadOnlyList<Order> instrument1 = book.Lookup(1L); // wait-free, immutable
    Console.WriteLine($"  instrument 1 book: [{string.Join(", ", instrument1.Select(o => o.Price))}]");
    Console.WriteLine($"  distinct instruments: {book.KeyCount}");
    Console.WriteLine();
}

static void ChunkedListDemo()
{
    Console.WriteLine("== ChunkedImmutableList<int> (standalone persistent list) ==");

    var list = ChunkedImmutableList<int>.CreateRange(Enumerable.Range(0, 10));
    var bigger = list.AddRange([10, 11, 12]); // O(touched chunks); 'list' stays unchanged

    Console.WriteLine($"  original count {list.Count}, extended count {bigger.Count}");
    Console.WriteLine($"  IndexOf(11) in extended = {bigger.IndexOf(11)}");

    // Array-speed scan via per-chunk spans.
    long sum = 0;
    foreach (ReadOnlySpan<int> span in bigger.Chunks)
    {
        foreach (int v in span)
        {
            sum += v;
        }
    }
    Console.WriteLine($"  sum via Chunks spans = {sum}");
}

internal readonly record struct Customer(long Id, string Name, string Region);
internal readonly record struct Order(long Id, decimal Price);
