using Uo.Pack;
using static SDL3.SDL;

namespace Uo.Render;

/// <summary>Reads one atlas page file straight into a mapped GPU transfer buffer (no main-thread memcpy).
/// Kind 0: raw read into the mapped buffer. Kind 1 (Brotli): the compressed payload is read into a reusable per-job
/// buffer and decoded with BrotliDecoder directly into the mapped buffer (no full-size intermediate copy).
/// Expand (16-bit pack, GPU without 16-bit sampling): the page is read or decoded in pieces into a reusable scratch
/// buffer and each piece is expanded to RGBA8 into the mapped buffer (the whole chain doubles, so mip offsets double).
/// Kind 2 (chunked Brotli): <see cref="ChunkedPageReader"/>'s workers decode the chunks in parallel straight into the
/// mapped buffer, or into its shared scratch when expanding.</summary>
public sealed unsafe class PageJob : Job
{
    public int Page, Slot;
    public PageResidency Owner;
    public string Path;
    public byte* Dst;
    public int Capacity;
    public bool Ok, Compressed, Expand;
    public int Mips, Size;
    public PixelFormat Format;
    public long ReadTicks, DecodeTicks, ExpandTicks, WaitTicks, FileBytes;
    byte[] _compressed = new byte[1 << 20];
    byte[] _scratch;

    public override void Run()
    {
        Ok = false;
        Compressed = false;
        DecodeTicks = 0;
        ExpandTicks = 0;
        WaitTicks = 0;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        using var fh0 = File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        FileBytes = RandomAccess.GetLength(fh0);
        long pos = 0;
        Span<byte> fhb = stackalloc byte[16];
        if (!ReadAt(fh0, fhb, ref pos)) return;
        var fh = System.Runtime.InteropServices.MemoryMarshal.Read<FileHeader>(fhb);
        if (fh.Magic != PackConst.MagicPage) return;
        if (Expand) _scratch ??= new byte[1 << 20];
        Span<byte> phb = stackalloc byte[16];
        PageHeader ph;
        if (fh.Kind == FileKind.Raw)
        {
            if (!ReadAt(fh0, phb, ref pos)) return;
            ph = System.Runtime.InteropServices.MemoryMarshal.Read<PageHeader>(phb);
            if (!Fits(ph)) return;
            int n = (int)ph.DataBytes;
            if (!Expand)
            {
                if (!ReadAt(fh0, new Span<byte>(Dst, n), ref pos)) return;
            }
            else
            {
                for (int done = 0; done < n;)
                {
                    int k = Math.Min(_scratch.Length, n - done);
                    if (!ReadAt(fh0, _scratch.AsSpan(0, k), ref pos)) return;
                    ExpandPiece(k, done);
                    done += k;
                }
            }
            ReadTicks = System.Diagnostics.Stopwatch.GetTimestamp() - t0 - ExpandTicks;
        }
        else if (fh.Kind == FileKind.Brotli)
        {
            Compressed = true;
            int n = (int)(FileBytes - 16);
            if (_compressed.Length < n) _compressed = new byte[n + (n >> 3)];
            if (!ReadAt(fh0, _compressed.AsSpan(0, n), ref pos)) return;
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            ReadTicks = t1 - t0;
            var dec = new System.IO.Compression.BrotliDecoder();
            try
            {
                var src = new ReadOnlySpan<byte>(_compressed, 0, n);
                // 1) the PageHeader (16 bytes) into a stack span
                if (!Decode(ref src, ref dec, phb)) return;
                ph = System.Runtime.InteropServices.MemoryMarshal.Read<PageHeader>(phb);
                if (!Fits(ph)) return;
                int total = (int)ph.DataBytes;
                if (!Expand)
                {
                    // 2) the mip chain straight into the mapped transfer buffer
                    if (!Decode(ref src, ref dec, new Span<byte>(Dst, total))) return;
                }
                else
                {
                    // 2) pieces into scratch, each expanded into the mapped transfer buffer
                    for (int done = 0; done < total;)
                    {
                        int k = Math.Min(_scratch.Length, total - done);
                        if (!Decode(ref src, ref dec, _scratch.AsSpan(0, k))) return;
                        ExpandPiece(k, done);
                        done += k;
                    }
                }
            }
            finally { dec.Dispose(); }
            DecodeTicks = System.Diagnostics.Stopwatch.GetTimestamp() - t1 - ExpandTicks;
        }
        else if (fh.Kind == FileKind.ChunkedBrotli)
        {
            Compressed = true;
            var cr = ChunkedPageReader.Instance;
            long tw = System.Diagnostics.Stopwatch.GetTimestamp();
            ReadTicks = tw - t0;
            lock (cr.Lock)
            {
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                WaitTicks = t1 - tw;
                if (!Expand)
                {
                    cr.Decoder.Decode(fh0, 16, new Span<byte>(Dst, Capacity), 16, phb);
                    ph = System.Runtime.InteropServices.MemoryMarshal.Read<PageHeader>(phb);
                    if (!Fits(ph)) return;
                    DecodeTicks = System.Diagnostics.Stopwatch.GetTimestamp() - t1;
                }
                else
                {
                    if (cr.Scratch.Length < Capacity / 2) cr.Scratch = new byte[Capacity / 2];
                    cr.Decoder.Decode(fh0, 16, cr.Scratch.AsSpan(0, Capacity / 2), 16, phb);
                    ph = System.Runtime.InteropServices.MemoryMarshal.Read<PageHeader>(phb);
                    if (!Fits(ph)) return;
                    long te = System.Diagnostics.Stopwatch.GetTimestamp();
                    DecodeTicks = te - t1;
                    PixelExpand.Expand(cr.Scratch.AsSpan(0, (int)ph.DataBytes), new Span<byte>(Dst, (int)ph.DataBytes * 2));
                    ExpandTicks = System.Diagnostics.Stopwatch.GetTimestamp() - te;
                }
            }
        }
        else return;
        Mips = ph.MipCount;
        Size = ph.Width;
        Ok = true;
    }

