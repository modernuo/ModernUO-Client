using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Uo.Net;

/// <summary>Big-endian reader over one packet. Reads past the end return 0 instead of throwing.</summary>
public ref struct PacketReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public PacketReader(ReadOnlySpan<byte> data, int start = 0)
    {
        _data = data;
        _pos = start;
    }

    public readonly int Position => _pos;
    public readonly int Remaining => _data.Length - _pos;
    public readonly ReadOnlySpan<byte> Span => _data;

    public void Skip(int n) => _pos = Math.Min(_data.Length, _pos + n);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte U8() => _pos < _data.Length ? _data[_pos++] : (byte)0;

    public sbyte I8() => (sbyte)U8();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort U16()
    {
        if (_pos + 2 > _data.Length) { _pos = _data.Length; return 0; }
        var v = BinaryPrimitives.ReadUInt16BigEndian(_data[_pos..]);
        _pos += 2;
        return v;
    }

    public short I16() => (short)U16();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint U32()
    {
        if (_pos + 4 > _data.Length) { _pos = _data.Length; return 0; }
        var v = BinaryPrimitives.ReadUInt32BigEndian(_data[_pos..]);
        _pos += 4;
        return v;
    }

    public int I32() => (int)U32();

    public ReadOnlySpan<byte> Bytes(int n)
    {
        n = Math.Min(n, Remaining);
        var s = _data.Slice(_pos, n);
        _pos += n;
        return s;
    }

    /// <summary>Latin-1 fixed field (or to end when n &lt; 0) decoded into <paramref name="dest"/>, stopping at NUL.</summary>
    public int Latin1(int n, scoped Span<char> dest)
    {
        var src = n < 0 ? Bytes(Remaining) : Bytes(n);
        var len = 0;
        for (var i = 0; i < src.Length && len < dest.Length; i++)
        {
            if (src[i] == 0) break;
            dest[len++] = (char)src[i];
        }
        return len;
    }

    /// <summary>UTF-16BE, NUL-terminated or to the end of the packet.</summary>
    public int UnicodeBE(scoped Span<char> dest)
    {
        var len = 0;
        while (Remaining >= 2 && len < dest.Length)
        {
            var c = (char)U16();
            if (c == 0) break;
            dest[len++] = c;
        }
        return len;
    }
}

/// <summary>Big-endian writer over a caller-provided span.</summary>
public ref struct PacketWriter
{
    private readonly Span<byte> _buf;
    private int _pos;

    public PacketWriter(Span<byte> buf)
    {
        _buf = buf;
        _pos = 0;
    }

    public readonly int Length => _pos;
    public readonly ReadOnlySpan<byte> Written => _buf[.._pos];

    public void U8(byte v) => _buf[_pos++] = v;

    public void U16(ushort v)
    {
        BinaryPrimitives.WriteUInt16BigEndian(_buf[_pos..], v);
        _pos += 2;
    }

    public void U32(uint v)
    {
        BinaryPrimitives.WriteUInt32BigEndian(_buf[_pos..], v);
        _pos += 4;
    }

    public void Zero(int n)
    {
        _buf.Slice(_pos, n).Clear();
        _pos += n;
    }

    /// <summary>ASCII/Latin-1 into a fixed field, zero padded (always leaves room for a NUL).</summary>
    public void Latin1Fixed(ReadOnlySpan<char> s, int field)
    {
        var dst = _buf.Slice(_pos, field);
        dst.Clear();
        var n = Math.Min(s.Length, field - 1);
        for (var i = 0; i < n; i++) dst[i] = s[i] < 256 ? (byte)s[i] : (byte)'?';
        _pos += field;
    }

    /// <summary>ASCII with a trailing NUL.</summary>
    public void Latin1Z(ReadOnlySpan<char> s)
    {
        foreach (var c in s) U8(c < 256 ? (byte)c : (byte)'?');
        U8(0);
    }

    /// <summary>UTF-16BE with a trailing NUL.</summary>
    public void UnicodeBEZ(ReadOnlySpan<char> s)
    {
        foreach (var c in s) U16(c);
        U16(0);
    }

    /// <summary>Patches the u16 length at offset 1 for a variable-length packet.</summary>
    public void FixLength() => BinaryPrimitives.WriteUInt16BigEndian(_buf[1..], (ushort)_pos);
}
