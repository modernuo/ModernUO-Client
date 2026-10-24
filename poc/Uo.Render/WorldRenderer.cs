using System.Diagnostics;
using static SDL3.SDL;

namespace Uo.Render;

public sealed class WorldOptions
{
    public int InstanceCapacity = 2 << 20;
    public int MaxVisibleChunks = 8192;
    /// <summary>Dynamic instances per frame (mobiles, items, effects): a reserved range of the instance buffer.</summary>
    public int MaxDynamic = 32768;
    /// <summary>Translucent instances per frame (alpha &lt; 255, spectral, shadow), drawn back to front in a second draw.</summary>
    public int MaxTranslucent = 4096;
    /// <summary>Override hash slots (power of two); at most half of them are filled per frame.</summary>
    public int OverrideSlots = 8192;
    public int Layers = 24, PageBudget = 2, ChunkBudget = 64;
    /// <summary>Indexed8 anim array layers; 0 = no anim residency (anim pages then can't be drawn).</summary>
    public int AnimLayers = 64;
    public int Workers = Math.Clamp(Environment.ProcessorCount / 2 - 1, 2, 8);
    public bool ForceExpand;
    /// <summary>false: no map streaming (debug scenes injected with <see cref="ChunkStore.InjectDebugChunk"/>).</summary>
    public bool StreamMap = true;
}

/// <summary>Camera for one frame. World pixels: X = (x − y)·22, Y = (x + y)·22 − z·4 is the centre of tile (x, y, z).</summary>
public struct WorldCamera
{
    public double CenterX, CenterY;
    /// <summary>Target pixels per world pixel (zoom × display density).</summary>
    public float Zoom;
    /// <summary>The world's rectangle inside the target, in pixels.</summary>
    public int ViewportX, ViewportY, ViewportWidth, ViewportHeight;

    /// <summary>Centred on a tile plus a world-pixel offset (ClassicUO: the player's tile and its walk offset).</summary>
    public static WorldCamera AtTile(int x, int y, int z, float offsetX, float offsetY, float zoom, int viewportWidth, int viewportHeight)
        => new()
        {
            CenterX = (x - y) * 22 + offsetX, CenterY = (x + y) * 22 - z * 4 + offsetY, Zoom = zoom,
            ViewportWidth = viewportWidth, ViewportHeight = viewportHeight,
        };
}

/// <summary>Where a sprite landed: for debugging (picking stays on the host's CPU path).</summary>
public struct SpriteDebugInfo
{
    public int InstanceIndex, Page, U, V, Width, Height;
    public uint Flags, Depth, HueRow;
    /// <summary>Screen rectangle in target pixels (last rendered camera).</summary>
    public int ScreenX, ScreenY, ScreenWidth, ScreenHeight;
}

/// <summary>The embeddable world pass: pack streaming (regions, chunk bakes, pages) around a camera, per-frame overrides for
/// baked land and statics, a per-frame span of dynamic sprites, and one opaque plus one translucent draw into a target.
///
/// Per frame, on the host's render thread:
/// <code>
///   world.Prepare(camera);                       // streaming + visible chunks
///   world.SetStaticOverride(x, y, z, g, alpha);  // any number, e.g. from AddTileToRenderList
///   world.AddDynamic() = instance;               // mobiles, items, effects
///   world.Render(target, w, h, format, load);    // records + submits its own command buffer
/// </code>
/// Overrides and dynamic sprites are cleared after each Render.</summary>
public sealed unsafe class WorldRenderer : IDisposable
{
    public readonly PackSet Pack;
    public readonly Renderer Renderer;
    public readonly WorkerPool Workers;
    public readonly PageResidency Pages, AnimPages;
    public readonly ChunkStore Chunks;
    readonly bool _ownsRenderer, _ownsWorkers;
    readonly WorldOptions _o;

    readonly ChunkDraw[] _table;
    readonly uint _dynFirst;
    readonly SpriteInstance[] _dynamic;
    int _dynCount;