    bool Fits(in PageHeader ph) => ph.Format == Format && (long)ph.DataBytes * (Expand ? 2 : 1) <= Capacity;

    void ExpandPiece(int bytes, int srcOffset)
    {
        long te = System.Diagnostics.Stopwatch.GetTimestamp();
        PixelExpand.Expand(_scratch.AsSpan(0, bytes), new Span<byte>(Dst + 2L * srcOffset, bytes * 2));
        ExpandTicks += System.Diagnostics.Stopwatch.GetTimestamp() - te;
    }

    static bool Decode(ref ReadOnlySpan<byte> src, ref System.IO.Compression.BrotliDecoder dec, scoped Span<byte> dst)
    {
        int total = 0;
        while (total < dst.Length)
        {
            var st = dec.Decompress(src, dst[total..], out int used, out int wrote);
            src = src[used..];
            total += wrote;
            if (st == System.Buffers.OperationStatus.InvalidData || (used == 0 && wrote == 0)) return false;
        }
        return true;
    }

    static bool ReadAt(Microsoft.Win32.SafeHandles.SafeFileHandle h, Span<byte> dst, ref long pos)
    {
        while (dst.Length > 0)
        {
            int r = RandomAccess.Read(h, dst, pos);
            if (r <= 0) return false;
            pos += r;
            dst = dst[r..];
        }
        return true;
    }
}

/// <summary>One process-wide <see cref="ChunkedBrotliDecoder"/> for Kind=2 pages. Page jobs take turns (one Decode at a
/// time), and each decode runs on all of the decoder's threads plus the calling worker, so one page's latency drops
/// rather than several pages decoding slowly side by side. The scratch buffer serves the RGBA8 expansion path.</summary>
public sealed class ChunkedPageReader
{
    public static int Workers = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
    static ChunkedPageReader _instance;
    static readonly object Create = new();

    public static ChunkedPageReader Instance
    {
        get
        {
            if (_instance != null) return _instance;
            lock (Create) return _instance ??= new ChunkedPageReader();
        }
    }

