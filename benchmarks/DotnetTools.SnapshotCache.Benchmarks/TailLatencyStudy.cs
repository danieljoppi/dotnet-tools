using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using DotnetTools.SnapshotCache;

namespace DotnetTools.SnapshotCache.Benchmarks;

/// <summary>
/// The experiment behind "if the refresh is async, why not just take the fastest reads?":
/// reader threads measure EVERY individual read with <see cref="Stopwatch.GetTimestamp"/> into a
/// latency histogram while an asynchronous refresher churns the structure in the background —
/// exactly the production shape. The question is not the average read (the per-op benchmarks
/// answer that) but the TAIL: what do the slowest reads look like when the refresh's garbage
/// lands on the shared heap?
///
/// Variants (one per process; the driver script runs each under both GC modes):
/// <list type="bullet">
///   <item><c>SnapshotTable</c> — keyed reads; refresher calls <c>ApplyChanges(batch)</c>.</item>
///   <item><c>FrozenDictionary</c> — keyed reads via volatile reference; refresher rebuilds the
///   whole dictionary (copy + apply + <c>ToFrozenDictionary</c>) and swaps.</item>
///   <item><c>ImmutableList</c> — positional reads (no key index; flatters it); refresher applies
///   the batch through <c>ToBuilder()/ToImmutable()</c> and swaps.</item>
///   <item><c>ImmutableArray</c> — positional reads (the raw-speed floor); refresher does the
///   naive full-copy rebuild and swaps.</item>
/// </list>
///
/// Cadence compression: at 10M rows the rebuild-to-cycle duty ratio with a refresh every 3 s is
/// comparable to a 100M-row table on the real 30 s cadence, so tail effects show at 1/10 the
/// wall-clock cost. Latency histogram: 100 ns buckets below 10 µs, 1 µs buckets to 1 ms,
/// power-of-two buckets above, plus exact max — far finer than the effects under study.
/// </summary>
public static class TailLatencyStudy
{
    private const int FineBuckets = 100;   // 100 ns resolution below 10 µs
    private const int MidBuckets = 990;    // 1 µs resolution from 10 µs to 1 ms

    public static void Run(string variant, int rows, int seconds, int batchSize, int refreshEveryMs)
    {
        var rng = new Random(42);
        var batchKeys = Enumerable.Range(0, batchSize).Select(_ => rng.Next(rows)).Distinct().ToArray();

        (Func<int, long> read, Action refresh) = Build(variant, rows, batchKeys);
        read(1); // touch
        refresh(); // warm one cycle so steady-state is measured

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var pauseBefore = GC.GetTotalPauseDuration();
        int gen2Before = GC.CollectionCount(2);

        var refresher = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                refresh();
                int remaining = refreshEveryMs - (int)sw.ElapsedMilliseconds;
                if (remaining > 0)
                {
                    try
                    {
                        Task.Delay(remaining, stop.Token).Wait(stop.Token);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }
        });

        var readers = Enumerable.Range(0, 2).Select(r => Task.Run(() =>
        {
            var histogram = new Histogram();
            var localRng = new Random(1000 + r);
            long checksum = 0;
            while (!stop.IsCancellationRequested)
            {
                int key = localRng.Next(rows);
                long t0 = Stopwatch.GetTimestamp();
                checksum += read(key);
                long t1 = Stopwatch.GetTimestamp();
                histogram.Record((t1 - t0) * 1_000_000_000 / Stopwatch.Frequency);
            }
            GC.KeepAlive(checksum);
            return histogram;
        })).ToArray();

        Task.WaitAll(readers);
        try
        {
            refresher.Wait();
        }
        catch (AggregateException)
        {
        }

        var merged = Histogram.Merge(readers.Select(t => t.Result));
        var gcPause = GC.GetTotalPauseDuration() - pauseBefore;
        int gen2 = GC.CollectionCount(2) - gen2Before;

