using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Uo.Render;

/// <summary>Map streaming: decides which 8×8 chunks are needed around the camera, loads regions and bakes chunks on
/// workers, sub-allocates baked instances into one persistent GPU buffer, evicts far chunks, and builds the per-frame
/// visible chunk table. Main thread only (workers only see jobs). Fixed-capacity arrays: no steady-state allocation.</summary>
public sealed unsafe class ChunkStore
{
    public const int MaxPagesPerChunk = 32;
    public const int MaxSlots = 16384;
    const int TopMargin = 800, BottomMargin = 600, SideMargin = 200; // art above its tile / negative z / wide art
    const int EvictAfterFrames = 30;          // and at least EvictAfterSeconds: hysteresis independent of fps
    const double EvictAfterSeconds = 1.0;
    readonly long[] _lastNeededTicks = new long[MaxSlots];
    long _nowTicks;
    /// <summary>Bounds the bake job pool (and its memory) and keeps worker latency predictable after a camera jump.</summary>
    public const int MaxBakesInFlight = 256;

    enum State : byte { Free, WaitRegions, Baking, Baked, Ready }

    readonly PackSet _pack;
    readonly WorkerPool _workers;
    public readonly RangeAllocator Alloc;

    readonly int[] _key = new int[MaxSlots], _bx = new int[MaxSlots], _by = new int[MaxSlots];
    readonly State[] _state = new State[MaxSlots];
    readonly int[] _first = new int[MaxSlots], _count = new int[MaxSlots];
    readonly int[] _minX = new int[MaxSlots], _minY = new int[MaxSlots], _maxX = new int[MaxSlots], _maxY = new int[MaxSlots];
    readonly ushort[] _pages = new ushort[MaxSlots * MaxPagesPerChunk];
    readonly byte[] _pageCount = new byte[MaxSlots];
    readonly long[] _lastNeeded = new long[MaxSlots];
    readonly BakeJob[] _job = new BakeJob[MaxSlots];
    readonly int[] _active = new int[MaxSlots], _activePos = new int[MaxSlots];
    int _activeCount;
    readonly int[] _freeSlots = new int[MaxSlots];
    int _freeCount;
    readonly Dictionary<int, int> _map = new(MaxSlots);
    readonly Stack<BakeJob> _jobPool = new(1024);

    readonly int[] _uploadQueue = new int[MaxSlots];
    int _uqHead, _uqCount;

    // regions
    readonly Dictionary<int, Region> _regions = new(64);
    readonly Region[] _regionList = new Region[256];
    int _regionCount;
    readonly Stack<RegionJob> _regionJobPool = new(64);
    readonly Stack<Region> _regionPool = new(64);
    const int MaxRegions = 24;

    // upload records for this frame (executed in the copy pass)
    public readonly uint[] UploadSrc = new uint[4096], UploadDst = new uint[4096], UploadSize = new uint[4096];
    public int UploadCount;

    public int ReadyChunks, PendingChunks, BakesInFlight;
    public JobStats BakeStats, RegionStats;
    /// <summary>Chunks intersecting the view that are not drawable yet this frame (visible pop-in).</summary>
    public int MissingVisible;
    public bool OutOfGpuSpace;

    public ChunkStore(PackSet pack, WorkerPool workers, int instanceCapacity)
    {
        _pack = pack;
        _workers = workers;
        Alloc = new RangeAllocator(instanceCapacity);
        for (int i = MaxSlots - 1; i >= 0; i--) _freeSlots[_freeCount++] = i;
        for (int i = 0; i < MaxBakesInFlight; i++) _jobPool.Push(new BakeJob()); // pre-sized: no steady-state allocation
        BakeJob.Pack = pack;
    }

    public int ActiveChunks => _activeCount;
    public int RegionsLoaded => _regionCount;

    /// <summary>Marks the chunks overlapping <paramref name="need"/> (world pixels) as needed, starting loads/bakes, and evicts stale ones.</summary>
    public void Update(long frame, in RectI view, in RectI need)
    {
        _nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        // Visible chunks first so they win the in-flight bake budget, then the prefetch ring.
        MarkRange(frame, view);
        MarkRange(frame, need);

        // evict stale chunks
        PendingChunks = 0; ReadyChunks = 0;
        for (int i = _activeCount - 1; i >= 0; i--)
        {
            int slot = _active[i];
            var st = _state[slot];
            if (st == State.Ready) ReadyChunks++; else PendingChunks++;
            if (_lastNeeded[slot] >= frame - EvictAfterFrames || st == State.Baking) continue;
            if (_nowTicks - _lastNeededTicks[slot] < (long)(EvictAfterSeconds * System.Diagnostics.Stopwatch.Frequency)) continue;
            if (st == State.Ready && _count[slot] > 0) Alloc.Free(_first[slot], _count[slot]);
            if (st == State.Baked) { ReturnJob(_job[slot]); _job[slot] = null; }
            FreeSlot(slot);
        }

        if ((frame & 63) == 0) EvictRegions(frame);
    }