    public readonly ChunkedBrotliDecoder Decoder = new(Workers);
    public readonly object Lock = new();
    public byte[] Scratch = [];
}

/// <summary>Maps logical atlas pages onto a fixed number of physical layers of one Texture2DArray, LRU-evicting pages
/// not used this frame. Loads run on workers into persistent staging transfer buffers; uploads are budgeted per frame.
/// The logical→physical page table lives in a GPU storage buffer, so chunk instance buffers never need re-baking.</summary>
public sealed unsafe class PageResidency
{
    enum PState : byte { None, Queued, Loading, Resident, Failed }

    readonly IntPtr _device;
    public readonly IntPtr Texture;
    public readonly int Layers, LogicalCount, PageSize, MipCount;
    /// <summary>Pack (source) format; shader flags follow it. <see cref="UploadFormat"/> is what the GPU array holds.</summary>
    public readonly PixelFormat Format, UploadFormat;
    /// <summary>16-bit pack on a GPU (or --force-expand) without sampleable B5G5R5A1: workers expand pages to RGBA8.</summary>
    public readonly bool Expanding, NativeSupported;
    public long ChainBytes => _chainBytes;
    /// <summary>VRAM held by the page array: layers × chain bytes.</summary>
    public long VramBytes => (long)Layers * _chainBytes;
    readonly string[] _paths;
    readonly WorkerPool _workers;

    readonly int[] _layerOf;      // logical → layer (-1)
    readonly int[] _logicalOf;    // layer → logical (-1)
    readonly long[] _layerUsed;   // last frame a layer was touched
    readonly PState[] _state;
    readonly long[] _requested;   // last frame a non-resident page was wanted
    readonly int[] _queue;
    int _qHead, _qCount;

    // staging
    readonly IntPtr[] _staging;
    readonly PageJob[] _jobs;
    readonly byte[] _slotState; // 0 free, 1 loading, 2 loaded
    readonly int _chainBytes;

    public readonly uint[] Table;   // GPU page table contents
    public bool TableDirty = true;

    public int UploadsThisFrame, ResidentCount, MissingVisible, Evictions, TotalUploads;
    public int UploadBudget;

    public PageResidency(IntPtr device, PackSet pack, WorkerPool workers, int layers, int uploadBudget, int stagingSlots, bool forceExpand = false)
        : this(device, pack.PagePaths, pack.Format, pack.PageSize, pack.PageMips, workers, layers, uploadBudget, stagingSlots, forceExpand) { }

