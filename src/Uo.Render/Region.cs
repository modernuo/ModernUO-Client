using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Uo.Buffers;
using Uo.Pack;

namespace Uo.Render;

/// <summary>A map region kept as its raw file bytes; blocks are decoded in place (zero per-block allocation).</summary>
public sealed class Region
{
    public int Rx, Ry;
    public byte[] Data;     // null = region file absent (all blocks empty); a large-pool rent (ReleaseData returns it)
    public int BlocksW, BlocksH;
    public int Pins;        // in-flight bake jobs reading this region (main thread only)
    public long LastUsed;
    public bool Loaded;

    static readonly int HeaderSize = Unsafe.SizeOf<FileHeader>() + Unsafe.SizeOf<RegionHeader>();

    public void Parse(string path)
    {
        if (!File.Exists(path)) { Data = null; BlocksW = BlocksH = 0; return; }
        // Kind 1 (v1): Brotli payload after the FileHeader. Decompressed here, on the worker, into the logical
        // Kind-0 file so block offsets stay file-relative. The file and the logical bytes are pooled rents (both
        // LOH-sized): the file is returned here, the logical buffer when the region is evicted (ReleaseData).
        byte[] bytes = null;
        using (var handle = File.OpenHandle(path))
        {
            int size = (int)RandomAccess.GetLength(handle);
            var file = BufferPool.Rent<byte>(size);
            try
            {
                RandomAccess.Read(handle, file.AsSpan(0, size), 0);
                RegionFile.DecompressInto(file.AsSpan(0, size), ref bytes, pooled: true);
            }
            finally { BufferPool.Return(file); }
        }
        var fh = MemoryMarshal.Read<FileHeader>(bytes);
        if (fh.Magic != PackConst.MagicRegion) throw new InvalidDataException($"{path}: bad magic");
        var rh = MemoryMarshal.Read<RegionHeader>(bytes.AsSpan(Unsafe.SizeOf<FileHeader>()));
        BlocksW = rh.BlocksW;
        BlocksH = rh.BlocksH;
        Data = bytes;
    }

    /// <summary>Returns <see cref="Data"/> to the pool. Main thread, only for a region no bake job pins.</summary>
    public void ReleaseData()
    {
        BufferPool.ReturnShared(Data);
        Data = null;
    }

    /// <summary>Byte offset of the block record, or 0 if empty. bx/by are region-local.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint BlockOffset(int bx, int by)
    {
        if (Data == null || (uint)bx >= (uint)BlocksW || (uint)by >= (uint)BlocksH) return 0;
        return MemoryMarshal.Read<uint>(Data.AsSpan(HeaderSize + (by * BlocksW + bx) * 4));
    }

    public ReadOnlySpan<LandCell> Land(uint offset) => MemoryMarshal.Cast<byte, LandCell>(Data.AsSpan((int)offset, 64 * 4));

    public ReadOnlySpan<StaticCell> Statics(uint offset)
    {
        int count = MemoryMarshal.Read<ushort>(Data.AsSpan((int)offset + 256));
        return MemoryMarshal.Cast<byte, StaticCell>(Data.AsSpan((int)offset + 260, count * 8));
    }
}

public sealed class RegionJob : Job
{
    public Region Region;
    public string Path;
    public override void Run() => Region.Parse(Path);
}