    readonly uint[] _trans;
    readonly ulong[] _transSort;
    int _transCount;

    struct PendingOverride
    {
        public int X, Y, Z, Graphic;
        public bool IsStatic;
        public uint Value;
    }

    readonly PendingOverride[] _pending;
    int _pendingCount;
    readonly OverrideEntry[] _ovTable;
    readonly int[] _ovUsed;
    int _ovUsedCount;

    // camera state
    WorldCamera _cam;
    double _cx, _cy, _prevCx, _prevCy, _velX, _velY;
    float _scale;
    RectI _view;
    long _lastTicks;
    bool _prepared;
    int _chunkCount, _instances;

    public long Frame;
    /// <summary>Last frame: overrides that matched a baked instance, missed (chunk not ready or no such object), dropped
    /// (hash or translucent list full); translucent instances drawn; dynamic instances.</summary>
    public int OverridesApplied, OverridesMissed, OverridesDropped, TranslucentCount, DynamicDrawn;
    /// <summary>Last frame: main-thread time to resolve overrides and build the translucent list (Stopwatch ticks).</summary>
    public long OverrideTicks;
    public int ChunkCount => _chunkCount;
    public int Instances => _instances;
    public RectI View => _view;
    public float Scale => _scale;

    // debug / bench hooks (app)
    public string ScreenshotPath;
    public bool GpuSample;
    public int FormatFlagsOverride = -1;
    public float GrayToleranceOverride = -1;

    public WorldRenderer(Renderer renderer, PackSet pack, WorkerPool workers, WorldOptions o)
        : this(renderer, pack, workers, o, false, false) { }

    /// <summary>External device mode: renders with the host's SDL_GPUDevice (FNA3D's), into a target passed to each
    /// <see cref="Render(IntPtr, uint, uint, SDL_GPUTextureFormat, bool)"/>. Owns its renderer resources and workers.</summary>
    public static WorldRenderer CreateExternal(IntPtr device, SDL_GPUTextureFormat colorFormat, PackSet pack, WorldOptions o)
    {
        var r = Renderer.CreateExternal(device, colorFormat, pack, o.InstanceCapacity, o.MaxVisibleChunks, o.MaxTranslucent, o.OverrideSlots);
        return new WorldRenderer(r, pack, new WorkerPool(o.Workers), o, true, true);
    }

    WorldRenderer(Renderer renderer, PackSet pack, WorkerPool workers, WorldOptions o, bool ownsRenderer, bool ownsWorkers)
    {
        Pack = pack;
        Renderer = renderer;
        Workers = workers;
        _o = o;
        _ownsRenderer = ownsRenderer;
        _ownsWorkers = ownsWorkers;
        Pages = new PageResidency(renderer.Device, pack, workers, o.Layers, o.PageBudget, Math.Max(2, o.PageBudget * 2), o.ForceExpand);
        if (pack.AnimIndexed && o.AnimLayers > 0)
            AnimPages = new PageResidency(renderer.Device, pack.AnimPagePaths, pack.AnimFormat, pack.AnimPageSize, pack.AnimPageMips, workers, o.AnimLayers,
                Math.Max(4, o.PageBudget * 4), Math.Max(4, o.PageBudget * 4));
        Chunks = new ChunkStore(pack, workers, o.InstanceCapacity);
        _table = new ChunkDraw[renderer.ChunkTableCapacity];
        _dynamic = new SpriteInstance[o.MaxDynamic];
        if (o.MaxDynamic > 0) _dynFirst = (uint)Chunks.Alloc.Alloc(o.MaxDynamic); // reserved before any chunk: never fails
        _trans = new uint[Math.Max(1, renderer.MaxTranslucent)];
        _transSort = new ulong[_trans.Length];
        _ovTable = new OverrideEntry[Math.Max(1, renderer.OverrideSlots)];
        _ovUsed = new int[_ovTable.Length];
        _pending = new PendingOverride[Math.Max(1, renderer.OverrideSlots / 2)];
        _lastTicks = Stopwatch.GetTimestamp();
    }

