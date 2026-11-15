using Uo.Pack;
using Uo.Render;

namespace Uo.Client;

/// <summary>Walk-relevant object at one tile (ClassicUO Pathfinder.PathObject as a struct).</summary>
public struct PathObject
{
    public uint Flags;
    public int Z, AverageZ, Height;
    public bool StretchedLand;
    public sbyte Z0, ZRight, ZBottom, ZLeft; // stretched land corners (ClassicUO Land.YOffsets >> 2)

    public const uint ImpassableOrSurface = 1, Surface = 2, Bridge = 4, NoDiagonal = 8;

    /// <summary>Land.CalculateCurrentAverageZ.</summary>
    public readonly int CurrentAverageZ(int direction)
    {
        int result = DirectionZ(((direction >> 1) + 1) & 3);
        if ((direction & 1) != 0) return result;
        return (result + DirectionZ(direction >> 1)) >> 1;
    }

    readonly int DirectionZ(int d) => d switch { 1 => ZRight, 2 => ZBottom, 3 => ZLeft, _ => Z0 };
}

/// <summary>Dynamic objects (ground items) that take part in walk checks. Implemented by the ECS world.</summary>
public interface IDynamicBlockers
{
    /// <summary>Appends ground items at (x, y) as static graphics: graphic and z. Returns the number written.</summary>
    int ItemsAt(int x, int y, Span<ushort> graphics, Span<sbyte> zs);
}

/// <summary>Map walkability for client-side prediction: a port of ClassicUO's Pathfinder.CanWalk / CalculateNewZ /
/// CalculateMinMaxZ for the normal step state (not dead/GM, flying or on a sea horse), reading land, statics and
/// tiledata straight from the pack's regions. Main thread only; regions are loaded synchronously on first use
/// (one region covers 512×512 tiles).</summary>
public sealed class WalkMap
{
    public const int DefaultBlockHeight = 16;
    const int MaxObjects = 64;

    static readonly int[] OffsetX = [0, 1, 1, 1, 0, -1, -1, -1, 0, 1];
    static readonly int[] OffsetY = [-1, -1, 0, 1, 1, 1, 0, -1, -1, -1];

    readonly PackSet _pack;
    readonly Dictionary<int, Region> _regions = new(16);
    readonly PathObject[] _list = new PathObject[MaxObjects + 1];
    readonly PathObject[] _minMax = new PathObject[MaxObjects + 1];
    readonly ushort[] _itemGfx = new ushort[MaxObjects];
    readonly sbyte[] _itemZ = new sbyte[MaxObjects];

    public IDynamicBlockers Dynamic;
    /// <summary>Extra impassable tiles (key = y * 65536 + x). Used by the fake server to disagree with the client.</summary>
    public HashSet<int> ExtraBlocked;
    public long RegionLoadTicks;
    public int RegionLoads;

    public WalkMap(PackSet pack) => _pack = pack;

    public int Width => _pack.MapWidth;
    public int Height => _pack.MapHeight;

    // ------------------------------------------------------------------ map access

    Region GetRegion(int bx, int by)
    {
        int rb = _pack.RegionBlocks;
        int rx = bx / rb, ry = by / rb;
        int key = ry * 4096 + rx;
        if (_regions.TryGetValue(key, out var r)) return r;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        r = new Region { Rx = rx, Ry = ry, Loaded = true };
        r.Parse(PackConst.RegionPath(_pack.FacetDir, rx, ry));
        RegionLoadTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        RegionLoads++;
        _regions[key] = r;
        return r;
    }

