namespace Uo.Render;

/// <summary>First-fit sub-allocator over a fixed range (instance slots of the big GPU buffer). Free ranges kept sorted
/// and coalesced. Arrays are preallocated, so allocation/free never touch the GC heap.</summary>
public sealed class RangeAllocator
{
    readonly int[] _start, _len;
    int _count;
    public int Capacity { get; private set; }
    public int Used { get; private set; }

    /// <summary>Extends the range to <paramref name="capacity"/>: [old capacity, capacity) becomes free (merged with a
    /// free range that ends at the old capacity). Existing allocations keep their offsets.</summary>
    public void Grow(int capacity)
    {
        if (capacity <= Capacity) return;
        int add = capacity - Capacity;
        if (_count > 0 && _start[_count - 1] + _len[_count - 1] == Capacity) _len[_count - 1] += add;
        else
        {
            if (_count == _start.Length) throw new InvalidOperationException("allocator fragment table full");
            _start[_count] = Capacity;
            _len[_count] = add;
            _count++;
        }
        Capacity = capacity;
    }

    public RangeAllocator(int capacity, int maxFragments = 1 << 16)
    {
        Capacity = capacity;
        _start = new int[maxFragments];
        _len = new int[maxFragments];
        _start[0] = 0;
        _len[0] = capacity;
        _count = 1;
    }

    public int Alloc(int n)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_len[i] < n) continue;
            int s = _start[i];
            _start[i] += n;
            _len[i] -= n;
            if (_len[i] == 0)
            {
                Array.Copy(_start, i + 1, _start, i, _count - i - 1);
                Array.Copy(_len, i + 1, _len, i, _count - i - 1);
                _count--;
            }
            Used += n;
            return s;
        }
        return -1;
    }

    public void Free(int start, int n)
    {
        if (n == 0) return;
        Used -= n;
        // binary search insertion point
        int lo = 0, hi = _count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_start[mid] < start) lo = mid + 1; else hi = mid;
        }
        bool mergePrev = lo > 0 && _start[lo - 1] + _len[lo - 1] == start;
        bool mergeNext = lo < _count && start + n == _start[lo];
        if (mergePrev && mergeNext)
        {
            _len[lo - 1] += n + _len[lo];
            Array.Copy(_start, lo + 1, _start, lo, _count - lo - 1);
            Array.Copy(_len, lo + 1, _len, lo, _count - lo - 1);
            _count--;
        }
        else if (mergePrev) _len[lo - 1] += n;
        else if (mergeNext) { _start[lo] = start; _len[lo] += n; }
        else
        {
            if (_count == _start.Length) throw new InvalidOperationException("allocator fragment table full");
            Array.Copy(_start, lo, _start, lo + 1, _count - lo);
            Array.Copy(_len, lo, _len, lo + 1, _count - lo);
            _start[lo] = start;
            _len[lo] = n;
            _count++;
        }
    }
}
