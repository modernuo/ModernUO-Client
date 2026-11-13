using System.Diagnostics;
using System.Runtime.CompilerServices;
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
    /// <summary>Grow-on-demand ceilings (<see cref="GrowthPolicy"/>): above the starting size, the art page array, the anim
    /// page array and the instance buffer start at <see cref="Layers"/>, <see cref="AnimLayers"/> and
    /// <see cref="InstanceCapacity"/> and double up to these. 0 = fixed at the starting size.</summary>
    public int LayersMax, AnimLayersMax, InstanceCapacityMax;
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
///   world.SetZHiding(maxZ, maxGroundZ, roofs);   // optional: ClassicUO's max-Z / roof hiding in the shader
///   world.SetCircleOfTransparency(cx, cy, r, z); // optional: shader-mode circle of transparency
///   world.AddDynamic() = instance;               // mobiles, items, effects
///   world.Render(target, w, h, format, load);    // records + submits its own command buffer
/// </code>
/// Overrides, dynamic sprites, z hiding and the circle of transparency are cleared after each Render.</summary>
public sealed unsafe class WorldRenderer : IDisposable
{
    /// <summary>The pack and facet being streamed; replaced by <see cref="ChangeFacet"/>.</summary>
    public PackSet Pack { get; private set; }
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

    // the translucent list grows on demand up to Renderer.TranslucentLimit (the frame table follows before the frame)
    uint[] _trans;
    ulong[] _transSort;
    static readonly Comparison<ulong> TransKeyOrder = static (a, b) => a.CompareTo(b);

    /// <summary>The translucent list's ascending key sort. NativeAOT's Span&lt;ulong&gt;.Sort() makes a comparison delegate per
    /// call (48 B every frame that has two or more translucent entries), so it sorts with a cached comparison there. JIT
    /// keeps the default sort, which uses the specialized primitive path and doesn't allocate. IsDynamicCodeSupported is a
    /// constant under NativeAOT (false), so the unused branch is trimmed.</summary>
    public static void SortTransKeys(Span<ulong> keys) => SortTransKeys(keys, !RuntimeFeature.IsDynamicCodeSupported);

    /// <summary>The sort with the path chosen by the caller (tests run both).</summary>
    public static void SortTransKeys(Span<ulong> keys, bool cachedComparison)
    {
        if (cachedComparison)
        {
            keys.Sort(TransKeyOrder);
        }
        else
        {
            keys.Sort();
        }
    }
    TransRun[] _transRuns;
    int _transCount;

    // not readonly: a mutable struct
    PendingOverrideQueue _pending;

    // per-frame uniforms set by the host (cleared after Render)
    int _maxZ, _maxGroundZ;
    uint _zFlags;
    int _cotZ;
    float _cotX, _cotY, _cotR;
    ushort _tintHue;
    ushort _rangeHue;
    int _rangeX, _rangeY, _rangeDist;
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
    public bool RenderViewReuse;
    RenderViewReuse _viewReuse;
#if CUO_DEVTOOLS
    // CLEANUP(release): shadow work is enabled only by the checker, never by the switch alone.
    static readonly bool ViewReuseCheck = Environment.GetEnvironmentVariable("CUO_RENDER_VIEW_REUSE_CHECK") == "1";
    ChunkDraw[] _checkTable;
    long _viewChecks, _viewMismatches, _viewCheckLog;
#endif

