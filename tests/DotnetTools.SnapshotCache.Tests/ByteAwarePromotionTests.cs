using System.Runtime.InteropServices;
using DotnetTools.SnapshotCache;

namespace DotnetTools.SnapshotCache.Tests;

/// <summary>
/// Issue #44: the flat-array → chunked promotion cap is byte-aware, so a flat <c>TEntity[]</c>
/// bucket never reaches the 85,000-byte LOH threshold even for wide value-type entities. The cap is
/// <c>min(1,024 elements, 84,000 bytes / sizeof(TEntity))</c> — unchanged (1,024) for reference
/// entities and narrow structs, lower for large structs. Byte-awareness only <i>tightens</i> the
/// cap here; raising it for small elements to reclaim per-chunk overhead is a separate, measured
/// change that awaits the production bucket-size distribution.
/// </summary>
public class ByteAwarePromotionTests
{
    // Explicit 1 KB value type: 84,000 / 1,024 = 82 elements, an ~82 KB flat array (just sub-LOH).
    [StructLayout(LayoutKind.Sequential, Size = 1024)]
    private struct Big1Kb
    {
    }

    [Fact]
    public void EffectiveCap_IsByteAware_PerElementSize()
    {
        // Reference entity: 8-byte slots → cap stays at the 1,024-element ceiling.
        Assert.Equal(1024, MultiValueSnapshotTable<long, string>.ArrayBucketMaxCount);
        // 1 KB struct → cap drops so the flat array stays under the LOH bar.
        Assert.Equal(82, MultiValueSnapshotTable<long, Big1Kb>.ArrayBucketMaxCount);
        // The chosen cap keeps the whole-array copy sub-LOH.
        Assert.True(82 * 1024 < 85_000);
    }

    [Fact]
    public void WideValueEntity_StaysArrayAtCap_PromotesJustPastIt()
    {
        int cap = MultiValueSnapshotTable<long, Big1Kb>.ArrayBucketMaxCount; // 82
        var table = new MultiValueSnapshotTable<long, Big1Kb>();

        // Exactly at the cap: still a single flat array (which is sub-LOH by construction).
        table.ApplyChanges([BucketChange.Append(1L, new Big1Kb[cap])]);
        Assert.IsType<Big1Kb[]>(table.Lookup(1L));
        Assert.Equal(cap, table.Lookup(1L).Count);

        // One element past the cap: promoted to chunks — no oversized flat array is ever built.
        table.ApplyChanges([BucketChange.Append(1L, default(Big1Kb))]);
        Assert.IsType<ChunkedImmutableList<Big1Kb>>(table.Lookup(1L));
        Assert.Equal(cap + 1, table.Lookup(1L).Count);
    }

    [Fact]
    public void WideValueEntity_ResetPastCap_MaterializesChunked()
    {
        int cap = MultiValueSnapshotTable<long, Big1Kb>.ArrayBucketMaxCount;
        var table = new MultiValueSnapshotTable<long, Big1Kb>();

        // Reset a bucket just under the cap → flat array; another well past it → chunked.
        table.Reset(
        [
            KeyValuePair.Create(1L, (IReadOnlyList<Big1Kb>)new Big1Kb[cap]),
            KeyValuePair.Create(2L, (IReadOnlyList<Big1Kb>)new Big1Kb[cap * 3]),
        ]);
        Assert.IsType<Big1Kb[]>(table.Lookup(1L));
        Assert.IsType<ChunkedImmutableList<Big1Kb>>(table.Lookup(2L));
        Assert.Equal(cap * 3, table.Lookup(2L).Count);
    }

    [Fact]
    public void ReferenceEntity_BucketAtOldCap_IsUnchanged()
    {
        // Regression guard: reference entities keep the exact prior behavior (promote past 1,024),
        // so this change is a no-op for the common reference-type workload.
        var table = new MultiValueSnapshotTable<long, string>();
        table.ApplyChanges([BucketChange.Append(1L, Enumerable.Range(0, 1024).Select(i => $"e{i}").ToArray())]);
        Assert.IsType<string[]>(table.Lookup(1L));
        table.ApplyChanges([BucketChange.Append(1L, "one-more")]);
        Assert.IsType<ChunkedImmutableList<string>>(table.Lookup(1L));
    }

    // --- Operator-tunable ceiling (issue #44, retained-heap lever): raise the flat-array cap to
    // keep larger buckets compact (fewer chunked instances), always floored by the byte limit. ---

    [Fact]
    public void RaisedCeiling_KeepsReferenceBucketFlatPastDefault()
    {
        var table = new MultiValueSnapshotTable<long, string>(maxArrayBucketElements: 8192);
        Assert.Equal(8192, table.EffectiveArrayBucketMaxCount); // 8192 * 8 B = 64 KB, sub-LOH

        table.ApplyChanges([BucketChange.Append(1L, Enumerable.Range(0, 8192).Select(i => $"e{i}").ToArray())]);
        Assert.IsType<string[]>(table.Lookup(1L)); // still a flat array well past the 1,024 default

        table.ApplyChanges([BucketChange.Append(1L, "one-more")]);
        Assert.IsType<ChunkedImmutableList<string>>(table.Lookup(1L)); // promotes past the raised cap
    }

    [Fact]
    public void RaisedCeiling_IsAlwaysFlooredByTheByteLimit()
    {
        // An absurd ceiling can never push a flat array onto the LOH — the byte floor wins.
        Assert.Equal(10_500, // 84,000 / 8
            new MultiValueSnapshotTable<long, string>(maxArrayBucketElements: 1_000_000).EffectiveArrayBucketMaxCount);
        Assert.Equal(82, // 84,000 / 1,024, unchanged by the requested ceiling
            new MultiValueSnapshotTable<long, Big1Kb>(maxArrayBucketElements: 1_000_000).EffectiveArrayBucketMaxCount);
    }

    [Fact]
    public void DefaultCeiling_MatchesTheStaticDefault()
    {
        Assert.Equal(
            MultiValueSnapshotTable<long, string>.ArrayBucketMaxCount,
            new MultiValueSnapshotTable<long, string>().EffectiveArrayBucketMaxCount);
    }

    [Fact]
    public void NegativeCeiling_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MultiValueSnapshotTable<long, string>(maxArrayBucketElements: -1));
}
