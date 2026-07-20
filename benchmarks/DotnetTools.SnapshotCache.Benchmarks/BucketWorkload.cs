namespace DotnetTools.SnapshotCache.Benchmarks;

/// <summary>
/// Shared fixtures for the shared-key → many-values workload (one-to-many buckets): a mid-width
/// entity row, bucket-size profiles (uniform, Zipf heavy-tail, refresh-shaped), and warm-batch
/// generation (append 1–50 entities to a key, or replace ~1% of a key's bucket). Used by
/// <see cref="SharedKeyBucketBenchmarks"/>, <see cref="LargeRefreshBenchmarks"/> and the
/// <c>--bucket-loh</c> console study so all three measure exactly the same shapes.
/// </summary>
public static class BucketWorkload
{
    /// <summary>Mid-width entity: id, group (shared key), a few ints/strings, decimal, DateTime.
    /// A reference type, so a bucket of ~10,625+ entity references is a LOH-sized array.</summary>
    public sealed record Entity(
        long Id, long GroupId, int Kind, int Version, string Label, decimal Amount, DateTime UpdatedAt);

    public static Entity MakeEntity(long groupId, long id, int version) => new(
        id, groupId, (int)(id % 7), version, $"entity-{id}", id * 0.73m + version, DateTime.UnixEpoch.AddSeconds(version));

    public enum SizeProfile
    {
        /// <summary>Every bucket holds ~N/K entities.</summary>
        Uniform,

        /// <summary>Zipf (s=1) heavy tail: bucket k holds ∝ 1/(k+1); at K=10k/N=1M the hottest
        /// bucket holds ~100k entities — far past the LOH threshold for an array of references.</summary>
        Zipf,

        /// <summary>Workload C shape: a fixed count of LOH-sized hot buckets plus a uniform tail.</summary>
        HeavyTailRefresh,
    }

    /// <summary>Bucket sizes for <paramref name="k"/> shared keys totalling exactly <paramref name="n"/>.</summary>
    public static int[] BuildSizes(SizeProfile profile, int k, int n, int hotBuckets = 15, int hotSize = 30_000)
    {
        var sizes = new int[k];
        switch (profile)
        {
            case SizeProfile.Uniform:
                for (int i = 0; i < k; i++)
                {
                    sizes[i] = n / k + (i < n % k ? 1 : 0);
                }
                break;
            case SizeProfile.Zipf:
            {
                double h = 0;
                for (int i = 0; i < k; i++)
                {
                    h += 1.0 / (i + 1);
                }
                long assigned = 0;
                for (int i = 0; i < k; i++)
                {
                    sizes[i] = Math.Max(1, (int)(n / ((i + 1) * h)));
                    assigned += sizes[i];
                }
                // Rounding drift lands on the mid-range buckets so the head stays exactly Zipf.
                for (long d = n - assigned; d != 0;)
                {
                    for (int i = k / 4; i < k && d != 0; i++)
                    {
                        int step = d > 0 ? 1 : sizes[i] > 1 ? -1 : 0;
                        sizes[i] += step;
                        d -= step;
                    }
                }
                break;
            }
            case SizeProfile.HeavyTailRefresh:
            {
                int rest = n - hotBuckets * hotSize;
                for (int i = 0; i < hotBuckets; i++)
                {
                    sizes[i] = hotSize;
                }
                int tail = k - hotBuckets;
                for (int i = 0; i < tail; i++)
                {
                    sizes[hotBuckets + i] = rest / tail + (i < rest % tail ? 1 : 0);
                }
                break;
            }
        }
        return sizes;
    }

    /// <summary>Materializes the entity population, bucket by bucket. Entity ids are globally
    /// unique; <paramref name="firstId"/> lets batches mint fresh ids past the population.</summary>
    public static Entity[][] BuildBuckets(int[] sizes, long firstId = 0)
    {
        var buckets = new Entity[sizes.Length][];
        long id = firstId;
        for (int g = 0; g < sizes.Length; g++)
        {
            var bucket = new Entity[sizes[g]];
            for (int i = 0; i < bucket.Length; i++)
            {
                bucket[i] = MakeEntity(g, id++, 0);
            }
            buckets[g] = bucket;
        }
        return buckets;
    }

    /// <summary>One warm change to one shared key: either appends (1–50 new entities) or
    /// replacements of ~1% of the bucket (at least one), never both empty.</summary>
    public sealed record Change(int GroupId, Entity[] Appends, (int Index, Entity Value)[] Replacements);

    /// <summary>
    /// Builds a warm batch touching <paramref name="touchCount"/> distinct keys. When
    /// <paramref name="weightBySize"/> is set, touched keys are sampled proportionally to bucket
    /// size (activity follows the heavy tail — the hot keys are the ones that keep changing);
    /// otherwise uniformly. Alternates append- and replace-shaped changes per touched key.
    /// </summary>
    public static Change[] BuildBatch(
        int[] sizes, int touchCount, bool weightBySize, Random rng, ref long nextId, int version)
    {
        long total = 0;
        var cumulative = new long[sizes.Length];
        for (int i = 0; i < sizes.Length; i++)
        {
            total += sizes[i];
            cumulative[i] = total;
        }

        var touched = new HashSet<int>();
        while (touched.Count < touchCount)
        {
            int g;
            if (weightBySize)
            {
                long point = rng.NextInt64(total);
                g = Array.BinarySearch(cumulative, point + 1);
                if (g < 0)
                {
                    g = ~g;
                }
            }
            else
            {
                g = rng.Next(sizes.Length);
            }
            touched.Add(g);
        }

        var changes = new Change[touchCount];
        int c = 0;
        foreach (int g in touched)
        {
            if (c % 2 == 0)
            {
                var appends = new Entity[rng.Next(1, 51)];
                for (int i = 0; i < appends.Length; i++)
                {
                    appends[i] = MakeEntity(g, nextId++, version);
                }
                changes[c] = new Change(g, appends, []);
            }
            else
            {
                // Replacements re-issue the entity that lives at the target index (populations are
                // built with sequential ids, so index → id is the bucket's start offset + index).
                // Keeping the id makes the change a value update for every approach, including the
                // rekeyed (groupId, entityId) table.
                long bucketStart = cumulative[g] - sizes[g];
                int count = Math.Max(1, sizes[g] / 100);
                var replacements = new (int, Entity)[count];
                for (int i = 0; i < count; i++)
                {
                    int index = rng.Next(sizes[g]);
                    replacements[i] = (index, MakeEntity(g, bucketStart + index, version));
                }
                changes[c] = new Change(g, [], replacements);
            }
            c++;
        }
        return changes;
    }
}