    void MarkRange(long frame, in RectI need)
    {
        // world px -> tile: X-Y = sx/22, X+Y = sy/22. Expand by margins so tall art and z offsets are covered.
        float l = need.X0 - SideMargin, r = need.X1 + SideMargin, t = need.Y0 - BottomMargin, b = need.Y1 + TopMargin;
        int txMin = (int)MathF.Floor((l + t) / 44f) - 1, txMax = (int)MathF.Ceiling((r + b) / 44f) + 1;
        int tyMin = (int)MathF.Floor((t - r) / 44f) - 1, tyMax = (int)MathF.Ceiling((b - l) / 44f) + 1;
        int bx0 = Math.Max(0, txMin >> 3), bx1 = Math.Min(_pack.BlocksX - 1, txMax >> 3);
        int by0 = Math.Max(0, tyMin >> 3), by1 = Math.Min(_pack.BlocksY - 1, tyMax >> 3);

        for (int by = by0; by <= by1; by++)
            for (int bx = bx0; bx <= bx1; bx++)
            {
                // conservative screen bounds of the block
                int x0 = (bx * 8 - (by * 8 + 7)) * 22 - 22 - SideMargin, x1 = ((bx * 8 + 7) - by * 8) * 22 + 22 + SideMargin;
                int y0 = (bx * 8 + by * 8) * 22 - 22 - TopMargin, y1 = (bx * 8 + by * 8 + 14) * 22 + 22 + BottomMargin;
                if (x1 < need.X0 || x0 > need.X1 || y1 < need.Y0 || y0 > need.Y1) continue;
                int key = by * _pack.BlocksX + bx;
                if (!_map.TryGetValue(key, out int slot))
                {
                    if (_freeCount == 0) continue;
                    slot = _freeSlots[--_freeCount];
                    _map[key] = slot;
                    _key[slot] = key; _bx[slot] = bx; _by[slot] = by;
                    _state[slot] = State.WaitRegions;
                    _activePos[slot] = _activeCount;
                    _active[_activeCount++] = slot;
                }
                _lastNeeded[slot] = frame;
                _lastNeededTicks[slot] = _nowTicks;
                if (_state[slot] == State.WaitRegions && BakesInFlight < MaxBakesInFlight) TryDispatch(slot, frame);
            }
    }

    void FreeSlot(int slot)
    {
        _map.Remove(_key[slot]);
        _state[slot] = State.Free;
        int pos = _activePos[slot];
        int last = _active[--_activeCount];
        _active[pos] = last;
        _activePos[last] = pos;
        _freeSlots[_freeCount++] = slot;
    }

    void TryDispatch(int slot, long frame)
    {
        int rb = _pack.RegionBlocks;
        int bx = _bx[slot], by = _by[slot];
        int rx = bx / rb, ry = by / rb;
        int lbx = bx - rx * rb, lby = by - ry * rb;
        // Neighbour regions are needed only for blocks on a region edge (stretched land samples x-1..x+2).
        int dx0 = lbx == 0 ? -1 : 0, dx1 = lbx == rb - 1 ? 1 : 0, dy0 = lby == 0 ? -1 : 0, dy1 = lby == rb - 1 ? 1 : 0;
        bool ready = true;
        for (int dy = dy0; dy <= dy1; dy++)
            for (int dx = dx0; dx <= dx1; dx++)
            {
                var reg = GetRegion(rx + dx, ry + dy, frame);
                if (reg != null && !reg.Loaded) ready = false;
            }
        if (!ready) return;

        var job = _jobPool.Count > 0 ? _jobPool.Pop() : new BakeJob();
        Array.Clear(job.Regions);
        job.Rx0 = rx - 1; job.Ry0 = ry - 1;
        for (int dy = dy0; dy <= dy1; dy++)
            for (int dx = dx0; dx <= dx1; dx++)
            {
                var reg = GetRegion(rx + dx, ry + dy, frame);
                if (reg == null) continue;
                reg.Pins++;
                job.Regions[(dy + 1) * 3 + dx + 1] = reg;
            }
        job.Slot = slot; job.Bx = bx; job.By = by;
        _job[slot] = job;
        _state[slot] = State.Baking;
        BakesInFlight++;
        _workers.Enqueue(job, high: false);
    }

