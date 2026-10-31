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

    public void Clear()
    {
        _count = 0;
        _dropped = 0;
    }
}