    public long Frame;
    /// <summary>Last frame: overrides that matched a baked instance, missed (chunk not ready or no such object), dropped
    /// (hash or translucent list full); translucent instances drawn; dynamic instances.</summary>
    public int OverridesApplied, OverridesMissed, OverridesDropped, TranslucentCount, DynamicDrawn;
    /// <summary>Last frame: translucent draws split by effect blend (0 = one draw).</summary>
    public int TransRunCount;
    /// <summary>Last frame: instances listed for the circle-of-transparency ring (translucent pass).</summary>
    public int CotRingCount;
    /// <summary>Last frame: baked translucent instances (tiledata Translucent statics, alpha 178) listed from the visible
    /// chunks' translucent sub-lists, without overrides.</summary>
    public int BakedTranslucentCount;
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
        pack.BakeTranslucentAlpha = true; // BuildFrame lists the chunks' translucent sub-lists
        Pages = new PageResidency(renderer.Device, pack, workers, o.Layers, o.PageBudget, Math.Max(2, o.PageBudget * 2), o.ForceExpand, o.LayersMax);
        if (pack.AnimIndexed && o.AnimLayers > 0)
            AnimPages = new PageResidency(renderer.Device, pack.AnimPagePaths, pack.AnimFormat, pack.AnimPageSize, pack.AnimPageMips, workers, o.AnimLayers,
                Math.Max(4, o.PageBudget * 4), Math.Max(4, o.PageBudget * 4), false, o.AnimLayersMax);
        _instanceMax = Math.Max(renderer.InstanceCapacity, o.InstanceCapacityMax);
        Chunks = new ChunkStore(pack, workers, o.InstanceCapacity);
        _table = new ChunkDraw[renderer.ChunkTableCapacity];
        _dynamic = new SpriteInstance[o.MaxDynamic];
        if (o.MaxDynamic > 0) _dynFirst = (uint)Chunks.Alloc.Alloc(o.MaxDynamic); // reserved before any chunk: never fails
        _trans = new uint[Math.Max(1, renderer.MaxTranslucent)];
        _transSort = new ulong[_trans.Length];
        _transRuns = new TransRun[_trans.Length];
        _ovTable = new OverrideEntry[Math.Max(1, renderer.OverrideSlots)];
        _ovUsed = new int[_ovTable.Length];
        _pending = new PendingOverrideQueue(renderer.OverrideSlots / 2);
        _lastTicks = Stopwatch.GetTimestamp();
    }

    /// <summary>Streams another facet of the same pack (<paramref name="pack"/> from <see cref="PackSet.WithFacet"/>):
    /// the chunks and regions are dropped and re-stream from the next <see cref="Prepare"/>, while the device resources
    /// (instance buffer, page arrays and their resident pages, palettes, pipelines), the workers and every CPU-side
    /// array are kept. Call between frames. The new pack's bake switches (<see cref="PackSet.WaterWobble"/>, remaps) are
    /// its own, as with a newly created world.</summary>
    public void ChangeFacet(PackSet pack)
    {
        if (ReferenceEquals(pack, Pack)) return;
        if (!ReferenceEquals(pack.PagePaths, Pack.PagePaths) || !ReferenceEquals(pack.AnimPagePaths, Pack.AnimPagePaths) || pack.AnimPageBase != Pack.AnimPageBase)
            throw new ArgumentException("ChangeFacet needs the same pack on another facet (PackSet.WithFacet)", nameof(pack));
        pack.BakeTranslucentAlpha = true;
        pack.BakeGeo = Pack.BakeGeo;
        Pack = pack;
        Chunks.ChangeFacet(pack);
        _waterX = _waterY = 1f;
        _velX = _velY = 0;
        FacetChanges++;
    }

    /// <summary>Times <see cref="ChangeFacet"/> switched the facet.</summary>
    public int FacetChanges;

    /// <summary>Streaming and the visible chunk list for this camera. Call once per frame before <see cref="Render()"/>.</summary>
    public void Prepare(in WorldCamera cam)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = Math.Min(0.1, (now - _lastTicks) / (double)Stopwatch.Frequency);
        _lastTicks = now;
        Frame++;
        _cam = cam;
        _jumped = false;
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
            if (Math.Abs(ddx) > halfW || Math.Abs(ddy) > halfH) { ddx = 0; ddy = 0; _velX = _velY = 0; _jumped = true; }
            double inv = 1.0 / Math.Max(dt, 1e-4);
            _velX = _velX * 0.8 + ddx * inv * 0.2; // world px / s
            _velY = _velY * 0.8 + ddy * inv * 0.2;
        }
        _prevCx = cx; _prevCy = cy;
        int leadX = (int)Math.Clamp(_velX * 0.4, -halfW, halfW), leadY = (int)Math.Clamp(_velY * 0.4, -halfH, halfH);
        if (leadX > 0) need.X1 += leadX; else need.X0 += leadX;
        if (leadY > 0) need.Y1 += leadY; else need.Y0 += leadY;

        long diagStreaming = RenderDiagnostics.Stamp();
        while (Workers.TryDequeueDone(out var job))
        {
            switch (job)
            {
                case BakeJob b: Chunks.OnBaked(b); break;
                case RegionJob rj: Chunks.OnRegionLoaded(rj); break;
                case PageJob pj: pj.Owner.OnLoaded(pj); break;
            }
        }
        bool reuse = _viewReuse.Matches(RenderViewReuse, _view, need, scale, Chunks.ChangeEpoch,
            Pages.TotalUploads, AnimPages?.TotalUploads ?? 0,
            Chunks.CanReuseMaintenance && !Pages.Pending && Pages.MissingVisible == 0
                && (AnimPages == null || !AnimPages.Pending && AnimPages.MissingVisible == 0));
        if (_o.StreamMap)
        {
            if (reuse) Chunks.ReuseMaintenance(Frame);
            else Chunks.Update(Frame, in _view, in need);
        }
        Pages.BeginFrame();
        AnimPages?.BeginFrame();
        // leave one table entry for the dynamic range
        RenderDiagnostics.Elapsed(DiagMetric.StreamingTicks, diagStreaming);
        long diagTable = RenderDiagnostics.Stamp();
        if (reuse)
        {
            Chunks.TouchVisiblePages(Pages, Frame);
            RenderDiagnostics.Add(DiagMetric.ChunkTableReuses, 1);
#if CUO_DEVTOOLS
            if (ViewReuseCheck) CheckReusedView();
#endif
        }
        else
        {
            _chunkCount = Chunks.BuildDrawList(in _view, _table.AsSpan(0, _table.Length - 1), Pages, Frame, out _instances, RenderViewReuse);
            RenderDiagnostics.Add(DiagMetric.ChunkTableRebuilds, 1);
        }
        if (RenderViewReuse) _viewReuse.Remember(_view, need, scale, Chunks.ChangeEpoch, Pages.TotalUploads, AnimPages?.TotalUploads ?? 0);
        else _viewReuse = default;
