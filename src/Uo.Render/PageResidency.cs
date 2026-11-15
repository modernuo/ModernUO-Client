using Uo.Pack;
using static SDL3.SDL;

namespace Uo.Render;

/// <summary>Reads one atlas page file straight into a mapped GPU transfer buffer (no main-thread memcpy).
/// Kind 0: raw read into the mapped buffer. Kind 1 (Brotli): the compressed payload is read into a reusable per-job
/// buffer and decoded with BrotliDecoder directly into the mapped buffer (no full-size intermediate copy).
/// Expand (16-bit pack, GPU without 16-bit sampling): the page is read or decoded in pieces into a reusable scratch
/// buffer and each piece is expanded to RGBA8 into the mapped buffer (the whole chain doubles, so mip offsets double).
/// Kind 2 (chunked Brotli): a decoder rented from <see cref="ChunkedPageReader"/> decodes the chunks in parallel straight
/// into the mapped buffer, or into the lease's scratch when expanding.</summary>
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
    ChunkedPageReader.Lease _own;

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
            // A page of about one chunk (1024² Indexed8 anims: 1 MiB + the 16-byte PageHeader) gains nothing from decoder
            // threads: decode it here, beside other pages, instead of queueing for a pooled decoder.
            ChunkedHeader ch = default;
            RandomAccess.Read(fh0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(new Span<ChunkedHeader>(ref ch)), 16);
            bool pooled = ch.LogicalBytes > ch.ChunkBytes + ch.ChunkBytes / 2;
            var lease = pooled ? cr.Rent() : _own ??= new ChunkedPageReader.Lease(1);
            try
            {
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                WaitTicks = t1 - tw;
                if (!Expand)
                {
                    lease.Decoder.Decode(fh0, 16, ch, new Span<byte>(Dst, Capacity), 16, phb); // ch read above: no second header read
                    ph = System.Runtime.InteropServices.MemoryMarshal.Read<PageHeader>(phb);
                    if (!Fits(ph)) return;
                    DecodeTicks = System.Diagnostics.Stopwatch.GetTimestamp() - t1;
                }
                else
                {
                    if (lease.Scratch.Length < Capacity / 2) lease.Scratch = new byte[Capacity / 2];
                    lease.Decoder.Decode(fh0, 16, ch, lease.Scratch.AsSpan(0, Capacity / 2), 16, phb);
                    ph = System.Runtime.InteropServices.MemoryMarshal.Read<PageHeader>(phb);
                    if (!Fits(ph)) return;
                    long te = System.Diagnostics.Stopwatch.GetTimestamp();
                    DecodeTicks = te - t1;
                    PixelExpand.Expand(lease.Scratch.AsSpan(0, (int)ph.DataBytes), new Span<byte>(Dst, (int)ph.DataBytes * 2));
                    ExpandTicks = System.Diagnostics.Stopwatch.GetTimestamp() - te;
                }
            }
            finally { if (pooled) cr.Return(lease); }
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

/// <summary>A small process-wide pool of <see cref="ChunkedBrotliDecoder"/>s for multi-chunk Kind=2 pages. Each page job
/// rents one for its Decode (its threads plus the calling worker decode that page's chunks in parallel), so a burst of
/// pages decodes <see cref="Decoders"/> at a time instead of queueing on one decoder; single-chunk pages skip the pool.
/// The lease's scratch serves the RGBA8 expansion path.</summary>
public sealed class ChunkedPageReader
{
    /// <summary>Pool size and threads per decoder (cores/2, 2..8, as the old single decoder); set before the first Kind=2
    /// page loads. Two decoders keep a lone page's latency and raise burst throughput ~1.7x (page-decode-bench --parallel 4).</summary>
    public static int Decoders = 2;
    public static int ThreadsPerDecoder = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
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

    public sealed class Lease(int threads)
    {
        public readonly ChunkedBrotliDecoder Decoder = new(threads);
        public byte[] Scratch = [];
    }

    readonly Stack<Lease> _free = new();
    readonly object _lock = new();
    int _created;

    public Lease Rent()
    {
        lock (_lock)
        {
            while (_free.Count == 0)
            {
                if (_created < Math.Max(1, Decoders)) { _created++; goto create; }
                Monitor.Wait(_lock);
            }
            return _free.Pop();
        }
    create:
        return new Lease(ThreadsPerDecoder);
    }

    public void Return(Lease lease)
    {
        lock (_lock)
        {
            _free.Push(lease);
            Monitor.Pulse(_lock);
        }
    }
}

/// <summary>Maps logical atlas pages onto a fixed number of physical layers of one Texture2DArray, LRU-evicting pages
/// not used this frame. Loads run on workers into persistent staging transfer buffers; uploads are budgeted per frame.
/// The logical→physical page table lives in a GPU storage buffer, so chunk instance buffers never need re-baking.</summary>
public sealed unsafe class PageResidency
{
    enum PState : byte { None, Queued, Loading, Resident, Failed }

