namespace Uo.Render;

/// <summary>Retains both emission order (the sort input) and sorted keys; equal inputs include equal-depth ties.</summary>
public sealed class TranslucentOrderCache
{
    ulong[] _input, _sorted;
    int _count = -1;
    public TranslucentOrderCache(int capacity)
    {
        _input = new ulong[capacity]; _sorted = new ulong[capacity];
    }

    /// <returns>True when the previous sort was reused.</returns>
    public bool Sort(Span<ulong> keys, bool enabled)
    {
        if (enabled && keys.Length == _count && keys.SequenceEqual(_input.AsSpan(0, _count)))
        {
            _sorted.AsSpan(0, _count).CopyTo(keys);
            return true;
        }
        if (enabled)
        {
            if (keys.Length > _input.Length)
            {
                int cap = Math.Max(keys.Length, _input.Length * 2);
                Array.Resize(ref _input, cap); Array.Resize(ref _sorted, cap);
            }
            keys.CopyTo(_input);
        }
        if (keys.Length > 1) WorldRenderer.SortTransKeys(keys);
        if (enabled) keys.CopyTo(_sorted);
        _count = enabled ? keys.Length : -1;
        return false;
    }
}
