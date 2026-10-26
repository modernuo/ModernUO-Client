using System.Numerics;
using System.Runtime.CompilerServices;
using Uo.Pack;

namespace Uo.Render;

/// <summary>Bakes one 8×8 map block into sprite instances on a worker thread, replicating ClassicUO's draw order:
/// per-tile PriorityZ ordering (Chunk.AddGameObject) and the isometric depth (View.CalculateDepthZ),
/// expressed as an integer depth key so one depth-tested draw reproduces ClassicUO's land-then-statics passes.</summary>
public sealed class BakeJob : Job
{
    public static PackSet Pack;

    // input
    public int Slot, Bx, By;
    public readonly Region[] Regions = new Region[9]; // 3×3 around the block's region; [4] = center
    public int Rx0, Ry0;                              // region coords of Regions[0]

    // output
    public SpriteInstance[] Instances = new SpriteInstance[256];
    /// <summary>Per instance: which map object it draws (<see cref="ObjectKey"/>); instances are in ascending cell order.</summary>
    public uint[] Keys = new uint[256];
    public int Count;
    public int MinX, MinY, MaxX, MaxY;
    public readonly ushort[] Pages = new ushort[ChunkStore.MaxPagesPerChunk];
    public int PageCount;
    public bool PagesOverflow;
    /// <summary>Chunk-local indices of the baked translucent instances (alpha &lt; 255), ascending.</summary>
    public int[] Translucent = new int[16];
    public int TranslucentCount;

    /// <summary>ClassicUO's alpha for tiledata Translucent statics (ProcessAlpha: CalculateAlpha(ref AlphaHue, 178)).</summary>
    public const byte TranslucentAlpha = 178;

    // scratch
    long[] _keys = new long[256];
    readonly bool[] _stretched = new bool[64];
    readonly uint[] _corners = new uint[64];
    readonly uint[] _light = new uint[64];

    const int DepthBias = 300;

    /// <summary>Integer depth key (bigger = nearer): ((D·16 + (x &amp; 15))·8 + rank), D = 100·(x+y) + 127 + priorityZ.
    /// D is ClassicUO's (x+y) + (127+z)·0.01 scaled by 100. The low bits make the key independent of draw order:
    /// at equal D the tile with larger x wins (ClassicUO's x-major traversal), and rank orders equal-priority objects
    /// within a tile by insertion order. Without them, overlapping sprites with equal keys in different chunks
    /// resolved by chunk-table order, which changes as chunks stream: visible flicker while panning.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint DepthKey(int x, int y, int priorityZ, int rank)
        => (uint)(((((x + y) * 100 + 127 + priorityZ + DepthBias) * 16) + (x & 15)) * 8 + Math.Min(rank, 7));

    /// <summary><see cref="DepthKey"/> from D = 100·(x+y) + 127 + priorityZ given directly (hosts that hold ClassicUO's float
    /// depth (x+y) + (127+z)·0.01 can pass round(depth·100)).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint DepthKeyD(int d, int x, int rank)
        => (uint)((((d + DepthBias) * 16) + (x & 15)) * 8 + Math.Clamp(rank, 0, 7));

    /// <summary>cell (y·8 + x in the block) &lt;&lt; 25 | static &lt;&lt; 24 | (u8)z &lt;&lt; 16 | graphic (land: tile id).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ObjectKey(int cell, bool isStatic, int z, int graphic)
        => ((uint)cell << 25) | (isStatic ? 1u << 24 : 0) | ((uint)(byte)(sbyte)z << 16) | (uint)(ushort)graphic;

    public static uint CameraDepthKey(float tileSum) => (uint)((int)(tileSum * 100) + 127 + DepthBias) * 128;