    /// <summary>Loads the regions around a tile ahead of time (login, teleport) so the first steps don't hitch.</summary>
    public void Preload(int x, int y)
    {
        int span = _pack.RegionBlocks * 8;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int tx = x + dx * span / 4, ty = y + dy * span / 4;
                if (tx >= 0 && ty >= 0 && tx < Width && ty < Height) GetRegion(tx >> 3, ty >> 3);
            }
    }

    public bool InBounds(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;

    /// <summary>Land tile id and z; false outside the map or where no block data exists.</summary>
    public bool Land(int x, int y, out ushort tile, out sbyte z)
    {
        tile = 0; z = 0;
        if (!InBounds(x, y)) return false;
        int bx = x >> 3, by = y >> 3;
        var r = GetRegion(bx, by);
        int rb = _pack.RegionBlocks;
        uint off = r.BlockOffset(bx - r.Rx * rb, by - r.Ry * rb);
        if (off == 0) return false;
        var cell = r.Land(off)[(y & 7) * 8 + (x & 7)];
        tile = (ushort)(cell.TileId & 0x3FFF);
        z = cell.Z;
        return true;
    }

    /// <summary>ClassicUO Map.GetTileZ: land z, -125 outside the map.</summary>
    public sbyte TileZ(int x, int y) => Land(x, y, out _, out var z) ? z : (sbyte)-125;

    ReadOnlySpan<StaticCell> BlockStatics(int x, int y)
    {
        int bx = x >> 3, by = y >> 3;
        var r = GetRegion(bx, by);
        int rb = _pack.RegionBlocks;
        uint off = r.BlockOffset(bx - r.Rx * rb, by - r.Ry * rb);
        return off == 0 ? default : r.Statics(off);
    }

    // ------------------------------------------------------------------ ClassicUO Pathfinder port

    /// <summary>Pathfinder.CreateItemList for the normal step state. Mobiles are ignored (ClassicUO ignores them at
    /// full stamina; the server shoves). Returns the number of objects written to <paramref name="list"/>.</summary>
    int CreateItemList(PathObject[] list, int x, int y)
    {
        if (!InBounds(x, y)) return 0;
        int n = 0;
        var landData = _pack.Land;
        var staticData = _pack.Statics;

        if (Land(x, y, out ushort g, out sbyte z))
        {
            if (g < 0x01AE && g != 2 || g > 0x01B5 && g != 0x01DB)
            {
                uint flags = PathObject.ImpassableOrSurface;
                if ((landData[g].Flags & TileFlag.Impassable) == 0) flags |= PathObject.Surface | PathObject.Bridge;
                ref var o = ref list[n++];
                o = default;
                o.Flags = flags;
                LandHeights(x, y, g, z, ref o);
            }
        }

        foreach (ref readonly var s in BlockStatics(x, y))
        {
            if (s.X != (x & 7) || s.Y != (y & 7) || n >= MaxObjects) continue;
            AddStatic(list, ref n, s.Graphic, s.Z, staticData, false);
        }

        if (Dynamic != null)
        {
            int k = Dynamic.ItemsAt(x, y, _itemGfx, _itemZ);
            for (int i = 0; i < k && n < MaxObjects; i++) AddStatic(list, ref n, _itemGfx[i], _itemZ[i], staticData, true);
        }

        if (ExtraBlocked != null && n < MaxObjects && ExtraBlocked.Contains(y * 65536 + x))
            list[n++] = new PathObject { Flags = PathObject.ImpassableOrSurface, Z = -128, AverageZ = 127, Height = 255 };

        return n;
    }

    static void AddStatic(PathObject[] list, ref int n, ushort graphic, sbyte z, StaticTileData[] staticData, bool isItem)
    {
        if (graphic >= staticData.Length) return;
        ref readonly var data = ref staticData[graphic];
        bool impassable = (data.Flags & TileFlag.Impassable) != 0, surface = (data.Flags & TileFlag.Surface) != 0, bridge = (data.Flags & TileFlag.Bridge) != 0;
        uint flags = 0;
        if (impassable || surface) flags = PathObject.ImpassableOrSurface;
        if (!impassable)
        {
            if (surface) flags |= PathObject.Surface;
            if (bridge) flags |= PathObject.Bridge;
        }
        if (isItem && (graphic >= 0x3946 && graphic <= 0x3964 || graphic == 0x0082)) flags &= ~PathObject.ImpassableOrSurface;
        if (flags == 0) return;
        int height = data.Height;
        int avg = bridge ? height / 2 : height;
        list[n++] = new PathObject { Flags = flags, Z = z, AverageZ = avg + z, Height = height };
    }

    /// <summary>Land.ApplyStretch: average/min z from the four corners when the tile has a texmap, else flat.
    /// Stretched = the corner neighbourhood isn't flat (Land.CalculateNormal returning true for any corner).</summary>
    void LandHeights(int x, int y, ushort g, sbyte z, ref PathObject o)
    {
        o.Z = z; o.AverageZ = z; o.Height = 0;
        o.Z0 = o.ZRight = o.ZBottom = o.ZLeft = z;
        int tex = _pack.Land[g].TexId;
        bool hasTex = tex != 0 && tex < _pack.Texmaps.Length && _pack.Texmaps[tex].Present;
        bool stretchedByFlag = tex == 0 && (_pack.Land[g].Flags & TileFlag.Wet) != 0;
        if (!hasTex || stretchedByFlag) return;

        sbyte zr = TileZ(x + 1, y), zl = TileZ(x, y + 1), zb = TileZ(x + 1, y + 1);
        int avg = Math.Abs(z - zb) <= Math.Abs(zl - zr) ? (z + zb) >> 1 : (zl + zr) >> 1;
        int min = Math.Min(z, Math.Min(zr, Math.Min(zl, zb)));
        o.Z = min;           // PathObject(flags, landMinZ, landAverageZ, landHeight)
        o.AverageZ = avg;
        o.Height = avg - min;
        o.Z0 = z; o.ZRight = zr; o.ZLeft = zl; o.ZBottom = zb;

        sbyte t10 = TileZ(x, y - 1), t20 = TileZ(x + 1, y - 1), t01 = TileZ(x - 1, y), t31 = TileZ(x + 2, y);
        sbyte t02 = TileZ(x - 1, y + 1), t32 = TileZ(x + 2, y + 1), t13 = TileZ(x, y + 2), t23 = TileZ(x + 1, y + 2);
        o.StretchedLand = !Flat(z, t10, zr, zl, t01) || !Flat(zr, t20, t31, zb, z) || !Flat(zb, zr, t32, t23, zl) || !Flat(zl, z, zb, t13, t02);
    }

    static bool Flat(int a, int b, int c, int d, int e) => a == b && a == c && a == d && a == e;

    static void Sort(PathObject[] list, int n)
    {
        // insertion sort by (Z, Height): tiny lists, no allocation
        for (int i = 1; i < n; i++)
        {
            var v = list[i];
            int j = i - 1;
            while (j >= 0 && (list[j].Z > v.Z || list[j].Z == v.Z && list[j].Height > v.Height)) { list[j + 1] = list[j]; j--; }
            list[j + 1] = v;
        }
    }

    /// <summary>Pathfinder.CalculateMinMaxZ: bounds from the tile the step starts on.</summary>
    int CalculateMinMaxZ(ref int minZ, ref int maxZ, int newX, int newY, int currentZ, int newDirection)
    {
        minZ = -128;
        maxZ = currentZ;
        newDirection &= 7;
        int direction = newDirection ^ 4;
        newX += OffsetX[direction];
        newY += OffsetY[direction];
        int n = CreateItemList(_minMax, newX, newY);
        if (n == 0) return 0;

        for (int i = 0; i < n; i++)
        {
            ref readonly var obj = ref _minMax[i];
            int averageZ = obj.AverageZ;
            if (averageZ <= currentZ && obj.StretchedLand)
            {
                int avgZ = obj.CurrentAverageZ(newDirection);
                if (minZ < avgZ) minZ = avgZ;
                if (maxZ < avgZ) maxZ = avgZ;
            }
            else
            {
                if ((obj.Flags & PathObject.ImpassableOrSurface) != 0 && averageZ <= currentZ && minZ < averageZ) minZ = averageZ;
                if ((obj.Flags & PathObject.Bridge) != 0 && currentZ == averageZ)
                {
                    int z = obj.Z, height = z + obj.Height;
                    if (maxZ < height) maxZ = height;
                    if (minZ > z) minZ = z;
                }
            }
        }
        maxZ += 2;
        return maxZ;
    }

    /// <summary>Pathfinder.CalculateNewZ: the z the mobile ends at on (x, y), or false if it can't stand there.</summary>
    public bool CalculateNewZ(int x, int y, ref sbyte z, int direction)
    {
        int minZ = -128, maxZ = z;
        CalculateMinMaxZ(ref minZ, ref maxZ, x, y, z, direction);

        var list = _list;
        int n = CreateItemList(list, x, y);
        if (n == 0) return false;
        Sort(list, n);
        list[n++] = new PathObject { Flags = PathObject.ImpassableOrSurface, Z = 128, AverageZ = 128, Height = 128 };

        int resultZ = -128;
        if (z < minZ) z = (sbyte)minZ;
        int currentTempObjZ = 1000000;
        int currentZ = -128;

        for (int i = 0; i < n; i++)
        {
            ref readonly var obj = ref list[i];
            if ((obj.Flags & PathObject.ImpassableOrSurface) == 0) continue;
            int objZ = obj.Z;
            if (objZ - minZ >= DefaultBlockHeight)
            {
                for (int j = i - 1; j >= 0; j--)
                {
                    ref readonly var t = ref list[j];
                    if ((t.Flags & (PathObject.Surface | PathObject.Bridge)) == 0) continue;
                    int tempAverageZ = t.AverageZ;
                    if (tempAverageZ >= currentZ && objZ - tempAverageZ >= DefaultBlockHeight &&
                        (tempAverageZ <= maxZ && (t.Flags & PathObject.Surface) != 0 || (t.Flags & PathObject.Bridge) != 0 && t.Z <= maxZ))
                    {
                        int delta = Math.Abs(z - tempAverageZ);
                        if (delta < currentTempObjZ)
                        {
                            currentTempObjZ = delta;
                            resultZ = tempAverageZ;
                        }
                    }
                }
            }
            int averageZ = obj.AverageZ;
            if (minZ < averageZ) minZ = averageZ;
            if (currentZ < averageZ) currentZ = averageZ;
        }

        z = (sbyte)resultZ;
        return resultZ != -128;
    }

    public static void GetNewXY(int direction, ref int x, ref int y)
    {
        switch (direction & 7)
        {
            case 0: y--; break;
            case 1: x++; y--; break;
            case 2: x++; break;
            case 3: x++; y++; break;
            case 4: y++; break;
            case 5: x--; y++; break;
            case 6: x--; break;
            case 7: x--; y--; break;
        }
    }

    /// <summary>Pathfinder.CanWalk: a diagonal needs both adjacent cardinals; if blocked it slides to an adjacent
    /// direction. On success x/y/z/direction are updated (direction may change).</summary>
    public bool CanWalk(ref int direction, ref int x, ref int y, ref sbyte z)
    {
        int newX = x, newY = y;
        sbyte newZ = z;
        int newDirection = direction & 7;
        GetNewXY(newDirection, ref newX, ref newY);
        bool passed = CalculateNewZ(newX, newY, ref newZ, newDirection);

        if ((newDirection & 1) != 0)
        {
            if (passed)
            {
                for (int i = 0; i < 2 && passed; i++)
                {
                    int tx = x, ty = y;
                    sbyte tz = z;
                    int td = (newDirection + (i == 0 ? 1 : 7)) & 7;
                    GetNewXY(td, ref tx, ref ty);
                    passed = CalculateNewZ(tx, ty, ref tz, td);
                }
            }
            if (!passed)
            {
                int orig = newDirection;
                for (int i = 0; i < 2 && !passed; i++)
                {
                    newX = x; newY = y; newZ = z;
                    newDirection = (orig + (i == 0 ? 1 : 7)) & 7;
                    GetNewXY(newDirection, ref newX, ref newY);
                    passed = CalculateNewZ(newX, newY, ref newZ, newDirection);
                }
            }
        }

        if (passed)
        {
            x = newX; y = newY; z = newZ;
            direction = newDirection;
        }
        return passed;
    }

    /// <summary>Standing z at a tile for spawning: CalculateNewZ from the land z.</summary>
    public bool SpawnZ(int x, int y, out sbyte z)
    {
        z = TileZ(x, y);
        if (z == -125) return false;
        return CalculateNewZ(x, y, ref z, 4);
    }

    /// <summary>Debug: true if any static or item on the tile has the given tiledata flag.</summary>
    public bool TileHasStaticFlag(int x, int y, ulong flag)
    {
        if (!InBounds(x, y)) return false;
        foreach (ref readonly var s in BlockStatics(x, y))
            if (s.X == (x & 7) && s.Y == (y & 7) && s.Graphic < _pack.Statics.Length && (_pack.Statics[s.Graphic].Flags & flag) != 0) return true;
        return false;
    }
}

/// <summary>UO tiledata flag bits used by walking (the renderer's <see cref="TileFlags"/> covers drawing).</summary>
public static class TileFlag
{
    public const ulong Background = 0x1, Wall = 0x10, Impassable = 0x40, Wet = 0x80, Surface = 0x200, Bridge = 0x400,
        Internal = 0x10000, PartialHue = 0x40000, NoDiagonal = 0x02000000, Door = 0x20000000,
        StairBack = 0x40000000, StairRight = 0x80000000, MultiMovable = 0x10000000000;
}