    /// <summary>A residency manager over any set of same-format pages (art+tex, or the Indexed8 anim pages).</summary>
    public PageResidency(IntPtr device, string[] paths, PixelFormat format, int pageSize, int mips, WorkerPool workers, int layers, int uploadBudget, int stagingSlots, bool forceExpand = false)
    {
        _device = device;
        _paths = paths;
        _workers = workers;
        Layers = layers;
        LogicalCount = Math.Max(1, paths.Length);
        PageSize = pageSize;
        MipCount = mips;
        Format = format;
        UploadBudget = uploadBudget;
        NativeSupported = Supports(device, Format);
        Expanding = Format == PageFormats.B5G5R5A1 && (forceExpand || !NativeSupported);
        UploadFormat = Expanding ? PixelFormat.Rgba8 : Format;
        _chainBytes = PageFormats.ChainBytes(PageSize, MipCount, UploadFormat);
        var gpuFormat = PageFormats.Gpu(UploadFormat);
        // Integer formats are never "sample"-capable on D3D12 (SHADER_SAMPLE), which is what SDL reports for SAMPLER
        // usage; the Indexed8 path only uses Load, which every backend supports for R8_UINT, so creation decides.
        if (Format == PixelFormat.Indexed8) NativeSupported = true;
        if (!(Expanding ? Supports(device, UploadFormat) : NativeSupported))
            throw new NotSupportedException($"GPU does not support {PageFormats.Name(UploadFormat)} ({gpuFormat}) as a sampled 2D array texture");

        var ci = new SDL_GPUTextureCreateInfo
        {
            type = SDL_GPUTextureType.SDL_GPU_TEXTURETYPE_2D_ARRAY,
            format = gpuFormat,
            usage = SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER,
            width = (uint)PageSize, height = (uint)PageSize,
            layer_count_or_depth = (uint)layers,
            num_levels = (uint)MipCount,
        };
        Texture = SDL_CreateGPUTexture(device, in ci);
        if (Texture == IntPtr.Zero) throw new Exception($"atlas texture ({PageFormats.Name(Format)}): " + SDL_GetError());

        _layerOf = new int[LogicalCount];
        _logicalOf = new int[layers];
        _layerUsed = new long[layers];
        _state = new PState[LogicalCount];
        _requested = new long[LogicalCount];
        _queue = new int[LogicalCount];
        Table = new uint[LogicalCount];
        Array.Fill(_layerOf, -1);
        Array.Fill(_logicalOf, -1);
        Array.Fill(_layerUsed, long.MinValue);
        Array.Fill(Table, 0xFFFFFFFFu);

        _staging = new IntPtr[stagingSlots];
        _jobs = new PageJob[stagingSlots];
        _slotState = new byte[stagingSlots];
        for (int i = 0; i < stagingSlots; i++)
        {
            var tci = new SDL_GPUTransferBufferCreateInfo { usage = SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD, size = (uint)_chainBytes };
            _staging[i] = SDL_CreateGPUTransferBuffer(device, in tci);
            _jobs[i] = new PageJob { Owner = this, Slot = i, Capacity = _chainBytes, Format = Format, Expand = Expanding };
        }
    }

    static bool Supports(IntPtr device, PixelFormat f)
        => SDL_GPUTextureSupportsFormat(device, PageFormats.Gpu(f), SDL_GPUTextureType.SDL_GPU_TEXTURETYPE_2D_ARRAY, SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER);

    public string FormatDescription => Expanding
        ? $"{PageFormats.Name(Format)} expanded to {PageFormats.Name(UploadFormat)} on workers (native 16-bit supported: {NativeSupported})"
        : PageFormats.Name(Format);

    /// <summary>A visible sprite uses this page this frame.</summary>
    public void Touch(int page, long frame)
    {
        if ((uint)page >= (uint)LogicalCount) return;
        int layer = _layerOf[page];
        if (layer >= 0) { _layerUsed[layer] = frame; return; }
        _requested[page] = frame;
        if (_state[page] == PState.None)
        {
            _state[page] = PState.Queued;
            _queue[(_qHead + _qCount) % LogicalCount] = page;
            _qCount++;
        }
        MissingVisible++;
    }

    public void BeginFrame()
    {
        UploadsThisFrame = 0;
        MissingVisible = 0;
    }

    /// <summary>Starts worker loads for queued pages into free staging buffers.</summary>
    public void StartLoads(long frame)
    {
        for (int s = 0; s < _staging.Length && _qCount > 0; s++)
        {
            if (_slotState[s] != 0) continue;
            int page = -1;
            while (_qCount > 0)
            {
                int p = _queue[_qHead];
                _qHead = (_qHead + 1) % LogicalCount; _qCount--;
                if (_state[p] != PState.Queued) continue;
                if (_requested[p] < frame - 10) { _state[p] = PState.None; continue; } // no longer wanted
                page = p;
                break;
            }
            if (page < 0) break;
            long tm = System.Diagnostics.Stopwatch.GetTimestamp();
            var ptr = SDL_MapGPUTransferBuffer(_device, _staging[s], true); // cycle: never stomp a buffer still being copied from
            MainUploadTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tm;
            if (ptr == IntPtr.Zero) { _state[page] = PState.None; break; }
            var job = _jobs[s];
            job.Page = page;
            job.Path = _paths[page];
            job.Dst = (byte*)ptr;
            _slotState[s] = 1;
            _state[page] = PState.Loading;
            _workers.Enqueue(job, high: true);
        }
    }