    readonly IntPtr _device;
    /// <summary>The page array. Replaced when it grows (bind it per frame, after <see cref="Upload"/>).</summary>
    public IntPtr Texture { get; private set; }
    /// <summary>Layers of the array now; grows on demand up to <see cref="MaxLayers"/> (<see cref="GrowthPolicy"/>).</summary>
    public int Layers { get; private set; }
    public readonly int MaxLayers;
    public readonly int LogicalCount, PageSize, MipCount;
    readonly SDL_GPUTextureCreateInfo _ci;
    readonly string _name;
    GrowthPolicy _growth;
    /// <summary>Growth steps and their main-thread cost (create the larger array + record the layer copies).</summary>
    public GrowthStats Growth;
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
    readonly StagingSlots _slots;
    readonly int _chainBytes;

    public readonly uint[] Table;   // GPU page table contents
    public bool TableDirty = true;

    public int UploadsThisFrame, ResidentCount, MissingVisible, Evictions, TotalUploads;
    public int UploadBudget;

    public PageResidency(IntPtr device, PackSet pack, WorkerPool workers, int layers, int uploadBudget, int stagingSlots, bool forceExpand = false, int maxLayers = 0)
        : this(device, pack.PagePaths, pack.Format, pack.PageSize, pack.PageMips, workers, layers, uploadBudget, stagingSlots, forceExpand, maxLayers) { }

