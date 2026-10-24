using Uo.Pack;
using static SDL3.SDL;

namespace Uo.Render;

/// <summary>Reads one atlas page file straight into a mapped GPU transfer buffer (no main-thread memcpy).
/// Kind 0: raw read into the mapped buffer. Kind 1 (Brotli): the compressed payload is read into a reusable per-job
/// buffer and decoded with BrotliDecoder directly into the mapped buffer (no full-size intermediate copy).</summary>
public sealed unsafe class PageJob : Job
{
    public int Page, Slot;
    public string Path;
    public byte* Dst;
    public int Capacity;
    public bool Ok, Compressed;
    public int Mips, Size;
    public PixelFormat Format;
    public long ReadTicks, DecodeTicks, FileBytes;
    byte[] _compressed = new byte[1 << 20];

    public override void Run()
    {
        Ok = false;
        Compressed = false;
        DecodeTicks = 0;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        FileBytes = fs.Length;
        Span<byte> fhb = stackalloc byte[16];
        fs.ReadExactly(fhb);
        var fh = System.Runtime.InteropServices.MemoryMarshal.Read<FileHeader>(fhb);
        if (fh.Magic != PackConst.MagicPage) return;
        Span<byte> phb = stackalloc byte[16];
        PageHeader ph;
        if (fh.Kind == FileKind.Raw)
        {
            fs.ReadExactly(phb);
            ph = System.Runtime.InteropServices.MemoryMarshal.Read<PageHeader>(phb);
            if (ph.Format != Format || ph.DataBytes > Capacity) return;
            fs.ReadExactly(new Span<byte>(Dst, (int)ph.DataBytes));
            ReadTicks = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        }
        else if (fh.Kind == FileKind.Brotli)
        {
            Compressed = true;
            int n = (int)(fs.Length - 16);
            if (_compressed.Length < n) _compressed = new byte[n + (n >> 3)];
            fs.ReadExactly(_compressed.AsSpan(0, n));
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            ReadTicks = t1 - t0;
            using var dec = new System.IO.Compression.BrotliDecoder();
            var src = new ReadOnlySpan<byte>(_compressed, 0, n);
            // 1) the PageHeader (16 bytes) into a stack span
            var st = dec.Decompress(src, phb, out int used, out int wrote);
            if (wrote != 16) return;
            src = src[used..];
            ph = System.Runtime.InteropServices.MemoryMarshal.Read<PageHeader>(phb);
            if (ph.Format != Format || ph.DataBytes > Capacity) return;
            // 2) the mip chain straight into the mapped transfer buffer
            var dst = new Span<byte>(Dst, (int)ph.DataBytes);
            int total = 0;
            while (total < dst.Length)
            {
                st = dec.Decompress(src, dst[total..], out used, out wrote);
                src = src[used..];
                total += wrote;
                if (st == System.Buffers.OperationStatus.InvalidData || (used == 0 && wrote == 0)) return;
            }
            DecodeTicks = System.Diagnostics.Stopwatch.GetTimestamp() - t1;
        }
        else return;
        Mips = ph.MipCount;
        Size = ph.Width;
        Ok = true;
    }
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
    public readonly PixelFormat Format;
    public long ChainBytes => _chainBytes;
    /// <summary>VRAM held by the page array: layers × chain bytes.</summary>
    public long VramBytes => (long)Layers * _chainBytes;
    readonly PackSet _pack;
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

    public PageResidency(IntPtr device, PackSet pack, WorkerPool workers, int layers, int uploadBudget, int stagingSlots)
    {
        _device = device;
        _pack = pack;
        _workers = workers;
        Layers = layers;
        LogicalCount = Math.Max(1, pack.PageCount);
        PageSize = pack.PageSize;
        MipCount = pack.PageMips;
        Format = pack.Format;
        UploadBudget = uploadBudget;
        _chainBytes = PageFormats.ChainBytes(PageSize, MipCount, Format);
        var gpuFormat = PageFormats.Gpu(Format);
        if (!SDL_GPUTextureSupportsFormat(device, gpuFormat, SDL_GPUTextureType.SDL_GPU_TEXTURETYPE_2D_ARRAY, SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER))
            throw new NotSupportedException($"GPU does not support {PageFormats.Name(Format)} ({gpuFormat}) as a sampled 2D array texture");

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
        if (Texture == IntPtr.Zero) throw new Exception("atlas texture: " + SDL_GetError());

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
            _jobs[i] = new PageJob { Slot = i, Capacity = _chainBytes, Format = Format };
        }
    }

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
            job.Path = _pack.PagePaths[page];
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
        FileBytesSum += job.FileBytes;
        if (job.Compressed) CompressedPages++;
        _slotState[job.Slot] = 2;
    }

    /// <summary>Worker page read time, and main-thread time to map a staging buffer plus unmap + record the mip copies.</summary>
    public JobStats ReadStats;
    public long MainUploadTicks, MainUploadCount;
    public long ReadTicksSum, DecodeTicksSum, FileBytesSum;
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
                offset += (uint)PageFormats.MipBytes(job.Size, m, Format);
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