    public bool Pending => _qCount > 0 || HasLoading();

    bool HasLoading()
    {
        for (int i = 0; i < _slotState.Length; i++) if (_slotState[i] != 0) return true;
        return false;
    }

    public void OnLoaded(PageJob job)
    {
        ReadStats.Add(job); // worker: file read (+ Brotli decode) straight into the mapped staging buffer
        ReadTicksSum += job.ReadTicks;
        DecodeTicksSum += job.DecodeTicks;
        ExpandTicksSum += job.ExpandTicks;
        WaitTicksSum += job.WaitTicks;
        FileBytesSum += job.FileBytes;
        if (job.Compressed) CompressedPages++;
        _slotState[job.Slot] = 2;
    }

    /// <summary>Worker page read time, and main-thread time to map a staging buffer plus unmap + record the mip copies.</summary>
    public JobStats ReadStats;
    public long MainUploadTicks, MainUploadCount;
    public long ReadTicksSum, DecodeTicksSum, ExpandTicksSum, WaitTicksSum, FileBytesSum;
    public int CompressedPages;

    /// <summary>Uploads loaded pages (up to the budget) into free or least-recently-used layers. Call inside a copy pass.</summary>
    public void Upload(IntPtr copyPass, long frame)
    {
        for (int s = 0; s < _staging.Length; s++)
        {
            if (_slotState[s] != 2) continue;
            var job = _jobs[s];
            int page = job.Page;
            if (!job.Ok)
            {
                SDL_UnmapGPUTransferBuffer(_device, _staging[s]);
                _slotState[s] = 0;
                _state[page] = PState.Failed;
                Console.Error.WriteLine($"page {page}: load failed");
                continue;
            }
            if (UploadsThisFrame >= UploadBudget) continue;
            int layer = PickLayer(frame);
            if (layer < 0) continue; // every layer is visible this frame: keep it staged and retry
            int old = _logicalOf[layer];
            if (old >= 0)
            {
                _layerOf[old] = -1;
                _state[old] = PState.None;
                Table[old] = 0xFFFFFFFFu;
                ResidentCount--;
                Evictions++;
            }
            long tu = System.Diagnostics.Stopwatch.GetTimestamp();
            SDL_UnmapGPUTransferBuffer(_device, _staging[s]);
            uint offset = 0;
            int mips = Math.Min(job.Mips, MipCount);
            for (int m = 0; m < mips; m++)
            {
                uint dim = (uint)(job.Size >> m);
                var src = new SDL_GPUTextureTransferInfo { transfer_buffer = _staging[s], offset = offset, pixels_per_row = dim, rows_per_layer = dim };
                var dst = new SDL_GPUTextureRegion { texture = Texture, mip_level = (uint)m, layer = (uint)layer, w = dim, h = dim, d = 1 };
                SDL_UploadToGPUTexture(copyPass, in src, in dst, false);
                offset += (uint)PageFormats.MipBytes(job.Size, m, UploadFormat);
            }
            MainUploadTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tu;
            MainUploadCount++;
            _layerOf[page] = layer;
            _logicalOf[layer] = page;
            _layerUsed[layer] = frame;
            _state[page] = PState.Resident;
            Table[page] = (uint)layer;
            TableDirty = true;
            ResidentCount++;
            UploadsThisFrame++;
            TotalUploads++;
            _slotState[s] = 0;
        }
    }

    int PickLayer(long frame)
    {
        int best = -1;
        long bestUsed = long.MaxValue;
        for (int l = 0; l < Layers; l++)
        {
            if (_logicalOf[l] < 0) return l;
            if (_layerUsed[l] < frame && _layerUsed[l] < bestUsed) { best = l; bestUsed = _layerUsed[l]; }
        }
        return best;
    }

    public void Dispose()
    {
        foreach (var t in _staging) SDL_ReleaseGPUTransferBuffer(_device, t);
        SDL_ReleaseGPUTexture(_device, Texture);
    }
}