    /// <summary>A residency manager over any set of same-format pages (art+tex, or the Indexed8 anim pages).
    /// <paramref name="maxLayers"/> &gt; <paramref name="layers"/>: the array starts at <paramref name="layers"/> and grows
    /// on demand (<see cref="GrowthPolicy"/>); 0 = fixed.</param></summary>
    public PageResidency(IntPtr device, string[] paths, PixelFormat format, int pageSize, int mips, WorkerPool workers, int layers, int uploadBudget, int stagingSlots, bool forceExpand = false, int maxLayers = 0)
    {
        _device = device;
        _paths = paths;
        _workers = workers;
        Layers = layers;
        MaxLayers = Math.Max(layers, maxLayers);
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
        _ci = ci;
        _name = $"Uo.Render pages {PageFormats.Name(Format)} {PageSize}px";
        Texture = Renderer.CreateNamedTexture(device, ci, $"{_name} x{layers}");
        if (Texture == IntPtr.Zero) throw new Exception($"atlas texture ({PageFormats.Name(Format)}): " + SDL_GetError());

        // per-layer tables at the ceiling (a few hundred bytes), so growing never reallocates them
        _layerOf = new int[LogicalCount];
        _logicalOf = new int[MaxLayers];
        _layerUsed = new long[MaxLayers];
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
        _slots = new StagingSlots(stagingSlots);
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

    public bool IsResident(int page) => (uint)page < (uint)LogicalCount && _layerOf[page] >= 0;

    /// <summary>A visible sprite uses this page this frame.</summary>
    public void Touch(int page, long frame)
    {
        RenderDiagnostics.Add(DiagMetric.PageTouches, 1);
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

#if CUO_DEVTOOLS
    // CLEANUP(release): the reuse checker verifies that a full touch walk cannot change any eviction input.
    internal ulong TouchDecisionHash()
    {
        ulong h = (ulong)MissingVisible;
        for (int i = 0; i < _layerUsed.Length; i++) h = (h ^ (ulong)_layerUsed[i]) * 1099511628211UL;
        for (int i = 0; i < LogicalCount; i++)
        {
            h = (h ^ (ulong)_requested[i]) * 1099511628211UL;
            h = (h ^ (ulong)_state[i]) * 1099511628211UL;
            h = (h ^ Table[i]) * 1099511628211UL;
        }
        return h;
    }
#endif

    /// <summary>The renderer's copy tracker (set by <see cref="Renderer.RenderFrame"/>): staging slots are reused only
    /// after their last copy retired, and mapped without cycling (<see cref="StagingSlots"/>).</summary>
    public CopyRetirement Retire { get => _slots.Retire; set => _slots.Retire = value; }
    /// <summary>Staging slot reuse and its waits (memory round 3).</summary>
    public StagingSlots Slots => _slots;

    /// <summary>Starts worker loads for queued pages into free staging buffers whose last copy has retired. A page that
    /// finds only slots still being copied from stays queued for a later frame (counted in <see cref="Slots"/>).</summary>
    public void StartLoads(long frame)
    {
        _slots.Retire?.Poll();
        bool blocked = false;
        for (int s = 0; s < _staging.Length && _qCount > 0; s++)
        {
            if (_slotState[s] != 0) continue;
            if (!_slots.Reusable(s)) { blocked = true; continue; }
            int page = NextQueued(frame);
            if (page < 0) break;
            _qHead = (_qHead + 1) % LogicalCount; _qCount--;
            long tm = System.Diagnostics.Stopwatch.GetTimestamp();
            // no cycling: the slot's last copy has retired, so rewriting it in place is safe and its container stays at
            // one buffer (cycling only without a tracker, as before)
            var ptr = SDL_MapGPUTransferBuffer(_device, _staging[s], _slots.CycleOnMap);
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
        _slots.Track(blocked && NextQueued(frame) >= 0, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    /// <summary>The page at the head of the queue that is still wanted, without taking it; drops stale entries.</summary>
    int NextQueued(long frame)
    {
        while (_qCount > 0)
        {
            int p = _queue[_qHead];
            if (_state[p] == PState.Queued)
            {
                if (_requested[p] >= frame - 10) return p;
                _state[p] = PState.None; // no longer wanted
            }
            _qHead = (_qHead + 1) % LogicalCount; _qCount--;
        }
        return -1;
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

    /// <summary>Uploads loaded pages (up to the budget) into free or least-recently-used layers. Call inside a copy pass,
    /// before anything samples <see cref="Texture"/> in this command buffer: it may first grow the array
    /// (<paramref name="hold"/> = the host shows its previous frame or the camera jumped, a good moment to grow).</summary>
    public void Upload(IntPtr copyPass, long frame, bool hold = false)
    {
        // the whole batch counts: growing only when the array is already full would let this batch's later uploads
        // evict (or wait for) layers that a larger array keeps
        int incoming = BatchUploads();
        if (Layers < MaxLayers && !GrowFailed && _growth.CheckBatch(ResidentCount, Layers, MaxLayers, incoming, hold, frame))
            Grow(copyPass, GrowthPolicy.NextFor(Layers, MaxLayers, (long)ResidentCount + incoming));

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
                RenderDiagnostics.Add(DiagMetric.TextureUploadBytes, PageFormats.MipBytes(job.Size, m, UploadFormat));
                RenderDiagnostics.Add(DiagMetric.TextureUploadCalls, 1);
                offset += (uint)PageFormats.MipBytes(job.Size, m, UploadFormat);
            }
            _slots.Copied(s);
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

    /// <summary>Loaded pages this upload pass will place: the loaded, successful slots, within what is left of the budget.</summary>
    int BatchUploads()
    {
        int n = 0;
        for (int i = 0; i < _slotState.Length; i++) if (_slotState[i] == 2 && _jobs[i].Ok) n++;
        return Math.Min(n, Math.Max(0, UploadBudget - UploadsThisFrame));
    }

    /// <summary>Replaces the array with a larger one: the resident layers are copied GPU-side (every mip) inside this copy
    /// pass, before this frame's uploads; layer indices and the page table stay as they are, so nothing is dropped or
    /// redrawn differently. The old array is released (SDL destroys it once the frames in flight are done with it).</summary>
    void Grow(IntPtr copyPass, int newLayers)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var ci = _ci;
        ci.layer_count_or_depth = (uint)newLayers;
        var tex = Renderer.CreateNamedTexture(_device, ci, $"{_name} x{newLayers}");
        _growth.Grew();
        if (tex == IntPtr.Zero)
        {
            Console.Error.WriteLine($"page array growth {Layers} -> {newLayers} layers failed: {SDL_GetError()}; staying at {Layers}");
            GrowFailed = true;
            return;
        }
        for (int l = 0; l < Layers; l++)
        {
            if (_logicalOf[l] < 0) continue;
            for (int m = 0; m < MipCount; m++)
            {
                uint dim = (uint)Math.Max(1, PageSize >> m);
                var src = new SDL_GPUTextureLocation { texture = Texture, mip_level = (uint)m, layer = (uint)l };
                var dst = new SDL_GPUTextureLocation { texture = tex, mip_level = (uint)m, layer = (uint)l };
                SDL_CopyGPUTextureToTexture(copyPass, in src, in dst, dim, dim, 1, false);
            }
        }
        SDL_ReleaseGPUTexture(_device, Texture);
        int from = Layers;
        Texture = tex;
        Layers = newLayers;
        Growth.Add((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency, from, newLayers);
    }

    /// <summary>A growth step could not create the larger array; the array stays at its size (and evicts as before).</summary>
    public bool GrowFailed;

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

    bool _disposed;

    /// <summary>One-shot: a second call (a retried world dispose) must not release the transfer buffers again; SDL's
    /// Vulkan backend frees their containers immediately.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var t in _staging) SDL_ReleaseGPUTransferBuffer(_device, t);
        SDL_ReleaseGPUTexture(_device, Texture);
    }
}