    public override void Run()
    {
        Count = 0;
        PageCount = 0;
        PagesOverflow = false;
        TranslucentCount = 0;
        MinX = MinY = int.MaxValue;
        MaxX = MaxY = int.MinValue;

        var reg = Regions[4];
        int rb = Pack.RegionBlocks;
        uint off = reg.BlockOffset(Bx - reg.Rx * rb, By - reg.Ry * rb);
        if (off == 0) return;
        var land = reg.Land(off);
        var statics = reg.Statics(off);

        bool wobble = Pack.WaterWobble;
        bool bakeTranslucent = Pack.BakeTranslucentAlpha;
        int need = 128 + statics.Length; // land, its animated-water copies, statics
        if (_keys.Length < need) _keys = new long[need * 2];
        if (Instances.Length < need) Instances = new SpriteInstance[need * 2];
        if (Keys.Length < Instances.Length) Keys = new uint[Instances.Length];

        var sprites = Pack.Sprites;
        var landData = Pack.Land;
        var remap = Pack.LandRemap; // season graphics (ClassicUO Land.UpdateGraphicBySeason)
        var staticData = Pack.Statics;
        var staticRemap = Pack.StaticRemap; // season graphics (ClassicUO Static.UpdateGraphicBySeason)
        int n = 0;
        int baseTx = Bx * 8, baseTy = By * 8;

        // ---- land (ClassicUO: Land.Create + ApplyStretch + AddGameObject case Land)
        for (int i = 0; i < 64; i++)
        {
            ushort tile = (ushort)(land[i].TileId & 0x3FFF);
            if (remap != null) tile = remap[tile];
            if (tile <= 2) continue; // Land.AllowedToDraw = graphic > 2
            int z = land[i].Z;
            int tx = baseTx + (i & 7), ty = baseTy + (i >> 3);
            int pz = z - 2;
            _stretched[i] = false;

            int texId = landData[tile].TexId;
            if (texId != 0 && texId < Pack.Texmaps.Length && Pack.Texmaps[texId].Present)
            {
                int zTop = z, zRight = GetZ(tx + 1, ty, z), zLeft = GetZ(tx, ty + 1, z), zBottom = GetZ(tx + 1, ty + 1, z);
                int t10 = GetZ(tx, ty - 1, z), t20 = GetZ(tx + 1, ty - 1, z), t01 = GetZ(tx - 1, ty, z), t31 = GetZ(tx + 2, ty, z);
                int t02 = GetZ(tx - 1, ty + 1, z), t32 = GetZ(tx + 2, ty + 1, z), t13 = GetZ(tx, ty + 2, z), t23 = GetZ(tx + 1, ty + 2, z);
                bool s = false;
                s |= Normal(z, t10, zRight, zLeft, t01, out var nTop);
                s |= Normal(zRight, t20, t31, zBottom, z, out var nRight);
                s |= Normal(zBottom, zRight, t32, t23, zLeft, out var nBottom);
                s |= Normal(zLeft, z, zBottom, t13, t02, out var nLeft);
                if (s)
                {
                    _stretched[i] = true;
                    int avg = Math.Abs(zTop - zBottom) <= Math.Abs(zLeft - zRight) ? (zTop + zBottom) >> 1 : (zLeft + zRight) >> 1;
                    pz = avg - 2;
                    _corners[i] = (uint)(byte)(sbyte)zTop | ((uint)(byte)(sbyte)zRight << 8) | ((uint)(byte)(sbyte)zLeft << 16) | ((uint)(byte)(sbyte)zBottom << 24);
                    _light[i] = LightByte(nTop) | (LightByte(nRight) << 8) | (LightByte(nLeft) << 16) | (LightByte(nBottom) << 24);
                }
            }
            _keys[n++] = SortKey(i, pz, 0, i);
        }

        // ---- statics (ClassicUO: Chunk.Load + AddGameObject default case)
        for (int j = 0; j < statics.Length; j++)
        {
            ref readonly var s = ref statics[j];
            if (s.X >= 8 || s.Y >= 8) continue;
            int g = staticRemap != null ? staticRemap[s.Graphic] : s.Graphic;
            if (!CanBeDrawn(g, staticData)) continue;
            ref readonly var data = ref staticData[g];
            int pz = s.Z;
            if ((data.Flags & TileFlags.Background) != 0) pz--;
            if (data.Height != 0) pz++;
            if ((data.Flags & TileFlags.MultiMovable) != 0) pz++;
            _keys[n++] = SortKey(s.Y * 8 + s.X, pz, 1, 64 + j);
        }

        // Stable per-tile order: by priorityZ, land before statics at equal priority, then insertion order.
        _keys.AsSpan(0, n).Sort();

        int prevCell = -1, prevPz = int.MinValue, rank = 0;
        for (int k = 0; k < n; k++)
        {
            long key = _keys[k];
            int cell = (int)(key >> 40);
            int pz = (int)((key >> 28) & 0xFFF) - 1024;
            int idx = (int)(key & 0x7FFFFFF);
            rank = cell == prevCell && pz == prevPz ? rank + 1 : 0; // rank within equal (tile, priorityZ)
            prevCell = cell;
            prevPz = pz;
            int tx = baseTx + (cell & 7), ty = baseTy + (cell >> 3);
            int sx = (tx - ty) * 22 - 22;           // ClassicUO UpdateRealScreenPosition without camera offset
            uint depth = DepthKey(tx, ty, pz, rank);

            if (idx < 64)
            {
                int z = land[idx].Z;
                ushort tile = (ushort)(land[idx].TileId & 0x3FFF);
                if (remap != null) tile = remap[tile];
                if (_stretched[idx])
                {
                    ref readonly var t = ref Pack.Texmaps[landData[tile].TexId];
                    int ax = sx, ay = (tx + ty) * 22 - 22; // posY += Z << 2 cancels the z offset; corners carry z
                    // corner byte 0 (top) is the tile's own z: the shader's max-Z test reads it
                    Emit(ax, ay, t, Pack.TexPageBase + t.Page, 0, SpriteInstance.FlagStretch | SpriteInstance.FlagLit | SpriteInstance.FlagZTest, depth, _light[idx], _corners[idx]);
                    Keys[Count - 1] = ObjectKey(cell, false, z, tile);
                    int zmin = Math.Min(Math.Min((sbyte)_corners[idx], (sbyte)(_corners[idx] >> 8)), Math.Min((sbyte)(_corners[idx] >> 16), (sbyte)(_corners[idx] >> 24)));
                    int zmax = Math.Max(Math.Max((sbyte)_corners[idx], (sbyte)(_corners[idx] >> 8)), Math.Max((sbyte)(_corners[idx] >> 16), (sbyte)(_corners[idx] >> 24)));
                    Bounds(ax, ay - zmax * 4, ax + 44, ay + 44 - zmin * 4);
                }
                else
                {
                    ref readonly var sp = ref sprites[tile];
                    if (!sp.Present) continue;
                    int x = sx + sp.TrimX, y = (tx + ty) * 22 - (z << 2) - 22 + sp.TrimY;
                    uint zc = (uint)(byte)(sbyte)z | SpriteInstance.ZLand << 16;
                    if (wobble && (landData[tile].Flags & TileFlags.Wet) != 0)
                    {
                        // LandView with AnimatedWaterEffect: a second, scaled copy over the tile (scale <= 1.2). Emitted first
                        // with the same key, so it wins the depth tie against its own tile and keeps the tile's order
                        // against every other object; overrides find both (same object key).
                        Emit(x, y, sp, sp.Page, 0, SpriteInstance.FlagZTest, depth, SpriteInstance.WobbleBits(sp.TrimX, sp.TrimY), zc);
                        Keys[Count - 1] = ObjectKey(cell, false, z, tile);
                        Bounds(sx, y - sp.TrimY, sx + (int)MathF.Ceiling((sp.TrimX + sp.W) * 1.2f) + 1, y - sp.TrimY + (int)MathF.Ceiling((sp.TrimY + sp.H) * 1.2f) + 1);
                    }
                    Emit(x, y, sp, sp.Page, 0, SpriteInstance.FlagZTest, depth, 0, zc);
                    Keys[Count - 1] = ObjectKey(cell, false, z, tile);
                    Bounds(x, y, x + sp.W, y + sp.H);
                }
            }
            else
            {
                ref readonly var s = ref statics[idx - 64];
                int g = staticRemap != null ? staticRemap[s.Graphic] : s.Graphic;
                ref readonly var sp = ref sprites[PackConst.StaticSpriteBase + g];
                if (!sp.Present) continue;
                // View.DrawStatic: x -= (w >> 1) - 22; y -= h - 44 (w,h = original art size)
                int x = sx - ((sp.OrigW >> 1) - 22) + sp.TrimX;
                int y = (tx + ty) * 22 - (s.Z << 2) - 22 - (sp.OrigH - 44) + sp.TrimY;
                uint flags = 0, hueRow = 0;
                int hue = s.Hue;
                bool partial = (staticData[g].Flags & TileFlags.PartialHue) != 0;
                if ((hue & 0x8000) != 0) { partial = true; hue &= 0x7FFF; }
                if (hue != 0 && (hue & 0x4000) == 0 && hue - 1 < Pack.HueCount)
                {
                    hueRow = (uint)(hue - 1);
                    flags = SpriteInstance.FlagHued | (partial ? SpriteInstance.FlagPartial : 0);
                }
                // the tile's low bytes for the shader's out-of-range tint; tiledata Translucent statics bake ClassicUO's
                // settled alpha 178 (ProcessAlpha) and go on the chunk's translucent sub-list
                uint light = SpriteInstance.StaticTileBits(tx, ty);
                bool translucent = bakeTranslucent && (staticData[g].Flags & TileFlags.Translucent) != 0;
                if (translucent) light |= 255u - TranslucentAlpha;
                Emit(x, y, sp, sp.Page, hueRow, flags | SpriteInstance.FlagZTest, depth, light, ZInfo(g, s.Z, staticData));
                Keys[Count - 1] = ObjectKey(cell, true, s.Z, s.Graphic);
                if (translucent) AddTranslucent(Count - 1);
                Bounds(x, y, x + sp.W, y + sp.H);
            }
        }

        // the out-of-range tint reconstructs each instance's tile (SpriteInstance.Tile, mirrored in sprite.hlsl): check it
        for (int i = 0; i < Count; i++)
        {
            int cell = (int)(Keys[i] >> 25);
            Instances[i].Tile(out int tx, out int ty);
            if (tx != baseTx + (cell & 7) || ty != baseTy + (cell >> 3)) Interlocked.Increment(ref TileMismatches);
        }
    }