#if CUO_DEVTOOLS
        if (ViewReuseCheck && now - _viewCheckLog >= Stopwatch.Frequency * 5)
        {
            Console.WriteLine($"[render-view-reuse-check] on={RenderViewReuse} checked={_viewChecks} mismatches={_viewMismatches}");
            _viewCheckLog = now;
        }
#endif
        RenderDiagnostics.Elapsed(DiagMetric.ChunkTableTicks, diagTable);
        _prepared = true;
    }

#if CUO_DEVTOOLS
    void CheckReusedView()
    {
        _checkTable ??= new ChunkDraw[_table.Length];
        ulong touches = Pages.TouchDecisionHash();
        int transCount = Chunks.VisibleTranslucentCount;
        int missing = Chunks.MissingNear, visible = Chunks.MissingVisible;
        // The full walk also refreshes the cached page set and translucent slots in exactly their original order.
        ulong slots = VisibleSlotHash();
        int count = Chunks.BuildDrawList(_view, _checkTable.AsSpan(0, _table.Length - 1), Pages, Frame, out int instances);
        bool same = count == _chunkCount && instances == _instances && touches == Pages.TouchDecisionHash()
            && transCount == Chunks.VisibleTranslucentCount && slots == VisibleSlotHash()
            && missing == Chunks.MissingNear && visible == Chunks.MissingVisible;
        for (int i = 0; same && i < count; i++)
            same = _table[i].First == _checkTable[i].First && _table[i].Prefix == _checkTable[i].Prefix;
        _viewChecks++;
        if (!same)
        {
            _viewMismatches++;
            Console.WriteLine($"[render-view-reuse-check] MISMATCH frame={Frame} chunks={_chunkCount}/{count} instances={_instances}/{instances} touches={touches}/{Pages.TouchDecisionHash()}");
        }
    }

    ulong VisibleSlotHash()
    {
        ulong h = 0;
        foreach (int slot in Chunks.VisibleTranslucentSlots) h = (h ^ (uint)slot) * 1099511628211UL;
        return h;
    }
