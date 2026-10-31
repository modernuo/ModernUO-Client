namespace Uo.Render;

/// <summary>
/// The per-sprite overrides queued for the next frame (<see cref="WorldRenderer"/>): appended in order, the ones past the
/// capacity dropped and counted. E12 (CUO_GPU_ITEM_CHECK): a check frame's probe pass takes a <see cref="Mark"/> before it
/// runs and restores it after, so the overrides it queued never reach the drawn frame (each queued partial-alpha override
/// adds a translucent draw: queued twice, the sprite would blend twice).
/// </summary>
public struct PendingOverrideQueue
{
    public struct Entry
    {
        public int X, Y, Z, Graphic;
        public bool IsStatic;
        public uint Value;

        /// <summary>Every field equal.</summary>
        public readonly bool Same(in Entry o) => X == o.X && Y == o.Y && Z == o.Z && Graphic == o.Graphic && IsStatic == o.IsStatic && Value == o.Value;
    }

    public readonly struct Mark
    {
        public readonly int Count, Dropped;

        public Mark(int count, int dropped)
        {
            Count = count;
            Dropped = dropped;
        }
    }

    private readonly Entry[] _items;
    private int _count, _dropped;

    public PendingOverrideQueue(int capacity)
    {
        _items = new Entry[Math.Max(1, capacity)];
        _count = 0;
        _dropped = 0;
    }

    public readonly int Count => _count;
    public readonly int Dropped => _dropped;
    public readonly int Capacity => _items.Length;

    public readonly ref readonly Entry this[int i] => ref _items[i];

    public void Add(in Entry entry)
    {
        if (_count >= _items.Length)
        {
            _dropped++;

            return;
        }

        _items[_count++] = entry;
    }

    public readonly Mark GetMark() => new Mark(_count, _dropped);

    /// <summary>Back to <paramref name="mark"/>: the entries queued since and the drops counted since are forgotten.</summary>
    public void Restore(Mark mark)
    {
        if (mark.Count > _count || mark.Dropped > _dropped)
        {
            throw new InvalidOperationException("the queue was cleared after the mark was taken");
        }

        _count = mark.Count;
        _dropped = mark.Dropped;
    }

    /// <summary>
    /// E12: the probe pass's end. Copies the entries queued since <paramref name="mark"/> into <paramref name="into"/>
    /// (cleared first), restores the mark (<see cref="Restore"/>), and returns how many were dropped since the mark.
    /// </summary>
    public int TakeSince(Mark mark, List<Entry> into)
    {
        into.Clear();

        for (int i = mark.Count; i < _count; i++)
        {
            into.Add(_items[i]);
        }

        int dropped = _dropped - mark.Dropped;
        Restore(mark);

        return dropped;
    }

    /// <summary>
    /// E12: the probe pass's overrides (<paramref name="probe"/>, <paramref name="probeDropped"/> from <see cref="TakeSince"/>)
    /// against what was queued since <paramref name="mark"/> (the reference pass): every field of every entry, in order, the
    /// counts, and the drop counts. Entries before the mark are not compared. <paramref name="refDropped"/> is the reference
    /// pass's drop count; either drop count above 0 means the queue overflowed (only what fitted was compared).
    /// </summary>
    public static bool Differ(List<Entry> probe, int probeDropped, in PendingOverrideQueue queue, Mark mark, out int refCount, out int refDropped)
    {
        refCount = queue._count - mark.Count;
        refDropped = queue._dropped - mark.Dropped;

        if (refCount != probe.Count || refDropped != probeDropped)
        {
            return true;
        }

        for (int i = 0; i < refCount; i++)
        {
            if (!probe[i].Same(in queue._items[mark.Count + i]))
            {
                return true;
            }
        }

        return false;
    }

    public void Clear()
    {
        _count = 0;
        _dropped = 0;
    }
}