    /// <summary>Baked instances whose reconstructed tile (<see cref="SpriteInstance.Tile"/>) is wrong, since start (0 expected).</summary>
    public static int TileMismatches;

    /// <summary>Corners of a <see cref="SpriteInstance.FlagZTest"/> static: (u8)z | height &lt;&lt; 8 | Z* bits &lt;&lt; 16, with
    /// Static._canBeTransparent (Static.Create) as <see cref="SpriteInstance.ZCotCapable"/>.</summary>
    static uint ZInfo(int g, int z, StaticTileData[] staticData)
    {
        ref readonly var d = ref staticData[g];
        ulong f = d.Flags;
        bool roof = (f & TileFlags.Roof) != 0, surface = (f & TileFlags.Surface) != 0, background = (f & TileFlags.Background) != 0;
        bool cotCapable = d.Height > 5 || d.Height == 0 || roof || surface && background || (f & TileFlags.Wall) != 0
            || d.Height == 5 && surface && !background;
        var noCot = Pack.StaticNoCot;
        bool exempt = (f & TileFlags.Foliage) != 0 || noCot != null && g < noCot.Length && noCot[g];
        uint bits = (roof ? SpriteInstance.ZRoof : 0) | (cotCapable ? SpriteInstance.ZCotCapable : 0) | (exempt ? SpriteInstance.ZNoCot : 0);
        return (uint)(byte)(sbyte)z | (uint)d.Height << 8 | bits << 16;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static long SortKey(int cell, int pz, int isStatic, int index)
        => ((long)cell << 40) | ((long)(pz + 1024) << 28) | ((long)isStatic << 27) | (long)index;

    void Emit(int x, int y, in SpriteEntry sp, int page, uint hueRow, uint flags, uint depth, uint light, uint corners)
    {
        ref var inst = ref Instances[Count++];
        inst.X = x;
        inst.Y = y;
        inst.Whf = SpriteInstance.PackWhf(sp.W, sp.H, flags);
        inst.Uv = sp.X | ((uint)sp.Y << 16);
        inst.PageHue = (uint)page | (hueRow << 16);
        inst.Depth = depth;
        inst.Light = light;
        inst.Corners = corners;
        AddPage((ushort)page);
    }

    void AddTranslucent(int local)
    {
        if (TranslucentCount == Translucent.Length) Array.Resize(ref Translucent, Translucent.Length * 2);
        Translucent[TranslucentCount++] = local;
    }

    void AddPage(ushort page)
    {
        for (int i = 0; i < PageCount; i++)
            if (Pages[i] == page) return;
        if (PageCount < Pages.Length) Pages[PageCount++] = page;
        else PagesOverflow = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void Bounds(int x0, int y0, int x1, int y1)
    {
        if (x0 < MinX) MinX = x0;
        if (y0 < MinY) MinY = y0;
        if (x1 > MaxX) MaxX = x1;
        if (y1 > MaxY) MaxY = y1;
    }

    /// <summary>Land z at a world tile, looking across region borders. Falls back to <paramref name="fallback"/> where no data is loaded.</summary>
    int GetZ(int tx, int ty, int fallback)
    {
        if (tx < 0 || ty < 0) return fallback;
        int bx = tx >> 3, by = ty >> 3;
        int rb = Pack.RegionBlocks;
        int rx = bx / rb, ry = by / rb;
        int ix = rx - Rx0, iy = ry - Ry0;
        if ((uint)ix > 2 || (uint)iy > 2) return fallback;
        var r = Regions[iy * 3 + ix];
        if (r == null) return fallback;
        uint off = r.BlockOffset(bx - rx * rb, by - ry * rb);
        if (off == 0) return fallback;
        return r.Land(off)[(ty & 7) * 8 + (tx & 7)].Z;
    }

    /// <summary>Port of Land.CalculateNormal.</summary>
    static bool Normal(int tile, int top, int right, int bottom, int left, out Vector3 normal)
    {
        if (tile == top && tile == right && tile == bottom && tile == left)
        {
            normal = new Vector3(0, 0, 1);
            return false;
        }
        var u = new Vector3(-22, -22, (left - tile) * 4);
        var v = new Vector3(-22, 22, (bottom - tile) * 4);
        var ret = Vector3.Cross(v, u);
        u = new Vector3(-22, 22, (bottom - tile) * 4);
        v = new Vector3(22, 22, (right - tile) * 4);
        ret += Vector3.Cross(v, u);
        u = new Vector3(22, 22, (right - tile) * 4);
        v = new Vector3(22, -22, (top - tile) * 4);
        ret += Vector3.Cross(v, u);
        u = new Vector3(22, -22, (top - tile) * 4);
        v = new Vector3(-22, -22, (left - tile) * 4);
        ret += Vector3.Cross(v, u);
        normal = Vector3.Normalize(ret);
        return true;
    }

    /// <summary>IsometricWorld.fx get_light with Brightlight = TerrainShadowsLevel(15) × 0.1, quantized (170 = 1.0).</summary>
    static uint LightByte(Vector3 n)
    {
        const float bright = 1.5f;
        var l = Vector3.Normalize(new Vector3(0, 1, 1));
        float b = MathF.Max(Vector3.Dot(Vector3.Normalize(n), l), 0) / 2 + 0.5f;
        float r = b + (bright * (b - 0.85355339f) - (b - 0.85355339f));
        return (uint)Math.Clamp((int)MathF.Round(r * 170), 0, 255);
    }

    /// <summary>GameObject.CanBeDrawn for statics; the tiledata "nodraw" name check is the converter's
    /// <see cref="TilePackFlags.NameNoDraw"/> (0 in v0 packs, which lack it).</summary>
    static bool CanBeDrawn(int g, StaticTileData[] data)
    {
        switch (g)
        {
            case 0x0001: case 0x21BC: case 0xA1FE: case 0xA1FF: case 0xA200: case 0xA201:
                return false;
            case 0x9E4C: case 0x9E64: case 0x9E65: case 0x9E7D:
                return g < data.Length && (data[g].Flags & (TileFlags.Background | TileFlags.Surface)) == 0;
        }
        if (g != 0x63D3 && g >= 0x2198 && g <= 0x21A4) return false;
        if (g >= data.Length) return false;
        if (g != 0x63D3 && (data[g].PackFlags & TilePackFlags.NameNoDraw) != 0) return false;
        return (data[g].Flags & TileFlags.NoDiagonal) == 0;
    }
}

/// <summary>UO TileFlag bit values used by the renderer.</summary>
public static class TileFlags
{
    public const ulong Background = 0x1, Translucent = 0x8, Wall = 0x10, Surface = 0x200, Wet = 0x80, Foliage = 0x20000, PartialHue = 0x40000,
        NoDiagonal = 0x02000000, Roof = 0x10000000, MultiMovable = 0x10000000000;
}