    Region GetRegion(int rx, int ry, long frame)
    {
        int rb = _pack.RegionBlocks;
        if (rx < 0 || ry < 0 || rx * rb >= _pack.BlocksX || ry * rb >= _pack.BlocksY) return null;
        int key = ry * 4096 + rx;
        if (_regions.TryGetValue(key, out var reg)) { reg.LastUsed = frame; return reg; }
        reg = _regionPool.Count > 0 ? _regionPool.Pop() : new Region();
        reg.Rx = rx; reg.Ry = ry; reg.Loaded = false; reg.Pins = 0; reg.Data = null; reg.LastUsed = frame;
        _regions[key] = reg;
        _regionList[_regionCount++] = reg;
        var job = _regionJobPool.Count > 0 ? _regionJobPool.Pop() : new RegionJob();
        job.Region = reg;
        job.Path = RegionPath(rx, ry);
        _workers.Enqueue(job, high: false);
        return reg;
    }

    // Region paths are created once per region load (rare), not per frame.
    string RegionPath(int rx, int ry) => Uo.Pack.PackConst.RegionPath(_pack.FacetDir, rx, ry);

    void EvictRegions(long frame)
    {
        while (_regionCount > MaxRegions)
        {
            int victim = -1;
            for (int i = 0; i < _regionCount; i++)
            {
                var r = _regionList[i];
                if (r.Pins > 0 || !r.Loaded || r.LastUsed >= frame - 120) continue;
                if (victim < 0 || r.LastUsed < _regionList[victim].LastUsed) victim = i;
            }
            if (victim < 0) return;
            var reg = _regionList[victim];
            _regions.Remove(reg.Ry * 4096 + reg.Rx);
            _regionList[victim] = _regionList[--_regionCount];
            reg.Data = null;
            _regionPool.Push(reg);
        }
    }

    public void OnRegionLoaded(RegionJob job)
    {
        RegionStats.Add(job);
        job.Region.Loaded = true;
        job.Region = null;
        _regionJobPool.Push(job);
    }

    /// <summary>Debug: injects a hand-built instance list as one chunk (uploaded through the normal path).
    /// Used by --hue-sheet with map streaming disabled.</summary>
    public void InjectDebugChunk(SpriteInstance[] instances, int count)
    {
        int slot = _freeSlots[--_freeCount];
        _map[-1] = slot;
        _key[slot] = -1;
        _activePos[slot] = _activeCount;
        _active[_activeCount++] = slot;
        var job = _jobPool.Pop();
        if (job.Instances.Length < count) job.Instances = new SpriteInstance[count];
        Array.Copy(instances, job.Instances, count);
        job.Count = count;
        job.MinX = job.MinY = int.MaxValue; job.MaxX = job.MaxY = int.MinValue;
        job.PageCount = 0;
        for (int i = 0; i < count; i++)
        {
            ref var e = ref instances[i];
            job.MinX = Math.Min(job.MinX, e.X); job.MinY = Math.Min(job.MinY, e.Y);
            job.MaxX = Math.Max(job.MaxX, e.X + (int)(e.Whf & 0xFFF)); job.MaxY = Math.Max(job.MaxY, e.Y + (int)((e.Whf >> 12) & 0xFFF));
            ushort pg = (ushort)(e.PageHue & 0xFFFF);
            bool have = false;
            for (int k = 0; k < job.PageCount; k++) have |= job.Pages[k] == pg;
            if (!have && job.PageCount < job.Pages.Length) job.Pages[job.PageCount++] = pg;
        }
        job.Slot = slot;
        _job[slot] = job;
        _state[slot] = State.Baking;
        BakesInFlight++;
        OnBaked(job);
    }

    public void OnBaked(BakeJob job)
    {
        BakeStats.Add(job);
        BakesInFlight--;
        for (int i = 0; i < 9; i++) if (job.Regions[i] != null) job.Regions[i].Pins--;
        Array.Clear(job.Regions);
        int slot = job.Slot;
        if (_state[slot] != State.Baking || _job[slot] != job) { ReturnJob(job); return; }
        _state[slot] = State.Baked;
        _uploadQueue[(_uqHead + _uqCount) % MaxSlots] = slot;
        _uqCount++;
    }

    void ReturnJob(BakeJob job) => _jobPool.Push(job);