        Console.WriteLine(
            $"| {variant} | {merged.Count / (double)seconds / 1_000_000:F2}M/s " +
            $"| {Format(merged.Percentile(50))} | {Format(merged.Percentile(99))} " +
            $"| {Format(merged.Percentile(99.9))} | {Format(merged.Max)} " +
            $"| {merged.CountAbove(1_000_000)} | {gcPause.TotalMilliseconds:F0} ms | {gen2} |");
    }

    private static (Func<int, long>, Action) Build(string variant, int rows, int[] batchKeys)
    {
        var source = Enumerable.Range(0, rows).Select(i => KeyValuePair.Create((long)i, (long)i * 3));
        switch (variant)
        {
            case "SnapshotTable":
            {
                var table = new SnapshotTable<long, long>(capacityHint: rows);
                table.ResetParallel(source);
                var batch = batchKeys.Select(k => KeyValuePair.Create((long)k, -1L)).ToArray();
                return (k => table.TryGetValue(k, out long v) ? v : 0, () => table.ApplyChanges(batch));
            }
            case "FrozenDictionary":
            {
                var current = source.ToFrozenDictionary();
                return (
                    k => Volatile.Read(ref current).TryGetValue(k, out long v) ? v : 0,
                    () =>
                    {
                        var next = new Dictionary<long, long>(Volatile.Read(ref current));
                        foreach (int k in batchKeys)
                        {
                            next[k] = -1;
                        }
                        Volatile.Write(ref current, next.ToFrozenDictionary());
                    });
            }
            case "ImmutableList":
            {
                var current = ImmutableList.CreateRange(source.Select(kv => kv.Value));
                return (
                    k => Volatile.Read(ref current)[k],
                    () =>
                    {
                        var builder = Volatile.Read(ref current).ToBuilder();
                        foreach (int k in batchKeys)
                        {
                            builder[k] = -1;
                        }
                        Volatile.Write(ref current, builder.ToImmutable());
                    });
            }
            case "ImmutableArray":
            {
                // ImmutableArray<T> is a struct and cannot be volatile-swapped; publish the
                // extracted backing array instead — the read is the same single array indexing.
                var current = System.Runtime.InteropServices.ImmutableCollectionsMarshal
                    .AsArray(ImmutableArray.CreateRange(source.Select(kv => kv.Value)))!;
                return (
                    k => Volatile.Read(ref current)[k],
                    () =>
                    {
                        var builder = ImmutableArray.Create(Volatile.Read(ref current)).ToBuilder();
                        foreach (int k in batchKeys)
                        {
                            builder[k] = -1;
                        }
                        Volatile.Write(ref current,
                            System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray(builder.ToImmutable())!);
                    });
            }
            default:
                throw new ArgumentException($"Unknown variant '{variant}'.", nameof(variant));
        }
    }

    private static string Format(long ns) =>
        ns < 1_000 ? $"{ns} ns"
        : ns < 1_000_000 ? $"{ns / 1_000.0:F1} µs"
        : $"{ns / 1_000_000.0:F1} ms";

    private sealed class Histogram
    {
        private readonly long[] _fine = new long[FineBuckets]; // 100 ns buckets, < 10 µs
        private readonly long[] _mid = new long[MidBuckets];   // 1 µs buckets, 10 µs – 1 ms
        private readonly long[] _above = new long[24];         // pow2 ms buckets, ≥ 1 ms
        public long Count { get; private set; }
        public long Max { get; private set; }

        public void Record(long ns)
        {
            Count++;
            if (ns > Max)
            {
                Max = ns;
            }
            if (ns < 10_000)
            {
                _fine[ns / 100]++;
            }
            else if (ns < 1_000_000)
            {
                _mid[(ns - 10_000) / 1_000]++;
            }
            else
            {
                int bucket = Math.Min(_above.Length - 1,
                    64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)(ns / 1_000_000)));
                _above[bucket]++;
            }
        }

        private IEnumerable<(long midNs, long count)> Buckets()
        {
            for (int b = 0; b < FineBuckets; b++)
            {
                yield return (b * 100L + 50, _fine[b]);
            }
            for (int b = 0; b < MidBuckets; b++)
            {
                yield return (10_000L + b * 1_000L + 500, _mid[b]);
            }
            for (int i = 0; i < _above.Length; i++)
            {
                yield return ((1L << i) * 1_000_000, _above[i]);
            }
        }

        public long CountAbove(long ns)
        {
            long count = 0;
            foreach (var (midNs, c) in Buckets())
            {
                if (midNs >= ns)
                {
                    count += c;
                }
            }
            return count;
        }

        public long Percentile(double p)
        {
            long target = (long)(Count * p / 100.0);
            long seen = 0;
            foreach (var (midNs, c) in Buckets())
            {
                seen += c;
                if (seen >= target)
                {
                    return midNs;
                }
            }
            return Max;
        }

        public static Histogram Merge(IEnumerable<Histogram> parts)
        {
            var merged = new Histogram();
            foreach (var h in parts)
            {
                for (int b = 0; b < FineBuckets; b++)
                {
                    merged._fine[b] += h._fine[b];
                }
                for (int b = 0; b < MidBuckets; b++)
                {
                    merged._mid[b] += h._mid[b];
                }
                for (int i = 0; i < merged._above.Length; i++)
                {
                    merged._above[i] += h._above[i];
                }
                merged.Count += h.Count;
                merged.Max = Math.Max(merged.Max, h.Max);
            }
            return merged;
        }
    }
}