    /// <summary>Streaming and the visible chunk list for this camera. Call once per frame before <see cref="Render()"/>.</summary>
    public void Prepare(in WorldCamera cam)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = Math.Min(0.1, (now - _lastTicks) / (double)Stopwatch.Frequency);
        _lastTicks = now;
        Frame++;
        _cam = cam;
        float scale = _scale = cam.Zoom;
        int pw = Math.Max(1, cam.ViewportWidth), ph = Math.Max(1, cam.ViewportHeight);
        double cx = Math.Round(cam.CenterX * scale) / scale, cy = Math.Round(cam.CenterY * scale) / scale;
        _cx = cx; _cy = cy;
        float halfW = pw * 0.5f / scale, halfH = ph * 0.5f / scale;
        _view = new RectI((int)(cx - halfW) - 1, (int)(cy - halfH) - 1, (int)(cx + halfW) + 1, (int)(cy + halfH) + 1);
        float pre = 0.35f;
        var need = new RectI((int)(cx - halfW * (1 + pre)) - 128, (int)(cy - halfH * (1 + pre)) - 128, (int)(cx + halfW * (1 + pre)) + 128, (int)(cy + halfH * (1 + pre)) + 128);
        // Bias the prefetch toward the direction of motion: 0.4 s of travel, at most half a view. Jumps larger than half
        // a view in one frame (teleport, or a hitch under a time-scripted camera) are not motion.
        if (Frame > 1)
        {
            double ddx = cx - _prevCx, ddy = cy - _prevCy;
            if (Math.Abs(ddx) > halfW || Math.Abs(ddy) > halfH) { ddx = 0; ddy = 0; _velX = _velY = 0; }
            double inv = 1.0 / Math.Max(dt, 1e-4);
            _velX = _velX * 0.8 + ddx * inv * 0.2; // world px / s
            _velY = _velY * 0.8 + ddy * inv * 0.2;
        }
        _prevCx = cx; _prevCy = cy;
        int leadX = (int)Math.Clamp(_velX * 0.4, -halfW, halfW), leadY = (int)Math.Clamp(_velY * 0.4, -halfH, halfH);
        if (leadX > 0) need.X1 += leadX; else need.X0 += leadX;
        if (leadY > 0) need.Y1 += leadY; else need.Y0 += leadY;

