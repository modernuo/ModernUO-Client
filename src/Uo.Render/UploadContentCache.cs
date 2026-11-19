namespace Uo.Render;

/// <summary>Exact contents of one persistent GPU buffer range. Invalidate whenever its backing can change.</summary>
public sealed class UploadContentCache
{
    byte[] _bytes;
    int _length = -1;
    IntPtr _buffer;
    uint _offset;

    public UploadContentCache(int capacity = 0) => _bytes = new byte[capacity];
    public void Invalidate() => _length = -1;

    public void EnsureCapacity(int capacity)
    {
        if (_bytes.Length < capacity) Array.Resize(ref _bytes, capacity);
    }

    /// <summary>Records what the caller will upload. A failed/cancelled submit must invalidate it.</summary>
    public bool Prepare(ReadOnlySpan<byte> bytes, IntPtr buffer, uint offset)
    {
        if (_buffer == buffer && _offset == offset && bytes.Length == _length
            && bytes.SequenceEqual(_bytes.AsSpan(0, _length))) return false;
        EnsureCapacity(bytes.Length);
        bytes.CopyTo(_bytes);
        _length = bytes.Length; _buffer = buffer; _offset = offset;
        return true;
    }
}