#endif

    /// <summary>Everything visible is drawable: no chunk, page or upload pending (screenshots and tests wait for this).</summary>
    public bool Settled => Chunks.PendingChunks == 0 && Chunks.UploadQueueLength == 0 && !Pages.Pending && Pages.MissingVisible == 0
        && (AnimPages == null || !AnimPages.Pending && AnimPages.MissingVisible == 0);

    /// <summary>The last frame drew everything its view needs: no chunk the view (with art margins) touches is still
    /// loading or baking, and no page a visible sprite uses is missing. Unlike <see cref="Settled"/> it ignores the
    /// prefetch ring and page loads nothing visible waits for, so a host can hold its previous frame until this is true.
    /// Read after Render/Stream.</summary>
    public bool VisibleReady => Chunks.MissingNear == 0 && Pages.MissingVisible == 0 && (AnimPages == null || AnimPages.MissingVisible == 0);

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

    /// <summary>Overrides the land tile at (x, y) this frame. alpha (0-255) replaces the baked alpha, -1 = keep it (land
    /// bakes 255, tiledata Translucent statics 178); hidden = not drawn; highlightHue (1-based UO hue, 0 = none) recolours
    /// the whole sprite; forceShow = draw it even where <see cref="SetZHiding"/> would hide it (an object still fading out).</summary>
    public void SetLandOverride(int x, int y, int alpha = -1, bool hidden = false, ushort highlightHue = 0, bool forceShow = false)
        => AddOverride(x, y, 0, 0, false, alpha, hidden, highlightHue, forceShow);

    /// <summary>Overrides the statics at (x, y) with this graphic and z (z = int.MinValue: any z) this frame.</summary>
    public void SetStaticOverride(int x, int y, int z, ushort graphic, int alpha = -1, bool hidden = false, ushort highlightHue = 0, bool forceShow = false)
        => AddOverride(x, y, z, graphic, true, alpha, hidden, highlightHue, forceShow);

    /// <summary>ClassicUO's roof and max-Z hiding for this frame, tested per baked instance in the shader: land with
    /// z &gt; <paramref name="maxGroundZ"/>, statics with z &gt;= <paramref name="maxZ"/>, and roofs when
    /// <paramref name="hideRoofs"/> are not drawn (unless a <c>forceShow</c> override keeps one).</summary>
    /// <summary>clip-2 depth model (host switch depth_model): true = per-pixel geometric depth (sprite.hlsl PSMainGeo,
    /// <see cref="GeoDepth"/>), false = one depth key per instance.
    /// Flipping it rebakes every chunk (<see cref="PackSet.BakeGeo"/>: the statics' shape bits are baked only while it is
    /// on, so the off state's instance data matches the key model's).</summary>
    public bool GeometricDepth
    {
        get => Pack.BakeGeo;
        set
        {
            if (value == Pack.BakeGeo) return;
            Pack.BakeGeo = value;
            Chunks.EvictAll();
            GeoRebakes++;
        }
    }

    /// <summary>Times <see cref="GeometricDepth"/> flipped and rebaked the chunks.</summary>
    public int GeoRebakes;

    /// <summary>Target depth per tile-sum unit of the geometric model: ±1250 units around the camera fit in [0, 1].</summary>
    public const float GeoScale = 1f / 2500f;

    public void SetZHiding(int maxZ, int maxGroundZ, bool hideRoofs)
    {
        _maxZ = maxZ;
        _maxGroundZ = maxGroundZ;
        _zFlags = 1u | (hideRoofs ? 2u : 0);
    }

    /// <summary>Recolours every baked land tile and static with this 1-based hue this frame (whole sprite, as a highlight;
    /// per-sprite highlight overrides win). ClassicUO's death screen; 0 = none.</summary>
    public void SetTint(ushort hue) => _tintHue = hue;

    /// <summary>ClassicUO's NoColorObjectsOutOfRange for this frame: every baked land tile and static whose tile is more than
    /// <paramref name="range"/> tiles (Chebyshev) from (<paramref name="tileX"/>, <paramref name="tileY"/>) is recoloured
    /// with this 1-based hue (whole sprite; wins over <see cref="SetTint"/>; per-sprite highlight overrides win over it).
    /// 0 = none.</summary>
    public void SetRangeTint(ushort hue, int tileX, int tileY, int range)
    {
        _rangeHue = hue;
        _rangeX = tileX;
        _rangeY = tileY;
        _rangeDist = range;
    }

    /// <summary>The shader's out-of-range test on the CPU (same rules as sprite.hlsl) for the current
    /// <see cref="SetRangeTint"/>.</summary>
    public bool RangeTinted(in SpriteInstance inst)
    {
        if (_rangeHue == 0 || (inst.Flags & SpriteInstance.FlagZTest) == 0) return false;
        inst.Tile(out int tx, out int ty);
        return Math.Max(Math.Abs(tx - _rangeX), Math.Abs(ty - _rangeY)) > _rangeDist;
    }

    /// <summary>Circle of transparency (ClassicUO's shader mode) for this frame: centre and radius in target pixels,
    /// <paramref name="z"/> = the player's z + 5 (Static.TransparentTest). Applies to baked statics that pass the test and
    /// to dynamic sprites with <see cref="SpriteInstance.LightCot"/>.</summary>
    public void SetCircleOfTransparency(float centerX, float centerY, float radius, int z)
    {
        _cotX = centerX;
        _cotY = centerY;
        _cotR = radius;
        _cotZ = z;
    }

    /// <summary>Replaces land graphics in the bake (0x4000 entries, null = none; ClassicUO's season land tiles) and
    /// rebakes every chunk. Meant for rare changes: the view re-streams.</summary>
    public void SetLandRemap(ushort[] remap)
    {
        if (remap != null && remap.Length < 0x4000) throw new ArgumentException("land remap needs 0x4000 entries", nameof(remap));
        Pack.LandRemap = remap;
        Chunks.EvictAll();
    }

    /// <summary>ClassicUO's animated water (profile AnimatedWaterEffect): <paramref name="on"/> bakes a scaled copy of every
    /// wet flat land tile (LandView's second draw; a change rebakes every chunk), and the frame scales
    /// <see cref="SpriteInstance.LightWobble"/> instances, baked or dynamic, by (<paramref name="scaleX"/>, <paramref name="scaleY"/>).</summary>
    public void SetWaterWobble(bool on, float scaleX, float scaleY)
    {
        _waterX = on ? scaleX : 1f;
        _waterY = on ? scaleY : 1f;
        if (on == Pack.WaterWobble) return;
        Pack.WaterWobble = on;
        Chunks.EvictAll();
    }

    public bool WaterWobble => Pack.WaterWobble;
    float _waterX = 1f, _waterY = 1f;

    /// <summary>Replaces static graphics in the bake (0x10000 entries, null = none; ClassicUO's season statics) and rebakes
    /// every chunk. Overrides keep addressing statics by their original graphic. Meant for rare changes: the view re-streams.</summary>
    public void SetStaticRemap(ushort[] remap)
    {
        if (remap != null && remap.Length < 0x10000) throw new ArgumentException("static remap needs 0x10000 entries", nameof(remap));
        Pack.StaticRemap = remap;
        Chunks.EvictAll();
    }

    /// <summary>The shader's z-hiding test on the CPU (same rules as sprite.hlsl).</summary>
    public bool ZHidden(in SpriteInstance inst, bool forceShow = false)
    {
        if ((_zFlags & 1) == 0 || forceShow || (inst.Flags & SpriteInstance.FlagZTest) == 0) return false;
        int z = (sbyte)inst.Corners;
        uint zb = inst.Corners >> 16 & 0xFF;
        if ((inst.Flags & SpriteInstance.FlagStretch) != 0 || (zb & SpriteInstance.ZLand) != 0) return z > _maxGroundZ;
        return z >= _maxZ || (_zFlags & 2) != 0 && (zb & SpriteInstance.ZRoof) != 0;
    }

    /// <summary>The shader's circle-of-transparency eligibility on the CPU (same rules as sprite.hlsl).</summary>
    public bool CotApplies(in SpriteInstance inst)
    {
        if (_cotR <= 0 || (inst.Flags & SpriteInstance.FlagLit) != 0) return false;
        if ((inst.Flags & SpriteInstance.FlagZTest) == 0) return (inst.Light & SpriteInstance.LightCot) != 0;
        if ((inst.Flags & SpriteInstance.FlagStretch) != 0) return false;
        int z = (sbyte)inst.Corners, height = (int)(inst.Corners >> 8 & 0xFF);
        uint zb = inst.Corners >> 16 & 0xFF;
        return (zb & (SpriteInstance.ZLand | SpriteInstance.ZNoCot)) == 0 && z > _cotZ - height && (z <= _cotZ || (zb & SpriteInstance.ZCotCapable) != 0);
    }

    void AddOverride(int x, int y, int z, int graphic, bool isStatic, int alpha, bool hidden, ushort highlightHue, bool forceShow)
    {
        bool keep = alpha < 0;
        if (keep && !hidden && highlightHue == 0 && !forceShow) return;
        uint a = keep ? 255u : (uint)Math.Min(alpha, 255);
        uint flags = (hidden || a == 0 ? OverrideEntry.Hidden : 0) | (highlightHue != 0 ? OverrideEntry.Highlight : 0)
            | (forceShow ? OverrideEntry.ForceShow : 0) | (keep ? OverrideEntry.KeepAlpha : 0);
        uint hueRow = highlightHue != 0 ? (uint)(highlightHue - 1) : 0;
        _pending.Add(new PendingOverrideQueue.Entry { X = x, Y = y, Z = z, Graphic = graphic, IsStatic = isStatic, Value = a | flags << 8 | hueRow << 16 });
    }

    /// <summary>A baked instance's alpha under an override value (sprite.hlsl: the override's alpha replaces the baked one
    /// unless <see cref="OverrideEntry.KeepAlpha"/>; hidden = 0).</summary>
    static int OverrideAlpha(in SpriteInstance inst, uint value)
    {
        uint f = value >> 8 & 0xFF;
        return (f & OverrideEntry.Hidden) != 0 ? 0 : (f & OverrideEntry.KeepAlpha) != 0 ? inst.Alpha : (int)(value & 0xFF);
    }

    /// <summary>E12 (CUO_GPU_ITEM_CHECK): the overrides queued for the next Render so far (read only).</summary>
    public ref readonly PendingOverrideQueue PendingOverrides => ref _pending;

    /// <summary>E12: a checkpoint of the queued overrides, for <see cref="RestorePendingOverrides"/>.</summary>
    public PendingOverrideQueue.Mark MarkPendingOverrides() => _pending.GetMark();

    /// <summary>E12: forgets the overrides queued since <paramref name="mark"/> (and their drops).</summary>
    public void RestorePendingOverrides(PendingOverrideQueue.Mark mark) => _pending.Restore(mark);

    /// <summary>E12: the probe pass's end: copies the overrides queued since <paramref name="mark"/> into
    /// <paramref name="into"/> and forgets them (<see cref="PendingOverrideQueue.TakeSince"/>); returns the drops since.</summary>
    public int TakePendingOverrides(PendingOverrideQueue.Mark mark, List<PendingOverrideQueue.Entry> into) => _pending.TakeSince(mark, into);

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

    /// <summary>Streams without drawing: this frame's chunk and page uploads run, no target is touched. A host that holds
    /// its previous frame (after a teleport) calls this instead of <see cref="Render(IntPtr, uint, uint, SDL_GPUTextureFormat, bool)"/>
    /// until <see cref="VisibleReady"/>.</summary>
    public bool Stream()
    {
        var fi = BuildFrame();
        fi.StreamOnly = true;
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
        long diagBuild = RenderDiagnostics.Stamp();
        int chunkCount = _chunkCount, instances = _instances;
        _transCount = 0;
        OverridesDropped = _pending.Dropped;
        CotRingCount = 0;
        bool cot = _cotR > 0;
        if (cot)
        {
            // the circle in world pixels (the target's viewport centre is the camera centre)
            double ox = _cam.ViewportX + Math.Max(1, _cam.ViewportWidth) * 0.5, oy = _cam.ViewportY + Math.Max(1, _cam.ViewportHeight) * 0.5;
            double wx = _cx + (_cotX - ox) / _scale, wy = _cy + (_cotY - oy) / _scale, wr = _cotR / _scale;
            _cotRect = new RectI((int)Math.Floor(wx - wr) - 1, (int)Math.Floor(wy - wr) - 1, (int)Math.Ceiling(wx + wr) + 1, (int)Math.Ceiling(wy + wr) + 1);
        }

        // dynamic: page residency, translucent list, one more chunk-table entry for the reserved range
        DynamicDrawn = _dynCount;
        int ownBlend = 0;
        if (_dynCount > 0)
        {
            for (int i = 0; i < _dynCount; i++)
            {
                ref readonly var d = ref _dynamic[i];
                if ((d.Flags & SpriteInstance.FlagIndexed) != 0) AnimPages?.Touch(d.Page - Pack.AnimPageBase, Frame);
                else Pages.Touch(d.Page, Frame);
                if (d.Alpha == 0) continue;
                // translucent, or opaque with a circle-of-transparency ring to blend (the shader discards the rest)
                if (!d.Translucent && !(cot && (d.Light & SpriteInstance.LightCot) != 0 && InCotRect(d))) continue;
                AddTrans(SortKey(d.Depth, _dynFirst + (uint)i));
                if (d.Blend is not (InstanceBlend.Normal or InstanceBlend.NormalOrdered or InstanceBlend.Screen)) ownBlend++;
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
        for (int p = 0; p < _pending.Count; p++)
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
                ref readonly var inst = ref cpu[locals[k]];
                int alpha = OverrideAlpha(inst, po.Value);
                if (alpha == 0 || ZHidden(inst, (po.Value >> 8 & OverrideEntry.ForceShow) != 0)) continue;
                // baked translucent: already listed from its chunk's sub-list; opaque: the circle-of-transparency scan
                // below lists it if it has a ring
                if (inst.Translucent || alpha == 255) continue;
                AddTrans(SortKey(inst.Depth, gi));
            }
        }

        // baked translucent statics (alpha 178): each visible chunk's sort keys from upload. Only those the frame shows take
        // a list entry: hidden (by override) and z-hidden ones are left out, so they never crowd out a visible fade.
        BakedTranslucentCount = 0;
        var transSlots = Chunks.VisibleTranslucentSlots;
        for (int c = 0; c < transSlots.Length; c++)
        {
            var keys = Chunks.TranslucentKeys(transSlots[c]);
            for (int k = 0; k < keys.Length; k++)
            {
                uint gi = uint.MaxValue - (uint)keys[k];
                ref readonly var inst = ref Chunks.CpuInstanceAt(gi);
                int alpha = inst.Alpha;
                bool force = false;
                if (_ovUsedCount > 0)
                {
                    uint ov = FindOverride(gi, mask, out bool found);
                    if (found)
                    {
                        alpha = OverrideAlpha(inst, ov);
                        force = (ov >> 8 & OverrideEntry.ForceShow) != 0;
                    }
                }
                if (alpha == 0 || ZHidden(inst, force)) continue;
                AddTrans(keys[k]);
                BakedTranslucentCount++;
            }
        }

        // circle of transparency: baked opaque statics it applies to also draw their faded ring in the translucent pass
        if (cot)
        {
            int nSlots = Chunks.ReadySlotsInRect(in _cotRect, _cotSlots);
            for (int c = 0; c < nSlots; c++)
            {
                int slot = _cotSlots[c];
                var cpu = Chunks.CpuInstances(slot);
                int first = Chunks.FirstInstance(slot);
                for (int k = 0; k < cpu.Length; k++)
                {
                    ref readonly var inst = ref cpu[k];
                    if ((inst.Flags & SpriteInstance.FlagZTest) == 0 || !InCotRect(inst) || !CotApplies(inst)) continue;
                    uint gi = (uint)(first + k);
                    uint ov = FindOverride(gi, mask, out bool found);
                    // hidden, or translucent through its override (already listed, gets the whole effect there)
                    if (found && OverrideAlpha(inst, ov) != 255) continue;
                    if (ZHidden(inst, found && (ov >> 8 & OverrideEntry.ForceShow) != 0)) continue;
                    // baked translucent: its chunk's sub-list entry draws the ring (or the whole effect)
                    if (inst.Translucent) continue;
                    AddTrans(SortKey(inst.Depth, gi));
                    CotRingCount++;
                }
            }
        }

        // the frame table must hold the whole list (it grows with the list, up to Renderer.TranslucentLimit)
        if (!Renderer.EnsureTranslucentCapacity(_transCount))
        {
            OverridesDropped += _transCount - Renderer.MaxTranslucent;
            _transCount = Renderer.MaxTranslucent;
        }

        // back to front: ascending depth key; equal keys (a mobile's layers) in reverse emission order, body first
        long diagSort = RenderDiagnostics.Stamp();
        if (_transCount > 1) SortTransKeys(_transSort.AsSpan(0, _transCount));
        for (int i = 0; i < _transCount; i++) _trans[i] = uint.MaxValue - (uint)_transSort[i];
        RenderDiagnostics.Elapsed(DiagMetric.TranslucentSortTicks, diagSort);
        TranslucentCount = _transCount;
        // effect blends with their own pipeline: split the sorted list into runs of one blend (only on frames that have one)
        int runCount = 0;
        if (ownBlend > 0)
        {
            for (int i = 0; i < _transCount; i++)
            {
                uint gi = _trans[i] - _dynFirst;
                var b = gi < (uint)_dynCount ? _dynamic[gi].Blend : InstanceBlend.Normal;
                if (b is InstanceBlend.Screen or InstanceBlend.NormalOrdered) b = InstanceBlend.Normal; // same pipeline
                if (runCount > 0 && _transRuns[runCount - 1].Blend == b) _transRuns[runCount - 1].Count++;
                else _transRuns[runCount++] = new TransRun { Start = i, Count = 1, Blend = b };
            }
        }
        TransRunCount = runCount;
        OverrideTicks = Stopwatch.GetTimestamp() - t0;
        RenderDiagnostics.Elapsed(DiagMetric.BuildFrameTicks, diagBuild);

        Pages.StartLoads(Frame);
        AnimPages?.StartLoads(Frame);

        float scale = _scale;
        int camPxX = (int)Math.Floor(_cx), camPxY = (int)Math.Floor(_cy);
        bool geo = Pack.BakeGeo;
        return new Renderer.FrameInput
        {
            Chunks = Chunks, Pages = Pages, AnimPages = AnimPages, AnimTableBase = Pack.AnimPageBase,
            Table = _table, ChunkCount = chunkCount, Instances = instances,
            Frame = Frame, LinearFilter = scale < 0.999f, ChunkUploadBudget = _o.ChunkBudget,
            Dynamic = _dynamic, DynamicCount = _dynCount, DynamicFirst = _dynFirst,
            Translucent = _trans, TranslucentCount = _transCount,
            TransRuns = _transRuns, TransRunCount = runCount,
            Overrides = _ovUsedCount > 0 ? _ovTable : null,
            ViewportX = _cam.ViewportX, ViewportY = _cam.ViewportY, ViewportWidth = _cam.ViewportWidth, ViewportHeight = _cam.ViewportHeight,
            ScreenshotPath = ScreenshotPath, GpuSample = GpuSample,
            Uniforms = new FrameUniforms
            {
                CamPxX = camPxX, CamPxY = camPxY,
                CamFracX = (float)(_cx - camPxX), CamFracY = (float)(_cy - camPxY),
                ScaleX = scale, ScaleY = scale,
                CamDepth = BakeJob.CameraDepthKey((float)(_cy / 22.0)),
                DepthModel = geo ? 1u : 0u,
                // the tile sum CameraDepthKey truncates to, so a key-fallback instance and a geometric one share the origin
                GeoOff = geo ? (float)(camPxY / 22.0 - (int)((float)(_cy / 22.0) * 100) / 100.0) : 0f,
                GeoScale = geo ? GeoScale : 0f,
                ChunkCount = (uint)chunkCount,
                InvAtlas = 1f / Pack.PageSize,
                FormatFlags = FormatFlagsOverride >= 0 ? (uint)FormatFlagsOverride : PageFormats.ShaderFlags(Pack.Format),
                GrayTolerance = (GrayToleranceOverride >= 0 ? GrayToleranceOverride : PageFormats.Bc7GrayTolerance) / 255f,
                DepthScale = Renderer.DepthScale,
                MaxZ = _maxZ, MaxGroundZ = _maxGroundZ, ZFlags = _zFlags,
                CotZ = _cotZ, CotCenterX = _cotX, CotCenterY = _cotY, CotRadius = _cotR,
                TintRow = _tintHue != 0 && _tintHue - 1 < Pack.HueCount ? _tintHue : 0u,
                WaterScaleX = _waterX, WaterScaleY = _waterY,
                RangeTintRow = _rangeHue != 0 && _rangeHue - 1 < Pack.HueCount ? _rangeHue : 0u,
                RangeX = _rangeX, RangeY = _rangeY, RangeDist = _rangeDist,
            },
        };
    }

    /// <summary>Appends a translucent-list entry, growing the list (up to <see cref="Renderer.TranslucentLimit"/>; beyond it
    /// the entry is dropped and counted in <see cref="OverridesDropped"/>).</summary>
    void AddTrans(ulong key)
    {
        if (_transCount == _transSort.Length)
        {
            int n = _transSort.Length;
            if (n >= Renderer.TranslucentLimit) { OverridesDropped++; return; }
            int m = Math.Min(Renderer.TranslucentLimit, Math.Max(1024, n * 2));
            Array.Resize(ref _transSort, m);
            Array.Resize(ref _trans, m);
            Array.Resize(ref _transRuns, m);
            TranslucentListGrowths++;
        }
        _transSort[_transCount++] = key;
    }

    /// <summary>Times the CPU translucent list grew (the frame table follows in <see cref="Renderer.EnsureTranslucentCapacity"/>).</summary>
    public int TranslucentListGrowths;

    internal static ulong SortKey(uint depth, uint gi) => (ulong)depth << 32 | (uint.MaxValue - gi);

    RectI _cotRect;
    readonly int[] _cotSlots = new int[1024];

    /// <summary>The instance's (unrotated) rectangle meets the circle of transparency's bounding box.</summary>
    bool InCotRect(in SpriteInstance i)
        => i.X <= _cotRect.X1 && i.X + i.Width >= _cotRect.X0 && i.Y <= _cotRect.Y1 && i.Y + i.Height >= _cotRect.Y0;

    uint FindOverride(uint gi, uint mask, out bool found)
    {
        found = false;
        if (_ovUsedCount == 0) return 0;
        uint h = OverrideEntry.Hash(gi);
        for (int p = 0; p < OverrideEntry.MaxProbes; p++)
        {
            ref readonly var e = ref _ovTable[(int)((h + (uint)p) & mask)];
            if (e.Index1 == 0) return 0;
            if (e.Index1 == gi + 1) { found = true; return e.Value; }
        }
        return 0;
    }

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
        fi.Hold = _jumped;
        GrowInstancesIfNeeded(fi.StreamOnly || _jumped);
        bool ok = Renderer.RenderFrame(ref fi);
        for (int i = 0; i < _ovUsedCount; i++) _ovTable[_ovUsed[i]] = default;
        _ovUsedCount = 0;
        _pending.Clear();
        _dynCount = 0;
        _zFlags = 0;
        _cotR = 0;
        _tintHue = 0;
        _rangeHue = 0;
        ScreenshotPath = null;
        GpuSample = false;
        _prepared = false;
        return ok;
    }

    // ---- grow on demand

    readonly int _instanceMax;
    GrowthPolicy _instanceGrowth;
    bool _jumped, _instanceGrowFailed;

    /// <summary>Instance buffer growth (<see cref="GrowthPolicy"/>): occupancy is the allocator's (the reserved dynamic
    /// range counts as used); starved = an upload found no free range last frame. Runs before the frame stages its
    /// uploads, so they can use the new range.</summary>
    void GrowInstancesIfNeeded(bool hold)
    {
        var alloc = Chunks.Alloc;
        if (_instanceGrowFailed || alloc.Capacity >= _instanceMax || Renderer.InstanceCapacity != alloc.Capacity) return;
        if (!_instanceGrowth.Check(alloc.Used, alloc.Capacity, _instanceMax, hold, Chunks.OutOfGpuSpace, Frame)) return;
        _instanceGrowth.Grew();
        int next = GrowthPolicy.Next(alloc.Capacity, _instanceMax);
        if (!Renderer.GrowInstances(next)) { _instanceGrowFailed = true; return; }
        alloc.Grow(next);
        Chunks.OutOfGpuSpace = false;
    }

    /// <summary>Growth steps so far, all arrays (a host logs when it changes).</summary>
    public int GrowthEvents => Pages.Growth.Count + (AnimPages?.Growth.Count ?? 0) + Renderer.InstanceGrowth.Count;

    /// <summary>One line: the arrays' sizes and growth steps (count, last/max/total main-thread ms).</summary>
    public string GrowthSummary()
    {
        static string One(string name, int size, int max, in GrowthStats g)
            => $"{name} {size}/{max} ({g.Count} steps" + (g.Count > 0 ? $", last {g.LastFrom}->{g.LastTo} {g.LastMs:F2} ms, max {g.MaxMs:F2} ms, total {g.TotalMs:F2} ms)" : ")");
        return One("art layers", Pages.Layers, Pages.MaxLayers, Pages.Growth) +
            (AnimPages != null ? "; " + One("anim layers", AnimPages.Layers, AnimPages.MaxLayers, AnimPages.Growth) : "") +
            "; " + One("instances", Renderer.InstanceCapacity, _instanceMax, Renderer.InstanceGrowth);
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

    /// <summary>Drains the GPU, then disposes the workers, page residencies and owned renderer. A failed drain (device
    /// lost) doesn't skip the rest: the renderer's Dispose still releases the copy fences; the first failure is rethrown
    /// afterwards (<see cref="Teardown"/>). One-shot: a second call, even after the first one threw, does nothing.</summary>
    public void Dispose()
    {
        var renderer = Renderer;
        var anim = AnimPages;
        Teardown.Once(ref _disposed, "world renderer dispose",
            renderer.DrainInFlight,
            _ownsWorkers ? Workers.Dispose : null,
            Pages.Dispose,
            anim != null ? anim.Dispose : null,
            _ownsRenderer ? renderer.Dispose : null);
    }

    int _disposed;
}
