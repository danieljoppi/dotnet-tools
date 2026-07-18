namespace DotnetTools.SnapshotCache;

/// <summary>Tuning options for <see cref="SnapshotTable{TKey,TValue}"/>.</summary>
public sealed class SnapshotTableOptions<TKey>
    where TKey : notnull
{
    /// <summary>Expected number of rows. Sizes the index shard count so each shard stays small
    /// (cheap to clone on insert/remove, far below the LOH threshold). The table grows fine past
    /// the hint. Default 0 (small table).</summary>
    public int CapacityHint { get; init; }

    /// <summary>Elements per row-storage chunk (power of two), or 0 for the automatic default
    /// (~4 KB of row data per chunk). Smaller chunks make sparse random update batches cheaper to
    /// copy; larger chunks favor dense batches and scans. The hard cap keeps every chunk below
    /// the LOH threshold regardless of this setting.</summary>
    public int ChunkRows { get; init; }

    /// <summary>Key comparer; defaults to <see cref="EqualityComparer{TKey}.Default"/>.</summary>
    public IEqualityComparer<TKey>? Comparer { get; init; }
}