        while (Workers.TryDequeueDone(out var job))
        {
            switch (job)
            {
                case BakeJob b: Chunks.OnBaked(b); break;
                case RegionJob rj: Chunks.OnRegionLoaded(rj); break;
                case PageJob pj: pj.Owner.OnLoaded(pj); break;
            }
        }
        if (_o.StreamMap) Chunks.Update(Frame, in _view, in need);
        Pages.BeginFrame();
        AnimPages?.BeginFrame();
        // leave one table entry for the dynamic range
        _chunkCount = Chunks.BuildDrawList(in _view, _table.AsSpan(0, _table.Length - 1), Pages, Frame, out _instances);
        _prepared = true;
    }

    /// <summary>Everything visible is drawable: no chunk, page or upload pending (screenshots and tests wait for this).</summary>
    public bool Settled => Chunks.PendingChunks == 0 && Chunks.UploadQueueLength == 0 && !Pages.Pending && Pages.MissingVisible == 0
        && (AnimPages == null || !AnimPages.Pending && AnimPages.MissingVisible == 0);

    // ---- dynamic sprites

    /// <summary>The next dynamic instance slot (zeroed), or a scratch slot when the frame's capacity is used up.</summary>
    public ref SpriteInstance AddDynamic()
    {
        if (_dynCount >= _dynamic.Length) { DynamicOverflow++; return ref _scratch; }
        ref var i = ref _dynamic[_dynCount++];
        i = default;
        return ref i;
    }

    SpriteInstance _scratch;
    public int DynamicOverflow;

    /// <summary>Bulk path: write instances into <see cref="DynamicBuffer"/>, then set <see cref="DynamicCount"/>.</summary>
    public Span<SpriteInstance> DynamicBuffer => _dynamic;

    public int DynamicCount
    {
        get => _dynCount;
        set => _dynCount = Math.Clamp(value, 0, _dynamic.Length);
    }

    // ---- overrides for baked land and statics

    /// <summary>Overrides the land tile at (x, y) this frame. alpha 255 = opaque; hidden = not drawn; highlightHue
    /// (1-based UO hue, 0 = none) recolours the whole sprite.</summary>
    public void SetLandOverride(int x, int y, byte alpha = 255, bool hidden = false, ushort highlightHue = 0)
        => AddOverride(x, y, 0, 0, false, alpha, hidden, highlightHue);

    /// <summary>Overrides the statics at (x, y) with this graphic and z (z = int.MinValue: any z) this frame.</summary>
    public void SetStaticOverride(int x, int y, int z, ushort graphic, byte alpha = 255, bool hidden = false, ushort highlightHue = 0)
        => AddOverride(x, y, z, graphic, true, alpha, hidden, highlightHue);

    void AddOverride(int x, int y, int z, int graphic, bool isStatic, byte alpha, bool hidden, ushort highlightHue)
    {
        if (alpha == 255 && !hidden && highlightHue == 0) return;
        if (_pendingCount >= _pending.Length) { _droppedPending++; return; }
        uint flags = (hidden || alpha == 0 ? OverrideEntry.Hidden : 0) | (highlightHue != 0 ? OverrideEntry.Highlight : 0);
        uint hueRow = highlightHue != 0 ? (uint)(highlightHue - 1) : 0;
        _pending[_pendingCount++] = new PendingOverride { X = x, Y = y, Z = z, Graphic = graphic, IsStatic = isStatic, Value = alpha | flags << 8 | hueRow << 16 };
    }

    int _droppedPending;

    // ---- render

    /// <summary>External mode: renders into the host's target (its SDL_GPUTexture*, re-fetched every frame) with a load op:
    /// <paramref name="load"/> keeps what the host already drew there, else the target is cleared to <see cref="ClearColor"/>.
    /// Records into its own command buffer and submits it, so call it before the host submits the work that samples the
    /// target. Note that host draws recorded earlier but submitted later execute after the world on the GPU.</summary>
    public bool Render(IntPtr target, uint width, uint height, SDL_GPUTextureFormat format, bool load)
    {
        var fi = BuildFrame();
        fi.Target = target; fi.TargetWidth = width; fi.TargetHeight = height; fi.TargetFormat = format;
        fi.LoadTarget = load;
        return Submit(ref fi);
    }

    /// <summary>Window or offscreen mode: renders into the swapchain or the offscreen texture.</summary>
    public bool Render()
    {
        var fi = BuildFrame();
        return Submit(ref fi);
    }

    public SDL_FColor ClearColor
    {
        get => Renderer.ClearColor;
        set => Renderer.ClearColor = value;
    }

    Renderer.FrameInput BuildFrame()
    {
        if (!_prepared) throw new InvalidOperationException("Prepare before Render");
        int chunkCount = _chunkCount, instances = _instances;
        _transCount = 0;
        int transCap = _trans.Length;
        OverridesDropped = _droppedPending;

        // dynamic: page residency, translucent list, one more chunk-table entry for the reserved range
        DynamicDrawn = _dynCount;
        if (_dynCount > 0)
        {
            for (int i = 0; i < _dynCount; i++)
            {
                ref readonly var d = ref _dynamic[i];
                if ((d.Flags & SpriteInstance.FlagIndexed) != 0) AnimPages?.Touch(d.Page - Pack.AnimPageBase, Frame);
                else Pages.Touch(d.Page, Frame);
                if (!d.Translucent || d.Alpha == 0) continue;
                if (_transCount < transCap) _transSort[_transCount++] = SortKey(d.Depth, _dynFirst + (uint)i);
                else OverridesDropped++;
            }
            _table[chunkCount].First = _dynFirst;
            _table[chunkCount].Prefix = (uint)instances;
            chunkCount++;
            instances += _dynCount;
        }

        // overrides: resolve against ready chunks into the hash; translucent ones join the sorted list
        long t0 = Stopwatch.GetTimestamp();
        OverridesApplied = 0; OverridesMissed = 0;
        Span<int> locals = stackalloc int[8];
        uint mask = (uint)(_ovTable.Length - 1);
        for (int p = 0; p < _pendingCount; p++)
        {
            ref readonly var po = ref _pending[p];
            int n = Chunks.FindObject(po.X, po.Y, po.IsStatic, po.Z, po.Graphic, out int slot, locals);
            if (n == 0) { OverridesMissed++; continue; }
            var cpu = Chunks.CpuInstances(slot);
            int first = Chunks.FirstInstance(slot);
            for (int k = 0; k < n; k++)
            {
                uint gi = (uint)(first + locals[k]);
                if (!InsertOverride(gi, po.Value, mask)) { OverridesDropped++; continue; }
                OverridesApplied++;
                bool hidden = (po.Value >> 8 & OverrideEntry.Hidden) != 0;
                ref readonly var inst = ref cpu[locals[k]];
                if (hidden || ((po.Value & 0xFF) == 255 && !inst.Translucent)) continue;
                if (_transCount < transCap) _transSort[_transCount++] = SortKey(inst.Depth, gi);
                else OverridesDropped++;
            }
        }

        // back to front: ascending depth key; equal keys (a mobile's layers) in reverse emission order, body first
        if (_transCount > 1) _transSort.AsSpan(0, _transCount).Sort();
        for (int i = 0; i < _transCount; i++) _trans[i] = uint.MaxValue - (uint)_transSort[i];
        TranslucentCount = _transCount;
        OverrideTicks = Stopwatch.GetTimestamp() - t0;

        Pages.StartLoads(Frame);
        AnimPages?.StartLoads(Frame);

        float scale = _scale;
        int camPxX = (int)Math.Floor(_cx), camPxY = (int)Math.Floor(_cy);
        return new Renderer.FrameInput
        {
            Chunks = Chunks, Pages = Pages, AnimPages = AnimPages, AnimTableBase = Pack.AnimPageBase,
            Table = _table, ChunkCount = chunkCount, Instances = instances,
            Frame = Frame, LinearFilter = scale < 0.999f, ChunkUploadBudget = _o.ChunkBudget,
            Dynamic = _dynamic, DynamicCount = _dynCount, DynamicFirst = _dynFirst,
            Translucent = _trans, TranslucentCount = _transCount,
            Overrides = _ovUsedCount > 0 ? _ovTable : null,
            ViewportX = _cam.ViewportX, ViewportY = _cam.ViewportY, ViewportWidth = _cam.ViewportWidth, ViewportHeight = _cam.ViewportHeight,
            ScreenshotPath = ScreenshotPath, GpuSample = GpuSample,
            Uniforms = new FrameUniforms
            {
                CamPxX = camPxX, CamPxY = camPxY,
                CamFracX = (float)(_cx - camPxX), CamFracY = (float)(_cy - camPxY),
                ScaleX = scale, ScaleY = scale,
                CamDepth = BakeJob.CameraDepthKey((float)(_cy / 22.0)),
                ChunkCount = (uint)chunkCount,
                InvAtlas = 1f / Pack.PageSize,
                FormatFlags = FormatFlagsOverride >= 0 ? (uint)FormatFlagsOverride : PageFormats.ShaderFlags(Pack.Format),
                GrayTolerance = (GrayToleranceOverride >= 0 ? GrayToleranceOverride : PageFormats.Bc7GrayTolerance) / 255f,
                DepthScale = Renderer.DepthScale,
            },
        };
    }

    static ulong SortKey(uint depth, uint gi) => (ulong)depth << 32 | (uint.MaxValue - gi);

    bool InsertOverride(uint gi, uint value, uint mask)
    {
        uint h = OverrideEntry.Hash(gi);
        for (int p = 0; p < OverrideEntry.MaxProbes; p++)
        {
            int s = (int)((h + (uint)p) & mask);
            ref var e = ref _ovTable[s];
            if (e.Index1 == gi + 1) { e.Value = value; return true; }
            if (e.Index1 != 0) continue;
            e.Index1 = gi + 1;
            e.Value = value;
            _ovUsed[_ovUsedCount++] = s;
            return true;
        }
        return false;
    }

    bool Submit(ref Renderer.FrameInput fi)
    {
        bool ok = Renderer.RenderFrame(ref fi);
        for (int i = 0; i < _ovUsedCount; i++) _ovTable[_ovUsed[i]] = default;
        _ovUsedCount = 0;
        _pendingCount = 0;
        _droppedPending = 0;
        _dynCount = 0;
        ScreenshotPath = null;
        GpuSample = false;
        _prepared = false;
        return ok;
    }

    // ---- debug

    /// <summary>The baked instance of a land tile or static (first match) and its screen rectangle at the last camera.</summary>
    public bool TryGetObjectInfo(int x, int y, bool isStatic, int z, int graphic, out SpriteDebugInfo info)
    {
        Span<int> locals = stackalloc int[1];
        info = default;
        if (Chunks.FindObject(x, y, isStatic, z, graphic, out int slot, locals) == 0) return false;
        info = Describe(Chunks.CpuInstances(slot)[locals[0]], Chunks.FirstInstance(slot) + locals[0]);
        return true;
    }

    /// <summary>Screen rectangle and atlas location of any instance (e.g. a dynamic one) at the last camera.</summary>
    public SpriteDebugInfo Describe(in SpriteInstance inst, int instanceIndex = -1)
    {
        int x0 = inst.X, y0 = inst.Y, x1 = inst.X + inst.Width, y1 = inst.Y + inst.Height;
        if ((inst.Flags & SpriteInstance.FlagStretch) != 0)
        {
            int zmin = int.MaxValue, zmax = int.MinValue;
            for (int c = 0; c < 4; c++) { int z = (sbyte)(inst.Corners >> (8 * c)); zmin = Math.Min(zmin, z); zmax = Math.Max(zmax, z); }
            x1 = inst.X + 44; y0 = inst.Y - zmax * 4; y1 = inst.Y + 44 - zmin * 4;
        }
        else if (inst.Mode == HueMode.Shadow)
        {
            x1 = inst.X + inst.Width + inst.Height / 2 + 1; y0 = inst.Y + inst.Height / 2 - 10; y1 = inst.Y + inst.Height - 10;
        }
        double ox = _cam.ViewportX + Math.Max(1, _cam.ViewportWidth) * 0.5, oy = _cam.ViewportY + Math.Max(1, _cam.ViewportHeight) * 0.5;
        int sx0 = (int)Math.Floor(ox + (x0 - _cx) * _scale), sy0 = (int)Math.Floor(oy + (y0 - _cy) * _scale);
        int sx1 = (int)Math.Ceiling(ox + (x1 - _cx) * _scale), sy1 = (int)Math.Ceiling(oy + (y1 - _cy) * _scale);
        return new SpriteDebugInfo
        {
            InstanceIndex = instanceIndex, Page = inst.Page, U = (int)(inst.Uv & 0xFFFF), V = (int)(inst.Uv >> 16),
            Width = inst.Width, Height = inst.Height, Flags = inst.Flags, Depth = inst.Depth, HueRow = inst.PageHue >> 16,
            ScreenX = sx0, ScreenY = sy0, ScreenWidth = sx1 - sx0, ScreenHeight = sy1 - sy0,
        };
    }

    public void Dispose()
    {
        Renderer.DrainInFlight();
        if (_ownsWorkers) Workers.Dispose();
        Pages.Dispose();
        AnimPages?.Dispose();
        if (_ownsRenderer) Renderer.Dispose();
    }
}