    /// <summary>Copies baked chunks into the mapped frame transfer buffer (up to the budgets) and records GPU uploads.</summary>
    public int StageUploads(byte* mapped, ref uint offset, uint capacity, int maxChunks)
    {
        UploadCount = 0;
        int uploaded = 0;
        int stride = sizeof(SpriteInstance);
        while (_uqCount > 0 && uploaded < maxChunks && UploadCount < UploadSrc.Length)
        {
            int slot = _uploadQueue[_uqHead];
            if (_state[slot] != State.Baked) { _uqHead = (_uqHead + 1) % MaxSlots; _uqCount--; continue; }
            var job = _job[slot];
            int n = job.Count;
            uint bytes = (uint)(n * stride);
            if (offset + bytes > capacity) break;
            int first = 0;
            if (n > 0)
            {
                first = Alloc.Alloc(n);
                if (first < 0) { OutOfGpuSpace = true; break; }
                fixed (SpriteInstance* src = job.Instances)
                    Buffer.MemoryCopy(src, mapped + offset, capacity - offset, bytes);
                UploadSrc[UploadCount] = offset;
                UploadDst[UploadCount] = (uint)(first * stride);
                UploadSize[UploadCount] = bytes;
                UploadCount++;
                offset += bytes;
            }
            _uqHead = (_uqHead + 1) % MaxSlots; _uqCount--;
            _first[slot] = first;
            _count[slot] = n;
            _minX[slot] = job.MinX; _minY[slot] = job.MinY; _maxX[slot] = job.MaxX; _maxY[slot] = job.MaxY;
            int pc = job.PageCount;
            _pageCount[slot] = (byte)pc;
            job.Pages.AsSpan(0, pc).CopyTo(_pages.AsSpan(slot * MaxPagesPerChunk, pc));
            _state[slot] = State.Ready;
            _job[slot] = null;
            ReturnJob(job);
            uploaded++;
        }
        return uploaded;
    }

    public int UploadQueueLength => _uqCount;

    /// <summary>Visible chunk table for this frame. Touches the pages of every visible chunk in the residency manager.</summary>
    public int BuildDrawList(in RectI view, ChunkDraw[] table, PageResidency residency, long frame, out int instances)
    {
        int n = 0;
        uint total = 0;
        MissingVisible = 0;
        for (int i = 0; i < _activeCount && n < table.Length; i++)
        {
            int slot = _active[i];
            if (_state[slot] != State.Ready)
            {
                // Not drawable yet: does its land footprint (no art margins) intersect the view? Then it pops in later.
                int bx = _bx[slot], by = _by[slot];
                int x0 = (bx * 8 - (by * 8 + 7)) * 22 - 22, x1 = ((bx * 8 + 7) - by * 8) * 22 + 22;
                int y0 = (bx * 8 + by * 8) * 22 - 22, y1 = (bx * 8 + by * 8 + 14) * 22 + 22;
                if (!(x1 < view.X0 || x0 > view.X1 || y1 < view.Y0 || y0 > view.Y1)) MissingVisible++;
                continue;
            }
            if (_count[slot] == 0) continue;
            if (_maxX[slot] < view.X0 || _minX[slot] > view.X1 || _maxY[slot] < view.Y0 || _minY[slot] > view.Y1) continue;
            int pc = _pageCount[slot];
            int pb = slot * MaxPagesPerChunk;
            for (int p = 0; p < pc; p++) residency.Touch(_pages[pb + p], frame);
            table[n].First = (uint)_first[slot];
            table[n].Prefix = total;
            total += (uint)_count[slot];
            n++;
        }
        instances = (int)total;
        return n;
    }
}

public struct RectI
{
    public int X0, Y0, X1, Y1;
    public RectI(int x0, int y0, int x1, int y1) { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; }
}

/// <summary>Run time and end-to-end latency (enqueue → drained on the main thread) of a job type.</summary>
public struct JobStats
{
    public long Count, RunTicks, LatencyTicks, MaxLatencyTicks;

    public void Add(Job j)
    {
        long lat = System.Diagnostics.Stopwatch.GetTimestamp() - j.EnqueuedAt;
        Count++;
        RunTicks += j.RunTicks;
        LatencyTicks += lat;
        if (lat > MaxLatencyTicks) MaxLatencyTicks = lat;
    }

    public override readonly string ToString()
    {
        double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return Count == 0 ? "none" : $"{Count} jobs, run avg {RunTicks * f / Count:F3} ms, latency avg {LatencyTicks * f / Count:F2} ms max {MaxLatencyTicks * f:F1} ms";
    }
}
