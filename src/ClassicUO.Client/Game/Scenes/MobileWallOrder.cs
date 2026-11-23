// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Uo.Pack;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// Switch <c>mobile_wall_order</c> (default off), depth key mode only (depth_model off): a mobile's strips below its anchor
    /// row (MobileSpriteExtract: depth + 1 + 2i, i ≥ 1: feet, robe hem, hooves, a large body's legs) are capped just behind a
    /// wall in front of the mobile, so they no longer show on its face (Felucca 1453 1668 z 10, the studio's south and east
    /// walls; clip-1 design note section 2).
    /// <list type="bullet">
    /// <item>A wall here is a static, multi piece or ground item with tiledata Wall whose [z, z + height) holds the mobile's z
    /// (the mobile is inside it, not standing on its top), on one of the tiles one or two diagonals ahead of the mobile's depth
    /// tile: (1,0), (0,1), (1,1), (2,0), (0,2). Further walls beat the + 2i strips already.</item>
    /// <item>Only the half of the wall's art that is in front of the mobile counts. Wall art is anchored at its tile's south
    /// vertex: a piece of a wall line along x (0x0034) is the left half, its face on the tile's south-west edge (Y = y + 1); a
    /// piece of a line along y (0x0035) is the right half, on the south-east edge (X = x + 1). On (1,0) the left half lies on
    /// the line Y = y + 1 that runs past the mobile's own tile (the mobile stands in that wall line, e.g. in a doorway): not in
    /// front; its right half is. (0,1) the mirror; (1,1) both halves; (2,0) and (0,2) like (1,0) and (0,1). That half, less
    /// 6 px at the vertex column and cut to the art's opaque columns (the pack sprite's trim), is the wall's column range
    /// relative to the depth tile's centre column.</item>
    /// <item>A strip whose frame columns overlap a wall's range takes the depth key one below the lowest such wall's
    /// (D − 1, D = 100 (x + y) + 127 + PriorityZ), never less than the upper strip's + 1, and never more than before.</item>
    /// <item>Round 2 (owner): other pieces with height &gt; 0 (furniture: counters, tables, easels) count over their whole
    /// diamond; a wall found ahead continues along its line as one segment (<see cref="SegmentReach"/>); mid-step both tiles
    /// are scanned and every strip may drop to the earlier tile's key (<see cref="Scan.MinAdd"/>); shadows lose to every piece
    /// they overlap (<see cref="ShadowAdd"/>); all layers take one cap from the composite's columns (MobileSpriteExtract measures them before the strips);
    /// pieces overhead on the mobile's diagonal (bottom at or over z + <see cref="PersonHeight"/>) cap the upper strip.</item>
    /// </list>
    /// While the rule (or the checker) is on each mobile's scan is resolved per frame (<see cref="Resolve"/>: the kept result while
    /// its tile, step and the chunks' cap versions are the same, nothing when the cap summaries show no piece around, else
    /// the full scan); its version is part of the mobile's select key (MobileFrameCache), so recorded strips follow it. <c>CUO_MOBILE_WALL_ORDER=1|0</c> overrides the setting and
    /// <c>CUO_MOBILE_WALL_ORDER_CHECK=1</c> logs each strip the rule moves (or would move, with the switch off) with the mobile,
    /// the wall and the keys, with a 5 s summary, in developer builds.
    /// </summary>
    internal static class MobileWallOrder
    {
        /// <summary>Tests: the switch (null: the setting).</summary>
        internal static bool? OverrideForTests;

        /// <summary>Tests: the art sprite table (null: the pack's).</summary>
        internal static SpriteEntry[] SpritesForTests;

#if CUO_DEVTOOLS
        private static readonly bool? s_env = Environment.GetEnvironmentVariable("CUO_MOBILE_WALL_ORDER") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        /// <summary>CUO_MOBILE_WALL_ORDER_CHECK=1: the scan runs with the switch off too, and every strip the rule moves or would
        /// move is logged.</summary>
        internal static readonly bool Check = Environment.GetEnvironmentVariable("CUO_MOBILE_WALL_ORDER_CHECK") == "1";

        /// <summary>CUO_MOBILE_WALL_ORDER_CACHE_CHECK=1: every scan <see cref="Resolve"/> returns (kept, empty from the cap
        /// summaries, or new) is compared with a fresh full scan; mismatches are logged with the mobile, its tile and the
        /// walls, with a 5 s summary.</summary>
        internal static readonly bool CacheCheck = Environment.GetEnvironmentVariable("CUO_MOBILE_WALL_ORDER_CACHE_CHECK") == "1";
#else
        internal const bool Check = false;
        internal const bool CacheCheck = false;
#endif

        public static bool On
        {
            get
            {
                if (OverrideForTests.HasValue)
                {
                    return OverrideForTests.Value;
                }

#if CUO_DEVTOOLS
                if (s_env.HasValue)
                {
                    return s_env.Value;
                }
#endif
                return Settings.GlobalSettings != null && Settings.GlobalSettings.MobileWallOrder;
            }
        }

        /// <summary>The rule applies: the switch is on and the depth key draws (depth_model has its own per-pixel walls).</summary>
        public static bool Rule => On && !DepthModel.On;

        public const int MaxWalls = 24;

        /// <summary>Pixels of each half next to the north vertex column that do not count (a wall's art is a few pixels thick
        /// across its vertex column: the pack's brick walls reach 4-5 px into the other half).</summary>
        public const int VertexMargin = 6;

        public struct Wall
        {
            /// <summary>The wall's depth key D (100 (x + y) + 127 + PriorityZ, as baked).</summary>
            public int D;

            /// <summary>Its in-front columns [Lo, Hi) relative to the depth tile's centre column.</summary>
            public short Lo, Hi;

            public ushort Graphic, X, Y;
            public sbyte Z;
            public byte Height;

            /// <summary>A piece above the mobile on its own diagonal (bottom at or over its top): caps the upper strip's rows
            /// above <see cref="Bottom"/> only.</summary>
            public bool Overhead;

            /// <summary>Overhead pieces: the screen row just below the art's lowest opaque row, relative to the depth tile's
            /// centre row at the mobile's z.</summary>
            public short Bottom;

            /// <summary>Round 3: a stair piece under a mobile standing on the staircase (its top at or under the mobile's
            /// standing top, or a step it could climb): the mobile's strips are raised just over it (<see cref="FloorAdd"/>).</summary>
            public bool Floor;

            /// <summary>Round 3: a piece with height behind the mobile (or beside it): only its shadow loses to it.</summary>
            public bool ShadowOnly;

            /// <summary>Round 3 item C: a piece on the mobile's own diagonal in front of the neighbouring tile its sprite hangs
            /// over (x − 1, y on the left, x, y − 1 on the right); its columns are clipped to that overhang (beyond ±22 px), and
            /// it caps every strip, the upper one too, down to <see cref="SideMinAdd"/>.</summary>
            public bool Side;
        }

        [InlineArray(MaxWalls)]
        public struct WallList
        {
            private Wall _e;
        }

        /// <summary>
        /// One mobile's scan: the walls in front of it whose height holds its z. A class so no 0.5 KB value is copied per
        /// mobile per frame: a scan is built in a scratch instance with <see cref="MaxWalls"/> entries (<see cref="Scan()"/>),
        /// and kept on the mobile's MobileFrameCache as <see cref="Empty"/> (no walls, shared, never written) or as a pooled
        /// instance whose wall array (Uo.Buffers.BufferPool) is sized to its count. <see cref="Version"/> is unique per kept
        /// result (0: <see cref="Empty"/>), so the select key holds that number instead of the walls.
        /// </summary>
        public sealed class Scan
        {
            /// <summary>The scan with no walls (what most mobiles have): shared, never written.</summary>
            public static readonly Scan Empty = new Scan(Array.Empty<Wall>());

            /// <summary>The rule applies (<see cref="Rule"/>); false with only the checker on.</summary>
            public bool Active;

            /// <summary>Walls found (at most <see cref="MaxWalls"/>; <see cref="Dropped"/> more were not kept).</summary>
            public byte Count, Dropped;

            /// <summary>The depth tile's column less the mobile tile's, in tiles (a step's bump, View.CalculateDepthZ).</summary>
            public sbyte Du;

            /// <summary>The depth tile's diagonal less the mobile tile's (a step's bump): the depth tile's centre row is
            /// 22 · Dv below the mobile tile's.</summary>
            public sbyte Dv;

            /// <summary>The lowest add a strip may be capped to (0: the upper strip's). Mid-step it is the tile sum of the
            /// earlier of the two tiles less the depth tile's (−1 or −2), so every strip, the upper one included, may drop to
            /// the key it has standing on that tile, behind a wall ahead of the tile it is leaving.</summary>
            public sbyte MinAdd;

            /// <summary>The walls, [0, <see cref="Count"/>).</summary>
            public Wall[] Walls;

            /// <summary>Unique per kept result; 0 for <see cref="Empty"/> and scratch scans.</summary>
            public uint Version;

            /// <summary>A scratch scan with room for <see cref="MaxWalls"/> walls (tests, the scan itself).</summary>
            public Scan() : this(new Wall[MaxWalls])
            {
            }

            private Scan(Wall[] walls)
            {
                Walls = walls;
            }

            internal void Clear()
            {
                Active = false;
                Count = Dropped = 0;
                Du = Dv = MinAdd = 0;
            }

            /// <summary>The same walls and keys (what the strips' depths depend on).</summary>
            public bool SameAs(Scan o)
            {
                if (ReferenceEquals(this, o))
                {
                    return true;
                }

                if (Count != o.Count || Count != 0 && (Active != o.Active || Du != o.Du || Dv != o.Dv || MinAdd != o.MinAdd))
                {
                    return false;
                }

                for (int i = 0; i < Count; i++)
                {
                    ref readonly Wall a = ref Walls[i];
                    ref readonly Wall b = ref o.Walls[i];

                    if (a.D != b.D || a.Lo != b.Lo || a.Hi != b.Hi || a.Overhead != b.Overhead || a.Bottom != b.Bottom || a.Floor != b.Floor || a.ShadowOnly != b.ShadowOnly || a.Side != b.Side)
                    {
                        return false;
                    }
                }

                return true;
            }

            private static readonly System.Collections.Generic.Stack<Scan> s_pool = new System.Collections.Generic.Stack<Scan>();
            private static uint s_version;

            /// <summary>Scans pooled (at most <see cref="PoolMax"/>), rents that allocated a new one.</summary>
            public static int PoolCount => s_pool.Count;
            public const int PoolMax = 256;
            public static long PoolMisses;

            /// <summary>A kept copy of scratch scan <paramref name="from"/> (main thread): <see cref="Empty"/> when it found
            /// nothing, else a pooled instance with a new <see cref="Version"/>.</summary>
            internal static Scan Keep(Scan from)
            {
                if (from.Count == 0)
                {
                    return Empty;
                }

                Scan k;

                if (s_pool.Count != 0)
                {
                    k = s_pool.Pop();
                }
                else
                {
                    k = new Scan(Array.Empty<Wall>());
                    PoolMisses++;
                }

                k.Walls = Uo.Buffers.BufferPool.Rent<Wall>(from.Count);
                Array.Copy(from.Walls, k.Walls, from.Count);
                k.Active = from.Active;
                k.Count = from.Count;
                k.Dropped = from.Dropped;
                k.Du = from.Du;
                k.Dv = from.Dv;
                k.MinAdd = from.MinAdd;
                k.Version = ++s_version;

                return k;
            }

            /// <summary>Gives a kept scan back (its wall array to the buffer pool); <see cref="Empty"/> is ignored.</summary>
            internal static void Release(Scan k)
            {
                if (k == null || ReferenceEquals(k, Empty) || k.Walls.Length == 0)
                {
                    return;
                }

                Uo.Buffers.BufferPool.Return(k.Walls);
                k.Walls = Array.Empty<Wall>();
                k.Count = 0;
                k.Version = 0;

                if (s_pool.Count < PoolMax)
                {
                    s_pool.Push(k);
                }
            }
        }

        /// <summary>
        /// A mobile's last scan inputs (MobileFrameCache.WallCache): tile, z, step (depth tile and step target), shadow, rule,
        /// and the cap versions (Chunk.CapVersion, CapFlatVersion) of the chunks around it. The scan depends only on these
        /// (it is tile-relative: the camera and the walking offset within a step do not change it), so while they are the
        /// same the kept result is reused.
        /// </summary>
        public struct ScanCache
        {
            public bool Valid, Active, Shadow, FlatOverhead;
            public ushort X, Y, Bx, By, Nx, Ny;
            public sbyte Z;
            public int Cx0, Cy0;
            public byte Cw, Ch;
            public ChunkStamps Chunks;
        }

        /// <summary>A chunk the scan read and its cap versions when it did (null: not loaded).</summary>
        public struct ChunkStamp
        {
            public Chunk Chunk;
            public uint Version, Flat;
        }

        /// <summary>The chunks covering every tile the scan read: at most 3 x 3 (a wall segment reaches 5 tiles ahead).</summary>
        [InlineArray(9)]
        public struct ChunkStamps
        {
            private ChunkStamp _e;
        }

        // the tiles ahead of a scanned tile and, for walls, the in-front half of each (columns relative to that tile's centre
        // column); other pieces (boxes: tables, counters, easels) count over their whole diamond, [c - 22, c + 22)
        private static readonly sbyte[] s_dx = { 1, 0, 1, 2, 0 };
        private static readonly sbyte[] s_dy = { 0, 1, 1, 0, 2 };
        private static readonly short[] s_lo = { 22 + VertexMargin, -44, -22, 44 + VertexMargin, -66 };
        private static readonly short[] s_hi = { 44, -22 - VertexMargin, 22, 66, -44 - VertexMargin };

        // the step's destination for each screen offset sign (View.CalculateDepthZ's cases): index (sign x + 1) * 3 + sign y + 1
        private static readonly sbyte[] s_stepDx = { -1, -1, 0, -1, 0, 1, 0, 1, 1 };
        private static readonly sbyte[] s_stepDy = { 0, 1, 1, -1, 0, 1, -1, -1, 0 };

        // the scratch scans (main thread): the one being built and the checker's reference
        private static readonly Scan s_scratch = new Scan(), s_reference = new Scan();

        /// <summary>
        /// The scan for <paramref name="m"/> this frame (MobileAnimationSystem.SelectCached; its <see cref="Scan.Version"/> is
        /// part of the select key): <see cref="Scan.Empty"/> unless the rule or the checker is on. <paramref name="shadow"/>: the
        /// mobile draws a shadow, so the pieces behind it are scanned for it too (<see cref="ScanBehind"/>).
        /// <list type="bullet">
        /// <item>While the inputs in <paramref name="cache"/>'s <see cref="ScanCache"/> are the same (tile, z, step, shadow, rule,
        /// the cap versions of the chunks around), the kept result: an idle mobile compares numbers only.</item>
        /// <item>Otherwise, when no tile the scan reads holds a piece that could count (<see cref="Quiet"/>, the chunks' cap
        /// summaries), <see cref="Scan.Empty"/> without walking any tile list (an open field).</item>
        /// <item>Otherwise the full scan (<see cref="FullScan"/>); a result equal to the kept one keeps it (and its version).</item>
        /// </list>
        /// <c>CUO_MOBILE_WALL_ORDER_CACHE_CHECK=1</c> compares every result with a fresh full scan.
        /// </summary>
        internal static Scan Resolve(Mobile m, MobileFrameCache cache, bool shadow) =>
            Resolve(m.World?.Map, m.Serial, m.X, m.Y, m.Z, m.Offset, cache, shadow);

        /// <summary><see cref="Resolve(Mobile, MobileFrameCache, bool)"/> for a mobile <paramref name="serial"/> on tile
        /// (<paramref name="x"/>, <paramref name="y"/>) at z <paramref name="mz"/> with this step offset.</summary>
        internal static Scan Resolve(Map.Map map, uint serial, int x, int y, int mz, Vector3 offset, MobileFrameCache cache, bool shadow)
        {
            bool rule = Rule;

            if (!rule && !Check || map == null)
            {
                Forget(cache);

                return Scan.Empty;
            }

            DepthTile(x, y, offset, out int bx, out int by);
            StepTile(x, y, offset, out int nx, out int ny);
            ref ScanCache c = ref cache.WallCache;
            Scan result;

            if (c.Valid && c.X == x && c.Y == y && c.Z == mz && c.Bx == bx && c.By == by && c.Nx == nx && c.Ny == ny && c.Shadow == shadow &&
                c.Active == rule && Unchanged(map, ref c, x, y, mz, nx, ny))
            {
#if CUO_DEVTOOLS
                FrameCacheHits++;
                TotalHits++;
#endif
                result = cache.Wall;
            }
            else
            {
                c.Valid = true;
                c.X = (ushort)x;
                c.Y = (ushort)y;
                c.Z = (sbyte)mz;
                c.Bx = (ushort)bx;
                c.By = (ushort)by;
                c.Nx = (ushort)nx;
                c.Ny = (ushort)ny;
                c.Shadow = shadow;
                c.Active = rule;
                c.FlatOverhead = FlatOverhead(map, x, y, mz, nx, ny);
                // the neighbourhood Quiet reads; a full scan widens it to every tile it read (segments reach further)
                s_minX = Math.Min(x, nx) - Reach;
                s_maxX = Math.Max(x, nx) + Reach;
                s_minY = Math.Min(y, ny) - Reach;
                s_maxY = Math.Max(y, ny) + Reach;

                if (Quiet(map, x, y, mz, nx, ny, shadow))
                {
#if CUO_DEVTOOLS
                    FrameEmpty++;
                    TotalEmpty++;
#endif
                    result = Scan.Empty;
                }
                else
                {
#if CUO_DEVTOOLS
                    TotalScans++;
#endif
                    s_scratch.Clear();
                    FullScan(map, x, y, mz, offset, bx, by, shadow, rule, s_scratch);
                    result = cache.Wall != null && cache.Wall.SameAs(s_scratch) ? cache.Wall : Scan.Keep(s_scratch);
                }

                TakeVersions(map, ref c);

                if (!ReferenceEquals(result, cache.Wall))
                {
                    Scan.Release(cache.Wall);
                }
            }

#if CUO_DEVTOOLS
            if (Check)
            {
                Tick(rule, result.Count != 0, result.Dropped);
            }

            if (CacheCheck)
            {
                Compare(map, serial, x, y, mz, offset, bx, by, nx, ny, shadow, rule, result);
            }
#endif

            return result;
        }

        /// <summary>Drops <paramref name="cache"/>'s kept scan (MobileFrameCache.Reset, rule off).</summary>
        internal static void Forget(MobileFrameCache cache)
        {
            if (!ReferenceEquals(cache.Wall, Scan.Empty))
            {
                Scan.Release(cache.Wall);
                cache.Wall = Scan.Empty;
            }

            cache.WallCache.Valid = false;

            for (int i = 0; i < 9; i++)
            {
                cache.WallCache.Chunks[i].Chunk = null;
            }
        }

        /// <summary>
        /// The full scan (the reference; before the cache, every mobile ran it every frame) for a mobile on tile
        /// (<paramref name="x"/>, <paramref name="y"/>) at z <paramref name="mz"/> into scratch scan <paramref name="s"/>
        /// (cleared by the caller).
        /// </summary>
        internal static void FullScan(Map.Map map, int x, int y, int mz, Vector3 offset, int bx, int by, bool shadow, bool rule, Scan s)
        {
#if CUO_DEVTOOLS
            FrameScans++;
#endif
            s.Active = rule;
            s.Du = (sbyte)((bx - by) - (x - y));
            s.Dv = (sbyte)((bx + by) - (x + y));
            ScanStep(map, x, y, mz, offset, bx, by, ref s);

            if (shadow)
            {
                ScanBehind(map, x, y, mz, ((x - y) - (bx - by)) * 22, ((x + y) - (bx + by)) * 22, ref s);
            }
        }

        // the reach of the tiles Quiet reads, as tile offsets from the mobile's tile and from the step's target
        private const int Reach = 2;

        // the tiles read since Resolve set them (Quiet's neighbourhood, then every tile the full scan visits)
        private static int s_minX, s_maxX, s_minY, s_maxY;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Visit(int tx, int ty)
        {
            if (tx < s_minX) s_minX = tx;
            if (tx > s_maxX) s_maxX = tx;
            if (ty < s_minY) s_minY = ty;
            if (ty > s_maxY) s_maxY = ty;
        }

        // the chunks covering the tiles read, with their cap versions
        private static void TakeVersions(Map.Map map, ref ScanCache c)
        {
            int cx0 = s_minX >> 3, cx1 = s_maxX >> 3, cy0 = s_minY >> 3, cy1 = s_maxY >> 3;
            c.Cx0 = cx0;
            c.Cy0 = cy0;
            c.Cw = (byte)Math.Min(3, cx1 - cx0 + 1);
            c.Ch = (byte)Math.Min(3, cy1 - cy0 + 1);

            for (int j = 0; j < c.Ch; j++)
            {
                for (int i = 0; i < c.Cw; i++)
                {
                    ref ChunkStamp st = ref c.Chunks[j * 3 + i];
                    int cx = cx0 + i, cy = cy0 + j;
                    st.Chunk = cx < 0 || cy < 0 ? null : map.GetChunk(cx << 3, cy << 3, false);
                    st.Version = st.Chunk?.CapVersion ?? 0;
                    st.Flat = st.Chunk?.CapFlatVersion ?? 0;
                }
            }
        }

        // the kept result still holds: every chunk it read is the same with the same cap version; a changed flat version (an
        // item without height came or went) matters only when such an item is or was overhead
        private static bool Unchanged(Map.Map map, ref ScanCache c, int x, int y, int mz, int nx, int ny)
        {
            bool flat = false;

            for (int j = 0; j < c.Ch; j++)
            {
                for (int i = 0; i < c.Cw; i++)
                {
                    ref ChunkStamp st = ref c.Chunks[j * 3 + i];
                    Chunk kept = st.Chunk;

                    if (kept == null)
                    {
                        // not loaded when scanned: still not loaded
                        int cx = c.Cx0 + i, cy = c.Cy0 + j;

                        if (cx >= 0 && cy >= 0 && map.GetChunk(cx << 3, cy << 3, false) != null)
                        {
                            return false;
                        }

                        continue;
                    }

                    if (kept.CapVersion != st.Version || kept.IsDestroyed)
                    {
                        return false;
                    }

                    flat |= kept.CapFlatVersion != st.Flat;
                }
            }

            if (flat)
            {
                if (c.FlatOverhead || FlatOverhead(map, x, y, mz, nx, ny))
                {
                    return false;
                }

                for (int k = 0; k < 9; k++)
                {
                    ref ChunkStamp st = ref c.Chunks[k];
                    st.Flat = st.Chunk?.CapFlatVersion ?? 0;
                }
            }

            return true;
        }

        // round 3 offsets (ScanBehind's s_bx, s_by) and the five tiles ahead (s_dx, s_dy) are read below with the sides and the
        // overhead diagonal; the step's target adds its own tile, five ahead and its diagonal

        /// <summary>
        /// No tile the full scan reads holds a piece that could count (Chunk cap summary): the tiles ahead of the mobile's tile
        /// and of the step's target, the side tiles, their own tiles, and the tiles behind with a shadow have no piece with
        /// height whose top is above <paramref name="mz"/> (AddPieces, ScanBehind: a piece with a top at or under the mobile's
        /// standing top never counts, and a standing top above <paramref name="mz"/> needs such a stair piece on its own
        /// tile), and the overhead diagonals have no piece at or above <paramref name="mz"/> + <see cref="PersonHeight"/>
        /// (ScanOverhead). Then the scan finds nothing. CUO_MOBILE_WALL_ORDER_CACHE_CHECK proves the reach.
        /// </summary>
        internal static bool Quiet(Map.Map map, int x, int y, int mz, int nx, int ny, bool shadow)
        {
            if (!QuietAround(map, x, y, mz))
            {
                return false;
            }

            if ((nx != x || ny != y) && !QuietAround(map, nx, ny, mz))
            {
                return false;
            }

            if (shadow)
            {
                for (int t = 0; t < s_bx.Length; t++)
                {
                    if (Capped(map, x + s_bx[t], y + s_by[t], mz, false))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        // a tile, the five ahead of it, and its diagonal neighbours (sides, overhead)
        private static bool QuietAround(Map.Map map, int x, int y, int mz)
        {
            for (int k = -1; k <= 1; k++)
            {
                if (Capped(map, x + k, y - k, mz, true))
                {
                    return false;
                }
            }

            for (int t = 0; t < s_dx.Length; t++)
            {
                if (Capped(map, x + s_dx[t], y + s_dy[t], mz, false))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool Capped(Map.Map map, int tx, int ty, int mz, bool overhead)
        {
            Chunk chunk = map.GetChunk(tx, ty, false);

            if (chunk == null)
            {
                return false;
            }

            int cell = (tx & 7) << 3 | (ty & 7);

            return chunk.CapTop[cell] > mz || overhead && chunk.CapZ[cell] >= mz + PersonHeight;
        }

        // an item without height at or above mz + PersonHeight on an overhead diagonal (the only place such items count)
        private static bool FlatOverhead(Map.Map map, int x, int y, int mz, int nx, int ny)
        {
            for (int k = -1; k <= 1; k++)
            {
                if (FlatAt(map, x + k, y - k, mz) || (nx != x || ny != y) && FlatAt(map, nx + k, ny - k, mz))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool FlatAt(Map.Map map, int tx, int ty, int mz)
        {
            Chunk chunk = map.GetChunk(tx, ty, false);

            return chunk != null && chunk.CapFlatZ[(tx & 7) << 3 | (ty & 7)] >= mz + PersonHeight;
        }

        /// <summary>The tile View.CalculateDepthZ sorts a mobile on: its own, or the next one while it steps south or east.</summary>
        public static void DepthTile(int x, int y, Vector3 offset, out int bx, out int by)
        {
            bx = x;
            by = y;

            if (offset.X > 0 && offset.Y >= 0)
            {
                // north-east, east
                bx++;
            }
            else if (offset.X == 0 && offset.Y > 0)
            {
                // south-east
                bx++;
                by++;
            }
            else if (offset.X < 0 && offset.Y >= 0)
            {
                // south, south-west
                by++;
            }
        }

        /// <summary>The tile a step with this screen offset ends on (the mobile's own while it stands).</summary>
        public static void StepTile(int x, int y, Vector3 offset, out int nx, out int ny)
        {
            int i = (Math.Sign(offset.X) + 1) * 3 + Math.Sign(offset.Y) + 1;
            nx = x + s_stepDx[i];
            ny = y + s_stepDy[i];
        }

        /// <summary>
        /// Fills <paramref name="s"/> for a mobile on tile (<paramref name="x"/>, <paramref name="y"/>) at z <paramref name="mz"/>
        /// with this step offset, sorted on depth tile (<paramref name="bx"/>, <paramref name="by"/>). Standing: the pieces ahead
        /// of its tile. Mid-step (the sprite moves between two tiles while the key sorts it on the later one): the pieces ahead
        /// of both tiles, and <see cref="Scan.MinAdd"/> lets every strip drop to the earlier tile's key.
        /// </summary>
        internal static void ScanStep(Map.Map map, int x, int y, int mz, Vector3 offset, int bx, int by, ref Scan s)
        {
            StepTile(x, y, offset, out int nx, out int ny);
            int depthCol = bx - by;
            bool stepping = nx != x || ny != y;
            int standTop = StandTop(map, x, y, mz);

            if (stepping)
            {
                standTop = Math.Max(standTop, StandTop(map, nx, ny, mz));
            }

            ScanAt(map, x, y, mz, standTop, ((x - y) - depthCol) * 22, ((x + y) - (bx + by)) * 22, stepping, ref s);
            ScanSides(map, x, y, mz, standTop, ((x - y) - depthCol) * 22, ((x + y) - (bx + by)) * 22, stepping, ref s);
            ScanOverhead(map, x, y, mz, ((x - y) - depthCol) * 22, ((x + y) - (bx + by)) * 22, ref s);

            if (nx != x || ny != y)
            {
                ScanAt(map, nx, ny, mz, standTop, ((nx - ny) - depthCol) * 22, ((nx + ny) - (bx + by)) * 22, true, ref s);
                ScanOverhead(map, nx, ny, mz, ((nx - ny) - depthCol) * 22, ((nx + ny) - (bx + by)) * 22, ref s);
                s.MinAdd = (sbyte)Math.Min(0, Math.Min(x + y, nx + ny) - (bx + by));
            }
        }

        /// <summary>The mobile height the overhead rule assumes: UO's person height (the server's walk checks use it for mounted
        /// mobiles too; a mounted rider's art reaches about 5 z higher, the seat offset).</summary>
        public const int PersonHeight = 16;

        /// <summary>The lowest add the upper strip is capped to under a piece overhead: its key stays above its own tile's
        /// floor (D 100 (x + y) + 127 + z) and the pieces standing on it at its z (+ 128 + z).</summary>
        public const float OverheadMinAdd = -0.49f;

        /// <summary>
        /// Adds to <paramref name="s"/> the pieces above a mobile on tile (<paramref name="tx"/>, <paramref name="ty"/>) at z
        /// <paramref name="mz"/>: statics, multi pieces and ground items on that tile and its two neighbours on the same
        /// diagonal whose bottom z is at or above <paramref name="mz"/> + <see cref="PersonHeight"/> (roofs, upper floors,
        /// balconies, of any height). Such a piece shares screen pixels only with the mobile's upper part and is in front of it
        /// there (rays rise toward the camera), but its key (+ 0.5 on the mobile's diagonal) lost to the upper strip (+ 1):
        /// the rider's head over the roof eave on its own tile (owner, Felucca 1468 1712 0, roof z 23). Columns: the art's
        /// within the tile's diamond.
        /// </summary>
        internal static void ScanOverhead(Map.Map map, int tx0, int ty0, int mz, int shift, int rowShift, ref Scan s)
        {
            SpriteEntry[] sprites = SpritesForTests ?? Client.Game?.UO?.FileManager?.Arts?.PackSpriteEntries;

            for (int k = -1; k <= 1; k++)
            {
                int tx = tx0 + k, ty = ty0 - k;
                Visit(tx, ty);
                Chunk chunk = map.GetChunk(tx, ty, false);

                if (chunk == null)
                {
                    continue;
                }

                int c = 2 * k * 22;

                CountTile();
                for (GameObject o = TileWalk.Begin(chunk, (tx & 7) << 3 | (ty & 7), out TileWalk walk); o != null; o = walk.Next())
                {
                    CountObject();
                    if (o is Item it)
                    {
                        if (it.IsDestroyed || !it.OnGround || it.IsCorpse || it.IsMulti)
                        {
                            continue;
                        }
                    }
                    else if (o is not Static && o is not Multi)
                    {
                        continue;
                    }

                    if (o.Z < mz + PersonHeight)
                    {
                        continue;
                    }

                    int lo = c - 22, hi = c + 22;
                    // View.DrawStatic: the art's bottom row on the tile's south vertex (centre + 22), 4 px up per z
                    int bottom = rowShift - 4 * (o.Z - mz) + 22;
                    int idx = PackConst.StaticSpriteBase + o.Graphic;

                    if (sprites != null && idx < sprites.Length && sprites[idx].Present && sprites[idx].OrigW != 0)
                    {
                        ref readonly SpriteEntry sp = ref sprites[idx];
                        int left = c - (sp.OrigW >> 1) + sp.TrimX;
                        lo = Math.Max(lo, left);
                        hi = Math.Min(hi, left + sp.W);
                        bottom += sp.TrimY + sp.H - sp.OrigH;
                    }

                    if (lo >= hi || Holds(in s, o, tx, ty))
                    {
                        continue;
                    }

                    int d = 100 * (tx + ty) + 127 + o.PriorityZ;
                    int slot = Slot(ref s, d);

                    if (slot < 0)
                    {
                        continue;
                    }

                    ref Wall w = ref s.Walls[slot];
                    w = default;
                    w.D = d;
                    w.Lo = (short)(lo + shift);
                    w.Hi = (short)(hi + shift);
                    w.Graphic = o.Graphic;
                    w.X = (ushort)tx;
                    w.Y = (ushort)ty;
                    w.Z = o.Z;
                    w.Height = o.Graphic < Chunk.StaticTileData.Length ? Chunk.StaticTileData[o.Graphic].Height : (byte)0;
                    w.Overhead = true;
                    w.Bottom = (short)bottom;
                }
            }
        }

        /// <summary>Fills <paramref name="s"/> with the walls ahead of depth tile (<paramref name="bx"/>, <paramref name="by"/>)
        /// that hold z <paramref name="mz"/>.</summary>
        internal static void ScanAt(Map.Map map, int bx, int by, int mz, ref Scan s) =>
            ScanAt(map, bx, by, mz, StandTop(map, bx, by, mz), 0, 0, false, ref s);

        /// <summary>
        /// Round 3 item C (owner, Felucca 1466 1670 0: a mount facing west, its chest and head over the table at x − 1, y + 1):
        /// a sprite's columns beyond the anchor tile's half-width stand on the neighbouring tile on that side, so the pieces in
        /// front of that tile on the mobile's own diagonal ((x − 1, y + 1) left, (x + 1, y − 1) right) cover those columns,
        /// with the same object rule and exemptions as the tiles ahead. One tile each side.
        /// </summary>
        internal static void ScanSides(Map.Map map, int x, int y, int mz, int standTop, int shift, int rowShift, bool stepping, ref Scan s)
        {
            StaticTiles[] data = Chunk.StaticTileData;
            SpriteEntry[] sprites = SpritesForTests ?? Client.Game?.UO?.FileManager?.Arts?.PackSpriteEntries;
            int minBottom = stepping ? int.MinValue : MinBottom;

            for (int side = -1; side <= 1; side += 2)
            {
                // left: (x - 1, y + 1), its diamond at columns [-66, -22); right: (x + 1, y - 1), [22, 66)
                int first = s.Count;
                AddPieces(map, data, sprites, x + side, y - side,
                    side * 44, rowShift, minBottom, mz, standTop, 1, shift, side < 0 ? -66 : 22, side < 0 ? -22 : 66, LineSide, ref s);

                for (int i = first; i < s.Count; i++)
                {
                    ref Wall w = ref s.Walls[i];
                    w.Side = true;

                    if (side < 0)
                    {
                        w.Hi = (short)Math.Min(w.Hi, -22 + shift);
                    }
                    else
                    {
                        w.Lo = (short)Math.Max(w.Lo, 22 + shift);
                    }
                }
            }
        }

        /// <summary>The lowest add a side piece (<see cref="Wall.Side"/>) caps the upper strip to: just over its own tile's
        /// floor and land (the pieces on the same diagonal are a whole diagonal under the upper strip's key).</summary>
        public const float SideMinAdd = -0.52f;

        /// <summary>
        /// The height a mobile at z <paramref name="mz"/> on tile (<paramref name="x"/>, <paramref name="y"/>) stands up to, as
        /// the pathfinder raises its climb reference (Pathfinder.CalculateMinMaxZ: on a bridge or stair piece whose walkable top,
        /// z + height / 2, is the mobile's z, the reference is the piece's full top). A mobile on stairs stands at the half
        /// height, so the steps and items around it up to that top are under it. Otherwise <paramref name="mz"/>.
        /// </summary>
        internal static int StandTop(Map.Map map, int x, int y, int mz)
        {
            Chunk chunk = map.GetChunk(x, y, false);
            int top = mz;

            if (chunk == null)
            {
                return top;
            }

            StaticTiles[] data = Chunk.StaticTileData;

            CountTile();
            for (GameObject o = TileWalk.Begin(chunk, (x & 7) << 3 | (y & 7), out TileWalk walk); o != null; o = walk.Next())
            {
                CountObject();
                if ((o is Static || o is Multi || o is Item { IsMulti: false }) && o.Graphic < data.Length)
                {
                    ref StaticTiles td = ref data[o.Graphic];

                    if (Stairlike(in td) && Pathfinder.WalkableTop(o.Z, td.Height, true) == mz)
                    {
                        top = Math.Max(top, o.Z + td.Height);
                    }
                }
            }

            return top;
        }

        private static bool Stairlike(in StaticTiles td) => (td.Flags & (TileFlag.Bridge | TileFlag.StairBack | TileFlag.StairRight)) != 0;

        /// <summary>A standing mobile's pieces ahead matter only where their art reaches the rows of its strips below the
        /// anchor and of its shadow (sheared up to half the frame height above the anchor, about 35 rows for a mount): art
        /// ending higher (screen rows relative to the depth tile's centre) is not kept. Mid-step every piece is kept (the
        /// upper strip may drop).</summary>
        public const int MinBottom = -48;

        /// <summary>
        /// Adds to <paramref name="s"/> the pieces ahead of tile (<paramref name="bx"/>, <paramref name="by"/>) that hold z
        /// <paramref name="mz"/>, their columns moved by <paramref name="shift"/> (that tile's centre column less the depth
        /// tile's). A piece is a static, multi piece or ground item with tiledata height &gt; 0 whose top (z + height) is above
        /// <paramref name="mz"/> (owner rule, round 2: a piece in front covers the mobile unless it is flat or the mobile stands
        /// above it; a piece entirely above the mobile in front covers it too): walls (and doors) count with their in-front half,
        /// other pieces with their whole diamond. A wall found ahead continues as a
        /// segment: the wall pieces on the neighbouring tiles of the same line (a left-half piece's line runs along x, a
        /// right-half piece's along y; a corner continues both), with their face half and their own key, up to
        /// <see cref="SegmentReach"/> tiles each way, so a wide sprite or its shadow reaching a piece further along the wall
        /// (beyond the five tiles) is capped by it too.
        /// </summary>
        internal static void ScanAt(Map.Map map, int bx, int by, int mz, int standTop, int shift, int rowShift, bool stepping, ref Scan s)
        {
            int minBottom = stepping ? int.MinValue : MinBottom;
            StaticTiles[] data = Chunk.StaticTileData;
            SpriteEntry[] sprites = SpritesForTests ?? Client.Game?.UO?.FileManager?.Arts?.PackSpriteEntries;

            // the scanned tiles first (their pieces keep their in-front columns), then the segments from them
            Span<int> found = stackalloc int[5];

            for (int t = 0; t < s_dx.Length; t++)
            {
                found[t] = AddPieces(map, data, sprites, bx + s_dx[t], by + s_dy[t], (s_dx[t] - s_dy[t]) * 22, rowShift + (s_dx[t] + s_dy[t]) * 22, minBottom, mz,
                    standTop, s_dx[t] + s_dy[t], shift, s_lo[t], s_hi[t], LineAny, ref s);
            }

            for (int t = 0; t < s_dx.Length; t++)
            {
                int dx = s_dx[t], dy = s_dy[t];
                int lines = found[t];

                for (int k = 1; k <= SegmentReach && (lines & LineX) != 0; k++)
                {
                    if ((AddPieces(map, data, sprites, bx + dx + k, by + dy, (dx + k - dy) * 22, rowShift + (dx + k + dy) * 22, minBottom, mz, standTop, 1, shift, 0, 0, LineX, ref s) & LineX) == 0)
                    {
                        break;
                    }
                }

                for (int k = 1; k <= SegmentReach && (lines & LineX) != 0; k++)
                {
                    if ((AddPieces(map, data, sprites, bx + dx - k, by + dy, (dx - k - dy) * 22, rowShift + (dx - k + dy) * 22, minBottom, mz, standTop, 1, shift, 0, 0, LineX, ref s) & LineX) == 0)
                    {
                        break;
                    }
                }

                for (int k = 1; k <= SegmentReach && (lines & LineY) != 0; k++)
                {
                    if ((AddPieces(map, data, sprites, bx + dx, by + dy + k, (dx - dy - k) * 22, rowShift + (dx + dy + k) * 22, minBottom, mz, standTop, 1, shift, 0, 0, LineY, ref s) & LineY) == 0)
                    {
                        break;
                    }
                }

                for (int k = 1; k <= SegmentReach && (lines & LineY) != 0; k++)
                {
                    if ((AddPieces(map, data, sprites, bx + dx, by + dy - k, (dx - dy + k) * 22, rowShift + (dx + dy - k) * 22, minBottom, mz, standTop, 1, shift, 0, 0, LineY, ref s) & LineY) == 0)
                    {
                        break;
                    }
                }
            }
        }

        /// <summary>Tiles a wall segment is followed each way from a piece found ahead (a wide mount's shadow reaches about
        /// three tiles' columns).</summary>
        public const int SegmentReach = 3;

        private const int LineX = 1, LineY = 2, LineAny = 4, LineSide = 8;

        /// <summary>
        /// The pieces of tile (<paramref name="tx"/>, <paramref name="ty"/>) (north vertex at column <paramref name="c"/>) that
        /// hold <paramref name="mz"/>. <paramref name="line"/> <see cref="LineAny"/>: a scanned tile; walls take [lo, hi) (their
        /// in-front half), other pieces their diamond. <see cref="LineX"/> / <see cref="LineY"/>: a segment tile; only wall
        /// pieces of that line count, with their face half. Returns the lines of the wall pieces found (LineX | LineY).
        /// </summary>
        private static int AddPieces(Map.Map map, StaticTiles[] data, SpriteEntry[] sprites, int tx, int ty, int c, int rowC, int minBottom, int mz,
            int standTop, int dist, int shift,
            int lo0, int hi0, int line, ref Scan s)
        {
            Visit(tx, ty);
            Chunk chunk = map.GetChunk(tx, ty, false);

            if (chunk == null)
            {
                return 0;
            }

            int lines = 0;

            CountTile();
            for (GameObject o = TileWalk.Begin(chunk, (tx & 7) << 3 | (ty & 7), out TileWalk walk); o != null; o = walk.Next())
            {
                CountObject();
                if (o is Item it)
                {
                    if (it.IsDestroyed || !it.OnGround || it.IsCorpse || it.IsMulti)
                    {
                        continue;
                    }
                }
                else if (o is not Static && o is not Multi)
                {
                    continue;
                }

                ushort g = o.Graphic;

                if (g >= data.Length)
                {
                    continue;
                }

                ref StaticTiles td = ref data[g];

                // owner rule (round 2): a piece in front covers the mobile unless it is flat or the mobile stands above it
                bool floor = false;

                if (td.Height == 0 || o.Z + td.Height <= standTop || Climbable(in td, o.Z, mz, standTop, dist))
                {
                    // round 3: standing on a staircase, the stair pieces it overlaps are under it, sideways too: its strips are
                    // raised over them (the step south of a mobile on a stair facing east beat its upper strip by key)
                    if ((line & (LineAny | LineSide)) == 0 || standTop <= mz || td.Height == 0 || !Stairlike(in td))
                    {
                        continue;
                    }

                    floor = true;
                }

                if ((line & (LineX | LineY)) != 0 && !td.IsWall)
                {
                    continue;
                }

                // owner 2026-11-22: walls and doors on the side tiles keep the round-2 checks only; the side overhang (round 3
                // item C) is for furniture in front of the overhung tile, and against a wall line or an open door beside the
                // mobile it cut heads (Hythloth 5984 39 22, studio 1453 1660 10, a door opened outward)
                if (line == LineSide && (td.IsWall || td.IsDoor) && !floor)
                {
                    continue;
                }

                // the art's opaque columns: View.DrawStatic puts the art's centre column (OrigW >> 1) on the north vertex and its
                // bottom row on the south vertex (centre + 22), 4 px up per z
                int artLo = c - 22, artHi = c + 22;
                int bottom = rowC - 4 * (o.Z - mz) + 22;
                int idx = PackConst.StaticSpriteBase + g;

                if (sprites != null && idx < sprites.Length && sprites[idx].Present && sprites[idx].OrigW != 0)
                {
                    ref readonly SpriteEntry sp = ref sprites[idx];
                    artLo = c - (sp.OrigW >> 1) + sp.TrimX;
                    artHi = artLo + sp.W;
                    bottom += sp.TrimY + sp.H - sp.OrigH;
                }

                // a wall's line from where its art lies: the left half is a face along x (Y = y + 1), the right half along y
                int pieceLines = 0;

                if (td.IsWall)
                {
                    if (artLo < c - VertexMargin)
                    {
                        pieceLines |= LineX;
                    }

                    if (artHi > c + VertexMargin)
                    {
                        pieceLines |= LineY;
                    }
                }

                int lo, hi;

                if (line == LineSide)
                {
                    lo = lo0;
                    hi = hi0;
                }
                else if (line == LineAny)
                {
                    lo = td.IsWall && !floor ? lo0 : c - 22;
                    hi = td.IsWall && !floor ? hi0 : c + 22;
                }
                else if ((pieceLines & line) != 0)
                {
                    // the face half of the segment's line
                    lo = line == LineX ? c - 22 : c + VertexMargin;
                    hi = line == LineX ? c - VertexMargin : c + 22;
                }
                else
                {
                    continue;
                }

                lo = Math.Max(lo, artLo);
                hi = Math.Min(hi, artHi);

                if (lo >= hi)
                {
                    continue;
                }

                // only a piece that counts ahead starts a segment (a segment continues past pieces too high to matter)
                if (!floor && line != LineSide)
                {
                    lines |= pieceLines & (line == LineAny ? LineX | LineY : line);
                }

                if (bottom < minBottom || Holds(in s, o, tx, ty))
                {
                    continue;
                }

                int d = 100 * (tx + ty) + 127 + o.PriorityZ;
                int slot = Slot(ref s, d, floor);

                if (slot < 0)
                {
                    continue;
                }

                ref Wall w = ref s.Walls[slot];
                w = default;
                w.D = d;
                w.Floor = floor;
                w.Lo = (short)(lo + shift);
                w.Hi = (short)(hi + shift);
                w.Graphic = g;
                w.X = (ushort)tx;
                w.Y = (ushort)ty;
                w.Z = o.Z;
                w.Height = td.Height;
            }

            return lines;
        }

        /// <summary>
        /// A piece the mobile could step onto from z <paramref name="mz"/>, as the pathfinder tests it (Pathfinder.CalculateNewZ
        /// with CalculateMinMaxZ's maxZ = z + <see cref="Pathfinder.ClimbLimit"/>): a bridge or stair piece whose base is within
        /// the climb limit, or a surface whose walkable top (<see cref="Pathfinder.WalkableTop"/>) is. It is a floor for the
        /// mobile (the first stair step, the teleporter altar blocks), so it does not cover it; one higher does.
        /// </summary>
        internal static bool Climbable(in StaticTiles td, int z, int mz) => Climbable(in td, z, mz, mz, 1);

        /// <summary><see cref="Climbable(in StaticTiles, int, int)"/> for a mobile standing up to <paramref name="standTop"/>
        /// (<see cref="StandTop"/>) on a piece <paramref name="dist"/> tiles away: on stairs (standTop above its z) the run
        /// ahead is floor too, a bridge or stair piece one step height higher per further tile (owner: a mobile on a staircase
        /// stands over all the stair tiles around it).</summary>
        internal static bool Climbable(in StaticTiles td, int z, int mz, int standTop, int dist)
        {
            int maxZ = standTop + Pathfinder.ClimbLimit;

            if (Stairlike(in td))
            {
                return z <= maxZ + (standTop > mz ? td.Height * (dist - 1) : 0);
            }

            return td.IsSurface && Pathfinder.WalkableTop(z, td.Height, false) <= maxZ;
        }

        /// <summary>A free entry for a piece with key <paramref name="d"/>, or when the list is full the entry least worth keeping
        /// if the new one is worth more: pieces that cover the mobile before floor and shadow-only pieces (<paramref name="low"/>),
        /// then lower keys (they cap the most); −1: dropped (<see cref="Scan.Dropped"/>).</summary>
        private static int Slot(ref Scan s, int d, bool low = false)
        {
            if (s.Count < MaxWalls)
            {
                return s.Count++;
            }

            s.Dropped++;
            int max = -1;
            long maxRank = long.MinValue;

            for (int i = 0; i < s.Count; i++)
            {
                long rank = Rank(s.Walls[i].D, s.Walls[i].Floor || s.Walls[i].ShadowOnly);

                if (rank > maxRank)
                {
                    maxRank = rank;
                    max = i;
                }
            }

            return maxRank > Rank(d, low) ? max : -1;
        }

        private static long Rank(int d, bool low) => d + (low ? 1L << 40 : 0);

        // a piece both tiles of a step see is kept once
        private static bool Holds(in Scan s, GameObject o, int tx, int ty)
        {
            for (int i = 0; i < s.Count; i++)
            {
                ref readonly Wall w = ref s.Walls[i];

                if (w.X == tx && w.Y == ty && w.Z == o.Z && w.Graphic == o.Graphic)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The add (MobileSpriteExtract.DepthOf kind 1: depth + 1 + add) of a strip below the anchor whose frame covers columns
        /// [<paramref name="lo"/>, <paramref name="hi"/>) relative to the depth tile's centre column: <paramref name="add"/>, or
        /// less so the strip's key is one below the lowest wall of <paramref name="s"/> it overlaps (never below
        /// <see cref="Scan.MinAdd"/>: 0, the upper strip's, unless mid-step). <paramref name="wall"/>: that wall's index, −1 when none overlaps.
        /// </summary>
        public static float CapAdd(float add, in Scan s, int lo, int hi, float depth, out int wall) =>
            CapAdd(add, in s, lo, hi, depth, false, out wall);

        /// <summary><see cref="CapAdd(float, in Scan, int, int, float, out int)"/> for strip <paramref name="upper"/> (the strip
        /// from the frame top to the anchor): the pieces ahead cap it only mid-step (<see cref="Scan.MinAdd"/> below 0).
        /// Overhead pieces are not considered here (<see cref="OverheadSplit"/>).</summary>
        public static float CapAdd(float add, in Scan s, int lo, int hi, float depth, bool upper, out int wall)
        {
            wall = -1;
            int minD = int.MaxValue;

            bool ahead = !upper || s.MinAdd < 0;
            bool side = false;

            for (int i = 0; i < s.Count; i++)
            {
                ref readonly Wall w = ref s.Walls[i];

                if (w.Overhead || w.Floor || w.ShadowOnly || lo >= w.Hi || hi <= w.Lo || w.D >= minD || !(ahead || w.Side))
                {
                    continue;
                }

                minD = w.D;
                wall = i;
                side = w.Side;
            }

            if (wall < 0)
            {
                return add;
            }

            // DepthOf's key is round((depth + 1 + add) * 100) − 50; D − 1 needs depth + 1 + add = (D + 49) / 100
            float floorAdd = side && upper ? Math.Min(SideMinAdd, s.MinAdd) : s.MinAdd;
            float cap = Math.Max((float)((minD + 49) / 100.0 - (double)(depth + 1f)), floorAdd);

            return cap < add ? cap : add;
        }

        /// <summary>
        /// The overhead pieces over columns [<paramref name="lo"/>, <paramref name="hi"/>): the screen row (relative to the depth
        /// tile's centre row) below which they cover nothing (the lowest art bottom among them) and the add their lowest key
        /// allows (D − 1, not below <see cref="OverheadMinAdd"/>). The upper strip's rows above that row take the add; the rows
        /// below keep theirs: lowering the whole strip let a mobile one diagonal behind (its lower strip at + 3, the same key as
        /// this upper strip at the same z) draw over this one's body. False: none, or none lower than <paramref name="add"/>.
        /// </summary>
        public static bool OverheadSplit(in Scan s, int lo, int hi, float depth, float add, out int splitRow, out float overheadAdd, out int wall)
        {
            splitRow = int.MinValue;
            overheadAdd = add;
            wall = -1;
            int minD = int.MaxValue;

            for (int i = 0; i < s.Count; i++)
            {
                ref readonly Wall w = ref s.Walls[i];

                if (w.Overhead && lo < w.Hi && hi > w.Lo)
                {
                    splitRow = Math.Max(splitRow, w.Bottom);

                    if (w.D < minD)
                    {
                        minD = w.D;
                        wall = i;
                    }
                }
            }

            if (wall < 0)
            {
                return false;
            }

            float cap = Math.Max((float)((minD + 49) / 100.0 - (double)(depth + 1f)), OverheadMinAdd);

            if (cap >= add)
            {
                return false;
            }

            overheadAdd = cap;

            return true;
        }

        /// <summary>Round 3: the lowest add a shadow takes, key 100 (x + y − 1) + 127 + z: just over the land and background
        /// floors one diagonal behind the mobile (+ 126 + z and lower), which the shadow lies on; lower, it vanished under them
        /// (bench-001075: a wall two diagonals behind took it there). The wall right behind (+ 128 + z) still beats it.</summary>
        public const float ShadowMinAdd = -1.51f;

        /// <summary>The most a strip is raised over the stairs under it: the lower strip's + 2 (a mobile one diagonal past them
        /// still sorts over it by key).</summary>
        public const float FloorMaxAdd = 2f;

        /// <summary>
        /// Round 3: the add that puts a strip over columns [<paramref name="lo"/>, <paramref name="hi"/>) one key above every
        /// stair piece under the mobile it overlaps (<see cref="Wall.Floor"/>), at most <see cref="FloorMaxAdd"/>. False: none.
        /// Pieces that cover the mobile (<see cref="CapAdd(float, in Scan, int, int, float, bool, out int)"/>) cap it after this.
        /// </summary>
        public static bool FloorAdd(in Scan s, int lo, int hi, float depth, out float add)
        {
            int maxD = int.MinValue;

            for (int i = 0; i < s.Count; i++)
            {
                ref readonly Wall w = ref s.Walls[i];

                if (w.Floor && lo < w.Hi && hi > w.Lo && w.D > maxD)
                {
                    maxD = w.D;
                }
            }

            if (maxD == int.MinValue)
            {
                add = 0f;

                return false;
            }

            // DepthOf's key is round((depth + 1 + add) * 100) − 50; D + 1 needs depth + 1 + add = (D + 51) / 100
            add = Math.Min((float)((maxD + 51) / 100.0 - (double)(depth + 1f)), FloorMaxAdd);

            return true;
        }

        // round 3: the tiles a shadow (cast north, sheared right) reaches behind and beside the mobile: dx, dy in [-2, 1],
        // dx + dy <= 0, not its own tile
        private static readonly sbyte[] s_bx = { -2, -2, -2, -2, -1, -1, -1, -1, 0, 0, 1, 1 };
        private static readonly sbyte[] s_by = { -2, -1, 0, 1, -2, -1, 0, 1, -2, -1, -2, -1 };

        /// <summary>
        /// Round 3 (owner, Felucca 1453 1660 10: a mounted mobile's shadow cast north over the face of the wall behind it): the
        /// pieces with height standing on the mobile's floor (base under its top, top over its z) on the tiles its shadow
        /// reaches behind it, with their whole art, for its shadow only (<see cref="Wall.ShadowOnly"/>). A shadow lies on the
        /// floor, and a floor pixel sharing a screen pixel with such a piece is behind its face, in front of the mobile or not.
        /// </summary>
        internal static void ScanBehind(Map.Map map, int x, int y, int mz, int shift, int rowShift, ref Scan s)
        {
            StaticTiles[] data = Chunk.StaticTileData;
            SpriteEntry[] sprites = SpritesForTests ?? Client.Game?.UO?.FileManager?.Arts?.PackSpriteEntries;

            for (int t = 0; t < s_bx.Length; t++)
            {
                int dx = s_bx[t], dy = s_by[t];
                int tx = x + dx, ty = y + dy;
                Visit(tx, ty);
                Chunk chunk = map.GetChunk(tx, ty, false);

                if (chunk == null)
                {
                    continue;
                }

                int c = (dx - dy) * 22, rowC = (dx + dy) * 22;

                CountTile();
                for (GameObject o = TileWalk.Begin(chunk, (tx & 7) << 3 | (ty & 7), out TileWalk walk); o != null; o = walk.Next())
                {
                    CountObject();
                    if (o is Item it)
                    {
                        if (it.IsDestroyed || !it.OnGround || it.IsCorpse || it.IsMulti)
                        {
                            continue;
                        }
                    }
                    else if (o is not Static && o is not Multi)
                    {
                        continue;
                    }

                    ushort g = o.Graphic;

                    if (g >= data.Length)
                    {
                        continue;
                    }

                    ref StaticTiles td = ref data[g];

                    if (td.Height == 0 || o.Z + td.Height <= mz || o.Z >= mz + PersonHeight)
                    {
                        continue;
                    }

                    int lo = c - 22, hi = c + 22;
                    int bottom = rowC - 4 * (o.Z - mz) + 22;
                    int idx = PackConst.StaticSpriteBase + g;

                    if (sprites != null && idx < sprites.Length && sprites[idx].Present && sprites[idx].OrigW != 0)
                    {
                        ref readonly SpriteEntry sp = ref sprites[idx];
                        int left = c - (sp.OrigW >> 1) + sp.TrimX;
                        lo = Math.Max(lo, left);
                        hi = Math.Min(hi, left + sp.W);
                        bottom += sp.TrimY + sp.H - sp.OrigH;
                    }

                    if (lo >= hi || bottom + rowShift < MinBottom || Holds(in s, o, tx, ty))
                    {
                        continue;
                    }

                    int d = 100 * (tx + ty) + 127 + o.PriorityZ;
                    int slot = Slot(ref s, d, true);

                    if (slot < 0)
                    {
                        continue;
                    }

                    ref Wall w = ref s.Walls[slot];
                    w = default;
                    w.D = d;
                    w.ShadowOnly = true;
                    w.Lo = (short)(lo + shift);
                    w.Hi = (short)(hi + shift);
                    w.Graphic = g;
                    w.X = (ushort)tx;
                    w.Y = (ushort)ty;
                    w.Z = o.Z;
                    w.Height = td.Height;
                }
            }
        }

        /// <summary>
        /// The add (DepthOf kind 1) of a shadow layer over columns [<paramref name="lo"/>, <paramref name="hi"/>) relative to the
        /// depth tile's centre column: −1 (its base depth, as kind 0 draws it) or less, one key below the lowest piece of
        /// <paramref name="s"/> it overlaps. A shadow lies on the floor, and a floor pixel sharing a screen pixel with a piece
        /// standing on that floor is behind the piece's face, so a shadow loses to every overlapped piece, behind the mobile's
        /// diagonal too (the shadow cast north-east over the next wall pieces of a line, owner Felucca 1453 1665). Floors
        /// already out-key shadows, so no lower bound. False: nothing overlapped.
        /// </summary>
        public static bool ShadowAdd(in Scan s, int lo, int hi, float depth, out float add, out int wall)
        {
            wall = -1;
            add = -1f;
            int minD = int.MaxValue;

            for (int i = 0; i < s.Count; i++)
            {
                ref readonly Wall w = ref s.Walls[i];

                // round 3: a shadow loses to the pieces behind the mobile too (ShadowOnly), not to the stairs under it (Floor)
                if (!w.Overhead && !w.Floor && lo < w.Hi && hi > w.Lo && w.D < minD)
                {
                    minD = w.D;
                    wall = i;
                }
            }

            if (wall < 0)
            {
                return false;
            }

            float cap = Math.Max((float)((minD + 49) / 100.0 - (double)(depth + 1f)), ShadowMinAdd);

            if (cap >= add)
            {
                return false;
            }

            add = cap;

            return true;
        }

        /// <summary>FrameStats counters (mwo_*): scans, tiles and objects walked this frame; read and cleared by
        /// <see cref="FlushFrameCounters"/>.</summary>
        internal static int FrameScans, FrameTiles, FrameObjects, FrameCacheHits, FrameEmpty;

        [System.Diagnostics.Conditional("CUO_DEVTOOLS")]
        private static void CountTile() => FrameTiles++;

        [System.Diagnostics.Conditional("CUO_DEVTOOLS")]
        private static void CountObject() => FrameObjects++;

        /// <summary>Adds this frame's scan counters to FrameStats and clears them.</summary>
        internal static void FlushFrameCounters()
        {
            FrameStats.Add(FrameCounter.MwoScans, FrameScans);
            FrameStats.Add(FrameCounter.MwoTiles, FrameTiles);
            FrameStats.Add(FrameCounter.MwoObjects, FrameObjects);
            FrameStats.Add(FrameCounter.MwoCacheHits, FrameCacheHits);
            FrameStats.Add(FrameCounter.MwoEmpty, FrameEmpty);
            FrameScans = FrameTiles = FrameObjects = FrameCacheHits = FrameEmpty = 0;
        }

#if CUO_DEVTOOLS
        private static long _ccCompares, _ccMismatches, _ccNextSummary;
        internal static long TotalHits, TotalEmpty, TotalScans;
        private static int _ccLogged;

        private static void Compare(Map.Map map, uint serial, int x, int y, int mz, Vector3 offset, int bx, int by, int nx, int ny, bool shadow, bool rule, Scan result)
        {
            int scans = FrameScans, tiles = FrameTiles, objects = FrameObjects;
            s_reference.Clear();
            FullScan(map, x, y, mz, offset, bx, by, shadow, rule, s_reference);
            // the reference scan is not the game's: keep the counters to what Resolve did
            FrameScans = scans;
            FrameTiles = tiles;
            FrameObjects = objects;
            _ccCompares++;

            if (!result.SameAs(s_reference))
            {
                _ccMismatches++;

                if (_ccLogged++ < 50)
                {
                    Log.Warn($"[mobile-wall-cache-check] 0x{serial:X8} at {x},{y},{mz} step to {nx},{ny} depth tile {bx},{by} shadow {shadow}: " +
                        $"kept {Describe(result)} | full scan {Describe(s_reference)}");
                }
            }

            long now = Environment.TickCount64;

            if (now >= _ccNextSummary)
            {
                if (_ccNextSummary != 0)
                {
                    Log.Info($"[mobile-wall-cache-check] summary: mobile_wall_order={On}, rule={Rule}, compares {_ccCompares}, mismatches {_ccMismatches}; kept {TotalHits}, empty from the cap summaries {TotalEmpty}, full scans {TotalScans}; " +
                        $"kept scans {Scan.PoolCount} pooled / {Scan.PoolMisses} new");
                }

                _ccNextSummary = now + 5000;
            }
        }

        private static string Describe(Scan s)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"{s.Count} walls (active {s.Active}, du {s.Du} dv {s.Dv} min {s.MinAdd})");

            for (int i = 0; i < s.Count; i++)
            {
                ref readonly Wall w = ref s.Walls[i];
                sb.Append($" [0x{w.Graphic:X4} {w.X},{w.Y},{w.Z} h{w.Height} D {w.D} {w.Lo}..{w.Hi}{(w.Overhead ? " over" : "")}{(w.Floor ? " floor" : "")}{(w.ShadowOnly ? " shadow" : "")}{(w.Side ? " side" : "")}]");
            }

            return sb.ToString();
        }

        private static long _scans, _withWalls, _dropped, _moved, _movedApplied, _nextSummary;
        private static int _logged;

        private static void Tick(bool rule, bool walls, int dropped)
        {
            _scans++;
            _dropped += dropped;

            if (walls)
            {
                _withWalls++;
            }

            long now = Environment.TickCount64;

            if (now < _nextSummary)
            {
                return;
            }

            if (_nextSummary != 0)
            {
                Log.Info($"[mobile-wall-check] summary: mobile_wall_order={On}, depth_model={DepthModel.On}, rule={rule}, scans {_scans}, " +
                    $"with a wall ahead {_withWalls}, walls dropped {_dropped}, strips moved {_moved} (applied {_movedApplied})");
            }

            _nextSummary = now + 5000;
        }

        /// <summary>A strip the rule moves (<paramref name="applied"/>) or would move: logged (first 50) and counted.</summary>
        internal static void NoteMoved(in Scan s, int wall, int mx, int my, int mz, ushort body, int strip, int rows, int lo, int hi, float depth,
            float oldAdd, float newAdd)
        {
            _moved++;

            if (s.Active)
            {
                _movedApplied++;
            }

            if (_logged++ >= 50)
            {
                return;
            }

            ref readonly Wall w = ref s.Walls[wall];
            int oldD = (int)MathF.Round((depth + 1f + oldAdd) * 100f) - 50;
            int newD = (int)MathF.Round((depth + 1f + newAdd) * 100f) - 50;
            Log.Info($"[mobile-wall-check] mobile {mx},{my},{mz} body {body} strip {strip} ({rows} rows, columns {lo}..{hi}) " +
                $"{(s.Active ? "moved" : "would move")} D {oldD} -> {newD}: wall 0x{w.Graphic:X4} at {w.X},{w.Y},{w.Z} h {w.Height} D {w.D} " +
                $"columns {w.Lo}..{w.Hi}");
        }
#endif
    }
}
